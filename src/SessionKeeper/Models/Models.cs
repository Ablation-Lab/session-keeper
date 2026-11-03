using System.Text.Json.Serialization;

namespace SessionKeeper.Models;

/// <summary>What we know about one Claude Code conversation, read from its JSONL file.</summary>
public sealed record SessionInfo
{
    public required string SessionId { get; init; }
    public required string FilePath { get; init; }
    /// <summary>Folder the session belongs to (where it must be resumed from). May be null if undeterminable.</summary>
    public string? ProjectDir { get; init; }
    public string? Title { get; init; }
    public string? FirstPrompt { get; init; }
    public string? LastPrompt { get; init; }
    public DateTimeOffset LastActive { get; init; }
    public DateTimeOffset? Started { get; init; }
    public string? ContinuedIn { get; init; }
}

public enum RunState { Working, Waiting, Idle, Other, Starting, Unmatched }

/// <summary>A Claude Code process that is running right now.</summary>
public sealed record RunningSession
{
    public required int Pid { get; init; }
    /// <summary>Null when the process could not be matched to a session.</summary>
    public string? SessionId { get; init; }
    public string? Cwd { get; init; }
    public RunState State { get; init; }
    public string? RawStatus { get; init; }
    public string? Name { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? StatusUpdatedAt { get; init; }
    /// <summary>Why it is unmatched (for the tooltip).</summary>
    public string? Note { get; init; }
}

public sealed class Snapshot
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Optional note for the whole snapshot (e.g. "end of Monday, waiting on council results").</summary>
    public string? Note { get; set; }
    public List<SnapshotSession> Sessions { get; set; } = new();
    /// <summary>Unknown fields from a hand-edited file, preserved on rename.</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class SnapshotSession
{
    /// <summary>Null means "start a fresh claude in this folder" (session had no messages / unmatched).</summary>
    public string? SessionId { get; set; }
    public string ProjectDir { get; set; } = "";
    /// <summary>Your SessionKeeper name for the session when the snapshot was saved (optional).</summary>
    public string? Nickname { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset? LastActive { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>A snapshot file on disk plus its parsed content (or the reason it could not be read).</summary>
public sealed class SnapshotFile
{
    [JsonIgnore] public required string Path { get; init; }
    public Snapshot? Snapshot { get; init; }
    public string? Error { get; init; }
}
