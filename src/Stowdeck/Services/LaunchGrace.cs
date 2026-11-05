namespace Stowdeck.Services;

/// <summary>
/// Sessions Stowdeck has just opened count as "running" until a scan first sees them (a new claude
/// takes a few seconds to register), for at most <see cref="Window"/>. This keeps the "already running"
/// warning working right after a launch. Once a session has been seen, only the live scan decides, so
/// closing its window clears the badge at the next refresh instead of lingering for the whole window.
/// </summary>
public sealed class LaunchGrace
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, DateTime> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan _window;
    private readonly Func<DateTime> _now;

    public LaunchGrace(TimeSpan? window = null, Func<DateTime>? now = null)
    {
        _window = window ?? Window;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public void Launched(string sessionId) => _pending[sessionId] = _now();

    /// <summary>The scanned running ids plus launches that haven't registered yet.</summary>
    public HashSet<string> Apply(IEnumerable<string> running)
    {
        var set = new HashSet<string>(running, StringComparer.OrdinalIgnoreCase);
        var now = _now();
        foreach (var (id, at) in _pending.ToList())
        {
            if (set.Contains(id) || now - at > _window) _pending.Remove(id); // registered (or never will): stop covering it
            else set.Add(id);
        }
        return set;
    }

    public int PendingCount => _pending.Count;
}
