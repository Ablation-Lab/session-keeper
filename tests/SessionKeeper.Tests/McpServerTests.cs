using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

/// <summary>The local MCP connector (SessionKeeper.exe --mcp): protocol, tools, argument checks, and no conversation text.</summary>
public sealed class McpServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sk-mcp " + Guid.NewGuid().ToString("N")[..8]);

    public McpServerTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private sealed class FakeActions : McpServer.IActions
    {
        public readonly List<string> Calls = new();
        public string ListRunning(bool includeTemp) { Calls.Add("running" + (includeTemp ? " +temp" : "")); return "2 Claude Code sessions running"; }
        public string ListRecent(int days, bool includeTemp) { Calls.Add("recent " + days + (includeTemp ? " +temp" : "")); return $"sessions in the last {days} days"; }
        public string ListSnapshots() { Calls.Add("snapshots"); return "No snapshots saved yet."; }
        public (string, bool) SaveSnapshot(string? name, string? note) { Calls.Add($"save [{name}] [{note}]"); return ("Saved snapshot", true); }
        public (string, bool) OpenApp() { Calls.Add("open app"); return ("Opened SessionKeeper", true); }
        public (string, bool) OpenSession(string id) { Calls.Add("open " + id); return ("Opened", true); }
    }

    private static JsonObject Send(McpServer.Handler h, object message) =>
        (JsonObject)JsonNode.Parse(h.HandleLine(JsonSerializer.Serialize(message))!)!;

    private static (string Text, bool IsError) Call(McpServer.Handler h, string tool, object? args = null)
    {
        var r = Send(h, new { jsonrpc = "2.0", id = 7, method = "tools/call", @params = new { name = tool, arguments = args ?? new { } } });
        var result = r["result"]!.AsObject();
        return ((string)result["content"]![0]!["text"]!, result["isError"]?.GetValue<bool>() == true);
    }

    // ------------------------------------------------------------------ protocol

    [Theory]
    [InlineData("2025-06-18", "2025-06-18")]
    [InlineData("2024-11-05", "2024-11-05")]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("1999-01-01", "2025-11-25")] // unknown: the newest we support
    public void InitializeNegotiatesTheProtocolVersion(string requested, string expected)
    {
        var h = new McpServer.Handler(new FakeActions());
        var r = Send(h, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { protocolVersion = requested, capabilities = new { }, clientInfo = new { name = "t", version = "1" } } });
        Assert.Equal(1, (int)r["id"]!);
        Assert.Equal(expected, (string)r["result"]!["protocolVersion"]!);
        Assert.Equal("sessionkeeper", (string)r["result"]!["serverInfo"]!["name"]!);
        Assert.NotNull(r["result"]!["capabilities"]!["tools"]);
        Assert.Contains("never conversation text", (string)r["result"]!["instructions"]!);
    }

    [Fact]
    public void NotificationsGetNoReplyAndIdsComeBackAsSent()
    {
        var h = new McpServer.Handler(new FakeActions());
        Assert.Null(h.HandleLine("""{"jsonrpc":"2.0","method":"notifications/initialized"}"""));
        Assert.Null(h.HandleLine("""{"jsonrpc":"2.0","id":5,"result":{}}""")); // a response, not a request
        var r = (JsonObject)JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":"abc","method":"ping"}""")!)!;
        Assert.Equal("abc", (string)r["id"]!);
        Assert.Empty(r["result"]!.AsObject());
    }

    [Fact]
    public void BadMessagesGetJsonRpcErrors()
    {
        var h = new McpServer.Handler(new FakeActions());
        Assert.Equal(-32700, (int)JsonNode.Parse(h.HandleLine("{not json")!)!["error"]!["code"]!);
        Assert.Equal(-32600, (int)JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":1,"method":5}""")!)!["error"]!["code"]!);
        Assert.Equal(-32600, (int)JsonNode.Parse(h.HandleLine("""[1]""")!)![0]!["error"]!["code"]!);
        Assert.Equal(-32601, (int)JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":1,"method":"resources/list"}""")!)!["error"]!["code"]!);
        Assert.Equal(-32602, (int)JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"delete_everything"}}""")!)!["error"]!["code"]!);
        Assert.Equal(-32600, (int)JsonNode.Parse(h.HandleLine(new string('x', 1024 * 1024 + 1))!)!["error"]!["code"]!);
    }

    [Fact]
    public void MalformedObjectsGetErrorsInsteadOfStoppingTheConnector()
    {
        var h = new McpServer.Handler(new FakeActions());
        var dup = JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":5,"method":"ping","method":"x"}""")!)!;
        Assert.Equal(-32600, (int)dup["error"]!["code"]!);
        var dupArgs = JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":6,"method":"tools/call","params":{"name":"list_recent_sessions","arguments":{"days":3,"days":4}}}""")!)!;
        Assert.NotNull(dupArgs["error"]);
        Assert.Equal(-32600, (int)JsonNode.Parse(h.HandleLine("[]")!)!["error"]!["code"]!);
        Assert.Equal(-32600, (int)JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":{"a":1},"method":"ping"}""")!)!["error"]!["code"]!);
        Assert.Equal(-32600, (int)JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":true,"method":"ping"}""")!)!["error"]!["code"]!);
        Assert.NotNull(JsonNode.Parse(h.HandleLine("""{"jsonrpc":"2.0","id":7,"method":"ping"}""")!)!["result"]); // still answering
    }

    [Fact]
    public void ABatchGetsOneReplyPerRequest()
    {
        var h = new McpServer.Handler(new FakeActions());
        var r = JsonNode.Parse(h.HandleLine("""[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":2,"method":"ping"}]""")!)!.AsArray();
        Assert.Equal(new[] { 1, 2 }, r.Select(x => (int)x!["id"]!));
    }

    [Fact]
    public void ToolsListHasTheSixToolsWithTheRightReadOnlyHints()
    {
        var tools = McpServer.Handler.Tools();
        var byName = tools.ToDictionary(t => (string)t!["name"]!, t => t!.AsObject());
        Assert.Equal(new[] { "list_running_sessions", "list_recent_sessions", "list_snapshots", "save_snapshot", "open_app", "open_session" }, byName.Keys);
        foreach (var name in new[] { "list_running_sessions", "list_recent_sessions", "list_snapshots" })
            Assert.True((bool)byName[name]["annotations"]!["readOnlyHint"]!);
        foreach (var name in new[] { "save_snapshot", "open_app", "open_session" })
            Assert.False((bool)byName[name]["annotations"]!["readOnlyHint"]!);
        Assert.All(byName.Values, t =>
        {
            Assert.False((bool)t["annotations"]!["openWorldHint"]!);
            Assert.False((bool)t["inputSchema"]!["additionalProperties"]!);
        });
        Assert.Equal("session_id", (string)byName["open_session"]["inputSchema"]!["required"]![0]!);
        foreach (var name in new[] { "list_running_sessions", "list_recent_sessions" })
        {
            Assert.Equal("boolean", (string)byName[name]["inputSchema"]!["properties"]!["include_temp"]!["type"]!);
            Assert.Contains("temp folders", (string)byName[name]["description"]!);
        }
        Assert.Contains("shell (running a background shell command", (string)byName["list_running_sessions"]["description"]!);
    }

    // ------------------------------------------------------------------ tool arguments

    [Fact]
    public void EachToolCallsItsActionWithCheckedArguments()
    {
        var fake = new FakeActions();
        var h = new McpServer.Handler(fake);
        Assert.Equal("2 Claude Code sessions running", Call(h, "list_running_sessions").Text);
        Assert.Equal("sessions in the last 1 days", Call(h, "list_recent_sessions").Text);
        Assert.Equal("sessions in the last 7 days", Call(h, "list_recent_sessions", new { days = 7 }).Text);
        Call(h, "list_snapshots");
        Call(h, "save_snapshot", new { note = "end of day", name = "Friday" });
        Call(h, "save_snapshot");
        Call(h, "open_app");
        Call(h, "open_session", new { session_id = " 11111111-2222-3333-4444-555555555555 " });
        Call(h, "list_running_sessions", new { include_temp = true });
        Call(h, "list_recent_sessions", new { days = 2, include_temp = true });
        Call(h, "list_recent_sessions", new { include_temp = false });
        Assert.Equal(new[]
        {
            "running", "recent 1", "recent 7", "snapshots", "save [Friday] [end of day]", "save [] []", "open app",
            "open 11111111-2222-3333-4444-555555555555", "running +temp", "recent 2 +temp", "recent 1",
        }, fake.Calls);
    }

    [Fact]
    public void BadArgumentsAreRefusedWithoutDoingAnything()
    {
        var fake = new FakeActions();
        var h = new McpServer.Handler(fake);
        Assert.True(Call(h, "list_recent_sessions", new { days = 0 }).IsError);
        Assert.True(Call(h, "list_recent_sessions", new { days = 400 }).IsError);
        Assert.True(Call(h, "list_recent_sessions", new { days = "7; calc" }).IsError);
        Assert.True(Call(h, "list_recent_sessions", new { days = 1.5 }).IsError);
        Assert.True(Call(h, "save_snapshot", new { note = 5 }).IsError);
        Assert.True(Call(h, "save_snapshot", new { path = @"C:\Windows" }).IsError); // unknown argument
        Assert.True(Call(h, "open_session").IsError);
        Assert.True(Call(h, "open_session", new { session_id = 12 }).IsError);
        Assert.True(Call(h, "open_session", new { session_id = "11111111-2222-3333-4444-555555555555", folder = @"C:\elsewhere" }).IsError);
        Assert.True(Call(h, "open_app", new { args = "--evil" }).IsError);
        Assert.True(Call(h, "list_recent_sessions", new { include_temp = "yes" }).IsError);
        Assert.True(Call(h, "list_running_sessions", new { include_temp = 1 }).IsError);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void NotesAndNamesAreCleanedAndCapped()
    {
        var fake = new FakeActions();
        var h = new McpServer.Handler(fake);
        Call(h, "save_snapshot", new { note = "line one\r\nline two\u0007\u202E", name = "Fri\u0000day\u200B" + new string('x', 200) });
        var call = Assert.Single(fake.Calls);
        Assert.StartsWith("save [Fri day", call); // the control character became a space, the invisible one went
        Assert.Contains("[line one\nline two]", call);
        Assert.Equal(McpServer.MaxNoteChars, McpServer.Handler.CleanNote(new string('n', 5000))!.Length);
        Assert.Null(McpServer.Handler.CleanNote("  \u0007 "));
        Assert.True(NicknameStore.Clean("Fri\u0000day" + new string('x', 200))!.Length <= 80);
    }

    [Fact]
    public void AFailingActionComesBackAsAToolError()
    {
        var h = new McpServer.Handler(new ThrowingActions());
        var (text, isError) = Call(h, "list_snapshots");
        Assert.True(isError);
        Assert.Contains("disk on fire", text);
    }

    private sealed class ThrowingActions : McpServer.IActions
    {
        public string ListRunning(bool includeTemp) => throw new IOException("disk on fire");
        public string ListRecent(int days, bool includeTemp) => throw new IOException("disk on fire");
        public string ListSnapshots() => throw new IOException("disk on fire");
        public (string, bool) SaveSnapshot(string? name, string? note) => throw new IOException("disk on fire");
        public (string, bool) OpenApp() => throw new IOException("disk on fire");
        public (string, bool) OpenSession(string id) => throw new IOException("disk on fire");
    }

    // ------------------------------------------------------------------ the real actions (nothing is launched)

    [Fact]
    public void OpenSessionRefusesBadOrUnknownIdsWithoutLaunching()
    {
        var live = new McpServer.LiveActions();
        Assert.False(live.OpenSession("not-a-guid").Item2);
        Assert.False(live.OpenSession("..\\..\\x").Item2);
        var (text, ok) = live.OpenSession("00000000-0000-0000-0000-00000000dead");
        Assert.False(ok);
        Assert.Contains("No conversation", text);
    }

    [Fact]
    public void McpLaunchesNeedTheirOwnApprovedAction()
    {
        var psi = new ProcessStartInfo(Launcher.ConhostPath, "x") { UseShellExecute = false };
        Assert.NotNull(LaunchGuard.Check(psi));
        using (LaunchGuard.UserAction(LaunchGuard.McpOpenSession)) Assert.Null(LaunchGuard.Check(psi));
        using (LaunchGuard.UserAction(LaunchGuard.McpOpenApp)) Assert.Null(LaunchGuard.Check(psi));
    }

    // ------------------------------------------------------------------ end to end, with a fake Claude folder

    private const string Id = "11111111-2222-3333-4444-555555555555";
    private const string Secret = "SECRET-PROMPT-TEXT-do-not-show";

    /// <summary>A Claude config folder with one recent conversation whose messages contain a secret.</summary>
    private string FakeClaudeConfig()
    {
        var config = Path.Combine(_root, "claude");
        var cwd = Path.Combine(_root, "demo project");
        Directory.CreateDirectory(cwd);
        var dir = Path.Combine(config, "projects", SessionStore.EncodeProjectDir(cwd));
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(config, "sessions"));
        string J(string s) => JsonSerializer.Serialize(s);
        var ts = DateTime.UtcNow.AddMinutes(-30).ToString("o");
        File.WriteAllText(Path.Combine(dir, Id + ".jsonl"), string.Join("\n",
            $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":{J(Secret + " first")}}},\"timestamp\":\"{ts}\",\"cwd\":{J(cwd)},\"sessionId\":\"{Id}\"}}",
            $"{{\"type\":\"assistant\",\"message\":{{\"role\":\"assistant\",\"content\":[{{\"type\":\"text\",\"text\":{J(Secret + " reply")}}}]}},\"timestamp\":\"{ts}\",\"cwd\":{J(cwd)},\"sessionId\":\"{Id}\"}}",
            $"{{\"type\":\"last-prompt\",\"lastPrompt\":{J(Secret + " last")},\"sessionId\":\"{Id}\"}}",
            $"{{\"type\":\"ai-title\",\"aiTitle\":\"Demo title\",\"sessionId\":\"{Id}\"}}") + "\n", new UTF8Encoding(false));
        return config;
    }

    private const string ThrowawayId = "99999999-8888-7777-6666-555555555555";

    /// <summary>Adds an untitled conversation in a folder inside the temp folder (where _root is), like a short throwaway run.</summary>
    private void AddThrowaway(string config)
    {
        var cwd = Path.Combine(_root, "throwaway run");
        Directory.CreateDirectory(cwd);
        var dir = Path.Combine(config, "projects", SessionStore.EncodeProjectDir(cwd));
        Directory.CreateDirectory(dir);
        var ts = DateTime.UtcNow.AddMinutes(-20).ToString("o");
        File.WriteAllText(Path.Combine(dir, ThrowawayId + ".jsonl"),
            $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"hi\"}},\"timestamp\":\"{ts}\",\"cwd\":{JsonSerializer.Serialize(cwd)},\"sessionId\":\"{ThrowawayId}\"}}\n",
            new UTF8Encoding(false));
    }

    private static string ExePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SessionKeeper.sln"))) dir = dir.Parent;
        var config = AppContext.BaseDirectory.Contains(@"\Release\") ? "Release" : "Debug";
        return Path.Combine(dir!.FullName, "src", "SessionKeeper", "bin", config, "net8.0-windows", "win-x64", "SessionKeeper.exe");
    }

    private static (int Code, string Out) Run(string config, string? stdin, params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = stdin != null, StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["CLAUDE_CONFIG_DIR"] = config;
        using var p = Process.Start(psi)!;
        if (stdin != null)
        {
            p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        var output = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "SessionKeeper didn't exit");
        return (p.ExitCode, output.Result);
    }

    [Fact]
    public void TheCommandLineLeavesOutMessagesWhenAsked()
    {
        var config = FakeClaudeConfig();
        var withMessages = Run(config, null, "--recent", "--days", "2");
        Assert.Contains(Secret, withMessages.Out); // the fixture really has message text to hide
        foreach (var args in new[] { new[] { "--recent", "--days", "2", "--no-messages" }, new[] { "--recent", "--days", "2", "--no-messages", "--json" } })
        {
            var r = Run(config, null, args);
            Assert.Equal(0, r.Code);
            Assert.Contains("Demo title", r.Out);
            Assert.Contains(Id, r.Out);
            Assert.DoesNotContain(Secret, r.Out);
        }
    }

    [Fact]
    public void TheConnectorAnswersOverStdioWithoutConversationText()
    {
        var config = FakeClaudeConfig();
        var input = string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
            """{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"list_recent_sessions","arguments":{"days":2}}}""",
            """{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"list_running_sessions","arguments":{}}}""") + "\n";
        var (code, output) = Run(config, input, "--mcp");
        Assert.Equal(0, code);
        var replies = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(new[] { 1, 2, 3, 4 }, replies.Select(r => (int)r["id"]!)); // only JSON-RPC on stdout, one line each
        Assert.Equal(6, replies[1]["result"]!["tools"]!.AsArray().Count);
        var recent = (string)replies[2]["result"]!["content"]![0]!["text"]!;
        Assert.Contains("Demo title", recent);
        Assert.Contains(Id, recent);
        Assert.DoesNotContain(Secret, output);
    }

    [Fact]
    public void UntitledTempSessionsAreHiddenOnlyWhenAsked()
    {
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\'), _root, StringComparison.OrdinalIgnoreCase);
        var config = FakeClaudeConfig();
        AddThrowaway(config);

        var all = Run(config, null, "--recent", "--days", "2", "--no-messages");
        Assert.Contains(ThrowawayId, all.Out);
        Assert.Contains(Id, all.Out);
        Assert.DoesNotContain("hidden", all.Out);

        var hidden = Run(config, null, "--recent", "--days", "2", "--no-messages", "--hide-temp");
        Assert.Equal(0, hidden.Code);
        Assert.DoesNotContain(ThrowawayId, hidden.Out);
        Assert.Contains(Id, hidden.Out); // titled, so listed even though it's in a temp folder
        Assert.Contains("1 untitled session in temp folders hidden", hidden.Out);
        Assert.StartsWith("1 session in the last 2 days", hidden.Out);

        var json = Run(config, null, "--recent", "--days", "2", "--no-messages", "--hide-temp", "--json");
        Assert.Single(JsonNode.Parse(json.Out)!.AsArray());

        // The connector hides them by default and lists them with include_temp.
        var input = string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"list_recent_sessions","arguments":{"days":2}}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"list_recent_sessions","arguments":{"days":2,"include_temp":true}}}""") + "\n";
        var (code, output) = Run(config, input, "--mcp");
        Assert.Equal(0, code);
        var texts = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => (string)JsonNode.Parse(l)!["result"]!["content"]![0]!["text"]!).ToList();
        Assert.DoesNotContain(ThrowawayId, texts[0]);
        Assert.Contains("hidden", texts[0]);
        Assert.Contains(ThrowawayId, texts[1]);
        Assert.DoesNotContain(Secret, output);
    }

    [Fact]
    public void TheRealConnectorSurvivesABadMessage()
    {
        var input = string.Join("\n",
            """{"jsonrpc":"2.0","id":1,"method":"ping","method":"x"}""",
            """{"jsonrpc":"2.0","id":2,"method":"ping"}""") + "\n";
        var (code, output) = Run(FakeClaudeConfig(), input, "--mcp");
        Assert.Equal(0, code);
        var replies = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(2, replies.Count);
        Assert.NotNull(replies[0]["error"]);
        Assert.Equal(2, (int)replies[1]["id"]!);
    }

    /// <summary>The installer's stop signal makes a running connector exit cleanly (with a test-only signal name).</summary>
    [Fact]
    public void TheConnectorStopsWhenTheInstallerAsks()
    {
        var name = @"Local\SessionKeeper.McpStop.Test." + Guid.NewGuid().ToString("N");
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var psi = new ProcessStartInfo(ExePath())
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        psi.ArgumentList.Add("--mcp");
        psi.Environment["SESSIONKEEPER_MCP_STOP_EVENT"] = name;
        using var p = Process.Start(psi)!;
        p.StandardInput.WriteLine("""{"jsonrpc":"2.0","id":1,"method":"ping"}""");
        p.StandardInput.Flush();
        Assert.Contains("\"id\":1", p.StandardOutput.ReadLine()); // running and answering, stdin still open
        stop.Set();
        Assert.True(p.WaitForExit(10_000), "the connector didn't stop");
        Assert.Equal(0, p.ExitCode);

        // While the signal is set, a new connector exits straight away.
        using var q = Process.Start(psi)!;
        Assert.True(q.WaitForExit(10_000));
        Assert.Contains(McpServer.StopEventName, File.ReadAllText(Path.Combine(FindRepo(), "install.ps1")).Replace(@"\\", @"\"));
    }


    private static string FindRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SessionKeeper.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    [Fact]
    public void McpTakesNoOtherOptions()
    {
        Assert.Throws<CliUsageException>(() => Cli.Parse(new[] { "--mcp", "--json" }));
        Assert.Throws<CliUsageException>(() => Cli.Parse(new[] { "--snapshots", "--no-messages" }));
        Assert.True(Cli.Parse(new[] { "--list", "--no-messages" }).NoMessages);
        Assert.True(Cli.Parse(new[] { "--recent", "--days", "3", "--no-messages" }).NoMessages);
        Assert.True(Cli.Parse(new[] { "--list", "--no-messages", "--hide-temp" }).HideTemp);
        Assert.True(Cli.Parse(new[] { "--recent", "--hide-temp", "--days", "3" }).HideTemp);
        Assert.False(Cli.Parse(new[] { "--recent" }).HideTemp);
        Assert.Throws<CliUsageException>(() => Cli.Parse(new[] { "--snapshots", "--hide-temp" }));
        Assert.Throws<CliUsageException>(() => Cli.Parse(new[] { "--snapshot", "--hide-temp" }));
        Assert.Throws<CliUsageException>(() => Cli.Parse(new[] { "--mcp", "--hide-temp" }));
    }

    [Fact]
    public void TempFoldersAreRecognised()
    {
        var roots = new[] { @"D:\Scratch\Temp" };
        Assert.True(TempFolders.Contains(@"D:\Scratch\Temp", roots));
        Assert.True(TempFolders.Contains(@"d:\scratch\temp\", roots));
        Assert.True(TempFolders.Contains(@"D:\Scratch\Temp\run 1\deeper", roots));
        Assert.True(TempFolders.Contains("D:/Scratch/Temp/run 2", roots));
        Assert.True(TempFolders.Contains(@"E:\tmp", roots));      // named tmp or Temp anywhere
        Assert.True(TempFolders.Contains(@"E:\Work\TEMP", roots));
        Assert.False(TempFolders.Contains(@"D:\Scratch\Temporary", roots));
        Assert.False(TempFolders.Contains(@"D:\Scratch", roots));
        Assert.False(TempFolders.Contains(@"E:\Work\templates", roots));
        Assert.False(TempFolders.Contains(@"E:\Work\Temp project", roots));
        Assert.False(TempFolders.Contains(@"D:\", new[] { @"D:\" }));  // a drive root never counts
        Assert.False(TempFolders.Contains(null, roots));
        Assert.False(TempFolders.Contains("  ", roots));
        Assert.True(TempFolders.Contains(Path.GetTempPath()));
        Assert.True(TempFolders.Contains(Path.Combine(Path.GetTempPath(), "some run")));
        Assert.False(TempFolders.Contains(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));
    }

    [Fact]
    public void OnlyUntitledUnnamedSessionsInTempFoldersAreThrowaway()
    {
        var temp = Path.Combine(Path.GetTempPath(), "some run");
        Assert.True(Cli.IsThrowaway(null, null, temp));
        Assert.True(Cli.IsThrowaway(" ", "", temp));
        Assert.False(Cli.IsThrowaway("Fix the build", null, temp));
        Assert.False(Cli.IsThrowaway(null, "My name", temp));
        Assert.False(Cli.IsThrowaway(null, null, @"D:\Projects\app"));
        Assert.False(Cli.IsThrowaway(null, null, null));
    }
}
