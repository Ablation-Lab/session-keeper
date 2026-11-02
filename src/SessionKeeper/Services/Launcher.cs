using System.Diagnostics;
using System.IO;
using System.Text;

namespace SessionKeeper.Services;

/// <summary>One terminal tab to open. SessionId null = start a fresh claude in the folder.</summary>
public sealed record LaunchItem(string ProjectDir, string? SessionId, string Label);

public sealed class LaunchResult
{
    public List<LaunchItem> OpenedItems { get; } = new();
    public IEnumerable<string> Opened => OpenedItems.Select(i => i.Label);
    public List<string> Skipped { get; } = new();
    public string? Error { get; set; }
    public bool UsedPowerShellWindows { get; set; }
    public bool ClaudeNotFound { get; set; }
}

/// <summary>
/// Opens Claude Code sessions in Windows Terminal tabs (or separate PowerShell windows as a fallback).
///
/// Injection safety: no user-controlled text (folder names, labels, notes) is ever parsed as a command.
/// Each tab runs <c>powershell -EncodedCommand</c> with a script whose only dynamic values are
/// single-quoted PowerShell literals (with every quote character doubled) and a GUID validated with
/// <see cref="Guid.TryParseExact(string, string, out Guid)"/>. On the wt command line the folder is
/// passed with Windows argv quoting plus wt's <c>\;</c> escape, and the tab title is reduced to a
/// safe character set. Notes are never passed to any process.
/// </summary>
public static class Launcher
{
    private const int MaxCommandLine = 28_000; // CreateProcess limit is 32,767

    public static string PowerShellPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    public static string? FindWindowsTerminal()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "WindowsApps", "wt.exe");
        if (File.Exists(alias)) return alias;
        return SearchPath("wt.exe");
    }

    /// <summary>Full path to the Claude Code CLI, or null if it can't be found (then "claude" is used via PATH).</summary>
    public static string? FindClaude()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var native = Path.Combine(home, ".local", "bin", "claude.exe");
        if (File.Exists(native)) return native;
        foreach (var name in new[] { "claude.exe", "claude.cmd" })
            if (SearchPath(name) is { } p) return p;
        var npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd");
        return File.Exists(npm) ? npm : null;
    }

    /// <summary>
    /// Searches PATH for an executable. Only fully qualified PATH entries are used: a relative entry such as
    /// "." would resolve differently once the launched shell has changed into the project folder, letting a
    /// repository plant its own claude.cmd.
    /// </summary>
    private static string? SearchPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var raw in paths)
        {
            try
            {
                var dir = Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"'));
                if (!Path.IsPathFullyQualified(dir)) continue;
                var p = Path.GetFullPath(Path.Combine(dir, exe));
                if (File.Exists(p)) return p;
            }
            catch (Exception) { }
        }
        return null;
    }

    public static LaunchResult Launch(IReadOnlyList<LaunchItem> items)
    {
        var result = new LaunchResult();
        var claude = FindClaude();
        result.ClaudeNotFound = claude == null;
        claude ??= "claude";
        bool claudeIsBatch = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                             claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var valid = new List<LaunchItem>();
        foreach (var it in items)
        {
            if (Validate(it) is { } why) result.Skipped.Add($"{it.Label}: {why}");
            // cmd.exe can't use a UNC current directory and silently falls back to C:\Windows.
            else if (claudeIsBatch && it.ProjectDir.StartsWith(@"\\", StringComparison.Ordinal))
                result.Skipped.Add($"{it.Label}: network (UNC) folders aren't supported with the npm claude.cmd");
            else valid.Add(it);
        }
        if (valid.Count == 0) return result;

        var configDir = ClaudePaths.CustomConfigDir;
        var wt = FindWindowsTerminal();
        try
        {
            if (wt != null) LaunchInWindowsTerminal(wt, valid, claude, configDir, result);
            else
            {
                result.UsedPowerShellWindows = true;
                foreach (var it in valid)
                {
                    LaunchInPowerShellWindow(it, claude, configDir);
                    result.OpenedItems.Add(it); // record each success as it happens
                }
            }
        }
        catch (Exception ex)
        {
            result.Error = ex.Message;
        }
        return result;
    }

    /// <summary>Returns a reason to skip, or null when the item is safe to launch.</summary>
    public static string? Validate(LaunchItem it)
    {
        if (string.IsNullOrWhiteSpace(it.ProjectDir)) return "no folder recorded";
        if (it.ProjectDir.IndexOfAny(new[] { '"', '\0', '\r', '\n' }) >= 0) return "folder path contains invalid characters";
        if (HasLoneSurrogate(it.ProjectDir)) return "folder name can't be passed to PowerShell (invalid Unicode)";
        try
        {
            if (!Path.IsPathFullyQualified(it.ProjectDir)) return "folder path is not absolute";
            if (!Directory.Exists(it.ProjectDir)) return "folder missing (" + it.ProjectDir + ")";
        }
        catch (Exception)
        {
            return "folder path is not valid";
        }
        if (it.SessionId != null && !SessionStore.IsValidSessionId(it.SessionId)) return "invalid session id";
        return null;
    }

    private static bool HasLoneSurrogate(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1])) return true;
                i++;
            }
            else if (char.IsLowSurrogate(s[i])) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ command building (pure, unit-tested)

    /// <summary>A PowerShell single-quoted literal. PowerShell treats ' and the Unicode single quotes as delimiters.</summary>
    public static string PsQuote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('\'');
        foreach (var ch in s)
        {
            sb.Append(ch);
            if (ch is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(ch);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// The PowerShell script each tab runs. Dynamic values appear only as PsQuote literals or a validated GUID.
    /// - $ErrorActionPreference='Stop' around Set-Location: if the folder can't be entered the script stops
    ///   (the shell stays open thanks to -NoExit) instead of starting claude in the wrong folder.
    /// - NoDefaultCurrentDirectoryInExePath: stops cmd.exe (npm's claude.cmd shim calls bare "node")
    ///   from running a node.exe/node.bat planted in the project folder.
    /// - CLAUDE_CONFIG_DIR is forwarded when SessionKeeper is reading a non-default Claude folder.
    /// </summary>
    public static string BuildScript(LaunchItem it, string claude, string? configDir = null)
    {
        if (it.SessionId != null && !SessionStore.IsValidSessionId(it.SessionId))
            throw new ArgumentException("Invalid session id", nameof(it));
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'Stop'; ");
        sb.Append("$env:NoDefaultCurrentDirectoryInExePath = '1'; ");
        if (!string.IsNullOrEmpty(configDir)) sb.Append("$env:CLAUDE_CONFIG_DIR = ").Append(PsQuote(configDir)).Append("; ");
        sb.Append("Set-Location -LiteralPath ").Append(PsQuote(it.ProjectDir)).Append("; ");
        sb.Append("$ErrorActionPreference = 'Continue'; ");
        sb.Append("Write-Host ").Append(PsQuote((it.SessionId != null ? "SessionKeeper: resuming " : "SessionKeeper: new session in ") + it.Label))
          .Append(" -ForegroundColor DarkGray; ");
        sb.Append("& ").Append(PsQuote(claude));
        if (it.SessionId != null) sb.Append(" --resume ").Append(it.SessionId);
        return sb.ToString();
    }

    public static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

    /// <summary>Quotes one argument so CommandLineToArgvW yields it back unchanged.</summary>
    public static string WinQuote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"', '\\' }) < 0) return arg;
        var sb = new StringBuilder("\"");
        int backslashes = 0;
        foreach (var ch in arg)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"') { sb.Append('\\', backslashes * 2 + 1).Append('"'); backslashes = 0; continue; }
            sb.Append('\\', backslashes).Append(ch);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string SafeTitle(string label)
    {
        var sb = new StringBuilder();
        foreach (var ch in label)
        {
            if (sb.Length >= 40) break;
            if (char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.' or '(' or ')') sb.Append(ch);
        }
        var t = sb.ToString().Trim();
        return t.Length == 0 ? "claude" : t;
    }

    /// <summary>
    /// wt splits its command line on ';' (escape rules for "\;" next to path backslashes are ambiguous) and
    /// expands %VAR% in -d, so a folder containing ';' or '%' is not passed via -d at all: the encoded
    /// script's Set-Location moves into it instead. The title never contains ';', '%' or quotes.
    /// </summary>
    public static string BuildTabArgs(LaunchItem it, string claude, string? configDir = null)
    {
        var sb = new StringBuilder("new-tab --title ").Append(WinQuote(SafeTitle(it.Label)));
        if (it.ProjectDir.IndexOfAny(new[] { ';', '%' }) < 0) sb.Append(" -d ").Append(WinQuote(it.ProjectDir));
        sb.Append(' ').Append(WinQuote(PowerShellPath)).Append(" -NoLogo -NoExit -EncodedCommand ")
          .Append(Encode(BuildScript(it, claude, configDir)));
        return sb.ToString();
    }

    /// <summary>Builds one or more wt command lines (split to stay under the OS length limit), with the items each opens.</summary>
    public static List<(string CommandLine, List<LaunchItem> Items)> BuildWtCommandLines(
        IReadOnlyList<LaunchItem> items, string claude, string windowName, string? configDir = null)
    {
        var lines = new List<(string, List<LaunchItem>)>();
        var prefix = "-w " + windowName + " ";
        var current = new StringBuilder();
        var currentItems = new List<LaunchItem>();
        foreach (var it in items)
        {
            var tab = BuildTabArgs(it, claude, configDir);
            if (current.Length > 0 && prefix.Length + current.Length + 3 + tab.Length > MaxCommandLine)
            {
                lines.Add((prefix + current, currentItems));
                current.Clear();
                currentItems = new List<LaunchItem>();
            }
            if (current.Length > 0) current.Append(" ; ");
            current.Append(tab);
            currentItems.Add(it);
        }
        if (current.Length > 0) lines.Add((prefix + current, currentItems));
        return lines;
    }

    // ------------------------------------------------------------------ process start

    private static void LaunchInWindowsTerminal(string wt, IReadOnlyList<LaunchItem> items, string claude, string? configDir,
        LaunchResult result)
    {
        var window = "sessionkeeper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var lines = BuildWtCommandLines(items, claude, window, configDir);
        for (int i = 0; i < lines.Count; i++)
        {
            var psi = new ProcessStartInfo(wt, lines[i].CommandLine) { UseShellExecute = false };
            CleanEnvironment(psi);
            var labels = string.Join(", ", lines[i].Items.Select(x => x.Label));
            using (LaunchGuard.Start(psi, $"Windows Terminal: {lines[i].Items.Count} tab(s): {labels}")) { }
            result.OpenedItems.AddRange(lines[i].Items); // record each chunk as it succeeds
            if (i < lines.Count - 1) Thread.Sleep(1500); // let the named window exist before adding more tabs
        }
    }

    private static void LaunchInPowerShellWindow(LaunchItem it, string claude, string? configDir)
    {
        var psi = new ProcessStartInfo(PowerShellPath, "-NoLogo -NoExit -EncodedCommand " + Encode(BuildScript(it, claude, configDir)))
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = it.ProjectDir,
        };
        CleanEnvironment(psi);
        using (LaunchGuard.Start(psi, $"PowerShell window: {it.Label}")) { }
    }

    /// <summary>
    /// Variables Claude Code sets for its own child processes. If SessionKeeper was started from inside a
    /// Claude Code session it inherits them, and they stop a new claude from starting normally. Only these
    /// are removed (and only when not part of the user's persistent environment), so user configuration such
    /// as CLAUDE_CONFIG_DIR or CLAUDE_CODE_USE_BEDROCK passes through untouched.
    /// </summary>
    private static readonly string[] SessionScopedVariables =
    {
        "CLAUDECODE", "CLAUDE_PID", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_SESSION_ATTENDED", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SSE_PORT",
    };

    internal static void CleanEnvironment(ProcessStartInfo psi)
    {
        foreach (var key in SessionScopedVariables)
        {
            if (!psi.Environment.ContainsKey(key)) continue;
            bool persistent =
                Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User) != null ||
                Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine) != null;
            if (!persistent) psi.Environment.Remove(key);
        }
    }
}
