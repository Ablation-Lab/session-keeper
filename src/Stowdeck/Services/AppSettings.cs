using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Stowdeck.Services;

/// <summary>How Open, Reopen selected and Restore open sessions.</summary>
public enum OpenMode
{
    /// <summary>A separate Windows PowerShell console window (the default).</summary>
    PowerShellWindow,
    /// <summary>A tab in a Windows Terminal window.</summary>
    WindowsTerminalTab,
}

/// <summary>
/// App preferences in %APPDATA%\Stowdeck\settings.json. A missing or unreadable file means defaults.
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

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>The default settings as JSON, as Save writes them (used to merge settings from the app's old name).</summary>
    internal static System.Text.Json.Nodes.JsonObject DefaultsAsJson() =>
        JsonSerializer.SerializeToNode(new AppSettings(), Json)!.AsObject();

    public static string DefaultPath =>
        Path.Combine(AppPaths.DataDir, "settings.json");

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
