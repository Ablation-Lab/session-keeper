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

    [Fact]
    public void TheTimedRefreshUpdatesTheRecentListInPlace()
    {
        const string C = "cccc3333-0000-4000-8000-000000000003";
        var t = new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        SessionInfo Info(string id, string title, int minutes) =>
            new() { SessionId = id, FilePath = "f", ProjectDir = @"C:\Projects\demo-app", Title = title, LastActive = t.AddMinutes(minutes) };
        RecentRow Row(SessionInfo info, bool running = false) => new() { Info = info, IsRunning = running };

        var rows = new System.Collections.ObjectModel.ObservableCollection<RecentRow> { Row(Info(A, "a", 2)), Row(Info(B, "b", 1)) };
        var subscribed = new HashSet<RecentRow>(rows);
        int resets = 0;
        rows.CollectionChanged += (_, e) => { if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset) resets++; };
        var keptA = rows[0];
        var oldB = rows[1];
        oldB.IsChecked = true;

        // A unchanged but now running; B has new activity (moves to the top); C is new.
        RecentRow.Sync(rows, new[] { Row(Info(B, "b", 5)), Row(Info(C, "c", 4)), Row(Info(A, "a", 2), running: true) },
            added: r => Assert.True(subscribed.Add(r)), removed: r => Assert.True(subscribed.Remove(r)));

        Assert.Equal(new[] { B, C, A }, rows.Select(r => r.Info.SessionId));
        Assert.Same(keptA, rows[2]);             // unchanged row keeps its object...
        Assert.True(keptA.IsRunning);            // ...with the badge updated
        Assert.NotSame(oldB, rows[0]);           // changed row replaced...
        Assert.True(rows[0].IsChecked);          // ...keeping its tick
        Assert.Equal(t.AddMinutes(5), rows[0].Info.LastActive);
        Assert.Equal(rows.ToHashSet(), subscribed); // every row in the list is subscribed, no stale ones
        Assert.Equal(0, resets);                    // never a full reset (no flicker or scroll jump)

        RecentRow.Sync(rows, new[] { Row(Info(C, "c", 4)) }, r => subscribed.Add(r), r => subscribed.Remove(r));
        Assert.Equal(C, Assert.Single(rows).Info.SessionId); // closed-out sessions leave the list
        Assert.Equal(rows.ToHashSet(), subscribed);
    }
}
