using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SessionKeeper.Services;

/// <summary>
/// Recognises temp folders, where short throwaway Claude Code runs (from scripts and tools) usually start. Only the
/// listings for Claude (the plugin, the skill and the MCP connector, through --hide-temp) use this, to leave out
/// untitled sessions there; the app's own lists show everything.
/// </summary>
internal static class TempFolders
{
    private static readonly Lazy<string[]> SystemRoots = new(() => FindRoots().ToArray());

    /// <summary>True for the Windows temp folders and anything inside them, and for any folder named Temp or tmp.</summary>
    internal static bool Contains(string? dir) => Contains(dir, SystemRoots.Value);

    internal static bool Contains(string? dir, IReadOnlyCollection<string> roots)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        var path = Normalize(dir);
        if (path == null) return false;
        var name = Path.GetFileName(path);
        if (name.Equals("temp", StringComparison.OrdinalIgnoreCase) || name.Equals("tmp", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var root in roots)
        {
            var r = Normalize(root);
            if (r == null) continue;
            if (path.Equals(r, StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IEnumerable<string> FindRoots()
    {
        string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var candidates = new[]
        {
            Safe(Path.GetTempPath),
            Environment.GetEnvironmentVariable("TEMP"),
            Environment.GetEnvironmentVariable("TMP"),
            local.Length > 0 ? Path.Combine(local, "Temp") : null,
            windows.Length > 0 ? Path.Combine(windows, "Temp") : null,
        };
        return candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Full path, long form (an 8.3 short name such as PROGRA~1 becomes the real name) and without a trailing separator.</summary>
    private static string? Normalize(string dir)
    {
        try
        {
            var full = Path.GetFullPath(dir.Trim().Replace('/', '\\'));
            var sb = new StringBuilder(1024);
            if (full.Contains('~') && GetLongPathName(full, sb, sb.Capacity) is var n && n > 0 && n < sb.Capacity) full = sb.ToString();
            var trimmed = full.TrimEnd('\\');
            return trimmed.Length == 0 || trimmed.EndsWith(':') ? null : trimmed; // a drive root is never a temp folder
        }
        catch
        {
            return null;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, int bufferLength);
}
