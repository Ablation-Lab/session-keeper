using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace SessionKeeper.Services;

/// <summary>
/// The only way SessionKeeper starts a process.
///
/// - A launch is allowed only inside an explicit user action (<see cref="UserAction"/>), opened by the
///   Reopen, Restore and Open folder buttons. Background work (auto-refresh, session detection, the
///   tray, the command line) has no such scope, so any launch attempted from there is refused.
/// - Shell execution is refused outright (no URIs, documents or verbs handed to the shell), the
///   executable must be a full path, and wt.exe must be given a new-tab command (wt with no or
///   malformed arguments opens help or settings windows).
/// - Every launch, every refused attempt, user actions and app start/exit are logged with a timestamp
///   and the calling source location to %APPDATA%\SessionKeeper\logs\launches-yyyy-MM-dd.log.
/// </summary>
public static class LaunchGuard
{
    public const string Reopen = "Reopen";
    public const string Restore = "Restore";
    public const string OpenSnapshotsFolder = "Open snapshots folder";
    private static readonly HashSet<string> AllowedActions = new() { Reopen, Restore, OpenSnapshotsFolder };

    private static readonly AsyncLocal<string?> CurrentActionSlot = new();
    private static readonly object LogGate = new();
    private const int KeepLogDays = 14;

    /// <summary>Tests point this at a temp folder.</summary>
    internal static string? LogDirOverride { get; set; }

    public static string LogDir => LogDirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionKeeper", "logs");

    public static string? CurrentAction => CurrentActionSlot.Value;

    /// <summary>
    /// Opens a launch scope for an explicit user action. The scope flows into awaits and Task.Run started
    /// inside it, and ends when disposed.
    /// </summary>
    public static IDisposable UserAction(string action,
        [CallerMemberName] string caller = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        if (!AllowedActions.Contains(action)) throw new ArgumentException("Not an allowed user action: " + action, nameof(action));
        var previous = CurrentActionSlot.Value;
        CurrentActionSlot.Value = action;
        Log("ACTION", $"action={action}  caller={Where(file, line, caller)}");
        return new Scope(previous);
    }

    /// <summary>
    /// Marks background work (auto-refresh, detection): clears any launch scope for the duration, so even
    /// a refresh that happened to start inside a user action can never launch anything.
    /// </summary>
    public static IDisposable Background()
    {
        var previous = CurrentActionSlot.Value;
        CurrentActionSlot.Value = null;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentActionSlot.Value = previous;
        }
    }

    /// <summary>Starts a process if allowed; otherwise logs the attempt and throws.</summary>
    public static Process? Start(ProcessStartInfo psi, string purpose,
        [CallerMemberName] string caller = "", [CallerFilePath] string file = "", [CallerLineNumber] int line = 0)
    {
        var where = Where(file, line, caller);
        var action = CurrentActionSlot.Value ?? "(none)";
        var what = $"action={action}  caller={where}  purpose={purpose}  exe={psi.FileName}  args={Summarise(psi.Arguments)}";
        if (Check(psi) is { } refusal)
        {
            Log("BLOCKED", what + "  reason=" + refusal);
            throw new InvalidOperationException("Launch blocked: " + refusal);
        }
        Log("LAUNCH", what);
        try
        {
            var p = Process.Start(psi);
            Log("STARTED", $"action={action}  caller={where}  pid={p?.Id.ToString() ?? "?"}");
            return p;
        }
        catch (Exception ex)
        {
            Log("FAILED", $"action={action}  caller={where}  error={ex.Message}");
            throw;
        }
    }

    /// <summary>Why a launch must be refused, or null if it's allowed.</summary>
    internal static string? Check(ProcessStartInfo psi)
    {
        if (CurrentActionSlot.Value == null)
            return "not inside an explicit user action (Reopen, Restore or Open folder)";
        if (psi.UseShellExecute)
            return "shell execution is not allowed";
        if (string.IsNullOrWhiteSpace(psi.FileName) || !Path.IsPathFullyQualified(psi.FileName))
            return "the executable must be a full path";
        if (string.Equals(Path.GetFileName(psi.FileName), "wt.exe", StringComparison.OrdinalIgnoreCase) &&
            !psi.Arguments.Contains("new-tab ", StringComparison.Ordinal))
            return "wt.exe must be given a new-tab command";
        return null;
    }

    /// <summary>Logs a lifecycle or diagnostic event (app start/exit, etc.).</summary>
    public static void LogEvent(string kind, string message) => Log(kind, message);

    private static string Where(string file, int line, string member) => $"{Path.GetFileName(file)}:{line} {member}";

    private static string Summarise(string args)
    {
        if (string.IsNullOrEmpty(args)) return "(none)";
        var flat = args.Replace('\r', ' ').Replace('\n', ' ');
        return flat.Length <= 300 ? flat : flat[..300] + $"… ({flat.Length} chars)";
    }

    private static void Log(string kind, string message)
    {
        try
        {
            lock (LogGate)
            {
                var dir = LogDir;
                Directory.CreateDirectory(dir);
                var now = DateTimeOffset.Now;
                var path = Path.Combine(dir, $"launches-{now:yyyy-MM-dd}.log");
                bool newFile = !File.Exists(path);
                File.AppendAllText(path, $"{now:yyyy-MM-dd HH:mm:ss.fff zzz}  {kind,-8} pid={Environment.ProcessId}  {message}\r\n",
                    new UTF8Encoding(false));
                if (newFile) PruneOldLogs(dir, now);
            }
        }
        catch
        {
            // Logging must never break the app.
        }
    }

    private static void PruneOldLogs(string dir, DateTimeOffset now)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "launches-*.log"))
        {
            var stamp = Path.GetFileNameWithoutExtension(f)["launches-".Length..];
            if (DateTime.TryParseExact(stamp, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var d) &&
                d < now.Date.AddDays(-KeepLogDays))
                File.Delete(f);
        }
    }
}
