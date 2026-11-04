using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SessionKeeper.Services;

/// <summary>How Open, Reopen selected and Restore open sessions.</summary>
public enum OpenMode
{
    /// <summary>A separate Windows PowerShell console window (the default).</summary>
    PowerShellWindow,
    /// <summary>A tab in a Windows Terminal window.</summary>
    WindowsTerminalTab,
}

/// <summary>
/// App preferences in %APPDATA%\SessionKeeper\settings.json. A missing or unreadable file means defaults.
/// (Start with Windows and the Start menu shortcut are not stored here: the registry value and the
/// shortcut file are their own source of truth.)
/// </summary>
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public OpenMode OpenIn { get; set; } = OpenMode.PowerShellWindow;

    /// <summary>
    /// Optional: the source repository the version footer compares against. When unset, the repository the
    /// app was built from (embedded at build time) is used.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SourceRepo { get; set; }

    /// <summary>The folder the Remotion Studio button last opened, so it's picked first next time.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LastRemotionProject { get; set; }

    /// <summary>Optional: where the Remotion Studio button looks for projects (default C:\Projects).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RemotionProjectsFolder { get; set; }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SessionKeeper", "settings.json");

    /// <summary>
    /// Like <see cref="Load"/>, but null when the file exists and couldn't be read (locked, corrupt or too big), so a
    /// caller about to save doesn't overwrite real settings with defaults. A missing file gives defaults.
    /// </summary>
    public static AppSettings? TryLoad(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return new AppSettings();
            if (new FileInfo(path).Length > 64 * 1024) return null;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8), Json);
            if (s == null) return null;
            if (!Enum.IsDefined(s.OpenIn)) s.OpenIn = OpenMode.PowerShellWindow;
            return s;
        }
        catch
        {
            return null;
        }
    }

    public static AppSettings Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return new AppSettings();
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new AppSettings();
            if (!Enum.IsDefined(s.OpenIn)) s.OpenIn = OpenMode.PowerShellWindow;
            return s;
        }
        catch
        {
            return new AppSettings(); // corrupt or locked: defaults, never crash
        }
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json), new UTF8Encoding(false));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
