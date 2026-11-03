using SessionKeeper.Models;
using SessionKeeper.ViewModels;

namespace SessionKeeper.Tests;

public sealed class DisplayTests
{
    private const string Id1 = "aaaa1111-0000-4000-8000-000000000001";
    private const string Id2 = "bbbb2222-0000-4000-8000-000000000002";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Continuing previous session")]
    [InlineData("continuing previous session.")]
    [InlineData("Continuation of previous session")]
    [InlineData("Resuming previous conversation")]
    [InlineData("New session")]
    [InlineData("Untitled")]
    [InlineData("Claude Code session")]
    public void GenericTitlesAreDetected(string? title) => Assert.True(Fmt.IsGenericTitle(title));

    [Theory]
    [InlineData("Photo viewer and manager")]
    [InlineData("Continuing the README rewrite")]       // starts like a generic title but names the work
    [InlineData("Session restore bug in SessionKeeper")]
    [InlineData("New session picker design")]
    public void SpecificTitlesAreKept(string title) => Assert.False(Fmt.IsGenericTitle(title));

    [Fact]
    public void TitleIsMainLabelWithFolderAsSecondary()
    {
        Assert.Equal("Fix the login form", Fmt.MainLabel("Fix the login form", @"C:\Projects\notes-app"));
        Assert.Equal("notes-app", Fmt.SecondaryText("Fix the login form", @"C:\Projects\notes-app"));
    }

    [Fact]
    public void GenericMissingOrFolderNamedTitlesFallBackToTheFolder()
    {
        foreach (var t in new[] { "Continuing previous session", null, "my-app" })
        {
            Assert.Equal("my-app", Fmt.MainLabel(t, @"C:\Projects\my-app"));
            Assert.Equal(@"C:\Projects\my-app", Fmt.SecondaryText(t, @"C:\Projects\my-app")); // no repeat of the label
        }
        Assert.Equal("(unknown folder)", Fmt.MainLabel(null, null));
        Assert.Equal("", Fmt.SecondaryText(null, null));
    }

    [Fact]
    public void TooltipsTellLookAlikeRowsApartByShortId()
    {
        RecentRow Row(string id) => new()
        {
            Info = new SessionInfo { SessionId = id, FilePath = "f", ProjectDir = @"C:\Projects\shared-project", Title = "Continuing previous session", LastActive = DateTimeOffset.Now },
        };
        var a = Row(Id1);
        var b = Row(Id2);
        Assert.Equal(a.MainLabel, b.MainLabel); // identical-looking rows...
        Assert.Contains("Session aaaa1111", a.Tooltip);
        Assert.Contains("Session bbbb2222", b.Tooltip);
        Assert.NotEqual(a.Tooltip.Split('\n')[1], b.Tooltip.Split('\n')[1]); // ...differ on the tooltip's ID line

        var current = new CurrentRow(new RunningSession { Pid = 1, SessionId = Id1, Cwd = @"C:\x", State = RunState.Idle },
            new SessionInfo { SessionId = Id1, FilePath = "f", ProjectDir = @"C:\x", Title = "Fix login", LastActive = DateTimeOffset.Now }, false);
        Assert.Equal("Fix login", current.MainLabel);
        Assert.StartsWith("Fix login\nSession aaaa1111", current.Tooltip);

        var snap = new SnapshotSessionRow { Session = new SnapshotSession { SessionId = Id2, ProjectDir = @"C:\y", Title = "Tidy docs", Summary = "last ask" } };
        Assert.Equal("Tidy docs", snap.MainLabel);
        Assert.Equal("last ask", snap.Summary); // title no longer repeated in the summary line
        Assert.Contains("Session bbbb2222", snap.Tooltip);
    }
}
