using System.IO;

namespace Stowdeck.Services;

/// <summary>Where the app keeps its own files: %APPDATA%\Stowdeck (settings, names, snapshots, logs, cache).</summary>
public static class AppPaths
{
    public const string DataFolderName = "Stowdeck";

    public static string AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    public static string DataDir => Path.Combine(AppData, DataFolderName);
}
