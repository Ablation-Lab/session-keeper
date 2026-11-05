using Stowdeck.Models;
using Stowdeck.Services;

namespace Stowdeck.Tests;

public sealed class SnapshotStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sk-snap-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void SaveListRenameRoundTrip()
    {
        var store = new SnapshotStore(_dir);
        var snap = new Snapshot
        {
            Name = "End of day — Mon",
            CreatedAt = DateTimeOffset.Now,
            Sessions =
            {
                new SnapshotSession { SessionId = "11111111-2222-3333-4444-555555555555", ProjectDir = @"C:\Projects\x", Note = "waiting on council results" },
                new SnapshotSession { ProjectDir = @"C:\Projects\y" },
            },
        };
        var path = store.Save(snap);
        Assert.Contains("end-of-day-mon", Path.GetFileName(path));
        Assert.Contains("waiting on council results", File.ReadAllText(path)); // readable JSON

        var listed = Assert.Single(store.List());
        Assert.Equal("End of day — Mon", listed.Snapshot!.Name);
        Assert.Equal(2, listed.Snapshot.Sessions.Count);
        Assert.Null(listed.Snapshot.Sessions[1].SessionId);

        var newPath = store.Rename(listed, "Friday wrap");
        Assert.False(File.Exists(path));
        Assert.Contains("friday-wrap", Path.GetFileName(newPath));
        Assert.Equal("Friday wrap", Assert.Single(store.List()).Snapshot!.Name);
    }

    [Fact]
    public void CorruptFileIsListedWithError()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ nope");
        var f = Assert.Single(new SnapshotStore(_dir).List());
        Assert.Null(f.Snapshot);
        Assert.NotNull(f.Error);
    }

    [Fact]
    public void DeleteRefusesFilesOutsideFolder()
    {
        var store = new SnapshotStore(_dir);
        var outside = new SnapshotFile { Path = Path.Combine(Path.GetTempPath(), "x.json") };
        Assert.Throws<InvalidOperationException>(() => store.Delete(outside));
    }

    [Theory]
    [InlineData("End of day", "end-of-day")]
    [InlineData("../../evil", "evil")]
    [InlineData("???", "snapshot")]
    [InlineData("CON", "con")]
    public void SlugIsFileSafe(string name, string expected) => Assert.Equal(expected, SnapshotStore.Slug(name));
}
