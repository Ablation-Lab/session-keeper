using System.Collections.Concurrent;
using System.IO;

namespace Stowdeck.Services;

/// <summary>
/// Cached "does this folder exist?" check. Directory.Exists on an unreachable network share can block for
/// ~20 s, so this must only be called from background threads, and results are cached (longer for
/// network locations) so a dead share doesn't stall every refresh.
/// </summary>
public static class FolderProbe
{
    private static readonly ConcurrentDictionary<string, (bool Exists, DateTime At)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan LocalTtl = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NetworkTtl = TimeSpan.FromMinutes(5);

    public static bool Exists(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var now = DateTime.UtcNow;
        var ttl = IsNetwork(dir) ? NetworkTtl : LocalTtl;
        if (Cache.TryGetValue(dir, out var hit) && now - hit.At < ttl) return hit.Exists;
        bool exists;
        try { exists = Directory.Exists(dir); }
        catch { exists = false; }
        Cache[dir] = (exists, now);
        return exists;
    }

    private static bool IsNetwork(string dir)
    {
        if (dir.StartsWith(@"\\", StringComparison.Ordinal)) return true;
        try
        {
            var root = Path.GetPathRoot(dir);
            return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch { return false; }
    }
}
