using System.Diagnostics;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

public sealed class BuildInfoTests
{
    private static Dictionary<string, string?> Meta(params (string, string?)[] kv) => kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void ParsesEmbeddedValuesAndFormatsTheFooter()
    {
        var b = BuildInfo.FromValues("1.2.3+deadbeef", Meta(
            ("CommitHash", "a1b2c3d4e5f60718293a4b5c6d7e8f9012345678"),
            ("BuildDate", "2026-11-03T14:02:00.0000000Z"),
            ("SourceRepo", @"C:\Src\app")));
        Assert.Equal("1.2.3", b.Version);
        Assert.Equal("a1b2c3d", b.ShortCommit);
        Assert.Equal(@"C:\Src\app", b.SourceRepo);
        Assert.False(b.SourceDirty);
        Assert.Equal("v1.2.3 \u00B7 build a1b2c3d \u00B7 3 Nov 2026", b.FooterText);
    }

    [Fact]
    public void ARepoUnderTheProfileIsEmbeddedWithoutTheUserName()
    {
        Assert.Equal(@"D:\Home\someone\src\app", BuildInfo.ExpandRepo(@"%USERPROFILE%\src\app", @"D:\Home\someone\"));
        Assert.Equal(@"C:\Src\app", BuildInfo.ExpandRepo(@"C:\Src\app", @"D:\Home\someone"));
        Assert.Equal(@"C:\%OTHER%\app", BuildInfo.ExpandRepo(@"C:\%OTHER%\app", @"D:\Home\someone")); // only the one token
        var embedded = BuildInfo.FromValues("1.0.0", Meta(("SourceRepo", @"%USERPROFILE%\src\app")));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "src", "app"), embedded.SourceRepo);
    }

    [Fact]
    public void MissingOrUntrustedValuesAreLeftOut()
    {
        var b = BuildInfo.FromValues("1.0.0", Meta(("CommitHash", "not-a-hash; rm -rf"), ("BuildDate", "garbage"), ("SourceDirty", "true")));
        Assert.Null(b.Commit);
        Assert.Null(b.BuildDate);
        Assert.Equal("v1.0.0", b.FooterText);
        Assert.Equal("v1.0.0 \u00B7 build abcdef0+", (b with { Commit = "abcdef0123" }).FooterText); // dirty builds get a "+"
    }

    [Fact]
    public void TheAppCarriesAVersion() =>
        Assert.Matches(@"^\d+\.\d+\.\d+", BuildInfo.Current.Version);
}

[Collection(nameof(LaunchGuardTests))] // shares the static log-folder override
public sealed class UpdateCheckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sk-repo-" + Guid.NewGuid().ToString("N"));
    private readonly string _logs;
    private readonly string? _git = LocalRepoUpdateSource.FindGit();

    public UpdateCheckTests()
    {
        _logs = Path.Combine(_root, "logs");
        LaunchGuard.LogDirOverride = _logs;
    }

    public void Dispose()
    {
        LaunchGuard.LogDirOverride = null;
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
        catch { }
    }

    // ------------------------------------------------------------------ allowlist

    [Theory]
    [InlineData("rev-list", "--count", "abc1234..HEAD", "--", "src/SessionKeeper")]
    [InlineData("rev-parse", "--short", "HEAD")]
    public void AllowsOnlyReadOnlyQueries(string command, params string[] args) =>
        Assert.Null(LaunchGuard.CheckReadOnlyGit(@"C:\Git\cmd\git.exe", @"C:\Src\app", command, args));

    [Theory]
    [InlineData(@"C:\Git\cmd\git.exe", "pull")]
    [InlineData(@"C:\Git\cmd\git.exe", "commit", "-m", "x")]
    [InlineData(@"C:\Git\cmd\git.exe", "rev-list", "--output=x")]
    [InlineData(@"C:\Git\cmd\git.exe", "rev-list", "-c", "core.pager=calc")]
    [InlineData(@"C:\Git\cmd\git.exe", "rev-list", "HEAD", "--", "../outside")]
    [InlineData("git.exe", "rev-parse", "HEAD")]
    [InlineData(@"C:\Windows\System32\cmd.exe", "rev-parse", "HEAD")]
    public void RefusesAnythingElse(string exe, string command, params string[] args) =>
        Assert.NotNull(LaunchGuard.CheckReadOnlyGit(exe, @"C:\Src\app", command, args));

    // ------------------------------------------------------------------ against a real throwaway repository

    private string Git(params string[] args)
    {
        var psi = new ProcessStartInfo(_git!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = _root,
        };
        foreach (var a in new[] { "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.autocrlf=false" }.Concat(args))
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.Equal(0, p.ExitCode);
        return output.Trim();
    }

    private string Commit(string path, string text)
    {
        var full = Path.Combine(_root, path.Replace('/', '\\'));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        Git("add", "-A");
        Git("commit", "-q", "-m", "change " + path);
        return Git("rev-parse", "HEAD");
    }

    private static BuildInfo Build(string? commit) => new("1.0.0", commit, DateTimeOffset.Now, null, false);

    [SkippableFact]
    public void CountsOnlyNewerCommitsThatTouchTheAppSource()
    {
        Skip.If(_git == null, "git is not installed: nothing to test against.");
        Directory.CreateDirectory(_root);
        Git("init", "-q");
        var built = Commit("src/SessionKeeper/a.cs", "1");
        Commit("README.md", "docs only");              // not app source: ignored
        Commit("src/SessionKeeper/a.cs", "2");
        var head = Commit("src/SessionKeeper/Sub/b.cs", "3");

        var source = new LocalRepoUpdateSource(_root, _git);
        var status = source.Check(Build(built), CancellationToken.None);
        Assert.NotNull(status);
        Assert.Equal(UpdateState.UpdateAvailable, status!.State);
        Assert.Equal(2, status.NewerCommits);
        Assert.Contains("2 newer commits", status.Detail);

        Assert.Equal(UpdateState.UpToDate, source.Check(Build(head), CancellationToken.None)!.State);
        Assert.Null(source.Check(Build("0123456789abcdef0123456789abcdef01234567"), CancellationToken.None)); // unknown commit: quiet
        Assert.Null(source.Check(Build(null), CancellationToken.None));

        var log = string.Concat(Directory.GetFiles(_logs, "launches-*.log").Select(File.ReadAllText));
        Assert.Contains("TOOL ", log);
        Assert.DoesNotContain("LAUNCH", log); // read-only queries, never a user launch
        Assert.DoesNotContain("BLOCKED", log);
    }

    [Fact]
    public void MissingRepoOrGitStaysQuiet()
    {
        Assert.Null(new LocalRepoUpdateSource(Path.Combine(_root, "nope"), _git ?? @"C:\Git\cmd\git.exe").Check(Build("abcdef0"), CancellationToken.None));
        Assert.Null(new LocalRepoUpdateSource(null, _git).Check(Build("abcdef0"), CancellationToken.None));
        Assert.Null(new LocalRepoUpdateSource(_root, null).Check(Build("abcdef0"), CancellationToken.None));
    }

    private sealed class FakeSource(Func<UpdateStatus?> run) : IUpdateSource
    {
        public string Name => "fake";
        public UpdateStatus? Check(BuildInfo build, CancellationToken ct) => run();
    }

    [Fact]
    public async Task CheckerSkipsBrokenSourcesAndGivesUpQuietly()
    {
        var ok = new UpdateStatus(UpdateState.UpToDate, 0, "ok");
        Assert.Equal(ok, await UpdateChecker.CheckAsync(Build("abcdef0"),
            new IUpdateSource[] { new FakeSource(() => throw new InvalidOperationException()), new FakeSource(() => ok) }, TimeSpan.FromSeconds(5)));

        var sw = Stopwatch.StartNew();
        var slow = await UpdateChecker.CheckAsync(Build("abcdef0"),
            new IUpdateSource[] { new FakeSource(() => { Thread.Sleep(3000); return ok; }) }, TimeSpan.FromMilliseconds(300));
        Assert.Null(slow);
        Assert.True(sw.ElapsedMilliseconds < 2000, "the checker waited past its timeout");
    }
}
