using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SessionKeeper.Models;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper.Tests;

public sealed class NamingTests : IDisposable
{
    private const string A = "aaaa1111-0000-4000-8000-000000000001";
    private const string B = "bbbb2222-0000-4000-8000-000000000002";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sk-names-" + Guid.NewGuid().ToString("N"));

    public NamingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ------------------------------------------------------------------ nickname store

    [Fact]
    public void NicknamesPersistByIdAndCanBeCleared()
    {
        var path = Path.Combine(_dir, "nicknames.json");
        var store = new NicknameStore(path);
        Assert.Null(store.Get(A));
        Assert.Equal("Login fix", store.Set(A, "  Login   fix "));
        store.Set(B, "Docs");

        var reopened = new NicknameStore(path); // read back from disk
        Assert.Equal("Login fix", reopened.Get(A));
        Assert.Equal("Docs", reopened.Get(B));
        Assert.Contains("Login fix", File.ReadAllText(path)); // readable JSON

        reopened.Clear(A);
        Assert.Null(new NicknameStore(path).Get(A));
        Assert.Null(reopened.Set(B, "   ")); // empty name = clear
        Assert.Equal(0, new NicknameStore(path).Count);

        Assert.Throws<ArgumentException>(() => store.Set("not-a-guid", "x"));
        Assert.Null(store.Get("../../etc"));
    }

    [Fact]
    public void NamesAreCleanedAndCapped()
    {
        Assert.Equal("a b", NicknameStore.Clean("a\r\n\tb"));
        Assert.Equal("ab", NicknameStore.Clean("a\u202Eb")); // invisible right-to-left override dropped
        Assert.Null(NicknameStore.Clean(" \0 "));
        Assert.Equal(NicknameStore.MaxLength, NicknameStore.Clean(new string('x', 500))!.Length);
    }

    [Fact]
    public void ACorruptFileStartsEmptyInsteadOfCrashing()
    {
        var path = Path.Combine(_dir, "nicknames.json");
        File.WriteAllText(path, "{ not json");
        var store = new NicknameStore(path);
        Assert.Null(store.Get(A));
        store.Set(A, "fresh"); // and can be written again
        Assert.Equal("fresh", new NicknameStore(path).Get(A));
    }

    // ------------------------------------------------------------------ labels

    private static SessionInfo Info(string id, string? title) =>
        new() { SessionId = id, FilePath = "f", ProjectDir = @"C:\Projects\notes-app", Title = title, LastActive = DateTimeOffset.Now };

    [Fact]
    public void NicknameIsTheMainLabelAndTheTitleMovesToTheTooltip()
    {
        var row = new RecentRow { Info = Info(A, "Fix the login form"), Nickname = "Login fix" };
        Assert.Equal("Login fix", row.MainLabel);
        Assert.Equal("notes-app", row.SecondaryText);
        Assert.StartsWith("Login fix\nTitle: Fix the login form\nSession aaaa1111", row.Tooltip);
        Assert.Equal("Login fix", row.ToLaunchItem()!.Title);

        row.Nickname = null; // cleared: back to the auto title
        Assert.Equal("Fix the login form", row.MainLabel);
        Assert.Null(row.ToLaunchItem()!.Title);
    }

    [Fact]
    public void SnapshotRowsFallBackToTheNameSavedWithTheSnapshot()
    {
        var saved = new SnapshotSession { SessionId = A, ProjectDir = @"C:\x", Title = "Auto title", Nickname = "Saved name" };
        var row = new SnapshotSessionRow { Session = saved };
        Assert.Equal("Saved name", row.MainLabel);
        row.Nickname = "Live name";
        Assert.Equal("Live name", row.MainLabel);
        Assert.Equal("Live name", row.ToLaunchItem()!.Title);
        Assert.Null(new SnapshotSessionRow { Session = new SnapshotSession { ProjectDir = @"C:\x" } }.NameableSessionId); // no ID: can't be named
    }

    [Fact]
    public void CurrentRowsCarryTheNameAcrossRefreshes()
    {
        var running = new RunningSession { Pid = 1, SessionId = A, Cwd = @"C:\x", State = RunState.Idle };
        var row = new CurrentRow(running, Info(A, "Auto"), false, "Named");
        Assert.Equal("Named", row.MainLabel);
        row.UpdateFrom(new CurrentRow(running, Info(A, "Auto"), false, null));
        Assert.Equal("Auto", row.MainLabel);
        Assert.Null(new CurrentRow(new RunningSession { Pid = 2, State = RunState.Unmatched }, null, false).NameableSessionId);
    }

    [Fact]
    public void SnapshotsIncludeTheNickname()
    {
        var entry = new RunningEntry(new RunningSession { Pid = 1, SessionId = A, Cwd = @"C:\x", State = RunState.Idle }, Info(A, "t"), false);
        var (snap, _) = SessionQueries.BuildSnapshot(new[] { entry }, "n", null, DateTimeOffset.Now, id => id == A ? "My name" : null);
        Assert.Equal("My name", Assert.Single(snap.Sessions).Nickname);
    }

    // ------------------------------------------------------------------ launch titles (escaping)

    [Fact]
    public void ANicknameTitlesTheWindowAndKeepsClaudeFromReplacingIt()
    {
        var hostile = "it's \"q\" ; $(calc) & `n %OS% \u2019x\u2019";
        var script = Launcher.BuildScript(new LaunchItem(@"C:\x", A, "x", hostile), "claude");
        Assert.Contains("try { $Host.UI.RawUI.WindowTitle = " + Launcher.PsQuote(hostile) + " } catch { };", script);
        Assert.Contains("$env:CLAUDE_CODE_DISABLE_TERMINAL_TITLE = '1';", script);

        var plain = Launcher.BuildScript(new LaunchItem(@"C:\x", A, "x"), "claude");
        Assert.DoesNotContain("WindowTitle", plain); // no nickname: Claude keeps its own live title
        Assert.DoesNotContain("DISABLE_TERMINAL_TITLE", plain);

        var tab = Launcher.BuildTabArgs(new LaunchItem(@"C:\x", A, "x", hostile), "claude");
        Assert.Contains("--suppressApplicationTitle", tab);
        var title = tab.Split(" --suppressApplicationTitle")[0]["new-tab --title ".Length..];
        Assert.DoesNotContain(";", title);
        Assert.DoesNotContain("%", title);
        Assert.DoesNotContain("$", title);
        Assert.Equal(2, title.Count(c => c == '"')); // only WinQuote's own pair; the name's quotes are stripped
        Assert.DoesNotContain("--suppressApplicationTitle", Launcher.BuildTabArgs(new LaunchItem(@"C:\x", A, "x"), "claude"));
    }

    /// <summary>Runs the generated script in real PowerShell with a fake claude: a hostile name is just a title.</summary>
    [Fact]
    public void HostileNicknamesRunAsPlainText()
    {
        var fake = Path.Combine(_dir, "fake claude.cmd");
        File.WriteAllText(fake, "@echo ARGS=%*\r\n@echo TITLE_OFF=%CLAUDE_CODE_DISABLE_TERMINAL_TITLE%\r\n");
        var nick = "it's; $(Set-Content pwned.txt x) & echo \u2018hi\u2019";
        var script = Launcher.BuildScript(new LaunchItem(_dir, A, "x", nick), fake);
        var psi = new ProcessStartInfo(Launcher.PowerShellPath, "-NoProfile -NonInteractive -EncodedCommand " + Launcher.Encode(script))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = _dir,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("ARGS=--resume " + A, stdout);
        Assert.Contains("TITLE_OFF=1", stdout);
        Assert.False(File.Exists(Path.Combine(_dir, "pwned.txt")));
    }

    // ------------------------------------------------------------------ new session

    [Fact]
    public void NewSessionNameAttachesToTheSessionThatAppearsInThatFolder()
    {
        var launched = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        RunningSession R(string id, string cwd, int secondsAfter) =>
            new() { Pid = Math.Abs(id.GetHashCode()), SessionId = id, Cwd = cwd, State = RunState.Idle, StartedAt = launched.AddSeconds(secondsAfter) };
        const string Old = "cccc3333-0000-4000-8000-000000000003";

        var running = new[]
        {
            R(Old, @"C:\Projects\notes-app", -600),   // was already running before the launch
            R(B, @"c:\projects\NOTES-APP\", 8),       // same folder (case, trailing slash), started later
            R(A, @"C:\Projects\notes-app", 4),        // same folder, started first: this one
        };
        Assert.Equal(A, PendingNameMatcher.Find(@"C:\Projects\notes-app", launched, running, _ => false));
        Assert.Equal(B, PendingNameMatcher.Find(@"C:\Projects\notes-app", launched, running, id => id == A)); // A already named
        Assert.Null(PendingNameMatcher.Find(@"C:\Projects\other", launched, running, _ => false));
        Assert.Null(PendingNameMatcher.Find(@"C:\Projects\notes-app", launched, new[] { new RunningSession { Pid = 9, Cwd = @"C:\Projects\notes-app", State = RunState.Starting, StartedAt = launched.AddSeconds(2) } }, _ => false));
    }

    [Fact]
    public void NewSessionFolderMustBeAFullExistingPath()
    {
        Assert.NotNull(NewSessionWindow.CheckFolder("", out _));
        Assert.NotNull(NewSessionWindow.CheckFolder(@"relative\path", out _));
        Assert.NotNull(NewSessionWindow.CheckFolder(Path.Combine(_dir, "missing"), out _));
        Assert.Null(NewSessionWindow.CheckFolder("\"" + _dir + "\\\"", out var full)); // quotes and trailing slash tolerated
        Assert.Equal(_dir.TrimEnd('\\'), full);
    }
}
