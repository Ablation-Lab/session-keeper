using System.Diagnostics;
using System.IO;
using System.Text;

namespace SessionKeeper.Services;

/// <summary>One terminal tab to open. SessionId null = start a fresh claude in the folder.</summary>
public sealed record LaunchItem(string ProjectDir, string? SessionId, string Label);

public sealed class LaunchResult
{
    public List<string> Opened { get; } = new();
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

    private static string? SearchPath(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            try
            {
                var p = Path.Combine(dir.Trim().Trim('"'), exe);
                if (File.Exists(p)) return p;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static LaunchResult Launch(IReadOnlyList<LaunchItem> items)
    {
        var result = new LaunchResult();
        var valid = new List<LaunchItem>();
        foreach (var it in items)
        {
            if (Validate(it) is { } why) result.Skipped.Add($"{it.Label}: {why}");
            else valid.Add(it);
        }
        if (valid.Count == 0) return result;

        var claude = FindClaude();
        result.ClaudeNotFound = claude == null;
        claude ??= "claude";

        var wt = FindWindowsTerminal();
        try
        {
            if (wt != null) LaunchInWindowsTerminal(wt, valid, claude);
            else
            {
                result.UsedPowerShellWindows = true;
                foreach (var it in valid) LaunchInPowerShellWindow(it, claude);
            }
            result.Opened.AddRange(valid.Select(v => v.Label));
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

    public static string BuildScript(LaunchItem it, string claude)
    {
        if (it.SessionId != null && !SessionStore.IsValidSessionId(it.SessionId))
            throw new ArgumentException("Invalid session id", nameof(it));
        var sb = new StringBuilder();
        sb.Append("Set-Location -LiteralPath ").Append(PsQuote(it.ProjectDir)).Append("; ");
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
    /// wt splits its command line on ';' (escape rules for "\;" next to path backslashes are ambiguous),
    /// so a folder containing ';' is not passed via -d at all: the encoded script's Set-Location
    /// moves into it instead. The title is restricted to characters that never include ';' or quotes.
    /// </summary>
    public static string BuildTabArgs(LaunchItem it, string claude)
    {
        var sb = new StringBuilder("new-tab --title ").Append(WinQuote(SafeTitle(it.Label)));
        if (!it.ProjectDir.Contains(';')) sb.Append(" -d ").Append(WinQuote(it.ProjectDir));
        sb.Append(' ').Append(WinQuote(PowerShellPath)).Append(" -NoLogo -NoExit -EncodedCommand ")
          .Append(Encode(BuildScript(it, claude)));
        return sb.ToString();
    }

    /// <summary>Builds one or more wt command lines (split to stay under the OS length limit).</summary>
    public static List<string> BuildWtCommandLines(IReadOnlyList<LaunchItem> items, string claude, string windowName)
    {
        var lines = new List<string>();
        var prefix = "-w " + windowName + " ";
        var current = new StringBuilder();
        foreach (var it in items)
        {
            var tab = BuildTabArgs(it, claude);
            if (current.Length > 0 && prefix.Length + current.Length + 3 + tab.Length > MaxCommandLine)
            {
                lines.Add(prefix + current);
                current.Clear();
            }
            if (current.Length > 0) current.Append(" ; ");
            current.Append(tab);
        }
        if (current.Length > 0) lines.Add(prefix + current);
        return lines;
    }

    // ------------------------------------------------------------------ process start

    private static void LaunchInWindowsTerminal(string wt, IReadOnlyList<LaunchItem> items, string claude)
    {
        var window = "sessionkeeper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var lines = BuildWtCommandLines(items, claude, window);
        for (int i = 0; i < lines.Count; i++)
        {
            var psi = new ProcessStartInfo(wt, lines[i]) { UseShellExecute = false };
            CleanEnvironment(psi);
            using var _ = Process.Start(psi);
            if (i < lines.Count - 1) Thread.Sleep(1500); // let the named window exist before adding more tabs
        }
    }

    private static void LaunchInPowerShellWindow(LaunchItem it, string claude)
    {
        var psi = new ProcessStartInfo(PowerShellPath, "-NoLogo -NoExit -EncodedCommand " + Encode(BuildScript(it, claude)))
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = it.ProjectDir,
        };
        CleanEnvironment(psi);
        using var _ = Process.Start(psi);
    }

    /// <summary>
    /// If SessionKeeper itself was started from inside a Claude Code session it inherits that session's
    /// variables (CLAUDECODE, CLAUDE_CODE_SESSION_ID, ...), which stop a new claude from starting normally.
    /// Remove CLAUDE* variables that are not part of the user's persistent environment.
    /// </summary>
    internal static void CleanEnvironment(ProcessStartInfo psi)
    {
        foreach (var key in psi.Environment.Keys.ToList())
        {
            if (!key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase)) continue;
            bool persistent =
                Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User) != null ||
                Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine) != null;
            if (!persistent) psi.Environment.Remove(key);
        }
    }
}
