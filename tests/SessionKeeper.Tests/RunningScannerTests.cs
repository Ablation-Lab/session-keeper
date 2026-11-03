using System.Text.Json;
using SessionKeeper.Models;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

/// <summary>
/// RunningScanner against a fake registry folder and a fake process table, so every filter is checked
/// without real Claude processes. Nothing here touches ~/.claude.
/// </summary>
public sealed class RunningScannerTests : IDisposable
{
    private const string A = "aaaa1111-0000-4000-8000-000000000001";
    private const string B = "bbbb2222-0000-4000-8000-000000000002";
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly string CliPath = @"D:\Tools\claude\claude.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sk-registry-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<int, FakeProc> _procs = new();
    private readonly List<string> _log = new();

    private sealed record FakeProc(string Name, string? Path, long? Created, int? Parent, string? Cwd);

    public RunningScannerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private RunningScanner NewScanner() => new(_dir,
        new RunningScanner.ProcessSource(
            () => _procs.ToDictionary(kv => kv.Key, kv => kv.Value.Name),
            pid => _procs.TryGetValue(pid, out var p) ? new Native.ProcessFacts(p.Path, p.Created, p.Parent) : default,
            pid => _procs.TryGetValue(pid, out var p) ? p.Cwd : null),
        () => Now,
        _log.Add);

    private static long Ft(DateTimeOffset t) => t.ToFileTime();

    private void Proc(int pid, DateTimeOffset started, string name = "claude", string? path = null, int? parent = null, string? cwd = @"C:\Projects\demo-app") =>
        _procs[pid] = new FakeProc(name, path ?? CliPath, Ft(started), parent, cwd);

    private string Reg(int pid, string? sessionId, DateTimeOffset procStart, string status = "idle", int? pidInside = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["pid"] = pidInside ?? pid,
            ["cwd"] = @"C:\Projects\demo-app",
            ["startedAt"] = procStart.AddSeconds(1).ToUnixTimeMilliseconds(),
            ["procStart"] = Ft(procStart).ToString(), // a string of FILETIME ticks, as Claude Code writes it
            ["status"] = status,
        };
        if (sessionId != null) entry["sessionId"] = sessionId;
        var path = Path.Combine(_dir, pid + ".json");
        File.WriteAllText(path, JsonSerializer.Serialize(entry));
        return path;
    }

    // ------------------------------------------------------------------ procStart format (C1)

    [Fact]
    public void RealProcStartFormatIsAFileTimeThatIsSameProcessAccepts()
    {
        // Sanitised copy of a live ~/.claude/sessions/<pid>.json: same fields and value formats, neutral values.
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "registry-entry.json");
        var reg = RunningScanner.ReadRegistryFile(fixture);
        Assert.NotNull(reg);
        Assert.Equal(134374735985000000, reg!.ProcStart);

        // FILETIME ticks (100 ns since 1601, UTC), just before the millisecond startedAt.
        var procStart = DateTime.FromFileTimeUtc(reg.ProcStart!.Value);
        var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(reg.StartedAtMs!.Value).UtcDateTime;
        Assert.InRange(startedAt - procStart, TimeSpan.Zero, TimeSpan.FromSeconds(5));

        // The process creation time (GetProcessTimes) equals procStart for the same process.
        Assert.True(RunningScanner.IsSameProcess(reg.ProcStart, reg.StartedAtMs, reg.ProcStart));
        Assert.True(RunningScanner.IsSameProcess(reg.ProcStart, reg.StartedAtMs, reg.ProcStart + TimeSpan.FromSeconds(2).Ticks));
        Assert.False(RunningScanner.IsSameProcess(reg.ProcStart, reg.StartedAtMs, reg.ProcStart + TimeSpan.FromMinutes(5).Ticks));

        // End to end: the fixture in a registry folder, matched to a process with that creation time.
        File.Copy(fixture, Path.Combine(_dir, "4242.json"));
        _procs[4242] = new FakeProc("claude", CliPath, reg.ProcStart, null, null);
        var s = Assert.Single(NewScanner().Scan());
        Assert.Equal("dddd4444-0000-4000-8000-000000000004", s.SessionId);
        Assert.Equal(RunState.Idle, s.State);
        Assert.Empty(_log);
    }

    // ------------------------------------------------------------------ registry validation (Q2)

    [Fact]
    public void AStaleEntryWhoseProcessHasGoneIsIgnored()
    {
        Reg(5000, A, Now.AddHours(-1));
        Assert.Empty(NewScanner().Scan());
        Assert.Empty(_log); // not even read, so nothing to log
    }

    [Fact]
    public void AReusedPidIsRejectedAndLoggedOnce()
    {
        Reg(5001, A, Now.AddHours(-2));
        Proc(5001, Now.AddHours(-1)); // a different claude now holds the PID
        var scanner = NewScanner();

        var s = Assert.Single(scanner.Scan());
        Assert.Null(s.SessionId);                 // the old entry's session is not attributed to it
        Assert.Equal(RunState.Unmatched, s.State); // shown as an unregistered claude instead
        var line = Assert.Single(_log);
        Assert.Contains("start-time mismatch", line);
        Assert.Contains("pid=5001", line);
        Assert.Contains("procStart=" + Ft(Now.AddHours(-2)), line);
        Assert.Contains("processCreated=" + Ft(Now.AddHours(-1)), line);

        scanner.Scan();
        Assert.Single(_log); // the same mismatch isn't logged on every refresh

        _procs[5001] = _procs[5001] with { Name = "node" }; // reused by something that isn't claude at all
        Assert.Empty(NewScanner().Scan());
    }

    [Fact]
    public void AnEntryForAnotherPidIsIgnored()
    {
        Reg(5002, A, Now.AddHours(-1), pidInside: 9999);
        Proc(5002, Now.AddHours(-1));
        var s = Assert.Single(NewScanner().Scan());
        Assert.Null(s.SessionId);
    }

    [Fact]
    public void AFileCaughtMidWriteFallsBackToTheLastGoodEntry()
    {
        var file = Reg(5003, A, Now.AddHours(-1), status: "busy");
        Proc(5003, Now.AddHours(-1));
        var scanner = NewScanner();
        Assert.Equal(RunState.Working, Assert.Single(scanner.Scan()).State);

        foreach (var partial in new[] { "{\"pid\": 50", "", "[]" })
        {
            File.WriteAllText(file, partial);
            var s = Assert.Single(scanner.Scan());
            Assert.Equal(A, s.SessionId);
            Assert.Equal(RunState.Working, s.State);
        }

        // With no earlier good read, a half-written file just leaves the process unregistered.
        File.WriteAllText(file, "{\"pid\": 50");
        var fresh = Assert.Single(NewScanner().Scan());
        Assert.Null(fresh.SessionId);
        Assert.Equal(RunState.Unmatched, fresh.State);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("../../etc/passwd")]
    public void AMissingOrInvalidSessionIdIsShownWithoutOne(string? sessionId)
    {
        Reg(5004, sessionId, Now.AddHours(-1));
        Proc(5004, Now.AddHours(-1));
        var s = Assert.Single(NewScanner().Scan());
        Assert.Null(s.SessionId);
        Assert.Equal(RunState.Idle, s.State);
        Assert.Equal("Registry entry has no usable session id", s.Note);
    }

    // ------------------------------------------------------------------ unregistered claude.exe filters

    [Theory]
    [InlineData(@"C:\Program Files\WindowsApps\Claude_1.0.0.0_x64__abc\app\claude.exe")]
    [InlineData(@"D:\Home\someone\AppData\Local\AnthropicClaude\app-1.0.0\claude.exe")]
    public void TheDesktopAppIsNotASession(string path)
    {
        Proc(6000, Now.AddHours(-1), path: path);
        Proc(6001, Now.AddHours(-1)); // a CLI claude next to it still shows
        var s = Assert.Single(NewScanner().Scan());
        Assert.Equal(6001, s.Pid);
    }

    [Fact]
    public void HelperProcessesOfAnotherClaudeAreNotSessions()
    {
        Reg(7000, A, Now.AddHours(-1));
        Proc(7000, Now.AddHours(-1));                                  // registered parent session
        Proc(7001, Now.AddMinutes(-30), parent: 7000);                // its helper: hidden
        Proc(7002, Now.AddMinutes(-30), name: "node");                // a node parent...
        Proc(7003, Now.AddMinutes(-20), parent: 7002);                // ...doesn't hide a claude child
        Proc(7004, Now.AddHours(-2), parent: 7000);                   // "parent" started after the child: its PID was reused
        var pids = NewScanner().Scan().Select(s => s.Pid).OrderBy(p => p).ToList();
        Assert.Equal(new[] { 7000, 7003, 7004 }, pids);
    }

    [Fact]
    public void ANewClaudeIsStartingForTwentySecondsThenUnmatched()
    {
        Proc(8000, Now.AddSeconds(-5));
        Proc(8001, Now.AddSeconds(-25));
        var byPid = NewScanner().Scan().ToDictionary(s => s.Pid);
        Assert.Equal(RunState.Starting, byPid[8000].State);
        Assert.Equal("Just started; not registered yet.", byPid[8000].Note);
        Assert.Equal(RunState.Unmatched, byPid[8001].State);
        Assert.Equal(@"C:\Projects\demo-app", byPid[8001].Cwd); // working directory read from the process
    }

    [Fact]
    public void ARegisteredSessionIsMatchedWithItsStatus()
    {
        Reg(9000, A, Now.AddHours(-1), status: "waiting");
        Reg(9001, B, Now.AddHours(-1), status: "something-new");
        Proc(9000, Now.AddHours(-1));
        Proc(9001, Now.AddHours(-1), name: "node"); // registered sessions may run under node or bun
        var byPid = NewScanner().Scan().ToDictionary(s => s.Pid);
        Assert.Equal(RunState.Waiting, byPid[9000].State);
        Assert.Equal(A, byPid[9000].SessionId);
        Assert.Equal(RunState.Other, byPid[9001].State); // unknown status shows as "unknown", not dropped
        Assert.Equal("something-new", byPid[9001].RawStatus);
    }
}
