using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SessionKeeper.Models;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

public sealed class CliTests
{
    private const string Id1 = "11111111-2222-3333-4444-555555555555";
    private const string Id2 = "66666666-7777-8888-9999-000000000000";

    [Theory]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "--startup" }, false)]
    [InlineData(new[] { "--list" }, true)]
    [InlineData(new[] { "--whatever" }, true)]
    public void CliModeDetection(string[] args, bool expected) => Assert.Equal(expected, Cli.IsCliInvocation(args));

    [Fact]
    public void ParsesSnapshotWithPositionalNoteAndName()
    {
        var o = Cli.Parse(new[] { "--snapshot", "waiting on results", "--name", "Monday", "--json" });
        Assert.Equal("--snapshot", o.Command);
        Assert.Equal("waiting on results", o.Note);
        Assert.Equal("Monday", o.Name);
        Assert.True(o.Json);

        var bare = Cli.Parse(new[] { "--snapshot" });
        Assert.Null(bare.Note);
        Assert.Equal("--snapshot", Cli.Parse(new[] { "--snapshot", "--note", "x" }).Command);
    }

    [Fact]
    public void ParsesRecentDaysAndAliases()
    {
        Assert.Equal(3, Cli.Parse(new[] { "--recent", "--days", "3" }).Days);
        Assert.Equal("--help", Cli.Parse(new[] { "-h" }).Command);
        Assert.Equal("--list", Cli.Parse(new[] { "--LIST" }).Command);
    }

    [Theory]
    [InlineData("--list", "--snapshot")]
    [InlineData("--recent", "--days", "0")]
    [InlineData("--recent", "--days", "abc")]
    [InlineData("--list", "--name", "x")]
    [InlineData("--snapshot", "--name")]
    [InlineData("--frobnicate")]
    [InlineData("--json")]
    public void RejectsBadUsage(params string[] args) => Assert.Throws<CliUsageException>(() => Cli.Parse(args));

    private static RunningEntry Entry(int pid, RunState state, string? id, string? dir, bool hasTranscript = true) =>
        new(new RunningSession { Pid = pid, State = state, SessionId = id, Cwd = dir },
            hasTranscript && id != null
                ? new SessionInfo { SessionId = id, FilePath = "x", ProjectDir = dir, Title = "T" + pid, LastPrompt = "p" + pid, LastActive = DateTimeOffset.Now }
                : null,
            FolderMissing: false);

    [Fact]
    public void BuildSnapshotUsesSaveDialogRules()
    {
        var running = new[]
        {
            Entry(1, RunState.Working, Id1, @"C:\a"),
            Entry(2, RunState.Idle, Id1, @"C:\a"),                       // same session open twice: once
            Entry(3, RunState.Idle, Id2, @"C:\b", hasTranscript: false), // no messages yet: fresh session
            Entry(4, RunState.Unmatched, null, @"C:\c"),                 // skipped
            Entry(5, RunState.Starting, null, @"C:\d"),                  // skipped
            Entry(6, RunState.Unmatched, null, null),                    // skipped, folder unknown
        };
        var (snap, skipped) = SessionQueries.BuildSnapshot(running, "Name", "  a note  ", DateTimeOffset.Now);
        Assert.Equal("a note", snap.Note);
        Assert.Equal(2, snap.Sessions.Count);
        Assert.Equal(Id1, snap.Sessions[0].SessionId);
        Assert.Null(snap.Sessions[1].SessionId);
        Assert.Equal(@"C:\b", snap.Sessions[1].ProjectDir);
        Assert.Equal(3, skipped.Count);
    }

    // ------------------------------------------------------------------ end to end against the built exe

    private static string ExePath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "SessionKeeper.exe");
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SessionKeeper.sln"))) dir = dir.Parent;
        var config = AppContext.BaseDirectory.Contains(@"\Release\") ? "Release" : "Debug";
        return Path.Combine(dir!.FullName, "src", "SessionKeeper", "bin", config, "net8.0-windows", "win-x64", "SessionKeeper.exe");
    }

    private static (int Code, string Out, string Err) RunExe(params string[] args)
    {
        var psi = new ProcessStartInfo(ExePath())
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "SessionKeeper did not exit");
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    [Fact]
    public void ExeHelpVersionAndUsageErrors()
    {
        var help = RunExe("--help");
        Assert.Equal(0, help.Code);
        Assert.Contains("--snapshot", help.Out);

        var version = RunExe("--version");
        Assert.Equal(0, version.Code);
        Assert.StartsWith("SessionKeeper ", version.Out);

        var bad = RunExe("--nope");
        Assert.Equal(2, bad.Code);
        Assert.Contains("Unknown option", bad.Err);
    }

    [Fact]
    public void ExeListJsonIsValid()
    {
        var r = RunExe("--list", "--json");
        Assert.Equal(0, r.Code);
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
    }
}
