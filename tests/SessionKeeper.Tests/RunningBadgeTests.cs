using System.ComponentModel;
using SessionKeeper.Models;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper.Tests;

public sealed class RunningBadgeTests
{
    private const string A = "aaaa1111-0000-4000-8000-000000000001";
    private const string B = "bbbb2222-0000-4000-8000-000000000002";

    [Fact]
    public void JustLaunchedSessionsCountAsRunningUntilTheyRegister()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var grace = new LaunchGrace(TimeSpan.FromSeconds(60), () => now);
        grace.Launched(A);

        Assert.Contains(A, grace.Apply(Array.Empty<string>())); // not registered yet: still covered

        now = now.AddSeconds(4);
        Assert.Contains(A, grace.Apply(new[] { A }));           // registered: the scan reports it
        Assert.Equal(0, grace.PendingCount);                    // ...and the grace stops covering it

        now = now.AddSeconds(3);                                // window closed with X a few seconds later
        Assert.DoesNotContain(A, grace.Apply(Array.Empty<string>())); // gone at the next refresh, not after 60-90 s
    }

    [Fact]
    public void GraceExpiresIfASessionNeverRegisters()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);
        var grace = new LaunchGrace(TimeSpan.FromSeconds(60), () => now);
        grace.Launched(B);
        now = now.AddSeconds(59);
        Assert.Contains(B, grace.Apply(Array.Empty<string>()));
        now = now.AddSeconds(2);
        Assert.DoesNotContain(B, grace.Apply(Array.Empty<string>()));
        Assert.Equal(0, grace.PendingCount);
    }

    [Fact]
    public void BadgesUpdateInPlace()
    {
        var recent = new RecentRow { Info = new SessionInfo { SessionId = A, FilePath = "f", ProjectDir = @"C:\x", LastActive = DateTimeOffset.Now }, IsRunning = true };
        var changed = new List<string?>();
        ((INotifyPropertyChanged)recent).PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        recent.IsRunning = false;
        Assert.Contains(nameof(RecentRow.RunningVisibility), changed);
        Assert.Contains(nameof(RecentRow.Tooltip), changed);
        Assert.Equal(System.Windows.Visibility.Collapsed, recent.RunningVisibility);
        changed.Clear();
        recent.IsRunning = false; // no change, no notification
        Assert.Empty(changed);

        var snap = new SnapshotSessionRow { Session = new SnapshotSession { SessionId = A, ProjectDir = @"C:\x" }, IsRunning = true };
        var snapChanged = new List<string?>();
        ((INotifyPropertyChanged)snap).PropertyChanged += (_, e) => snapChanged.Add(e.PropertyName);
        snap.IsRunning = false;
        Assert.Contains(nameof(SnapshotSessionRow.RunningVisibility), snapChanged);
    }
}
