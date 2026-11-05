using System.IO;
using System.Text;
using Stowdeck.Models;

namespace Stowdeck.Services;

/// <summary>
/// What a reopened terminal runs. <see cref="Exe"/> null = just the PowerShell window in the folder. Otherwise the
/// new window's PowerShell starts <see cref="Exe"/> (a full path) with <see cref="Args"/> passed to Windows
/// unchanged; neither is ever parsed by PowerShell or a shell on Stowdeck's side. <see cref="Display"/> is
/// built from exactly those two values, so what the confirmation shows is what runs.
/// </summary>
public sealed record TerminalCommand(string? Exe, string Args, string Display, bool IsPlainShell);

/// <summary>One way to reopen a saved terminal: run its command again, resume the agent, or open the shell only.</summary>
public sealed record TerminalOption(string Kind, string Title, TerminalCommand Command, string? Warning)
{
    public const string Run = "run", Resume = "resume", ShellOnly = "shell";
    public override string ToString() => Title;
}

/// <summary>The reopen choices for one saved terminal, and why its command can't be re-run (if so).</summary>
public sealed record TerminalPlan(SnapshotTerminal Entry, IReadOnlyList<TerminalOption> Options, string? Problem)
{
    public TerminalOption Default => Options[0];
    /// <summary>The same agent or program is still running in that folder, so reopening it would start a second copy.</summary>
    public bool StillRunning { get; init; }
    /// <summary>Ticked by default only when the default choice doesn't look destructive and isn't still running.</summary>
    public bool DefaultTicked => Default.Warning == null && !StillRunning;
    /// <summary>
    /// Nothing but a shell, and nothing was saved to run: safe to open without a confirmation. An entry whose
    /// saved command was refused still gets the dialog, so the user sees why it isn't re-run.
    /// </summary>
    public bool IsPlainShell => Entry.CommandLine == null && Problem == null && Options.Count == 1 && Options[0].Command.IsPlainShell;
}

/// <summary>Command-line handling for terminal sessions. Pure string work, unit-tested.</summary>
public static class TerminalCommands
{
    /// <summary>Longer commands aren't re-run (they'd also strain the window's command-line limit).</summary>
    public const int MaxCommandLength = 4000;

    // ------------------------------------------------------------------ parsing

    /// <summary>The program (argv[0], as Windows reads it) and the raw rest of the command line.</summary>
    public static (string Program, string Args) SplitProgram(string commandLine)
    {
        var s = commandLine.TrimStart(' ', '\t');
        string program;
        int i;
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            program = end < 0 ? s[1..] : s[1..end];
            i = end < 0 ? s.Length : end + 1;
        }
        else
        {
            i = 0;
            while (i < s.Length && s[i] != ' ' && s[i] != '\t') i++;
            program = s[..i];
        }
        return (program, s[i..].TrimStart(' ', '\t'));
    }

    /// <summary>A token of a command line: its unquoted value and its exact original text.</summary>
    public readonly record struct Token(string Value, string Raw, int Start = 0);

    /// <summary>Splits arguments with the C runtime's rules (quotes, backslashes before quotes, "" inside quotes).</summary>
    public static List<Token> Tokenize(string args)
    {
        var tokens = new List<Token>();
        int i = 0;
        while (i < args.Length)
        {
            while (i < args.Length && args[i] is ' ' or '\t') i++;
            if (i >= args.Length) break;
            int start = i;
            var value = new StringBuilder();
            bool quoted = false;
            while (i < args.Length && (quoted || args[i] is not (' ' or '\t')))
            {
                if (args[i] == '\\')
                {
                    int n = 0;
                    while (i < args.Length && args[i] == '\\') { n++; i++; }
                    if (i < args.Length && args[i] == '"')
                    {
                        value.Append('\\', n / 2);
                        if (n % 2 == 1) { value.Append('"'); i++; }
                    }
                    else value.Append('\\', n);
                    continue;
                }
                if (args[i] == '"')
                {
                    if (quoted && i + 1 < args.Length && args[i + 1] == '"') { value.Append('"'); i += 2; continue; }
                    quoted = !quoted;
                    i++;
                    continue;
                }
                value.Append(args[i++]);
            }
            tokens.Add(new Token(value.ToString(), args[start..i], start));
        }
        return tokens;
    }

    /// <summary>The command line as saved: the program's real image path (quoted) and the original arguments.</summary>
    public static string? ToSaved(string? imagePath, string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || commandLine == null || imagePath.Contains('"')) return null;
        var (_, args) = SplitProgram(commandLine);
        return "\"" + imagePath + "\"" + (args.Length > 0 ? " " + args : "");
    }

    /// <summary>Checks a saved command line (which may have been hand-edited) and turns it into something runnable.</summary>
    public static TerminalCommand? ParseSaved(string? commandLine, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(commandLine)) { problem = "no command saved"; return null; }
        if (commandLine.Length > MaxCommandLength) { problem = $"the command is longer than {MaxCommandLength} characters"; return null; }
        if (HiddenCharacter(commandLine, allowTab: true) is { } hidden) { problem = "the command contains " + hidden; return null; }
        var (exe, args) = SplitProgram(commandLine);
        if (CheckExe(exe) is { } why) { problem = why; return null; }
        return new TerminalCommand(exe, args, Show(exe, args), IsPlainShell: false);
    }

    /// <summary>Only an .exe given by full path is ever run (no shell-associated files, no PATH lookups).</summary>
    internal static string? CheckExe(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return "no program recorded";
        if (exe.IndexOfAny(new[] { '"', '*', '?', '<', '>', '|' }) >= 0 || HiddenCharacter(exe, allowTab: false) != null)
            return "the program path contains invalid characters";
        // Only a plain local path: no network share (\\server), device path (\\?\, \\.\), ".." that could make a
        // path read like System32 while pointing elsewhere, or alternate data stream (a second ':').
        if (exe.StartsWith(@"\\", StringComparison.Ordinal) || exe.StartsWith("//", StringComparison.Ordinal))
            return "programs on network or device paths aren't re-run";
        if (exe.Replace('/', '\\').Split('\\').Any(seg => seg == ".." || seg == ".")) return "the program path contains . or .. segments";
        if (exe.IndexOf(':', 2) >= 0) return "the program path is not valid";
        try
        {
            if (!Path.IsPathFullyQualified(exe)) return "the program path is not absolute";
        }
        catch (Exception) { return "the program path is not valid"; }
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return "only .exe programs can be re-run";
        return null;
    }

    /// <summary>Exactly what Windows is given: the quoted program, then the arguments unchanged.</summary>
    public static string Show(string exe, string args) => "\"" + exe + "\"" + (args.Length > 0 ? " " + args : "");

    // ------------------------------------------------------------------ reopen choices

    /// <summary>Shells that ship with Windows: reopened from System32, whatever path the snapshot file says.</summary>
    private static readonly HashSet<string> SystemShells = new(StringComparer.OrdinalIgnoreCase) { "cmd", "wsl" };

    /// <summary>
    /// Opens the shell only (an idle shell, or the fallback for any terminal); never runs saved arguments.
    /// Windows PowerShell is the new window itself. cmd and WSL are started from System32 (the saved path is
    /// ignored), so they count as plain shells that open without a confirmation. Any other shell (PowerShell 7,
    /// Git Bash, …) runs from its saved path, which a hand-edited snapshot could change, so it isn't "plain":
    /// the confirmation shows the path first.
    /// </summary>
    public static TerminalCommand ShellCommand(string? shellName, string? shellExe, string? systemDir = null)
    {
        var shell = KnownAgents.Shell(shellExe) ?? KnownAgents.Shells.FirstOrDefault(s => s.Name == shellName);
        var powerShell = new TerminalCommand(null, "", "PowerShell in the folder", IsPlainShell: true);
        if (shell == null || shell.ProcessName == "powershell") return powerShell;
        if (SystemShells.Contains(shell.ProcessName))
        {
            var exe = Path.Combine(systemDir ?? Environment.GetFolderPath(Environment.SpecialFolder.System), shell.ProcessName + ".exe");
            return new TerminalCommand(exe, shell.ReopenArgs, Show(exe, shell.ReopenArgs), IsPlainShell: true);
        }
        if (shellExe == null || CheckExe(shellExe) != null ||
            !string.Equals(KnownAgents.ProcessName(shellExe), shell.ProcessName, StringComparison.OrdinalIgnoreCase))
            return powerShell;
        return new TerminalCommand(shellExe, shell.ReopenArgs, Show(shellExe, shell.ReopenArgs), IsPlainShell: false);
    }

    /// <summary>
    /// The agent's verified resume command, run with the same program (and, for node/python, the same script)
    /// as before. Null when the agent has no verified resume option.
    /// </summary>
    public static TerminalCommand? ResumeCommand(KnownAgent? agent, TerminalCommand? run)
    {
        if (agent?.Resume is not { Length: > 0 } resume || run?.Exe == null) return null;
        // Only for the agent's own program (or a script host running it), whatever the snapshot's "agent" says.
        if (!KnownAgents.Matches(agent, run.Exe, run.Display)) return null;
        var kept = new List<string>();
        if (KnownAgents.ScriptHosts.Contains(KnownAgents.ProcessName(run.Exe) ?? ""))
        {
            var tokens = Tokenize(run.Args);
            if (tokens.Count == 0) return null;
            int keep = tokens[0].Value == "-m" && tokens.Count > 1 ? 2 : 1;
            if (tokens[0].Value.StartsWith('-') && keep == 1) return null; // can't tell where the script is
            kept.AddRange(tokens.Take(keep).Select(t => t.Raw));
        }
        kept.AddRange(resume.Select(Launcher.WinQuote));
        var args = string.Join(" ", kept);
        return new TerminalCommand(run.Exe, args, Show(run.Exe, args), IsPlainShell: false);
    }

    public static TerminalPlan Plan(SnapshotTerminal t)
    {
        var options = new List<TerminalOption>();
        string? problem = null;
        var shellOnly = ShellCommand(t.Shell, t.ShellExe);
        if (t.CommandLine != null)
        {
            var run = ParseSaved(t.CommandLine, out problem);
            if (run != null)
            {
                options.Add(new TerminalOption(TerminalOption.Run, "Run the command again", run, LooksDestructive(run.Display)));
                var agent = KnownAgents.Agents.FirstOrDefault(a => a.Name == t.Agent);
                if (ResumeCommand(agent, run) is { } resume)
                    options.Add(new TerminalOption(TerminalOption.Resume, $"Resume {agent!.Name}'s last conversation", resume, LooksDestructive(resume.Display)));
            }
        }
        options.Add(new TerminalOption(TerminalOption.ShellOnly,
            shellOnly.Exe == null ? "Open PowerShell only" : $"Open {KnownAgents.Shell(shellOnly.Exe)?.Name ?? "the shell"} only", shellOnly, null));
        return new TerminalPlan(t, options, problem);
    }

    /// <summary>
    /// Whether a saved terminal is still running: a live terminal in the same folder running the same command, or the
    /// same agent. An idle shell never counts (a second one is harmless).
    /// </summary>
    public static bool IsStillRunning(SnapshotTerminal saved, IEnumerable<SnapshotTerminal> live)
    {
        if (saved.CommandLine == null || string.IsNullOrWhiteSpace(saved.ProjectDir)) return false;
        static string Dir(string d) => d.TrimEnd('\\', '/');
        return live.Any(l => l.CommandLine != null && string.Equals(Dir(l.ProjectDir), Dir(saved.ProjectDir), StringComparison.OrdinalIgnoreCase) &&
                             (string.Equals(l.CommandLine, saved.CommandLine, StringComparison.OrdinalIgnoreCase) ||
                              (saved.Agent != null && string.Equals(l.Agent, saved.Agent, StringComparison.Ordinal))));
    }

    /// <summary>What a live terminal session is saved as.</summary>
    public static SnapshotTerminal ToSnapshot(TerminalSession s, string? note = null) => new()
    {
        Kind = SnapshotTerminal.TerminalKind,
        Type = s.Type,
        Label = s.Label,
        ProjectDir = s.Folder ?? "",
        FolderIsStartFolder = s.FolderIsStartFolder ? true : null,
        Shell = s.ShellName,
        ShellExe = s.ShellPath,
        Agent = s.AgentName,
        CommandLine = ToSaved(s.CommandPath, s.CommandLine),
        StartedAt = s.StartedAt,
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
    };

    /// <summary>
    /// Whether a shell process is running a command rather than sitting at a prompt: cmd /c, bash -c or a script,
    /// powershell -Command/-File/-EncodedCommand (or a script) without -NoExit, a PowerShell job host, wsl with a
    /// command. PowerShell wraps .cmd files in "cmd /c", so "npm run dev" shows up as one.
    /// </summary>
    public static bool RunsCommand(string? exeName, string? commandLine)
    {
        if (commandLine == null) return false;
        var tokens = Tokenize(SplitProgram(commandLine).Args).Select(t => t.Value.ToLowerInvariant()).ToList();
        switch (KnownAgents.ProcessName(exeName)?.ToLowerInvariant())
        {
            case "cmd":
                // Whichever of /c and /k comes first decides (/k runs a command, then stays at the prompt).
                return tokens.FirstOrDefault(t => t.StartsWith("/c", StringComparison.Ordinal) || t.StartsWith("/k", StringComparison.Ordinal))
                    ?.StartsWith("/c", StringComparison.Ordinal) == true;
            case "powershell" or "pwsh":
                if (tokens.Contains("-noexit")) return false;
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (!t.StartsWith('-')) return true; // a script or command given without -Command
                    if (t is "-c" or "-f" or "-e" or "-ec" or "-enc" or "-s" or "-servermode" ||
                        t.Length >= 3 && ("-command".StartsWith(t, StringComparison.Ordinal) || "-file".StartsWith(t, StringComparison.Ordinal) ||
                                          "-encodedcommand".StartsWith(t, StringComparison.Ordinal)))
                        return true;
                    if (t is "-executionpolicy" or "-ex" or "-ep" or "-windowstyle" or "-w" or "-inputformat" or "-if" or "-outputformat" or "-of" or
                        "-version" or "-v" or "-configurationname" or "-workingdirectory" or "-wd" or "-settingsfile" or "-custompipename" or
                        "-psconsolefile" or "-configurationfile")
                        i++; // skip its value
                }
                return false;
            case "wsl":
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t is "-e" or "--exec" or "--") return true;
                    if (t is "-d" or "--distribution" or "-u" or "--user" or "--cd" or "--shell-type") { i++; continue; }
                    if (!t.StartsWith('-')) return true; // wsl ls -la
                }
                return false;
            default:
                // bash/sh/nu: -c (also combined, like -lc), or a script as the first plain argument.
                foreach (var t in tokens)
                {
                    if (t.StartsWith("--", StringComparison.Ordinal)) continue;
                    if (t.StartsWith('-')) { if (t.Contains('c')) return true; continue; }
                    return true;
                }
                return false;
        }
    }

    // ------------------------------------------------------------------ display

    /// <summary>
    /// A short, readable form for the list ("npm run dev"): program names without folders or extensions, and
    /// cmd's /c wrapper (which PowerShell adds around .cmd files) removed. The tooltip shows the exact text.
    /// </summary>
    public static string Pretty(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return "";
        var (program, args) = SplitProgram(commandLine);
        var name = ShortName(program);
        if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
        {
            var tokens = Tokenize(args);
            int c = tokens.FindIndex(t => t.Value.Equals("/c", StringComparison.OrdinalIgnoreCase) || t.Value.Equals("/k", StringComparison.OrdinalIgnoreCase));
            if (c >= 0 && tokens.Take(c).All(t => t.Value.StartsWith('/')))
            {
                var inner = args[(tokens[c].Start + tokens[c].Raw.Length)..].Trim();
                if (inner.Length >= 2 && inner[0] == '"' && inner[^1] == '"') inner = inner[1..^1];
                if (inner.Length > 0) return Pretty(inner);
            }
        }
        var parts = new List<string> { name };
        foreach (var t in Tokenize(args))
        {
            var v = t.Value.Contains('\\') && t.Value.Length > 2 && t.Value[1] == ':' ? ShortName(t.Value, keepExtension: true) : t.Value;
            parts.Add(v.Length == 0 || v.IndexOfAny(new[] { ' ', '\t' }) >= 0 ? "\"" + v + "\"" : v);
        }
        return string.Join(" ", parts);
    }

    private static string ShortName(string path, bool keepExtension = false)
    {
        var file = path.Replace('/', '\\');
        file = file[(file.LastIndexOf('\\') + 1)..];
        if (keepExtension) return file;
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com", ".ps1" })
            if (file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return file[..^ext.Length];
        return file;
    }

    // ------------------------------------------------------------------ safety

    private static readonly HashSet<string> DestructiveWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "rm", "rmdir", "rd", "del", "erase", "deltree", "format", "format-volume", "clear-disk", "initialize-disk",
        "remove-item", "ri", "rimraf", "shred", "diskpart", "shutdown", "stop-computer", "restart-computer", "cipher",
        "clear-content", "clc", "clear-recyclebin", "remove-itemproperty", "taskkill", "stop-process", "kill", "vssadmin",
        "wbadmin", "bcdedit", "mkfs",
    };

    /// <summary>Program + subcommand pairs that delete or overwrite (docker system prune, terraform destroy, …).</summary>
    private static readonly (string Program, string[] Subcommands)[] DestructiveSubcommands =
    {
        ("docker", new[] { "prune", "rm", "rmi" }),
        ("kubectl", new[] { "delete", "drain" }),
        ("terraform", new[] { "destroy" }),
        ("npm", new[] { "unpublish" }),
        ("reg", new[] { "delete" }),
        ("robocopy", new[] { "/mir", "/purge", "/move" }),
        ("az", new[] { "delete" }),
        ("gcloud", new[] { "delete" }),
        ("aws", new[] { "rm", "rb", "delete-bucket", "terminate-instances" }),
    };

    /// <summary>
    /// Why a command looks destructive, or null. A heuristic over the whole text (including what cmd /c or a script
    /// host would run): delete, format, kill and shutdown commands, rm -rf, git push/reset --hard/clean/stash drop,
    /// and code that is hidden from the reader (PowerShell -EncodedCommand, Invoke-Expression). It can't see
    /// inside scripts, so it's a warning, not a guarantee.
    /// </summary>
    public static string? LooksDestructive(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var words = command.Replace("^", "") // cmd's escape: d^el is del
            .Split(new[] { ' ', '\t', '"', '\'', '&', '|', ';', '(', ')', '<', '>', '`', ',', '{', '}', '@', '=' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToLowerInvariant())
            .ToList();
        // "of=/dev/sdb" is split on '=' above, so dd's target is checked separately on the raw text.
        if (System.Text.RegularExpressions.Regex.IsMatch(command, @"(^|[\s""'])dd(\.exe)?\s.*\bof=", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "it contains \u201Cdd of=\u201D";
        // A program given by path or with an extension counts by its name (C:\...\git.exe → git, /usr/bin/rm → rm).
        for (int k = 0; k < words.Count; k++)
            if (!words[k].StartsWith('-') && !(words[k].StartsWith('/') && words[k].Length <= 6)) words[k] = ShortName(words[k]);

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (DestructiveWords.Contains(w) || w.StartsWith("mkfs", StringComparison.Ordinal)) return $"it contains \u201C{w}\u201D";
            if (w is "-rf" or "-fr" or "--no-preserve-root") return $"it contains \u201C{w}\u201D";
            if (w is "iex" or "invoke-expression") return "it runs code from a string (Invoke-Expression), which can't be checked";
            if (w is "-encodedcommand" or "-enc" or "-ec" || (w == "-e" && words.Take(i).Any(x => x is "powershell" or "pwsh")))
                return "it runs an encoded PowerShell command, which can't be read";
            foreach (var (program, subs) in DestructiveSubcommands)
                if (w == program && words.Skip(i + 1).Take(8).FirstOrDefault(subs.Contains) is { } sub) // docker system prune, robocopy a b /mir
                    return $"it contains \u201C{program} {sub}\u201D";
            if (w == "git" && i + 1 < words.Count)
            {
                // The git subcommand is the first word that isn't an option (git -C dir push).
                int j = i + 1;
                while (j < words.Count && words[j].StartsWith('-')) j += words[j] == "-c" ? 2 : 1; // -c name=value, -C dir
                if (j >= words.Count) continue;
                var sub = words[j];
                var rest = words.Skip(j + 1).ToList();
                if (sub is "push" or "clean") return $"it contains \u201Cgit {sub}\u201D";
                if (sub is "reset" or "checkout" or "switch" && rest.Any(x => x is "--hard" or "--force" or "-f" or "--" or "."))
                    return $"it contains \u201Cgit {sub}\u201D that discards changes";
                // git restore discards working-tree changes, unless it only unstages (--staged without --worktree).
                if (sub == "restore" && !(rest.Contains("--staged") && !rest.Any(x => x is "--worktree" or "-w"))) return "it contains \u201Cgit restore\u201D";
                if (sub == "stash" && rest.FirstOrDefault() is "drop" or "clear") return $"it contains \u201Cgit stash {rest[0]}\u201D";
                if (sub == "branch" && rest.Any(x => x is "-d" or "--delete")) return "it contains \u201Cgit branch -D\u201D";
                if (sub is "rebase" or "filter-branch" or "filter-repo") return $"it contains \u201Cgit {sub}\u201D";
            }
        }
        return null;
    }

    // ------------------------------------------------------------------ secrets and hidden characters

    private static readonly System.Text.RegularExpressions.Regex SecretPattern = new(
        @"(?<k>(--?(password|passwd|pwd|token|api-?key|secret|access-?key|auth)[=:\s]+))(?<v>""[^""]*""|\S+)|" +
        @"(?<k>\b(bearer|basic)\s+)(?<v>[A-Za-z0-9._~+/=-]{8,})|" +
        @"(?<k>://[^/\s:@]+:)(?<v>[^@\s/]+)(?=@)|" +
        @"(?<k>(^|\s)-p)(?<v>[^\s-]\S*)|" +
        @"(?<k>)(?<v>\b(sk-[A-Za-z0-9_-]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|xox[abpr]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{30,}))",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether a command line looks like it carries a password, token or key.</summary>
    public static bool MayContainSecret(string? command) => command != null && SecretPattern.IsMatch(command);

    /// <summary>The command with likely secrets replaced by ***, for the launch log.</summary>
    public static string Redact(string command) => SecretPattern.Replace(command, m => m.Groups["k"].Value + "***");

    /// <summary>
    /// Describes the first character that could make text look different from what it is (control characters,
    /// escape sequences, bidi overrides, zero-width and other invisible format characters, line separators,
    /// broken surrogates), or null if there is none.
    /// </summary>
    public static string? HiddenCharacter(string s, bool allowTab)
    {
        for (int i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            if (ch == '\t' && allowTab) continue;
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            if (char.IsSurrogate(ch)) return "invalid Unicode";
            if (char.IsControl(ch)) return ch is '\r' or '\n' ? "a line break" : $"a control character (U+{(int)ch:X4})";
            var cat = char.GetUnicodeCategory(ch);
            if (cat is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.LineSeparator
                    or System.Globalization.UnicodeCategory.ParagraphSeparator)
                return $"an invisible character (U+{(int)ch:X4})";
        }
        return null;
    }
}
