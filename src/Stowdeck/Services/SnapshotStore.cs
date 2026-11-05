using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Stowdeck.Models;

namespace Stowdeck.Services;

/// <summary>Snapshots are human-readable JSON files in %APPDATA%\Stowdeck\snapshots\.</summary>
public sealed class SnapshotStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep non-ASCII readable; file is never rendered as HTML
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public string Dir { get; }

    public SnapshotStore(string? dir = null)
    {
        Dir = dir
              ?? NonEmpty(Environment.GetEnvironmentVariable("STOWDECK_SNAPSHOTS"))
              ?? Path.Combine(AppPaths.DataDir, "snapshots");
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public List<SnapshotFile> List()
    {
        var list = new List<SnapshotFile>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var f in Directory.EnumerateFiles(Dir, "*.json"))
        {
            try
            {
                var fi = new FileInfo(f);
                if (fi.Length > 5 * 1024 * 1024) { list.Add(new SnapshotFile { Path = f, Error = "File too large" }); continue; }
                var snap = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(f, Encoding.UTF8), JsonOptions);
                if (snap == null) throw new JsonException("empty");
                snap.Sessions ??= new();
                snap.Sessions.RemoveAll(s => s == null);
                if (string.IsNullOrWhiteSpace(snap.Name)) snap.Name = Path.GetFileNameWithoutExtension(f);
                list.Add(new SnapshotFile { Path = f, Snapshot = snap });
            }
            catch (Exception ex)
            {
                list.Add(new SnapshotFile { Path = f, Error = "Unreadable: " + ex.Message });
            }
        }
        list.Sort((a, b) => (b.Snapshot?.CreatedAt ?? DateTimeOffset.MinValue).CompareTo(a.Snapshot?.CreatedAt ?? DateTimeOffset.MinValue));
        return list;
    }

    public string Save(Snapshot snap)
    {
        Directory.CreateDirectory(Dir);
        var path = UniquePath(snap.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss") + "_" + Slug(snap.Name));
        WriteAtomic(path, snap);
        return path;
    }

    /// <summary>Renames a snapshot (the name inside the file, and the file name to match). Returns the new path.</summary>
    public string Rename(SnapshotFile file, string newName)
    {
        if (file.Snapshot == null) throw new InvalidOperationException("Snapshot is unreadable.");
        file.Snapshot.Name = newName.Trim();
        var stamp = file.Snapshot.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd_HHmmss");
        WriteAtomic(file.Path, file.Snapshot);
        var target = Path.Combine(Dir, stamp + "_" + Slug(newName) + ".json");
        if (string.Equals(target, file.Path, StringComparison.OrdinalIgnoreCase)) return file.Path;
        target = UniquePath(stamp + "_" + Slug(newName));
        File.Move(file.Path, target);
        return target;
    }

    /// <summary>Deletes a snapshot file to the Recycle Bin (only ever files inside our own folder).</summary>
    public void Delete(SnapshotFile file)
    {
        var full = Path.GetFullPath(file.Path);
        var dir = Path.GetFullPath(Dir).TrimEnd('\\') + "\\";
        if (!full.StartsWith(dir, StringComparison.OrdinalIgnoreCase) || !full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to delete a file outside the snapshots folder.");
        Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(full,
            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
    }

    private string UniquePath(string stem)
    {
        var path = Path.Combine(Dir, stem + ".json");
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(Dir, $"{stem}-{i}.json");
        return path;
    }

    private static void WriteAtomic(string path, Snapshot snap)
    {
        var tmp = path + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(JsonSerializer.Serialize(snap, JsonOptions));
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    /// <summary>File-name-safe slug: letters, digits and dashes only, max 40 chars.</summary>
    public static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (var ch in name.Trim().ToLowerInvariant())
        {
            if (sb.Length >= 40) break;
            if (ch < 128 && char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length == 0 ? "snapshot" : s;
    }
}
