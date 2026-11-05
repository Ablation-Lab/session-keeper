using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Stowdeck.ViewModels;

namespace Stowdeck.Services;

/// <summary>
/// The local MCP connector: <c>Stowdeck.exe --mcp</c>, a Model Context Protocol server over stdio (one JSON-RPC
/// message per line on stdin/stdout). Claude Desktop or Claude Code starts it as a child process; it opens no network
/// port and talks to nothing else.
///
/// Its tools reuse the command line's own queries and output, always without messages: results give titles, names,
/// folders, times, statuses and session IDs, never what was said in a conversation. Everything is read-only except
/// save_snapshot (one file in the snapshots folder) and the two open tools, which start the app or resume a session
/// through the same <see cref="LaunchGuard"/> and <see cref="Launcher"/> path as the app's Open button. The client
/// asks the user before calling a tool that isn't marked read-only.
/// </summary>
public static class McpServer
{
    /// <summary>Protocol revisions with the initialize handshake, newest first. Requests also work without initialize.</summary>
    public static readonly string[] SupportedProtocols = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };
    private const int MaxMessageChars = 1024 * 1024;
    /// <summary>
    /// Set by install.ps1 while it updates the app: running connectors exit cleanly (they lock Stowdeck.exe), and
    /// ones started meanwhile exit straight away. Manual-reset, and separate from the app's own exit signal.
    /// </summary>
    internal const string StopEventName = @"Local\Stowdeck.McpStop";
    internal const int MaxNoteChars = 1000;

    /// <summary>Runs until stdin closes. Returns the process exit code.</summary>
    public static int RunStdio()
    {
        KeepStdioToOurselves();
        // Tests use their own signal name, so they never stop a connector Claude is actually using.
        var stopName = Environment.GetEnvironmentVariable("STOWDECK_MCP_STOP_EVENT") is { Length: > 0 } custom && custom.StartsWith(@"Local\Stowdeck.", StringComparison.Ordinal)
            ? custom : StopEventName;
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        if (stop.WaitOne(0)) return 0; // an install is in progress
        new Thread(() =>
        {
            stop.WaitOne();
            LaunchGuard.LogEvent("MCP", "connector stopped for an install or update");
            Environment.Exit(0);
        }) { IsBackground = true, Name = "MCP stop signal" }.Start();
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var server = new Handler(new LiveActions());
        LaunchGuard.LogEvent("MCP", "connector started (stdio)");
        string? line;
        while ((line = input.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var reply = server.HandleLine(line);
            if (reply != null) output.WriteLine(reply);
        }
        LaunchGuard.LogEvent("MCP", "connector stopped (stdin closed)");
        return 0;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll")] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    /// <summary>
    /// The pipes to the MCP client must not be inherited by anything this process starts (the app window, a session's
    /// console): a child holding them open would keep the client waiting for the connector's output to end.
    /// </summary>
    private static void KeepStdioToOurselves()
    {
        foreach (var std in new[] { -10, -11, -12 })
        {
            var h = GetStdHandle(std);
            if (h != IntPtr.Zero && h != new IntPtr(-1)) SetHandleInformation(h, 1 /*HANDLE_FLAG_INHERIT*/, 0);
        }
    }

    // ------------------------------------------------------------------ the actions behind the tools

    /// <summary>What the tools do. Tests use a fake; <see cref="LiveActions"/> reads and launches for real.</summary>
    internal interface IActions
    {
        string ListRunning(bool includeTemp);
        string ListRecent(int days, bool includeTemp);
        string ListSnapshots();
        (string Text, bool Ok) SaveSnapshot(string? name, string? note);
        (string Text, bool Ok) OpenApp();
        (string Text, bool Ok) OpenSession(string sessionId);
    }

    internal sealed class LiveActions : IActions
    {
        /// <summary>Sessions this connector opened in the last minute: they may not be registered as running yet.</summary>
        private static readonly Dictionary<string, DateTime> RecentlyOpened = new(StringComparer.OrdinalIgnoreCase);
        internal static readonly TimeSpan OpenGrace = TimeSpan.FromSeconds(60);

        public string ListRunning(bool includeTemp) =>
            Capture(w => Cli.List(w, new CliOptions("--list", NoMessages: true, HideTemp: !includeTemp)));
        public string ListRecent(int days, bool includeTemp) =>
            Capture(w => Cli.Recent(w, new CliOptions("--recent", Days: days, NoMessages: true, HideTemp: !includeTemp)));
        public string ListSnapshots() => Capture(w => Cli.Snapshots(w, new CliOptions("--snapshots")));

        public (string, bool) SaveSnapshot(string? name, string? note)
        {
            var output = new StringWriter();
            var errors = new StringWriter();
            int code = Cli.SaveSnapshot(output, errors, new CliOptions("--snapshot", note, name));
            return code == 0 ? (output.ToString().TrimEnd(), true) : (errors.ToString().Replace("Stowdeck: ", "").TrimEnd(), false);
        }

        /// <summary>Starts this same Stowdeck.exe with no arguments: the window opens, or an open one comes to the front.</summary>
        public (string, bool) OpenApp()
        {
            var exe = Environment.ProcessPath;
            if (exe == null || !string.Equals(Path.GetFileName(exe), "Stowdeck.exe", StringComparison.OrdinalIgnoreCase))
                return ("Couldn't find Stowdeck.exe to open.", false);
            using (LaunchGuard.UserAction(LaunchGuard.McpOpenApp))
            using (LaunchGuard.Start(new System.Diagnostics.ProcessStartInfo(exe)
                   { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! }, "Stowdeck window (MCP)")) { }
            return ("Opened Stowdeck (an open window comes to the front).", true);
        }

        /// <summary>
        /// Resumes a session in a new window, like the app's Open button. Only the session ID comes from the caller:
        /// the folder is the one Claude Code recorded for that conversation, checked the same way Open checks it. A
        /// session that's already running isn't opened a second time.
        /// </summary>
        public (string, bool) OpenSession(string sessionId)
        {
            if (!SessionStore.IsValidSessionId(sessionId)) return ("That isn't a valid session ID (expected a GUID such as 1b2c3d4e-...).", false);
            var store = new SessionStore();
            var file = store.FindFile(sessionId);
            var info = file == null ? null : store.GetSessionFromFile(file);
            if (info?.ProjectDir is not { } folder) return ($"No conversation with session ID {sessionId} was found.", false);
            // As Open does: the conversation must be found for this folder and belong to it, or --resume would fail or resume elsewhere.
            var again = store.FindFile(sessionId, folder) is { } f2 ? store.GetSessionFromFile(f2) : null;
            if (again?.ProjectDir == null || !string.Equals(again.ProjectDir.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return ("The conversation's folder couldn't be confirmed, so it wasn't opened.", false);
            if (!FolderProbe.Exists(folder)) return ($"The session's folder no longer exists ({folder}).", false);
            lock (RecentlyOpened)
                if (RecentlyOpened.TryGetValue(sessionId, out var at) && DateTime.UtcNow - at < OpenGrace)
                    return ("That session was opened from here less than a minute ago and is probably still starting, so it wasn't opened again.", false);
            if (new RunningScanner().Scan().Any(r => string.Equals(r.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
                return ("That session is already running. A second window on the same conversation can mix up its history, so it wasn't opened; switch to its existing window instead.", false);

            var nickname = new NicknameStore().Get(sessionId);
            var item = new LaunchItem(folder, sessionId, Fmt.FolderName(folder), nickname);
            var mode = AppSettings.Load().OpenIn;
            LaunchResult result;
            using (LaunchGuard.UserAction(LaunchGuard.McpOpenSession))
                result = Launcher.Launch(new[] { item }, mode);
            var label = nickname ?? info.Title ?? Fmt.FolderName(folder);
            if (result.OpenedItems.Count == 1)
                lock (RecentlyOpened) RecentlyOpened[sessionId] = DateTime.UtcNow;
            if (result.OpenedItems.Count == 1)
                return ($"Opened “{Fmt.Trim(label, 100)}” in {folder}, in a {(result.UsedPowerShellWindows ? "PowerShell window" : "Windows Terminal tab")}.", true);
            return ("Couldn't open it: " + (result.Error ?? string.Join("; ", result.Skipped)), false);
        }

        private static string Capture(Func<TextWriter, int> run)
        {
            var w = new StringWriter();
            run(w);
            return w.ToString().TrimEnd();
        }
    }

    // ------------------------------------------------------------------ JSON-RPC

    internal sealed class Handler(IActions actions)
    {
        /// <summary>Handles one line from the client; returns the line to send back, or null for a notification.</summary>
        public string? HandleLine(string line)
        {
            if (line.Length > MaxMessageChars) return Error(null, -32600, "Message too large").ToJsonString();
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                return Error(null, -32700, "Parse error").ToJsonString();
            }
            if (node is JsonArray batch)
            {
                if (batch.Count == 0) return Error(null, -32600, "Invalid request: empty batch").ToJsonString();
                var replies = new JsonArray();
                foreach (var item in batch)
                    if (Guarded(item) is { } r) replies.Add(r);
                return replies.Count == 0 ? null : replies.ToJsonString();
            }
            return Guarded(node)?.ToJsonString();
        }

        /// <summary>
        /// Nothing a client sends can stop the connector: a malformed object (for example a key given twice, which the
        /// JSON reader only notices when the object is first read) gets an Invalid request error instead.
        /// </summary>
        private JsonNode? Guarded(JsonNode? node)
        {
            try
            {
                return Handle(node);
            }
            catch (Exception ex)
            {
                return Error(null, -32600, "Invalid request: " + ex.Message);
            }
        }

        private JsonNode? Handle(JsonNode? node)
        {
            if (node is not JsonObject msg || msg["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var method))
                return node is JsonObject { } o && o.ContainsKey("id") && !o.ContainsKey("method") ? null /* a response to us: ignore */
                    : Error(IdOf(node), -32600, "Invalid request");
            bool isNotification = !msg.ContainsKey("id");
            var id = IdOf(msg);
            if (id is JsonObject or JsonArray || (id is JsonValue idValue && idValue.GetValueKind() is not (JsonValueKind.String or JsonValueKind.Number)))
                return Error(null, -32600, "Invalid request: id must be a string or a number");
            if (isNotification) return null; // notifications/initialized, notifications/cancelled: nothing to do
            try
            {
                return method switch
                {
                    "initialize" => Result(id, Initialize(msg["params"] as JsonObject)),
                    "ping" => Result(id, new JsonObject()),
                    "tools/list" => Result(id, new JsonObject { ["tools"] = Tools() }),
                    "tools/call" => CallTool(id, msg["params"] as JsonObject),
                    _ => Error(id, -32601, "Method not found: " + method),
                };
            }
            catch (ArgumentException ex)
            {
                return Error(id, -32602, "Invalid params: " + ex.Message); // e.g. a key given twice in the arguments
            }
            catch (Exception ex)
            {
                return Error(id, -32603, "Internal error: " + ex.Message);
            }
        }

        private static JsonNode? IdOf(JsonNode? node) => node is JsonObject o && o["id"] is { } id ? id.DeepClone() : null;

        private static JsonObject Initialize(JsonObject? p)
        {
            var requested = (p?["protocolVersion"] as JsonValue)?.TryGetValue<string>(out var v) == true ? v : null;
            return new JsonObject
            {
                ["protocolVersion"] = requested != null && SupportedProtocols.Contains(requested) ? requested : SupportedProtocols[0],
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                ["serverInfo"] = new JsonObject { ["name"] = "stowdeck", ["version"] = BuildInfo.Current.Version },
                ["instructions"] = "Stowdeck lists the user's Claude Code sessions on this Windows PC (running and recent), lists saved " +
                                   "snapshots, saves a snapshot of the running sessions, and opens the Stowdeck app or resumes a session " +
                                   "in a new window. Results contain titles, names, folders, times, statuses and session IDs only, never " +
                                   "conversation text. Restoring a whole snapshot and anything about other terminals is done in the app.",
            };
        }

        // ------------------------------------------------------------------ tools

        internal static JsonArray Tools() => new()
        {
            Tool("list_running_sessions", "List running sessions",
                "List the Claude Code sessions running right now: status, folder, how long ago they were active, title or name, session ID and PID. " +
                "Statuses: working (busy), needs input (waiting for the user, e.g. a permission prompt), idle, shell (running a background " +
                "shell command, such as a dev server), starting (just launched), unmatched (not in Claude Code's session registry). " +
                HiddenTempText,
                new JsonObject { ["include_temp"] = IncludeTempProperty() }, readOnly: true),
            Tool("list_recent_sessions", "List recent sessions",
                "List Claude Code sessions active in the last N days (default 1), newest first, with folder, time, title or name, session ID and whether each is running. " +
                HiddenTempText,
                new JsonObject
                {
                    ["days"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 365, ["description"] = "How many days back to look (1-365, default 1)." },
                    ["include_temp"] = IncludeTempProperty(),
                },
                readOnly: true),
            Tool("list_snapshots", "List snapshots",
                "List saved Stowdeck snapshots: name, when saved, how many sessions and terminals, and the note.",
                new JsonObject(), readOnly: true),
            Tool("save_snapshot", "Save a snapshot",
                "Save the running Claude Code sessions as a new snapshot (one file in the user's snapshots folder), optionally with a name and a note. Unmatched or still-starting sessions are skipped and listed.",
                new JsonObject
                {
                    ["note"] = new JsonObject { ["type"] = "string", ["maxLength"] = MaxNoteChars, ["description"] = "Optional note, worded as the user said it." },
                    ["name"] = new JsonObject { ["type"] = "string", ["maxLength"] = 80, ["description"] = "Optional snapshot name (default: \"Snapshot\" and the date and time)." },
                }, readOnly: false),
            Tool("open_app", "Open Stowdeck",
                "Open the Stowdeck window on this PC (an open window comes to the front). Restoring snapshots and reopening several sessions is done there.",
                new JsonObject(), readOnly: false),
            Tool("open_session", "Open a session",
                "Resume one Claude Code session in a new PowerShell window or Windows Terminal tab (per Stowdeck's settings), in the folder Claude Code recorded for it. Refused if it's already running. Get the session ID from list_recent_sessions.",
                new JsonObject { ["session_id"] = new JsonObject { ["type"] = "string", ["description"] = "The session ID (a GUID) from list_recent_sessions." } },
                readOnly: false, required: new[] { "session_id" }),
        };

        private const string HiddenTempText =
            "Untitled sessions in temp folders (short throwaway runs) are left out and counted at the end, unless include_temp is true.";

        private static JsonObject IncludeTempProperty() => new()
        {
            ["type"] = "boolean",
            ["description"] = "Also list untitled sessions in temp folders (default false). Set it only when the user asks for them.",
        };

        private static JsonObject Tool(string name, string title, string description, JsonObject properties, bool readOnly, string[]? required = null)
        {
            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties, ["additionalProperties"] = false };
            if (required != null) schema["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray());
            return new JsonObject
            {
                ["name"] = name,
                ["title"] = title,
                ["description"] = description,
                ["inputSchema"] = schema,
                ["annotations"] = new JsonObject
                {
                    ["title"] = title,
                    ["readOnlyHint"] = readOnly,
                    ["destructiveHint"] = false,
                    ["idempotentHint"] = readOnly,
                    ["openWorldHint"] = false,
                },
            };
        }

        private JsonObject CallTool(JsonNode? id, JsonObject? p)
        {
            var name = (p?["name"] as JsonValue)?.TryGetValue<string>(out var n) == true ? n : null;
            var args = p?["arguments"] as JsonObject ?? new JsonObject();
            if (name == null || !Tools().Any(t => (string?)t!["name"] == name)) return Error(id, -32602, "Unknown tool: " + (name ?? "(none)"));
            var known = ((JsonObject)Tools().First(t => (string?)t!["name"] == name)!["inputSchema"]!["properties"]!).Select(kv => kv.Key).ToHashSet();
            if (args.Select(kv => kv.Key).FirstOrDefault(k => !known.Contains(k)) is { } extra) return ToolText(id, $"Unknown argument: {extra}", isError: true);

            try
            {
                switch (name)
                {
                    case "list_running_sessions":
                    {
                        if (!OptionalBool(args, "include_temp", out var includeTemp)) return ToolText(id, "include_temp must be true or false.", isError: true);
                        return ToolText(id, actions.ListRunning(includeTemp));
                    }
                    case "list_snapshots": return ToolText(id, actions.ListSnapshots());
                    case "list_recent_sessions":
                    {
                        int days = 1;
                        if (args["days"] is { } d && (d is not JsonValue dv || !dv.TryGetValue(out days) || days is < 1 or > 365))
                            return ToolText(id, "days must be a whole number from 1 to 365.", isError: true);
                        if (!OptionalBool(args, "include_temp", out var includeTemp)) return ToolText(id, "include_temp must be true or false.", isError: true);
                        return ToolText(id, actions.ListRecent(days, includeTemp));
                    }
                    case "save_snapshot":
                    {
                        if (!OptionalString(args, "note", out var note) || !OptionalString(args, "name", out var snapName))
                            return ToolText(id, "note and name must be text.", isError: true);
                        note = CleanNote(note);
                        snapName = NicknameStore.Clean(snapName); // no control or invisible characters, at most 80
                        var (text, ok) = actions.SaveSnapshot(snapName, note);
                        return ToolText(id, text, !ok);
                    }
                    case "open_app":
                    {
                        var (text, ok) = actions.OpenApp();
                        return ToolText(id, text, !ok);
                    }
                    case "open_session":
                    {
                        if (!OptionalString(args, "session_id", out var sid) || sid == null)
                            return ToolText(id, "session_id is required (a GUID from list_recent_sessions).", isError: true);
                        var (text, ok) = actions.OpenSession(sid.Trim());
                        return ToolText(id, text, !ok);
                    }
                }
            }
            catch (Exception ex)
            {
                return ToolText(id, "Stowdeck couldn't do that: " + ex.Message, isError: true);
            }
            return Error(id, -32602, "Unknown tool: " + name);
        }

        private static bool OptionalString(JsonObject args, string key, out string? value)
        {
            value = null;
            if (args[key] is not { } node) return true;
            return node is JsonValue v && v.TryGetValue(out value);
        }

        private static bool OptionalBool(JsonObject args, string key, out bool value)
        {
            value = false;
            if (args[key] is not { } node) return true;
            return node is JsonValue v && v.TryGetValue(out value);
        }

        /// <summary>A note keeps its line breaks but loses other control and invisible characters, and is capped.</summary>
        internal static string? CleanNote(string? note)
        {
            if (string.IsNullOrWhiteSpace(note)) return null;
            var sb = new StringBuilder();
            foreach (var ch in note.Replace("\r\n", "\n"))
            {
                if (ch == '\n' || !(char.IsControl(ch) || char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.Format)) sb.Append(ch);
                if (sb.Length >= MaxNoteChars) break;
            }
            var s = sb.ToString().Trim();
            return s.Length == 0 ? null : s;
        }

        private static JsonObject ToolText(JsonNode? id, string text, bool isError = false)
        {
            var result = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text.Length == 0 ? "(nothing)" : text }),
            };
            if (isError) result["isError"] = true;
            return Result(id, result);
        }

        private static JsonObject Result(JsonNode? id, JsonNode result) => new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JsonObject Error(JsonNode? id, int code, string message) =>
            new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
    }
}
