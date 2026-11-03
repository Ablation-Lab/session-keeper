using System.Text;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

public sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sk-tests-" + Guid.NewGuid().ToString("N"));
    private const string Id = "11111111-2222-3333-4444-555555555555";
    private const string Cwd = @"C:\Work\my-proj";

    public SessionStoreTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string ProjectDir => Path.Combine(_root, SessionStore.EncodeProjectDir(Cwd));
    private string FilePath => Path.Combine(ProjectDir, Id + ".jsonl");

    private static string J(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    private static string User(string content, string ts, string cwd = Cwd, string extra = "") =>
        $"{{\"parentUuid\":null,\"isSidechain\":false,\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":{content}}},\"timestamp\":\"{ts}\",\"cwd\":{J(cwd)},\"sessionId\":\"{Id}\"{extra}}}";

    private static string Assistant(string ts, string cwd = Cwd) =>
        $"{{\"type\":\"assistant\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":\"ok\"}}]}},\"timestamp\":\"{ts}\",\"cwd\":{J(cwd)},\"sessionId\":\"{Id}\"}}";

    private void Write(params string[] lines)
    {
        Directory.CreateDirectory(ProjectDir);
        File.AppendAllText(FilePath, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
    }

    [Fact]
    public void DeletedTranscriptsAreDroppedFromTheCache()
    {
        Write(User(J("hello"), "2026-11-02T10:00:00Z"), Assistant("2026-11-02T10:00:05Z"));
        var store = new SessionStore(_root);
        var since = DateTimeOffset.Parse("2026-11-01T00:00:00Z");
        Assert.Single(store.GetRecent(since));
        Assert.Equal(1, store.CachedCount);

        File.Delete(FilePath);
        Assert.Empty(store.GetRecent(since));
        Assert.Equal(0, store.CachedCount); // not listed any more and gone: evicted

        Write(User(J("again"), "2026-11-02T11:00:00Z"), Assistant("2026-11-02T11:00:05Z"));
        Assert.Equal("again", Assert.Single(store.GetRecent(since)).LastPrompt); // a new file with the same name is read fresh
        File.Delete(FilePath);
        Assert.Null(store.GetSessionFromFile(FilePath)); // a direct lookup of a deleted file also evicts it
        Assert.Equal(0, store.CachedCount);
    }

    [Fact]
    public void EncodeMatchesClaudeConvention()
    {
        Assert.Equal("C--Projects", SessionStore.EncodeProjectDir(@"C:\Projects"));
        Assert.Equal("C--Projects-my-app-v2", SessionStore.EncodeProjectDir(@"C:\Projects\my-app_v2"));
        Assert.Equal("C--Work-my-proj", SessionStore.EncodeProjectDir(Cwd));
    }

    [Fact]
    public void ParsesPromptsTitlesAndTimes()
    {
        Write(
            "{\"type\":\"mode\",\"mode\":\"normal\",\"sessionId\":\"" + Id + "\"}",
            User(J("<local-command-caveat>noise</local-command-caveat>"), "2026-11-02T10:00:00Z", extra: ",\"isMeta\":true"),
            User(J("<command-name>/model</command-name>"), "2026-11-02T10:00:01Z"),
            User(J("Build   the\nthing please"), "2026-11-02T10:00:02Z"),
            Assistant("2026-11-02T10:00:05Z", cwd: Cwd + @"\src"), // Claude cd'd into a subfolder
            User("[{\"type\":\"tool_result\",\"tool_use_id\":\"x\",\"content\":\"lots\"}]", "2026-11-02T10:00:06Z", Cwd + @"\src"),
            User("[{\"type\":\"text\",\"text\":\"Second ask\"},{\"type\":\"image\"}]", "2026-11-02T10:01:00Z", Cwd + @"\src"),
            "{\"type\":\"ai-title\",\"aiTitle\":\"AI title\",\"sessionId\":\"" + Id + "\"}",
            Assistant("2026-11-02T10:02:00Z", cwd: Cwd + @"\src"));

        var info = new SessionStore(_root).GetSessionFromFile(FilePath)!;
        Assert.Equal(Id, info.SessionId);
        Assert.Equal(Cwd, info.ProjectDir); // not the subfolder
        Assert.Equal("Build the thing please", info.FirstPrompt);
        Assert.Equal("Second ask", info.LastPrompt);
        Assert.Equal("AI title", info.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T10:02:00Z"), info.LastActive);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T10:00:00Z"), info.Started);
    }

    [Fact]
    public void CustomTitleWinsAndLastPromptRecordIsUsed()
    {
        Write(
            User(J("first"), "2026-11-02T10:00:00Z"),
            "{\"type\":\"custom-title\",\"customTitle\":\"Mine\",\"sessionId\":\"" + Id + "\"}",
            "{\"type\":\"ai-title\",\"aiTitle\":\"Generated\",\"sessionId\":\"" + Id + "\"}",
            "{\"type\":\"last-prompt\",\"lastPrompt\":\"truncated prompt\uFFFD\",\"sessionId\":\"" + Id + "\"}");
        var info = new SessionStore(_root).GetSessionFromFile(FilePath)!;
        Assert.Equal("Mine", info.Title);
        Assert.Equal("truncated prompt", info.LastPrompt);
    }

    [Fact]
    public void IncrementalAppendAndPartialLines()
    {
        var store = new SessionStore(_root);
        Write(User(J("one"), "2026-11-02T10:00:00Z"));
        Assert.Equal("one", store.GetSessionFromFile(FilePath)!.LastPrompt);

        // A half-written line (no newline yet) must not be consumed.
        var full = User(J("two"), "2026-11-02T10:05:00Z");
        File.AppendAllText(FilePath, full[..20]);
        Assert.Equal("one", store.GetSessionFromFile(FilePath)!.LastPrompt);

        File.AppendAllText(FilePath, full[20..] + "\r\n");
        var info = store.GetSessionFromFile(FilePath)!;
        Assert.Equal("two", info.LastPrompt);
        Assert.Equal("one", info.FirstPrompt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T10:05:00Z"), info.LastActive);
    }

    [Fact]
    public void OversizedAndMalformedLinesAreSkipped()
    {
        var huge = "{\"type\":\"user\",\"message\":{\"content\":\"" + new string('x', 5 * 1024 * 1024) + "\"},\"timestamp\":\"2026-11-02T11:00:00Z\"}";
        Write(User(J("before"), "2026-11-02T10:00:00Z"), "{not json", huge, User(J("after"), "2026-11-02T10:30:00Z"));
        var info = new SessionStore(_root).GetSessionFromFile(FilePath)!;
        Assert.Equal("before", info.FirstPrompt);
        Assert.Equal("after", info.LastPrompt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T10:30:00Z"), info.LastActive);
    }

    [Fact]
    public void LoneSurrogateEscapeDoesNotStallTheFile()
    {
        // JSON.stringify of a string cut mid-emoji produces a lone surrogate escape; GetString() throws on it.
        Write(User(J("first"), "2026-11-02T10:00:00Z"),
              "{\"type\":\"last-prompt\",\"lastPrompt\":\"cut emoji \\ud83d\",\"sessionId\":\"" + Id + "\"}",
              User(J("after the bad line"), "2026-11-02T10:10:00Z"));
        var store = new SessionStore(_root);
        var info = store.GetSessionFromFile(FilePath)!;
        Assert.Equal("after the bad line", info.LastPrompt);
        Write(User(J("appended later"), "2026-11-02T10:20:00Z"));
        Assert.Equal("appended later", store.GetSessionFromFile(FilePath)!.LastPrompt);
    }

    [Fact]
    public void LargeFileFirstLoadReadsHeadAndTailThenAppends()
    {
        var filler = "{\"type\":\"api-request-blob\",\"message\":\"" + new string('z', 1000) + "\"}";
        var lines = new List<string> { User(J("the first ask"), "2026-11-02T09:00:00Z") };
        lines.AddRange(Enumerable.Repeat(filler, 9 * 1024)); // ~9 MB, over the fast-path threshold
        lines.Add("{\"type\":\"ai-title\",\"aiTitle\":\"Tail title\",\"sessionId\":\"" + Id + "\"}");
        lines.Add(User(J("the latest ask"), "2026-11-02T12:00:00Z"));
        Write(lines.ToArray());

        var store = new SessionStore(_root);
        var info = store.GetSessionFromFile(FilePath)!;
        Assert.Equal(Cwd, info.ProjectDir);
        Assert.Equal("the first ask", info.FirstPrompt);
        Assert.Equal("the latest ask", info.LastPrompt);
        Assert.Equal("Tail title", info.Title);
        Assert.Equal(DateTimeOffset.Parse("2026-11-02T12:00:00Z"), info.LastActive);

        Write(User(J("one more"), "2026-11-02T13:00:00Z"));
        Assert.Equal("one more", store.GetSessionFromFile(FilePath)!.LastPrompt);
    }

    [Fact]
    public void RewrittenLargerFileIsReparsed()
    {
        var store = new SessionStore(_root);
        Write(User(J("old content"), "2026-11-02T10:00:00Z"));
        Assert.Equal("old content", store.GetSessionFromFile(FilePath)!.LastPrompt);

        // Replace the file with different, longer content (offset no longer on a line boundary).
        File.WriteAllText(FilePath, User(J("completely different and longer content here"), "2026-11-02T11:00:00Z", extra: ",\"pad\":\"xxxxxxxxxxxxxxxxxxxxxxxx\"") + "\n");
        var info = store.GetSessionFromFile(FilePath)!;
        Assert.Equal("completely different and longer content here", info.FirstPrompt);
    }

    [Fact]
    public void FileWithoutConversationIsIgnored()
    {
        Write("{\"type\":\"mode\",\"mode\":\"normal\",\"sessionId\":\"" + Id + "\"}");
        Assert.Null(new SessionStore(_root).GetSessionFromFile(FilePath));
    }

    [Fact]
    public void FindFileAndRecentWindow()
    {
        Write(User(J("hello"), DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O")));
        var store = new SessionStore(_root);
        Assert.Equal(FilePath, store.FindFile(Id, Cwd));
        Assert.Equal(FilePath, store.FindFile(Id, @"D:\elsewhere")); // falls back to scanning
        Assert.Null(store.FindFile("not-a-guid"));
        Assert.Single(store.GetRecent(DateTimeOffset.UtcNow.AddHours(-1)));
        Assert.Empty(store.GetRecent(DateTimeOffset.UtcNow.AddMinutes(-1)));
    }
}
