using System.IO;
using System.Text;
using System.Text.Json;

namespace Stowdeck.Services;

/// <summary>
/// Your own names for sessions, kept in %APPDATA%\Stowdeck\nicknames.json and keyed by session ID.
/// Stowdeck's data only: Claude Code's files are never touched. Thread-safe; writes are atomic.
/// </summary>
public sealed class NicknameStore
{
    public const int MaxLength = 80;

    private sealed record Entry(string Name, DateTimeOffset SetAt);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable file
    };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _names = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public NicknameStore(string? path = null)
    {
        _path = path ?? Path.Combine(AppPaths.DataDir, "nicknames.json");
    }

    public string FilePath => _path;

    /// <summary>The nickname for a session, or null.</summary>
    public string? Get(string? sessionId)
    {
        if (!SessionStore.IsValidSessionId(sessionId)) return null;
        lock (_gate)
        {
            EnsureLoaded();
            return _names.TryGetValue(sessionId!, out var e) ? e.Name : null;
        }
    }

    /// <summary>Sets (or, for an empty name, clears) a session's nickname. Returns the stored name or null.</summary>
    public string? Set(string sessionId, string? name)
    {
        if (!SessionStore.IsValidSessionId(sessionId)) throw new ArgumentException("Not a session ID.", nameof(sessionId));
        var clean = Clean(name);
        lock (_gate)
        {
            EnsureLoaded();
            var next = new Dictionary<string, Entry>(_names, StringComparer.OrdinalIgnoreCase);
            if (clean == null) next.Remove(sessionId);
            else next[sessionId] = new Entry(clean, DateTimeOffset.Now);
            Save(next);
            _names = next; // only after the file was written
            return clean;
        }
    }

    public void Clear(string sessionId) => Set(sessionId, null);

    public int Count
    {
        get { lock (_gate) { EnsureLoaded(); return _names.Count; } }
    }

    /// <summary>Trims, collapses whitespace, drops control characters and caps the length; empty means "no name".</summary>
    public static string? Clean(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var sb = new StringBuilder(Math.Min(name.Length, MaxLength));
        bool space = false;
        foreach (var ch in name.Trim())
        {
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
            if (char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.Format) continue; // invisible bidi/format marks
            if (space) { sb.Append(' '); space = false; }
            if (sb.Length >= MaxLength) break;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (!File.Exists(_path) || new FileInfo(_path).Length > 4 * 1024 * 1024) return;
            var raw = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path, Encoding.UTF8), Json);
            if (raw == null) return;
            foreach (var (id, e) in raw)
                if (SessionStore.IsValidSessionId(id) && Clean(e?.Name) is { } n) _names[id] = new Entry(n, e!.SetAt);
        }
        catch
        {
            // Unreadable file: start empty rather than crash; the next rename rewrites it.
        }
    }

    private void Save(Dictionary<string, Entry> names)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(names, Json), new UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }
}
