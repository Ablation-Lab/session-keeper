using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SessionKeeper.Models;

namespace SessionKeeper.Services;

/// <summary>
/// Finds Claude Code sessions that are running right now.
///
/// Primary source: Claude Code's live registry, ~/.claude/sessions/&lt;pid&gt;.json, which maps each
/// interactive process to its session id, cwd and busy/idle status. Entries are validated against
/// the live process table (PID alive and start time matching <c>procStart</c>) because a killed
/// process leaves its entry behind.
///
/// Fallback: claude.exe processes with no registry entry (e.g. sitting at the folder-trust prompt)
/// are reported as unmatched, with their working directory read from the process when allowed.
/// </summary>
public sealed class RunningScanner
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(3);
    private readonly string _registryDir;

    public RunningScanner(string? registryDir = null)
    {
        _registryDir = registryDir ?? Path.Combine(ClaudePaths.ClaudeDir, "sessions");
    }

    private sealed record RegistryEntry(int Pid, string? SessionId, string? Cwd, string? Status, string? Name,
        string? NameSource, long? ProcStart, long? StartedAtMs, long? StatusUpdatedAtMs);

    private sealed record Proc(int Pid, string ProcessName, string? Path, long? Created, int? Parent);

    public List<RunningSession> Scan()
    {
        var procs = SnapshotProcesses();
        var results = new List<RunningSession>();
        var claimed = new HashSet<int>();

        foreach (var reg in ReadRegistry())
        {
            if (!procs.TryGetValue(reg.Pid, out var p)) continue; // stale entry
            if (!IsSameProcess(reg, p)) continue;                  // PID reused by something else
            if (!claimed.Add(reg.Pid)) continue;

            results.Add(new RunningSession
            {
                Pid = reg.Pid,
                SessionId = SessionStore.IsValidSessionId(reg.SessionId) ? reg.SessionId : null,
                Cwd = reg.Cwd,
                RawStatus = reg.Status,
                State = reg.Status switch
                {
                    "busy" => RunState.Working,
                    "waiting" => RunState.Waiting, // blocked on the user (e.g. a permission prompt)
                    "idle" => RunState.Idle,
                    _ => RunState.Other,
                },
                Name = reg.NameSource is null or "derived" ? null : reg.Name,
                StartedAt = FromMs(reg.StartedAtMs) ?? FromFileTime(p.Created),
                StatusUpdatedAt = FromMs(reg.StatusUpdatedAtMs),
                Note = SessionStore.IsValidSessionId(reg.SessionId) ? null : "Registry entry has no usable session id",
            });
        }

        // claude.exe processes that the registry doesn't know about.
        var claudePids = procs.Values.Where(p => p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase))
                                     .Select(p => p.Pid).ToHashSet();
        foreach (var p in procs.Values)
        {
            if (claimed.Contains(p.Pid)) continue;
            if (!p.ProcessName.Equals("claude", StringComparison.OrdinalIgnoreCase)) continue;
            if (IsDesktopApp(p.Path)) continue;
            // Helper processes spawned by another claude.exe (desktop app or CLI) are not sessions.
            if (p.Parent is int parent && claudePids.Contains(parent)) continue;

            var cwd = Native.TryGetCurrentDirectory(p.Pid);
            var started = FromFileTime(p.Created);
            // A new claude takes a few seconds to write its registry entry; don't flash "unmatched".
            bool young = started is { } s && DateTimeOffset.UtcNow - s < TimeSpan.FromSeconds(20);
            results.Add(new RunningSession
            {
                Pid = p.Pid,
                Cwd = cwd,
                State = young ? RunState.Starting : RunState.Unmatched,
                StartedAt = started,
                Note = young ? "Just started; not registered yet." : p.Path == null
                    ? "Not in Claude Code's session registry; process details are not readable (possibly elevated)."
                    : "Not in Claude Code's session registry (e.g. waiting at the folder-trust prompt, or launched from inside another session).",
            });
        }

        return results;
    }

    private static bool IsSameProcess(RegistryEntry reg, Proc p)
    {
        var name = p.ProcessName.ToLowerInvariant();
        if (name is not ("claude" or "node" or "bun")) return false;
        if (reg.ProcStart is long ps && p.Created is long created)
            return Math.Abs(ps - created) <= StartTolerance.Ticks;
        if (reg.StartedAtMs is long ms && FromFileTime(p.Created) is { } c)
            return FromMs(ms) >= c - StartTolerance;
        return true; // can't verify (access denied): trust name + pid
    }

    private static bool IsDesktopApp(string? path) =>
        path != null &&
        (path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
         path.Contains(@"\AnthropicClaude\", StringComparison.OrdinalIgnoreCase));

    private static Dictionary<int, Proc> SnapshotProcesses()
    {
        var map = new Dictionary<int, Proc>();
        foreach (var name in new[] { "claude", "node", "bun" })
        {
            Process[] list;
            try { list = Process.GetProcessesByName(name); }
            catch { continue; }
            foreach (var pr in list)
            {
                using (pr)
                {
                    var facts = Native.QueryProcess(pr.Id);
                    map[pr.Id] = new Proc(pr.Id, name, facts.ImagePath, facts.CreationFileTimeUtc, facts.ParentPid);
                }
            }
        }
        return map;
    }

    private IEnumerable<RegistryEntry> ReadRegistry()
    {
        if (!Directory.Exists(_registryDir)) yield break;
        string[] files;
        try { files = Directory.GetFiles(_registryDir, "*.json"); }
        catch { yield break; }

        foreach (var f in files)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(f), out _)) continue;
            RegistryEntry? entry = null;
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length is 0 or > 64 * 1024) continue;
                byte[] bytes;
                using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bytes = new byte[fs.Length];
                    fs.ReadExactly(bytes);
                }
                using var doc = JsonDocument.Parse(bytes);
                var r = doc.RootElement;
                if (r.ValueKind != JsonValueKind.Object) continue;
                if (!r.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) continue;
                entry = new RegistryEntry(
                    pid,
                    Str(r, "sessionId"),
                    Str(r, "cwd"),
                    Str(r, "status"),
                    Str(r, "name"),
                    Str(r, "nameSource"),
                    long.TryParse(Str(r, "procStart"), out var ps) ? ps : null,
                    Num(r, "startedAt"),
                    Num(r, "statusUpdatedAt") ?? Num(r, "updatedAt"));
            }
            catch
            {
                // Half-written or locked: skip this round.
            }
            if (entry != null) yield return entry;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static DateTimeOffset? FromMs(long? ms)
    {
        if (ms is not long v) return null;
        try { return DateTimeOffset.FromUnixTimeMilliseconds(v); }
        catch { return null; }
    }

    private static DateTimeOffset? FromFileTime(long? ft)
    {
        if (ft is not long v || v <= 0) return null;
        try { return new DateTimeOffset(DateTime.FromFileTimeUtc(v), TimeSpan.Zero); }
        catch { return null; }
    }
}
