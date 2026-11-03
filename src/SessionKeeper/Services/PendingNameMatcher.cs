using System.IO;
using SessionKeeper.Models;

namespace SessionKeeper.Services;

/// <summary>
/// Finds the session a "New session" launch produced, so its name can be attached once Claude Code registers
/// it: a running session in the same folder, started after the launch, with a session ID and no name yet.
/// If several qualify, the earliest-started one wins (each launch claims one).
/// </summary>
public static class PendingNameMatcher
{
    public static string? Find(string folder, DateTimeOffset since, IEnumerable<RunningSession> running, Func<string, bool> alreadyNamed)
    {
        var target = Normalise(folder);
        if (target == null) return null;
        return running
            .Where(r => SessionStore.IsValidSessionId(r.SessionId)
                        && r.StartedAt is { } started && started >= since
                        && string.Equals(Normalise(r.Cwd), target, StringComparison.OrdinalIgnoreCase)
                        && !alreadyNamed(r.SessionId!))
            .OrderBy(r => r.StartedAt)
            .Select(r => r.SessionId)
            .FirstOrDefault();
    }

    private static string? Normalise(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var full = Path.GetFullPath(dir.Trim());
            return full.Length > 3 ? full.TrimEnd('\\', '/') : full;
        }
        catch { return null; }
    }
}
