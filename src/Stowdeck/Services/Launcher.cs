using System.Diagnostics;
using System.IO;
using System.Text;

namespace Stowdeck.Services;

/// <summary>
/// One window/tab to open. SessionId null = start a fresh claude in the folder. Title (the session's nickname)
/// becomes the window/tab title and is kept there (Claude's own title updates are switched off for it).
/// With <paramref name="Terminal"/> set it's a terminal session instead (no claude): the shell opens in the
/// folder and runs that command, which the user has confirmed. With <paramref name="Npm"/> set it starts Remotion
/// Studio or the dev server in the folder (npm.cmd run dev or npx.cmd remotion studio), from the Tools menu.
/// </summary>
public sealed record LaunchItem(string ProjectDir, string? SessionId, string Label, string? Title = null, TerminalCommand? Terminal = null,
    NpmCommand? Npm = null)
{
    /// <summary>A Claude Code session (new or resumed), not a terminal or an npm launch (Remotion Studio, dev server).</summary>
    public bool IsClaude => Terminal == null && Npm == null;
}

public sealed class LaunchResult
{
    public List<LaunchItem> OpenedItems { get; } = new();
    public IEnumerable<string> Opened => OpenedItems.Select(i => i.Label);
    public List<string> Skipped { get; } = new();
    public string? Error { get; set; }
    public bool UsedPowerShellWindows { get; set; }
    /// <summary>Windows Terminal tabs were asked for but Terminal isn't installed, so PowerShell windows were used.</summary>
    public bool TerminalMissing { get; set; }
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

    /// <summary>
    /// The classic console host. Starting PowerShell through it gives a real PowerShell window even when
    /// Windows' default terminal is set to Windows Terminal (which would otherwise take over new consoles).
    /// </summary>
    public static string ConhostPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");

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
    internal static string? SearchPath(string exe)
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

    public static LaunchResult Launch(IReadOnlyList<LaunchItem> items, OpenMode mode = OpenMode.PowerShellWindow)
    {
        var result = new LaunchResult();
        bool anyClaude = items.Any(i => i.IsClaude);
        var claude = anyClaude ? FindClaude() : null;
        result.ClaudeNotFound = anyClaude && claude == null;
        claude ??= "claude";
        bool claudeIsBatch = claude.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
                             claude.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var valid = new List<LaunchItem>();
        foreach (var it in items)
        {
            if (Validate(it) is { } why) result.Skipped.Add($"{it.Label}: {why}");
            // cmd.exe can't use a UNC current directory and silently falls back to C:\Windows.
            else if (it.IsClaude && claudeIsBatch && it.ProjectDir.StartsWith(@"\\", StringComparison.Ordinal))
                result.Skipped.Add($"{it.Label}: network (UNC) folders aren't supported with the npm claude.cmd");
            else valid.Add(it);
        }
        if (valid.Count == 0) return result;

        var configDir = ClaudePaths.CustomConfigDir;
        var wt = mode == OpenMode.WindowsTerminalTab ? FindWindowsTerminal() : null;
        result.TerminalMissing = mode == OpenMode.WindowsTerminalTab && wt == null;
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
        if (it.Terminal != null && it.Npm != null) return "an entry can't be both a terminal and an npm launch";
        if (it.Terminal is { } t) return ValidateTerminal(t, it.SessionId);
        if (it.Npm is { } s) return ValidateNpm(s, it.ProjectDir, it.SessionId);
        return null;
    }

    /// <summary>
    /// Remotion Studio and the dev server: only npm.cmd run dev or npx.cmd remotion studio, from a local folder that still has its
    /// package.json. npm.cmd runs through cmd.exe, which can't use a network (UNC) folder and would silently start in
    /// C:\Windows instead, so those are refused.
    /// </summary>
    internal static string? ValidateNpm(NpmCommand s, string projectDir, string? sessionId)
    {
        if (sessionId != null) return s.What + " can't carry a Claude session id";
        if (projectDir.StartsWith(@"\\", StringComparison.Ordinal) || projectDir.StartsWith("//", StringComparison.Ordinal))
            return "network (UNC) folders aren't supported for " + s.What;
        if (RemotionStudio.Check(s, projectDir) is { } why) return why;
        if (RemotionStudio.CheckFolder(projectDir) is { } folderWhy) return folderWhy;
        try
        {
            if (!File.Exists(s.Exe)) return Path.GetFileName(s.Exe) + " no longer exists (" + s.Exe + ")";
            if (!File.Exists(Path.Combine(projectDir, "package.json"))) return "the folder has no package.json";
        }
        catch (Exception)
        {
            return "the path is not valid";
        }
        return null;
    }

    /// <summary>A terminal command must name an existing .exe by full path, with arguments on one line.</summary>
    internal static string? ValidateTerminal(TerminalCommand t, string? sessionId)
    {
        if (sessionId != null) return "a terminal entry can't carry a Claude session id";
        if (t.Args.Length > TerminalCommands.MaxCommandLength) return "the command is too long";
        if (ValidateTerminalShape(t) is { } why) return why;
        if (t.Exe == null) return null;
        try
        {
            if (!File.Exists(t.Exe)) return "the program no longer exists (" + t.Exe + ")";
        }
        catch (Exception)
        {
            return "the program path is not valid";
        }
        return null;
    }

    /// <summary>The checks that don't touch the disk (BuildTerminalScript runs them again before building).</summary>
    private static string? ValidateTerminalShape(TerminalCommand t)
    {
        // No control, escape, bidi or invisible characters: what the confirmation showed must be what runs.
        if (TerminalCommands.HiddenCharacter(t.Args, allowTab: true) is { } hidden) return "the command contains " + hidden;
        if (t.Exe == null) return t.Args.Length == 0 ? null : "arguments without a program";
        return TerminalCommands.CheckExe(t.Exe);
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
    /// - CLAUDE_CONFIG_DIR is forwarded when Stowdeck is reading a non-default Claude folder.
    /// - With a Title (nickname): the console title is set to it (a quoted literal) and Claude's own
    ///   terminal-title updates are switched off (CLAUDE_CODE_DISABLE_TERMINAL_TITLE), or Claude would
    ///   replace it within a second. Sessions without a nickname keep Claude's live title.
    /// </summary>
    public static string BuildScript(LaunchItem it, string claude, string? configDir = null)
    {
        if (it.Terminal != null) return BuildTerminalScript(it);
        if (it.Npm != null) return BuildNpmScript(it);
        if (it.SessionId != null && !SessionStore.IsValidSessionId(it.SessionId))
            throw new ArgumentException("Invalid session id", nameof(it));
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'Stop'; ");
        sb.Append("$env:NoDefaultCurrentDirectoryInExePath = '1'; ");
        if (!string.IsNullOrEmpty(configDir)) sb.Append("$env:CLAUDE_CONFIG_DIR = ").Append(PsQuote(configDir)).Append("; ");
        if (NicknameStore.Clean(it.Title) is { } title)
        {
            // A title must never stop the session from starting, so a host that refuses it is ignored.
            sb.Append("try { $Host.UI.RawUI.WindowTitle = ").Append(PsQuote(title)).Append(" } catch { }; ");
            sb.Append("$env:CLAUDE_CODE_DISABLE_TERMINAL_TITLE = '1'; ");
        }
        sb.Append("Set-Location -LiteralPath ").Append(PsQuote(it.ProjectDir)).Append("; ");
        sb.Append("$ErrorActionPreference = 'Continue'; ");
        sb.Append("Write-Host ").Append(PsQuote((it.SessionId != null ? "Stowdeck: resuming " : "Stowdeck: new session in ") + it.Label))
          .Append(" -ForegroundColor DarkGray; ");
        sb.Append("& ").Append(PsQuote(claude));
        if (it.SessionId != null) sb.Append(" --resume ").Append(it.SessionId);
        return sb.ToString();
    }

    /// <summary>
    /// The script for a terminal entry. As with Claude sessions, every dynamic value is a PsQuote literal. The
    /// command is not PowerShell text: the program's path and its argument string go into a ProcessStartInfo, so
    /// Windows receives the arguments exactly as saved, and nothing in them is interpreted by PowerShell. The
    /// program shares the window's console (interactive programs work) and the shell stays open afterwards.
    /// </summary>
    public static string BuildTerminalScript(LaunchItem it)
    {
        var t = it.Terminal ?? throw new ArgumentException("Not a terminal entry", nameof(it));
        if (ValidateTerminalShape(t) is { } why) throw new ArgumentException(why, nameof(it));
        // The label is shown in the console, so it's cleaned of control and escape characters (no terminal sequences).
        var label = NicknameStore.Clean(it.Label) ?? "terminal";
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'Stop'; ");
        sb.Append("$env:NoDefaultCurrentDirectoryInExePath = '1'; ");
        sb.Append("try { $Host.UI.RawUI.WindowTitle = ").Append(PsQuote(SafeTitle(NicknameStore.Clean(it.Title) ?? label))).Append(" } catch { }; ");
        sb.Append("Set-Location -LiteralPath ").Append(PsQuote(it.ProjectDir)).Append("; ");
        sb.Append("$ErrorActionPreference = 'Continue'; ");
        sb.Append("Write-Host ").Append(PsQuote("Stowdeck: " + label + " in " + it.ProjectDir)).Append(" -ForegroundColor DarkGray; ");
        if (t.Exe != null)
        {
            sb.Append("Write-Host ").Append(PsQuote("Stowdeck: running " + t.Display)).Append(" -ForegroundColor DarkGray; ");
            sb.Append("$skStart = New-Object System.Diagnostics.ProcessStartInfo; ");
            sb.Append("$skStart.FileName = ").Append(PsQuote(t.Exe)).Append("; ");
            sb.Append("$skStart.Arguments = ").Append(PsQuote(t.Args)).Append("; ");
            sb.Append("$skStart.UseShellExecute = $false; ");
            sb.Append("$skStart.WorkingDirectory = ").Append(PsQuote(it.ProjectDir)).Append("; ");
            sb.Append("try { $skProcess = [System.Diagnostics.Process]::Start($skStart); $skProcess.WaitForExit() } ");
            sb.Append("catch { Write-Host ('Stowdeck: could not start it: ' + $_.Exception.Message) -ForegroundColor Red }");
        }
        return sb.ToString();
    }

    /// <summary>
    /// The script for Remotion Studio or the dev server. As for every launch, the folder is only a PsQuote literal, and Set-Location
    /// runs with $ErrorActionPreference='Stop': if the folder can't be entered the script ends there (the shell
    /// stays open) and npm never runs, so it can't start in the wrong folder. The tool path and its fixed arguments
    /// are literals too. NoDefaultCurrentDirectoryInExePath stops cmd.exe (which runs npm.cmd) from picking up a
    /// node.exe from the folder instead of the installed one.
    /// </summary>
    public static string BuildNpmScript(LaunchItem it)
    {
        var s = it.Npm ?? throw new ArgumentException("Not an npm launch", nameof(it));
        if (RemotionStudio.Check(s, it.ProjectDir) is { } why) throw new ArgumentException(why, nameof(it));
        if (it.ProjectDir.IndexOfAny(RemotionStudio.CmdSpecial) >= 0)
            throw new ArgumentException("the folder path contains a cmd.exe command character", nameof(it));
        var label = NicknameStore.Clean(it.Label) ?? "project";
        var sb = new StringBuilder();
        sb.Append("$ErrorActionPreference = 'Stop'; ");
        sb.Append("$env:NoDefaultCurrentDirectoryInExePath = '1'; ");
        sb.Append("try { $Host.UI.RawUI.WindowTitle = ").Append(PsQuote(SafeTitle(NicknameStore.Clean(it.Title) ?? label))).Append(" } catch { }; ");
        sb.Append("Set-Location -LiteralPath ").Append(PsQuote(it.ProjectDir)).Append("; ");
        sb.Append("$ErrorActionPreference = 'Continue'; ");
        sb.Append("Write-Host ").Append(PsQuote("Stowdeck: " + s.What + " for " + label + " in " + it.ProjectDir)).Append(" -ForegroundColor DarkGray; ");
        sb.Append("Write-Host ").Append(PsQuote("Stowdeck: running " + s.Display)).Append(" -ForegroundColor DarkGray; ");
        sb.Append("& ").Append(PsQuote(s.Exe));
        foreach (var a in s.Args) sb.Append(' ').Append(PsQuote(a));
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

    /// <summary>
    /// A tab title safe for the wt command line: letters, digits, spaces and a little harmless punctuation.
    /// Never ';' (wt's command separator), '%', quotes, backslashes or control characters.
    /// </summary>
    public static string SafeTitle(string label)
    {
        var sb = new StringBuilder();
        foreach (var ch in label)
        {
            if (sb.Length >= 60) break;
            if (char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' or '.' or '(' or ')' or '[' or ']' or ':' or ',' or '!' or '?' or '\'' or '+' or '#' or '@' or '&' or '/')
                sb.Append(ch);
        }
        var t = sb.ToString().Trim();
        if (t.Length == 0) return "claude";
        // wt would read a leading '-' as one of its own options ("--title -d x" or "--help"), so prefix it.
        return t[0] == '-' ? "_" + t : t;
    }

    /// <summary>
    /// wt splits its command line on ';' (escape rules for "\;" next to path backslashes are ambiguous) and
    /// expands %VAR% in -d, so a folder containing ';' or '%' is not passed via -d at all: the encoded
    /// script's Set-Location moves into it instead. The title never contains ';', '%' or quotes.
    /// With a nickname, the tab is titled with it and --suppressApplicationTitle keeps Claude from replacing it.
    /// </summary>
    public static string BuildTabArgs(LaunchItem it, string claude, string? configDir = null)
    {
        // Claude sessions with a nickname, and npm tabs, keep their title (npm and cmd would otherwise replace it).
        var nickname = it.Terminal == null ? NicknameStore.Clean(it.Title) : null;
        var sb = new StringBuilder("new-tab --title ").Append(WinQuote(SafeTitle(nickname ?? it.Title ?? it.Label)));
        if (nickname != null) sb.Append(" --suppressApplicationTitle");
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
        var window = "stowdeck-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var lines = BuildWtCommandLines(items, claude, window, configDir);
        for (int i = 0; i < lines.Count; i++)
        {
            var psi = new ProcessStartInfo(wt, lines[i].CommandLine) { UseShellExecute = false };
            CleanEnvironment(psi);
            foreach (var it in lines[i].Items) LogTerminalCommand(it);
            var labels = string.Join(", ", lines[i].Items.Select(x => x.Label));
            using (LaunchGuard.Start(psi, $"Windows Terminal: {lines[i].Items.Count} tab(s): {labels}")) { }
            result.OpenedItems.AddRange(lines[i].Items); // record each chunk as it succeeds
            if (i < lines.Count - 1) Thread.Sleep(1500); // let the named window exist before adding more tabs
        }
    }

    private static void LaunchInPowerShellWindow(LaunchItem it, string claude, string? configDir)
    {
        var psi = BuildPowerShellWindowStart(it, claude, configDir);
        CleanEnvironment(psi);
        LogTerminalCommand(it);
        using (LaunchGuard.Start(psi, $"PowerShell window: {it.Label}")) { }
    }

    /// <summary>
    /// The launch log records each terminal command in plain text (the window's own command line is base64), in
    /// full (at most 4,000 characters), so the log shows what actually ran. Likely passwords, tokens and keys are
    /// replaced by *** first.
    /// </summary>
    private static void LogTerminalCommand(LaunchItem it)
    {
        if (it.Npm is { } s)
        {
            LaunchGuard.LogEvent("COMMAND", $"action={LaunchGuard.CurrentAction ?? "(none)"}  label={NicknameStore.Clean(it.Label)}  folder={it.ProjectDir}  run={s.Exe} {string.Join(' ', s.Args)}");
            return;
        }
        if (it.Terminal is not { } t) return;
        var run = t.Exe == null ? "(shell only)" : TerminalCommands.Redact(t.Display);
        LaunchGuard.LogEvent("COMMAND", $"action={LaunchGuard.CurrentAction ?? "(none)"}  label={NicknameStore.Clean(it.Label)}  folder={it.ProjectDir}  run={run}");
    }

    /// <summary>
    /// conhost.exe "…\powershell.exe" -NoLogo -NoExit -EncodedCommand &lt;base64&gt;
    /// The only text on this command line is two fixed system paths and base64, so nothing from the folder
    /// name or session can be parsed as a command; the folder reaches the script as a quoted literal and
    /// is also the process's working directory.
    /// </summary>
    public static ProcessStartInfo BuildPowerShellWindowStart(LaunchItem it, string claude, string? configDir = null) =>
        new(ConhostPath, WinQuote(PowerShellPath) + " -NoLogo -NoExit -EncodedCommand " + Encode(BuildScript(it, claude, configDir)))
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = it.ProjectDir,
        };

    /// <summary>
    /// Variables Claude Code sets for its own child processes. If Stowdeck was started from inside a
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
        var kept = CleanEnvironment(psi, IsPersistent);
        // Other CLAUDE* variables pass through (they may be deliberate per-shell settings such as
        // CLAUDE_CODE_USE_BEDROCK), but a new session-scoped one in a future Claude Code would too, so their
        // names (never values) are logged to make that visible.
        if (kept.Count > 0)
            LaunchGuard.LogEvent("ENV", "passed through non-persistent CLAUDE* variables: " + string.Join(", ", kept));
    }

    /// <summary>
    /// Removes the session-scoped variables that aren't persistent, and returns the names of any other
    /// non-persistent CLAUDE* variables left in place.
    /// </summary>
    internal static List<string> CleanEnvironment(ProcessStartInfo psi, Func<string, bool> isPersistent)
    {
        foreach (var key in SessionScopedVariables)
            if (psi.Environment.ContainsKey(key) && !isPersistent(key)) psi.Environment.Remove(key);
        return psi.Environment.Keys
            .Where(k => k.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) && !isPersistent(k) &&
                        !SessionScopedVariables.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsPersistent(string key) =>
        Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.User) != null ||
        Environment.GetEnvironmentVariable(key, EnvironmentVariableTarget.Machine) != null;
}
