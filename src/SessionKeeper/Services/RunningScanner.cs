using System.Diagnostics;
using System.IO;
using System.Text.Json;
using SessionKeeper.Models;

namespace SessionKeeper.Services;

/// <summary>
/// Finds Claude Code sessions that are running right now.
///
/// Primary source: Claude Code's live registry, ~/.claude/sessions/&lt;pid&gt;.json, which maps each
/// interactive process to its session id, cwd and busy/idle/waiting status. Entries are validated against
/// the live process table (PID alive and start time matching <c>procStart</c>) because a killed
/// process leaves its entry behind.
///
/// Fallback: claude.exe processes with no registry entry (e.g. sitting at the folder-trust prompt)
/// are reported as unmatched, with their working directory read from the process when allowed.
/// </summary>
public sealed class RunningScanner
{
    private static readonly TimeSpan StartTolerance = TimeSpan.FromSeconds(3);
    private static readonly string[] HostNames = { "claude", "node", "bun" };
    private readonly string _registryDir;
    private readonly object _gate = new();

    /// <summary>Last successfully parsed registry entry per PID, used when a read catches a file mid-write.</summary>
    private Dictionary<int, RegistryEntry> _lastGood = new();

    public RunningScanner(string? registryDir = null)
    {
        _registryDir = registryDir ?? Path.Combine(ClaudePaths.ClaudeDir, "sessions");
    }

    private sealed record RegistryEntry(int Pid, string? SessionId, string? Cwd, string? Status, string? Name,
        string? NameSource, long? ProcStart, long? StartedAtMs, long? StatusUpdatedAtMs);

    private sealed record Proc(int Pid, string ProcessName, string? Path, long? Created, int? Parent);

    public List<RunningSession> Scan()
    {
        lock (_gate) return ScanCore();
    }

    private List<RunningSession> ScanCore()
    {
        // One system-wide snapshot; only claude.exe and registry-listed PIDs get opened and queried.
        var hosts = SnapshotHostProcesses();
        var registryPids = ListRegistryPids();
        var facts = new Dictionary<int, Proc>();
        Proc? Facts(int pid)
        {
            if (facts.TryGetValue(pid, out var p)) return p;
            if (!hosts.TryGetValue(pid, out var name)) return null;
            var f = Native.QueryProcess(pid);
            return facts[pid] = new Proc(pid, name, f.ImagePath, f.CreationFileTimeUtc, f.ParentPid);
        }

        var results = new List<RunningSession>();
        var claimed = new HashSet<int>();
        var lastGood = new Dictionary<int, RegistryEntry>();

        foreach (var (pid, file) in registryPids)
        {
            if (!hosts.ContainsKey(pid)) continue; // stale entry: don't even read it
            var reg = ReadRegistryFile(file);
            if (reg == null && _lastGood.TryGetValue(pid, out var cached)) reg = cached; // caught mid-write
            if (reg == null || reg.Pid != pid) continue;
            var p = Facts(pid)!;
            if (!IsSameProcess(reg, p)) continue; // PID reused by something else
            lastGood[pid] = reg;
            claimed.Add(pid);

            results.Add(new RunningSession
            {
                Pid = pid,
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
        _lastGood = lastGood;

        // claude.exe processes that the registry doesn't know about.
        foreach (var (pid, name) in hosts)
        {
            if (name != "claude" || claimed.Contains(pid)) continue;
            var p = Facts(pid)!;
            if (IsDesktopApp(p.Path)) continue;
            // Helper processes spawned by another claude.exe (desktop app or CLI) are not sessions.
            // The start-time check guards against a reused parent PID hiding a real session.
            if (p.Parent is int parentPid && parentPid != pid && hosts.TryGetValue(parentPid, out var pn) && pn == "claude" &&
                Facts(parentPid) is { } parent && parent.Created is long pc && p.Created is long cc && pc <= cc)
                continue;

            var cwd = Native.TryGetCurrentDirectory(pid);
            var started = FromFileTime(p.Created);
            // A new claude takes a few seconds to write its registry entry; don't flash "unmatched".
            bool young = started is { } s && DateTimeOffset.UtcNow - s < TimeSpan.FromSeconds(20);
            results.Add(new RunningSession
            {
                Pid = pid,
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

    /// <summary>PID → lower-case name for claude/node/bun processes, from a single process snapshot.</summary>
    private static Dictionary<int, string> SnapshotHostProcesses()
    {
        var map = new Dictionary<int, string>();
        Process[] all;
        try { all = Process.GetProcesses(); }
        catch { return map; }
        foreach (var pr in all)
        {
            using (pr)
            {
                string name;
                try { name = pr.ProcessName; }
                catch { continue; }
                foreach (var h in HostNames)
                    if (name.Equals(h, StringComparison.OrdinalIgnoreCase)) { map[pr.Id] = h; break; }
            }
        }
        return map;
    }

    private List<(int Pid, string File)> ListRegistryPids()
    {
        var list = new List<(int, string)>();
        try
        {
            if (!Directory.Exists(_registryDir)) return list;
            foreach (var f in Directory.EnumerateFiles(_registryDir, "*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), out var pid)) list.Add((pid, f));
        }
        catch
        {
            // Unreadable registry folder: everything shows as unmatched.
        }
        return list;
    }

    private static RegistryEntry? ReadRegistryFile(string f)
    {
        try
        {
            byte[] bytes;
            using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (fs.Length is 0 or > 64 * 1024) return null;
                bytes = new byte[fs.Length];
                fs.ReadExactly(bytes);
            }
            using var doc = JsonDocument.Parse(bytes);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (!r.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) return null;
            return new RegistryEntry(
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
            return null; // half-written, locked or malformed
        }
    }

    private static string? Str(JsonElement e, string name)
    {
        try { return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null; }
        catch (InvalidOperationException) { return null; }
    }

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
