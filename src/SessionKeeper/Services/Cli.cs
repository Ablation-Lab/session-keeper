using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SessionKeeper.Models;
using SessionKeeper.ViewModels;

namespace SessionKeeper.Services;

public sealed class CliUsageException(string message) : Exception(message);

public sealed record CliOptions(string Command, string? Note = null, string? Name = null, int Days = 1, bool Json = false);

/// <summary>
/// Headless command-line mode. Read-only except --snapshot (writes one file to the snapshots folder) and
/// --install-shortcut (creates the per-user Start menu shortcut). Never launches or touches sessions.
///
/// Exit codes: 0 success, 1 nothing to do or an error, 2 usage error.
/// </summary>
public static class Cli
{
    public const string Usage = """
        SessionKeeper - see, save and reopen Claude Code sessions.

        Usage:
          SessionKeeper.exe                         open the app (brings an open window to the front)
          SessionKeeper.exe --list [--json]         list running Claude Code sessions
          SessionKeeper.exe --recent [--days N] [--json]
                                                    list sessions active in the last N days (default 1)
          SessionKeeper.exe --snapshots [--json]    list saved snapshots
          SessionKeeper.exe --snapshot ["note"] [--name "name"] [--json]
                                                    save the running sessions as a snapshot
          SessionKeeper.exe --install-shortcut      add SessionKeeper to the Start menu (per user)
          SessionKeeper.exe --help | --version

        Exit codes: 0 ok, 1 nothing to do or error, 2 usage error.
        In PowerShell, pipe the output (e.g. "| Out-Host") so the shell waits for it.
        """;

    private static readonly string[] Commands = { "--list", "--recent", "--snapshots", "--snapshot", "--install-shortcut", "--help", "--version" };

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Any argument other than the internal --startup flag means command-line mode.</summary>
    public static bool IsCliInvocation(string[] args) =>
        args.Any(a => !string.Equals(a, Program.StartupFlag, StringComparison.OrdinalIgnoreCase));

    public static CliOptions Parse(string[] args)
    {
        string? command = null, note = null, name = null;
        int days = 1;
        bool json = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            var lower = a.ToLowerInvariant();
            if (lower is "-h" or "/?" or "help") lower = "--help";

            if (Commands.Contains(lower))
            {
                if (command != null && command != lower) throw new CliUsageException($"Use one command at a time ({command} and {a}).");
                command = lower;
                // --snapshot takes an optional positional note.
                if (lower == "--snapshot" && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    note = args[++i];
                continue;
            }
            switch (lower)
            {
                case "--json": json = true; break;
                case "--note": note = Value(args, ref i, a); break;
                case "--name": name = Value(args, ref i, a); break;
                case "--days":
                    var v = Value(args, ref i, a);
                    if (!int.TryParse(v, out days) || days < 1 || days > 365) throw new CliUsageException("--days must be a whole number from 1 to 365.");
                    break;
                case "--startup": break;
                default: throw new CliUsageException($"Unknown option: {a}");
            }
        }

        if (command == null) throw new CliUsageException("No command given.");
        if ((note != null || name != null) && command != "--snapshot") throw new CliUsageException("--note and --name only apply to --snapshot.");
        if (name != null && string.IsNullOrWhiteSpace(name)) throw new CliUsageException("--name can't be empty.");
        return new CliOptions(command, note, name?.Trim(), days, json);
    }

    private static string Value(string[] args, ref int i, string flag)
    {
        if (i + 1 >= args.Length) throw new CliUsageException($"{flag} needs a value.");
        return args[++i];
    }

    public static int Run(string[] args)
    {
        using var output = CliOutput.Open(error: false);
        using var errors = CliOutput.Open(error: true);
        CliOptions opts;
        try
        {
            opts = Parse(args);
        }
        catch (CliUsageException ex)
        {
            errors.WriteLine("SessionKeeper: " + ex.Message);
            errors.WriteLine();
            errors.WriteLine(Usage);
            return 2;
        }

        try
        {
            return opts.Command switch
            {
                "--help" => Write(output, Usage, 0),
                "--version" => Write(output, "SessionKeeper " + BuildInfo.Current.FooterText.TrimStart('v'), 0),
                "--list" => List(output, opts),
                "--recent" => Recent(output, opts),
                "--snapshots" => Snapshots(output, opts),
                "--snapshot" => SaveSnapshot(output, errors, opts),
                "--install-shortcut" => InstallShortcut(output),
                _ => throw new CliUsageException("Unknown command."),
            };
        }
        catch (Exception ex)
        {
            errors.WriteLine("SessionKeeper: " + ex.Message);
            return 1;
        }
    }

    private static int Write(TextWriter w, string text, int code)
    {
        w.WriteLine(text);
        return code;
    }

    // ------------------------------------------------------------------ commands

    private static int List(TextWriter w, CliOptions o)
    {
        var running = SessionQueries.GetRunning(new RunningScanner(), new SessionStore());
        var names = new NicknameStore();
        if (o.Json)
        {
            w.WriteLine(JsonSerializer.Serialize(running.Select(e => new
            {
                pid = e.Running.Pid,
                status = StatusText(e.Running),
                sessionId = e.Running.SessionId,
                nickname = names.Get(e.Running.SessionId),
                folder = e.ProjectDir,
                folderMissing = e.FolderMissing,
                title = e.Running.Name ?? e.Info?.Title,
                lastMessage = e.Info?.LastPrompt,
                lastActive = e.LastActive,
            }), Json));
            return 0;
        }

        if (running.Count == 0) return Write(w, "No Claude Code sessions are running.", 0);
        var counts = running.GroupBy(e => StatusText(e.Running)).Select(g => $"{g.Count()} {g.Key}");
        w.WriteLine($"{running.Count} Claude Code session{(running.Count == 1 ? "" : "s")} running ({string.Join(", ", counts)})");
        foreach (var e in running)
        {
            w.WriteLine();
            w.WriteLine($"  {StatusText(e.Running),-12} {Fmt.FolderName(e.ProjectDir)}  ·  {Fmt.Ago(e.LastActive)}{(e.FolderMissing ? "  ·  FOLDER MISSING" : "")}");
            if (names.Get(e.Running.SessionId) is { } nick) w.WriteLine($"  {"",-12} name: {Fmt.Trim(nick, 100)}");
            if ((e.Running.Name ?? e.Info?.Title) is { } title) w.WriteLine($"  {"",-12} {Fmt.Trim(title, 100)}");
            if (e.Info?.LastPrompt is { } lp) w.WriteLine($"  {"",-12} last: {Fmt.Trim(lp, 100)}");
            w.WriteLine($"  {"",-12} {e.ProjectDir ?? "(folder unknown)"}  ·  " +
                        (e.Running.SessionId != null ? "session " + e.Running.SessionId : "no session id") + $"  ·  PID {e.Running.Pid}");
        }
        return 0;
    }

    private static int Recent(TextWriter w, CliOptions o)
    {
        var store = new SessionStore();
        var recent = store.GetRecent(DateTimeOffset.Now.AddDays(-o.Days));
        var running = new RunningScanner().Scan().Where(r => r.SessionId != null).Select(r => r.SessionId!)
                                              .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new NicknameStore();
        if (o.Json)
        {
            w.WriteLine(JsonSerializer.Serialize(recent.Select(s => new
            {
                sessionId = s.SessionId,
                nickname = names.Get(s.SessionId),
                folder = s.ProjectDir,
                title = s.Title,
                firstMessage = s.FirstPrompt,
                lastMessage = s.LastPrompt,
                lastActive = s.LastActive,
                running = running.Contains(s.SessionId),
            }), Json));
            return 0;
        }

        if (recent.Count == 0) return Write(w, $"No sessions in the last {o.Days} day{(o.Days == 1 ? "" : "s")}.", 0);
        w.WriteLine($"{recent.Count} session{(recent.Count == 1 ? "" : "s")} in the last {o.Days} day{(o.Days == 1 ? "" : "s")}:");
        foreach (var s in recent)
        {
            w.WriteLine();
            w.WriteLine($"  {Fmt.When(s.LastActive),-16} {Fmt.FolderName(s.ProjectDir)}{(running.Contains(s.SessionId) ? "  ·  running" : "")}");
            if (names.Get(s.SessionId) is { } nick) w.WriteLine($"  {"",-16} name: {Fmt.Trim(nick, 100)}");
            if (s.Title != null) w.WriteLine($"  {"",-16} {Fmt.Trim(s.Title, 100)}");
            if ((s.LastPrompt ?? s.FirstPrompt) is { } lp) w.WriteLine($"  {"",-16} last: {Fmt.Trim(lp, 100)}");
            w.WriteLine($"  {"",-16} {s.ProjectDir}  ·  session {s.SessionId}");
        }
        return 0;
    }

    private static int Snapshots(TextWriter w, CliOptions o)
    {
        var files = new SnapshotStore().List();
        if (o.Json)
        {
            w.WriteLine(JsonSerializer.Serialize(files.Select(f => new
            {
                name = f.Snapshot?.Name,
                createdAt = f.Snapshot?.CreatedAt,
                note = f.Snapshot?.Note,
                sessions = f.Snapshot?.Sessions.Count,
                file = f.Path,
                error = f.Error,
            }), Json));
            return 0;
        }

        if (files.Count == 0) return Write(w, "No snapshots saved yet.", 0);
        w.WriteLine($"{files.Count} snapshot{(files.Count == 1 ? "" : "s")}:");
        foreach (var f in files)
        {
            if (f.Snapshot is not { } s)
            {
                w.WriteLine($"  (unreadable) {Path.GetFileName(f.Path)}: {f.Error}");
                continue;
            }
            w.WriteLine($"  {Fmt.When(s.CreatedAt),-16} {s.Name}  ·  {s.Sessions.Count} session{(s.Sessions.Count == 1 ? "" : "s")}" +
                        (s.Note != null ? $"  ·  {Fmt.Trim(s.Note, 80)}" : ""));
        }
        return 0;
    }

    private static int SaveSnapshot(TextWriter w, TextWriter err, CliOptions o)
    {
        var now = DateTimeOffset.Now;
        var running = SessionQueries.GetRunning(new RunningScanner(), new SessionStore());
        var names = new NicknameStore();
        var (snap, skipped) = SessionQueries.BuildSnapshot(running, o.Name ?? SessionQueries.DefaultSnapshotName(now), o.Note, now, names.Get);
        if (snap.Sessions.Count == 0)
        {
            err.WriteLine("SessionKeeper: no matched Claude Code sessions are running, so nothing was saved.");
            foreach (var s in skipped) err.WriteLine("  skipped " + s);
            return 1;
        }

        var path = new SnapshotStore().Save(snap);
        if (o.Json)
        {
            w.WriteLine(JsonSerializer.Serialize(new
            {
                file = path,
                name = snap.Name,
                note = snap.Note,
                sessions = snap.Sessions.Select(s => new { sessionId = s.SessionId, folder = s.ProjectDir, title = s.Title }),
                skipped,
            }, Json));
            return 0;
        }

        w.WriteLine($"Saved snapshot \"{snap.Name}\" with {snap.Sessions.Count} session{(snap.Sessions.Count == 1 ? "" : "s")}" +
                    (snap.Note != null ? $" (note: {snap.Note})" : "") + ".");
        foreach (var s in snap.Sessions)
            w.WriteLine($"  {Fmt.FolderName(s.ProjectDir)}  ·  {(s.Title ?? (s.SessionId == null ? "new session" : Fmt.ShortId(s.SessionId)))}");
        foreach (var s in skipped) w.WriteLine("  skipped " + s);
        w.WriteLine("File: " + path);
        return 0;
    }

    private static int InstallShortcut(TextWriter w)
    {
        var integration = new WindowsIntegration();
        integration.SetStartMenuShortcut(true, WindowsIntegration.ExePath);
        w.WriteLine("Start menu shortcut: " + integration.StartMenuShortcutPath);
        return 0;
    }

    private static string StatusText(RunningSession r) => r.State switch
    {
        RunState.Working => "working",
        RunState.Waiting => "needs input",
        RunState.Idle => "idle",
        RunState.Starting => "starting",
        RunState.Unmatched => "unmatched",
        _ => r.RawStatus ?? "unknown",
    };
}

/// <summary>
/// Output for a GUI-subsystem exe. When stdout/stderr are redirected (pipes, files, Claude's tools) they are
/// used directly as UTF-8. Otherwise the parent console is attached so output still appears when typed at
/// a prompt (PowerShell doesn't wait for GUI apps unless their output is piped).
/// </summary>
internal static class CliOutput
{
    private const int StdOutput = -11, StdError = -12;
    private static bool _consoleAttached, _consoleTried;

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr hFile);
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    [DllImport("kernel32.dll")] private static extern uint GetConsoleOutputCP();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

    public static TextWriter Open(bool error)
    {
        var h = GetStdHandle(error ? StdError : StdOutput);
        if (h != IntPtr.Zero && h != new IntPtr(-1) && GetFileType(h) != 0)
        {
            var stream = error ? Console.OpenStandardError() : Console.OpenStandardOutput();
            return new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        }

        if (!_consoleTried)
        {
            _consoleTried = true;
            _consoleAttached = AttachConsole(-1); // ATTACH_PARENT_PROCESS
        }
        if (!_consoleAttached) return TextWriter.Null;

        var con = CreateFile("CONOUT$", 0x40000000 /*GENERIC_WRITE*/, 0x2 /*FILE_SHARE_WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (con.IsInvalid) return TextWriter.Null;
        Encoding enc;
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            enc = Encoding.GetEncoding((int)GetConsoleOutputCP());
        }
        catch
        {
            enc = new UTF8Encoding(false);
        }
        var writer = new StreamWriter(new FileStream(con, FileAccess.Write), enc) { AutoFlush = true };
        if (!error) writer.WriteLine(); // the prompt has already been printed; start on a fresh line
        return writer;
    }
}
