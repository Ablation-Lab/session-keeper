using SessionKeeper.Models;

namespace SessionKeeper.Services;

/// <summary>A running session joined with its transcript details.</summary>
public sealed record RunningEntry(RunningSession Running, SessionInfo? Info, bool FolderMissing)
{
    public string? ProjectDir => Info?.ProjectDir ?? Running.Cwd;
    public DateTimeOffset? LastActive => Info?.LastActive ?? Running.StatusUpdatedAt ?? Running.StartedAt;

    /// <summary>Matched to a registry entry, so restoring it means "this conversation" (or a fresh one if it had no messages).</summary>
    public bool Confident => Running.State is not (RunState.Unmatched or RunState.Starting);
}

/// <summary>Queries shared by the UI and the command line, so both apply the same rules.</summary>
public static class SessionQueries
{
    /// <summary>Running sessions, matched ones first, most recently active first. Call off the UI thread.</summary>
    public static List<RunningEntry> GetRunning(RunningScanner scanner, SessionStore store)
    {
        return scanner.Scan()
            .Select(r =>
            {
                var info = r.SessionId != null ? store.GetSession(r.SessionId, r.Cwd) : null;
                var dir = info?.ProjectDir ?? r.Cwd;
                return new RunningEntry(r, info, dir != null && !FolderProbe.Exists(dir));
            })
            .OrderBy(e => e.Confident ? 0 : 1)
            .ThenByDescending(e => e.LastActive ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>
    /// A snapshot of the running sessions, using the same defaults as the Save dialog: matched sessions are
    /// included; unmatched or still-starting processes are skipped, because without a session id restoring
    /// them would start a new conversation rather than resume one.
    /// </summary>
    public static (Snapshot Snapshot, List<string> Skipped) BuildSnapshot(
        IEnumerable<RunningEntry> running, string name, string? note, DateTimeOffset now)
    {
        var snap = new Snapshot
        {
            Name = name,
            CreatedAt = now,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in running)
        {
            var label = e.ProjectDir ?? $"PID {e.Running.Pid}";
            if (e.ProjectDir == null) { skipped.Add($"{label}: folder unknown"); continue; }
            if (!e.Confident) { skipped.Add($"{label}: {(e.Running.State == RunState.Starting ? "still starting" : "unmatched")} (PID {e.Running.Pid})"); continue; }
            var id = e.Info != null ? e.Running.SessionId : null; // no transcript yet => restore as a fresh session
            if (id != null && !seen.Add(id)) continue;
            snap.Sessions.Add(new SnapshotSession
            {
                SessionId = id,
                ProjectDir = e.ProjectDir,
                Title = e.Info?.Title,
                Summary = e.Info?.LastPrompt,
                LastActive = e.LastActive,
            });
        }
        return (snap, skipped);
    }

    public static string DefaultSnapshotName(DateTimeOffset now) => "Snapshot " + now.ToLocalTime().ToString("ddd d MMM HH:mm");
}
