using System.IO;

namespace SessionKeeper.Services;

/// <summary>
/// Whether this copy of the app was installed by install.ps1 / install.cmd. The installer writes a marker
/// file next to the exe it installs (also when -Destination is used); build output folders never have one.
/// </summary>
public static class InstallLocation
{
    public const string MarkerFileName = ".sessionkeeper-install";

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "SessionKeeper");

    public static bool IsInstalledCopy => IsInstalledFolder(AppContext.BaseDirectory);

    public static bool IsInstalledFolder(string? folder)
    {
        try { return !string.IsNullOrWhiteSpace(folder) && File.Exists(Path.Combine(folder, MarkerFileName)); }
        catch { return false; }
    }

    /// <summary>The source repository to send people to for install.cmd (settings override, else where the app was built).</summary>
    public static string? SourceRepo(AppSettings settings, BuildInfo build)
    {
        var repo = settings.SourceRepo ?? build.SourceRepo;
        try
        {
            if (string.IsNullOrWhiteSpace(repo)) return null;
            var full = Path.GetFullPath(repo);
            return Directory.Exists(full) ? full.TrimEnd('\\', '/') : null;
        }
        catch { return null; }
    }
}
