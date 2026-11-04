using System.IO;
using SessionKeeper.Models;

namespace SessionKeeper.Services;

/// <summary>
/// Finds terminal sessions that aren't Claude Code: Windows Terminal tabs and classic console windows, with the
/// shell, the program running in it, its folder and start time. Strictly read-only: one process-list snapshot,
/// one window enumeration (classes, visibility and owners only), then query-only handles on the processes of each
/// session, plus reading exe headers from disk to tell windowed programs from console ones.
///
/// A session's root is the process Windows reports as the owner of its console window: for Windows Terminal,
/// the invisible PseudoConsoleWindow of each tab or pane (its root's parent must be a known terminal host or a
/// shell, so background tools that use a pseudo console don't count); for a classic console, the visible window.
/// Only processes that share the session's console belong to it: windowed programs started from the shell
/// (notepad, code .) and processes with a console of their own (start cmd) are left out, with everything below
/// them. Sessions with Claude Code in them are left out (they have their own tabs), as is SessionKeeper.
/// </summary>
public sealed class TerminalScanner
{
    private static readonly HashSet<string> ConsoleHosts = new(StringComparer.OrdinalIgnoreCase) { "conhost", "OpenConsole" };
    private const int MaxDepth = 16, MaxTree = 200;

    /// <summary>Where process facts come from: the live system, or fakes in tests.</summary>
    internal sealed record Source(
        Func<IReadOnlyList<Native.ProcEntry>> Processes,
        Func<IReadOnlyList<Native.ConsoleOwner>> ConsoleOwners,
        Func<int, Native.ProcessFacts> Query,
        Func<int, string?> CommandLine,
        Func<int, string?> CurrentDirectory,
        Func<string?, bool?> IsGuiImage,
        int OwnPid,
        string? OwnPath)
    {
        public static Source Live => new(Native.SnapshotProcesses, Native.ConsoleWindowOwners, Native.QueryProcess,
            Native.TryGetCommandLine, Native.TryGetCurrentDirectory, Native.IsGuiImage, Environment.ProcessId, Environment.ProcessPath);
    }

    private readonly Source _src;
    private readonly object _gate = new();
    /// <summary>Command lines don't change, so each is read once per process (PID + start time).</summary>
    private Dictionary<(int, long?), string?> _commandLines = new();
    /// <summary>Windowed or console, per exe path (read from the file once).</summary>
    private readonly Dictionary<string, bool?> _guiImages = new(StringComparer.OrdinalIgnoreCase);

    public TerminalScanner() : this(null) { }

    internal TerminalScanner(Source? source) => _src = source ?? Source.Live;

    public List<TerminalSession> Scan()
    {
        lock (_gate) return ScanCore();
    }

    private List<TerminalSession> ScanCore()
    {
        var procs = _src.Processes();
        var byPid = new Dictionary<int, Native.ProcEntry>();
        foreach (var p in procs) byPid.TryAdd(p.Pid, p);
        var children = procs.Where(p => p.Pid != p.ParentPid && p.Pid != 0).ToLookup(p => p.ParentPid);
        var owners = _src.ConsoleOwners();
        var ownsConsole = owners.Select(o => o.Pid).ToHashSet();

        var facts = new Dictionary<int, Native.ProcessFacts>();
        Native.ProcessFacts Facts(int pid) => facts.TryGetValue(pid, out var f) ? f : facts[pid] = _src.Query(pid);
        var commandLines = new Dictionary<(int, long?), string?>();
        string? Cmd(int pid)
        {
            var key = (pid, Facts(pid).CreationFileTimeUtc);
            if (commandLines.TryGetValue(key, out var c)) return c;
            if (!_commandLines.TryGetValue(key, out c) || c == null) c = _src.CommandLine(pid); // a failed read is retried
            return commandLines[key] = c;
        }
        bool IsGui(int pid)
        {
            var path = Facts(pid).ImagePath;
            if (path == null) return false;
            if (!_guiImages.TryGetValue(path, out var gui)) _guiImages[path] = gui = _src.IsGuiImage(path);
            return gui == true;
        }

        // A child really belongs to its parent only if both start times are known and it started after it
        // (a parent PID can be reused; the user's own processes are always readable with query rights).
        bool StartedAfter(int child, int parent) =>
            Facts(child).CreationFileTimeUtc is long c && Facts(parent).CreationFileTimeUtc is long p && p <= c;
        bool IsSelf(Native.ProcEntry p) =>
            p.Pid == _src.OwnPid ||
            (_src.OwnPath != null && string.Equals(Path.GetFileName(_src.OwnPath), "SessionKeeper.exe", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(Facts(p.Pid).ImagePath, _src.OwnPath, StringComparison.OrdinalIgnoreCase));
        // Processes sharing this console: not console hosts, windowed programs or processes with a console of their own.
        List<Native.ProcEntry> Kids(int pid) => children[pid]
            .Where(k => !ConsoleHosts.Contains(KnownAgents.ProcessName(k.ExeName) ?? "") && !ownsConsole.Contains(k.Pid) &&
                        StartedAfter(k.Pid, pid) && !IsSelf(k) && !IsGui(k.Pid))
            .ToList();
        List<Native.ProcEntry> Tree(int pid)
        {
            var list = new List<Native.ProcEntry>();
            var visited = new HashSet<int> { pid };
            var stack = new Stack<Native.ProcEntry>(Kids(pid));
            while (stack.Count > 0 && list.Count < MaxTree)
            {
                var p = stack.Pop();
                if (!visited.Add(p.Pid)) continue;
                list.Add(p);
                foreach (var k in Kids(p.Pid)) stack.Push(k);
            }
            return list;
        }

        var results = new List<TerminalSession>();
        var seen = new HashSet<int>();
        foreach (var owner in owners)
        {
            if (!owner.Visible || !seen.Add(owner.Pid) || !byPid.TryGetValue(owner.Pid, out var root)) continue;
            if (ConsoleHosts.Contains(KnownAgents.ProcessName(root.ExeName) ?? "") || IsSelf(root)) continue;

            string? host = "console window";
            if (owner.Kind == Native.ConsoleKind.Pseudo)
            {
                // A tab of a known terminal, or one started from a shell (start cmd, with Windows Terminal as the
                // default terminal). Background tools that use a pseudo console have neither as parent.
                if (!byPid.TryGetValue(root.ParentPid, out var parent) || !StartedAfter(root.Pid, parent.Pid)) continue;
                var parentName = KnownAgents.ProcessName(parent.ExeName) ?? "";
                if (!KnownAgents.TerminalHosts.TryGetValue(parentName, out host))
                {
                    if (KnownAgents.Shell(parentName) == null) continue;
                    host = "terminal (started from another terminal)";
                }
            }

            var tree = Tree(root.Pid);
            var all = new List<Native.ProcEntry> { root };
            all.AddRange(tree);
            if (all.Any(p => KnownAgents.IsClaudeCode(p.ExeName, Facts(p.Pid).ImagePath,
                    KnownAgents.ScriptHosts.Contains(KnownAgents.ProcessName(p.ExeName) ?? "") ? Cmd(p.Pid) : null)))
                continue; // Claude Code has its own tabs

            KnownAgent? agent = null;
            int? agentPid = null;
            foreach (var p in all)
            {
                if (KnownAgents.Shell(p.ExeName) != null) continue; // a shell is never the agent, whatever its command line
                if (KnownAgents.Recognise(p.ExeName, Cmd(p.Pid)) is { } a) { agent = a; agentPid = p.Pid; break; }
            }
            // The agent's ancestors inside the session, so the shell loop can follow the branch that leads to it.
            var agentBranch = new HashSet<int>();
            if (agentPid is int ap)
                for (int pid = ap, guard = 0; pid != root.Pid && guard < MaxTree && byPid.TryGetValue(pid, out var e); pid = e.ParentPid, guard++)
                    agentBranch.Add(pid);

            // The shell and the program running in it. Nested interactive shells (cmd typed into PowerShell, Git
            // Bash's launcher) are followed down to the innermost one.
            Native.ProcEntry? shell = null, command = null;
            if (KnownAgents.Shell(root.ExeName) == null || agentPid == root.Pid) command = root;
            else
            {
                shell = root;
                var visited = new HashSet<int> { root.Pid };
                for (int depth = 0; depth < MaxDepth; depth++)
                {
                    var kids = Kids(shell.Value.Pid).Where(k => !visited.Contains(k.Pid)).ToList();
                    if (kids.Count == 0) break;
                    var pick = kids.FirstOrDefault(k => agentBranch.Contains(k.Pid));
                    if (pick.Pid == 0) pick = kids.OrderByDescending(k => Facts(k.Pid).CreationFileTimeUtc ?? 0).First();
                    visited.Add(pick.Pid);
                    if (KnownAgents.Shell(pick.ExeName) != null && pick.Pid != agentPid && !TerminalCommands.RunsCommand(pick.ExeName, Cmd(pick.Pid)))
                    {
                        shell = pick; // an interactive shell inside the shell
                        continue;
                    }
                    command = pick;
                    break;
                }
            }

            // Folder: a running program's own folder is reliable. An idle shell's is too for cmd, but PowerShell
            // (and others) don't change the process folder on cd, so that is only the folder it started in.
            string? folder = null;
            bool startFolder = false;
            if (command is { } cmdProc && (shell == null || cmdProc.Pid != shell.Value.Pid)) folder = _src.CurrentDirectory(cmdProc.Pid);
            if (folder == null && shell is { } sh)
            {
                folder = _src.CurrentDirectory(sh.Pid);
                startFolder = folder != null && !string.Equals(KnownAgents.ProcessName(sh.ExeName), "cmd", StringComparison.OrdinalIgnoreCase);
            }

            var shellFacts = shell is { } s2 ? Facts(s2.Pid) : default;
            var commandFacts = command is { } c2 ? Facts(c2.Pid) : default;
            results.Add(new TerminalSession
            {
                RootPid = root.Pid,
                RootName = root.ExeName,
                RootCreated = Facts(root.Pid).CreationFileTimeUtc,
                Host = host,
                ShellName = shell is { } s3 ? KnownAgents.Shell(s3.ExeName)?.Name : null,
                ShellPath = shell != null ? shellFacts.ImagePath : null,
                CommandPid = command?.Pid,
                CommandPath = command != null ? commandFacts.ImagePath : null,
                CommandLine = command is { } c3 ? Cmd(c3.Pid) : null,
                CommandStarted = command != null ? FromFileTime(commandFacts.CreationFileTimeUtc) : null,
                AgentName = agent?.Name,
                Folder = folder,
                FolderIsStartFolder = startFolder,
                StartedAt = FromFileTime(Facts(root.Pid).CreationFileTimeUtc),
            });
        }
        _commandLines = commandLines; // forget processes that have gone

        return results.OrderBy(r => r.StartedAt ?? DateTimeOffset.MaxValue).ThenBy(r => r.RootPid).ToList();
    }

    private static DateTimeOffset? FromFileTime(long? ft)
    {
        if (ft is not long v || v <= 0) return null;
        try { return new DateTimeOffset(DateTime.FromFileTimeUtc(v), TimeSpan.Zero); }
        catch { return null; }
    }
}
