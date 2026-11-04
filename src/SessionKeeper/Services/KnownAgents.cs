namespace SessionKeeper.Services;

/// <summary>
/// A command-line program SessionKeeper recognises in a terminal.
/// <paramref name="ProcessNames"/>: exe names without ".exe" (e.g. "codex" for codex.exe).
/// <paramref name="CommandLineMarkers"/>: text that identifies it in the script that node, python and other script
/// hosts run, such as the npm package path ("@openai/codex"). Only the script argument is checked (not the whole
/// command line, so "npm i -g @openai/codex" isn't Codex). "python -m name" matches a <paramref name="ProcessNames"/>
/// entry. Matching ignores case and treats \ and / alike.
/// <paramref name="Resume"/>: arguments that resume the agent's last conversation. Set it only for an option
/// you've checked in the agent's own <c>--help</c> on a machine where it's installed.
/// </summary>
public sealed record KnownAgent(string Name, string[] ProcessNames, string[] CommandLineMarkers, string[]? Resume = null);

/// <summary>A shell, and the fixed arguments used to start it again in a new window.</summary>
public sealed record KnownShell(string Name, string ProcessName, string ReopenArgs);

/// <summary>
/// The programs the Terminals tab recognises. To add an agent, add one line to <see cref="Agents"/>.
/// </summary>
public static class KnownAgents
{
    /// <summary>
    /// CLI agents, recognised by process name or command line. None has a <c>Resume</c> yet: none of them was
    /// installed where this was written, so no resume option could be verified. Example once verified:
    /// <c>new("Codex CLI", new[] { "codex" }, new[] { "@openai/codex" }, Resume: new[] { "resume", "--last" }),</c>
    /// </summary>
    public static readonly IReadOnlyList<KnownAgent> Agents = new KnownAgent[]
    {
        new("Codex CLI", new[] { "codex" }, new[] { "@openai/codex" }),
        new("Gemini CLI", new[] { "gemini" }, new[] { "@google/gemini-cli" }),
        new("Aider", new[] { "aider" }, new[] { "/aider-chat/" }),
        new("GitHub Copilot CLI", new[] { "copilot" }, new[] { "@github/copilot" }),
        new("OpenCode", new[] { "opencode" }, new[] { "/opencode-ai/" }),
        new("Cursor Agent", new[] { "cursor-agent" }, Array.Empty<string>()),
        new("Qwen Code", new[] { "qwen" }, new[] { "@qwen-code/qwen-code" }),
        new("Amp", new[] { "amp" }, new[] { "@sourcegraph/amp" }),
        new("Goose", new[] { "goose" }, Array.Empty<string>()),
        new("Crush", new[] { "crush" }, new[] { "@charmland/crush" }),
    };

    /// <summary>
    /// Shells. An idle shell reopens as itself: the new window is PowerShell, which then starts the shell with
    /// these fixed arguments (never anything from the old command line). Windows PowerShell is the window itself.
    /// </summary>
    public static readonly IReadOnlyList<KnownShell> Shells = new KnownShell[]
    {
        new("PowerShell", "powershell", ""),
        new("PowerShell 7", "pwsh", "-NoLogo"),
        new("Command Prompt", "cmd", ""),
        new("Bash", "bash", "--login -i"), // Git Bash, or WSL's bash.exe
        new("WSL", "wsl", ""),
        new("Nushell", "nu", ""),
    };

    /// <summary>Hosts whose ConPTY tabs count as terminal sessions (the parent of the tab's root process).</summary>
    public static readonly IReadOnlyDictionary<string, string> TerminalHosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["WindowsTerminal"] = "Windows Terminal",
        ["explorer"] = "default terminal", // a console program started from Explorer and handed to Windows Terminal
    };

    /// <summary>Programs that run a script named by their first argument (so "node codex.js" is Codex, not Node).</summary>
    internal static readonly HashSet<string> ScriptHosts = new(StringComparer.OrdinalIgnoreCase) { "node", "bun", "deno", "python", "pythonw", "py" };

    public static KnownAgent? Recognise(string? exeName, string? commandLine)
    {
        var name = ProcessName(exeName);
        if (name == null) return null;
        var (script, module) = ScriptHosts.Contains(name) ? ScriptArgument(commandLine) : (null, null);
        var scriptPath = Normalise(script);
        return Agents.FirstOrDefault(a => Matches(a, name, module, scriptPath));
    }

    /// <summary>Whether a process is this particular agent (same rules as <see cref="Recognise"/>).</summary>
    public static bool Matches(KnownAgent agent, string? exeName, string? commandLine)
    {
        var name = ProcessName(exeName);
        if (name == null) return false;
        var (script, module) = ScriptHosts.Contains(name) ? ScriptArgument(commandLine) : (null, null);
        return Matches(agent, name, module, Normalise(script));
    }

    private static bool Matches(KnownAgent a, string name, string? module, string? scriptPath) =>
        a.ProcessNames.Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase) || p.Equals(module, StringComparison.OrdinalIgnoreCase)) ||
        (scriptPath != null && a.CommandLineMarkers.Any(m => scriptPath.Contains(Normalise(m)!, StringComparison.Ordinal)));

    /// <summary>The script a script host runs (its first plain argument), or the module after python's -m.</summary>
    internal static (string? Script, string? Module) ScriptArgument(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return (null, null);
        var tokens = TerminalCommands.Tokenize(TerminalCommands.SplitProgram(commandLine).Args);
        for (int i = 0; i < tokens.Count; i++)
        {
            var v = tokens[i].Value;
            if (v == "-m") return (null, i + 1 < tokens.Count ? tokens[i + 1].Value : null);
            if (v is "-e" or "-c" or "--eval" or "-p" or "--print") return (null, null); // inline code, no script
            if (v is "-r" or "--require" or "--import" or "--loader" or "-X" or "-W") { i++; continue; }
            if (!v.StartsWith('-')) return (v, null);
        }
        return (null, null);
    }

    public static KnownShell? Shell(string? exeName)
    {
        var name = ProcessName(exeName);
        return name == null ? null : Shells.FirstOrDefault(s => s.ProcessName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Claude Code itself (it has its own tabs): the claude CLI, or node/bun running the npm package. The Claude
    /// desktop app (also claude.exe) isn't a terminal program and is ignored.
    /// </summary>
    public static bool IsClaudeCode(string? exeName, string? imagePath, string? commandLine)
    {
        if (string.Equals(ProcessName(exeName), "claude", StringComparison.OrdinalIgnoreCase))
            return imagePath == null ||
                   !(imagePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
                     imagePath.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase));
        return Normalise(commandLine) is { } cmd && cmd.Contains("@anthropic-ai/claude-code", StringComparison.Ordinal);
    }

    /// <summary>"Codex.EXE" → "codex"; also accepts a full path.</summary>
    public static string? ProcessName(string? exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName)) return null;
        var file = exeName.Replace('/', '\\');
        file = file[(file.LastIndexOf('\\') + 1)..];
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
    }

    private static string? Normalise(string? s) => string.IsNullOrEmpty(s) ? null : s.Replace('\\', '/').ToLowerInvariant();
}
