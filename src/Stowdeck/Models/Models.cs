using System.Text.Json.Serialization;

namespace Stowdeck.Models;

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
    /// <summary>
    /// Terminal sessions that aren't Claude Code (other agents, programs, plain shells). A separate list, so an
    /// older Stowdeck never mistakes one for a Claude session (it keeps the list untouched on rename).
    /// </summary>
    [JsonConverter(typeof(TolerantTerminalListConverter))]
    public List<SnapshotTerminal>? Terminals { get; set; }

    /// <summary>The terminal entries that are really marked "terminal" (null or other entries in a hand-edited file are ignored).</summary>
    [JsonIgnore]
    public List<SnapshotTerminal> ValidTerminals =>
        (Terminals ?? new()).Where(t => t != null && t.Kind == SnapshotTerminal.TerminalKind).ToList();
    /// <summary>Unknown fields from a hand-edited file, preserved on rename.</summary>
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

public sealed class SnapshotSession
{
    /// <summary>Null means "start a fresh claude in this folder" (session had no messages / unmatched).</summary>
    public string? SessionId { get; set; }
    public string ProjectDir { get; set; } = "";
    /// <summary>Your Stowdeck name for the session when the snapshot was saved (optional).</summary>
    public string? Nickname { get; set; }
    public string? Title { get; set; }
    public string? Summary { get; set; }
    public DateTimeOffset? LastActive { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }

    /// <summary>
    /// An entry in "sessions" marked as some other kind (for example a terminal written there by hand or by a future
    /// version). It's never restored as a Claude session.
    /// </summary>
    [JsonIgnore]
    public bool IsOtherKind => Extra != null && Extra.TryGetValue("kind", out var k) &&
                               !(k.ValueKind == System.Text.Json.JsonValueKind.String && k.GetString() is "claude" or "session");
}

/// <summary>
/// A terminal session saved in a snapshot. Reopening it opens a shell in <see cref="ProjectDir"/> and, only after
/// the user confirms the exact text, runs <see cref="CommandLine"/> again.
/// </summary>
public sealed class SnapshotTerminal
{
    public const string TerminalKind = "terminal";
    /// <summary>"terminal": marks the entry in the file. An entry without it is ignored, never assumed to be one.</summary>
    public string? Kind { get; set; }
    /// <summary>"agent" (a recognised CLI agent), "program" (some other command) or "shell" (an idle shell).</summary>
    public string Type { get; set; } = "shell";
    public string Label { get; set; } = "";
    public string ProjectDir { get; set; } = "";
    /// <summary>True when the folder is the shell's start folder (PowerShell doesn't report a later cd).</summary>
    public bool? FolderIsStartFolder { get; set; }
    /// <summary>The shell's name and exe ("Command Prompt", C:\Windows\system32\cmd.exe), when there was one.</summary>
    public string? Shell { get; set; }
    public string? ShellExe { get; set; }
    /// <summary>The recognised agent's name (see KnownAgents).</summary>
    public string? Agent { get; set; }
    /// <summary>The running program: its full exe path (quoted) followed by its original arguments. Null for an idle shell.</summary>
    public string? CommandLine { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public string? Note { get; set; }
    [JsonExtensionData] public Dictionary<string, System.Text.Json.JsonElement>? Extra { get; set; }
}

/// <summary>
/// Reads the "terminals" list one entry at a time, so a malformed entry (a hand-edited date, say) is skipped instead
/// of making the whole snapshot, and its Claude sessions, unreadable. Anything that isn't a list reads as none.
/// </summary>
public sealed class TolerantTerminalListConverter : JsonConverter<List<SnapshotTerminal>?>
{
    public override List<SnapshotTerminal>? Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        using var doc = System.Text.Json.JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array) return null;
        var list = new List<SnapshotTerminal>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            try
            {
                if (System.Text.Json.JsonSerializer.Deserialize<SnapshotTerminal>(item, options) is { } t) list.Add(t);
            }
            catch (System.Text.Json.JsonException) { }
            catch (InvalidOperationException) { }
        }
        return list;
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, List<SnapshotTerminal>? value, System.Text.Json.JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var t in value ?? new()) System.Text.Json.JsonSerializer.Serialize(writer, t, options);
        writer.WriteEndArray();
    }
}

/// <summary>A terminal session that isn't Claude Code, found running right now.</summary>
public sealed record TerminalSession
{
    /// <summary>The process that owns the tab or console window (usually the shell).</summary>
    public required int RootPid { get; init; }
    public required string RootName { get; init; }
    public long? RootCreated { get; init; }
    /// <summary>"Windows Terminal", "console window", …</summary>
    public string? Host { get; init; }
    /// <summary>The innermost shell (e.g. cmd started from PowerShell), or null when the tab runs a program directly.</summary>
    public string? ShellName { get; init; }
    public string? ShellPath { get; init; }
    public int? CommandPid { get; init; }
    public string? CommandPath { get; init; }
    /// <summary>The running program's command line as Windows reports it.</summary>
    public string? CommandLine { get; init; }
    public DateTimeOffset? CommandStarted { get; init; }
    public string? AgentName { get; init; }
    public string? Folder { get; init; }
    public bool FolderIsStartFolder { get; init; }
    public DateTimeOffset? StartedAt { get; init; }

    public string Type => AgentName != null ? "agent" : CommandLine != null ? "program" : "shell";

    /// <summary>The agent's name if recognised, otherwise the program or shell ("PowerShell").</summary>
    public string Label => AgentName ?? ShellName ?? Services.KnownAgents.ProcessName(CommandPath ?? RootName) ?? RootName;
}

/// <summary>A snapshot file on disk plus its parsed content (or the reason it could not be read).</summary>
public sealed class SnapshotFile
{
    [JsonIgnore] public required string Path { get; init; }
    public Snapshot? Snapshot { get; init; }
    public string? Error { get; init; }
}
