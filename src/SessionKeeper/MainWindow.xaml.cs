using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SessionKeeper.Models;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper;

public partial class MainWindow : Window
{
    private static readonly TimeSpan RecentlyLaunchedWindow = TimeSpan.FromSeconds(90);

    private readonly SessionStore _store = new();
    private readonly RunningScanner _scanner = new();
    private readonly SnapshotStore _snapshots = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

    private readonly ObservableCollection<CurrentRow> _currentRows = new();
    private HashSet<string> _runningIds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Sessions we launched recently: they may not have registered yet, so treat them as running.</summary>
    private readonly Dictionary<string, DateTime> _recentlyLaunched = new(StringComparer.OrdinalIgnoreCase);
    private List<RecentRow> _recentRows = new();
    private Task? _currentRefresh;
    private bool _recentLoaded, _launching;
    private CancellationTokenSource? _recentCts;
    private int _snapshotDetailsVersion;
    private string _currentStatus = "", _recentStatus = "", _snapshotStatus = "";
    private DateTime _lastCurrentRefresh = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        CurrentList.ItemsSource = _currentRows;
        _timer.Tick += async (_, _) => await AutoRefreshAsync();
        Loaded += async (_, _) =>
        {
            await RefreshCurrentAsync();
            _timer.Start();
        };
        Activated += async (_, _) =>
        {
            if (TabCurrent.IsChecked == true && DateTime.UtcNow - _lastCurrentRefresh > TimeSpan.FromSeconds(3))
                await RefreshCurrentAsync();
        };
        Closed += (_, _) => { _timer.Stop(); _recentCts?.Cancel(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    // ============================================================ tabs

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool cur = TabCurrent.IsChecked == true, rec = TabRecent.IsChecked == true, snap = TabSnapshots.IsChecked == true;
        PanelCurrent.Visibility = BarCurrent.Visibility = cur ? Visibility.Visible : Visibility.Collapsed;
        PanelRecent.Visibility = BarRecent.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        PanelSnapshots.Visibility = BarSnapshots.Visibility = snap ? Visibility.Visible : Visibility.Collapsed;

        if (cur) _ = RefreshCurrentAsync();
        if (rec && !_recentLoaded) _ = LoadRecentAsync();
        if (snap) _ = LoadSnapshotsAsync(refreshRunning: true);
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = TabCurrent.IsChecked == true ? _currentStatus
            : TabRecent.IsChecked == true ? _recentStatus
            : _snapshotStatus;
    }

    /// <summary>Running session ids from a fresh scan, plus sessions we launched in the last 90 s.</summary>
    private HashSet<string> WithRecentlyLaunched(IEnumerable<string> running)
    {
        var set = new HashSet<string>(running, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;
        foreach (var (id, at) in _recentlyLaunched.ToList())
        {
            if (now - at > RecentlyLaunchedWindow) _recentlyLaunched.Remove(id);
            else set.Add(id);
        }
        return set;
    }

    private async Task<HashSet<string>> ScanRunningIdsAsync()
    {
        var ids = await Task.Run(() => _scanner.Scan().Where(r => r.SessionId != null).Select(r => r.SessionId!).ToList());
        return WithRecentlyLaunched(ids);
    }

    // ============================================================ current

    private async Task AutoRefreshAsync()
    {
        // Only scan while the user can actually see the list.
        if (TabCurrent.IsChecked != true || WindowState == WindowState.Minimized || !IsVisible) return;
        await RefreshCurrentAsync();
    }

    /// <summary>Starts a refresh, or returns the one already in flight so callers always await fresh rows.</summary>
    private Task RefreshCurrentAsync()
    {
        if (_currentRefresh is { IsCompleted: false }) return _currentRefresh;
        return _currentRefresh = RefreshCurrentCoreAsync();
    }

    private async Task RefreshCurrentCoreAsync()
    {
        try
        {
            var rows = await Task.Run(() =>
            {
                var running = _scanner.Scan();
                return running.Select(r =>
                {
                    var info = r.SessionId != null ? _store.GetSession(r.SessionId, r.Cwd) : null;
                    var dir = info?.ProjectDir ?? r.Cwd;
                    return new CurrentRow(r, info, dir != null && !FolderProbe.Exists(dir));
                }).ToList();
            });

            rows = rows
                .OrderBy(r => r.Running.State is RunState.Unmatched or RunState.Starting ? 1 : 0)
                .ThenByDescending(r => r.LastActive ?? DateTimeOffset.MinValue)
                .ToList();

            SyncCurrentRows(rows);
            _runningIds = WithRecentlyLaunched(rows.Where(r => r.SessionId != null).Select(r => r.SessionId!));
            CurrentEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            SaveCurrentButton.IsEnabled = rows.Any(r => r.ProjectDir != null);

            int working = rows.Count(r => r.Running.State == RunState.Working);
            int waiting = rows.Count(r => r.Running.State == RunState.Waiting);
            int unmatched = rows.Count(r => r.Running.State == RunState.Unmatched);
            _currentStatus = $"{rows.Count} running" + (working > 0 ? $" · {working} working" : "") +
                             (waiting > 0 ? $" · {waiting} need{(waiting == 1 ? "s" : "")} input" : "") +
                             (unmatched > 0 ? $" · {unmatched} unmatched" : "") + $" · updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _currentStatus = "Refresh failed: " + ex.Message;
        }
        finally
        {
            _lastCurrentRefresh = DateTime.UtcNow;
            UpdateStatus();
        }
    }

    /// <summary>Updates the observable list in place (keyed by PID) instead of replacing it.</summary>
    private void SyncCurrentRows(List<CurrentRow> fresh)
    {
        var freshPids = fresh.Select(r => r.Running.Pid).ToHashSet();
        for (int i = _currentRows.Count - 1; i >= 0; i--)
            if (!freshPids.Contains(_currentRows[i].Running.Pid)) _currentRows.RemoveAt(i);

        for (int target = 0; target < fresh.Count; target++)
        {
            var row = fresh[target];
            int existing = -1;
            for (int j = target; j < _currentRows.Count; j++)
                if (_currentRows[j].Running.Pid == row.Running.Pid) { existing = j; break; }

            if (existing < 0) _currentRows.Insert(target, row);
            else
            {
                if (existing != target) _currentRows.Move(existing, target);
                _currentRows[target].UpdateFrom(row);
            }
        }
    }

    private async void RefreshCurrent_Click(object sender, RoutedEventArgs e) => await RefreshCurrentAsync();

    private async void SaveCurrent_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentAsync();
        await SaveSnapshotFromCurrentAsync();
    }

    // ============================================================ recent

    private TimeSpan RecentWindow =>
        Win3d.IsChecked == true ? TimeSpan.FromDays(3)
        : Win7d.IsChecked == true ? TimeSpan.FromDays(7)
        : Win30d.IsChecked == true ? TimeSpan.FromDays(30)
        : TimeSpan.FromHours(24);

    private void Window_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _ = LoadRecentAsync();
    }

    private async Task LoadRecentAsync()
    {
        _recentCts?.Cancel();
        var cts = new CancellationTokenSource();
        _recentCts = cts;
        _recentLoaded = true;

        var since = DateTimeOffset.Now - RecentWindow;
        _recentStatus = "Loading…";
        UpdateStatus();

        try
        {
            var (rows, running) = await Task.Run(() =>
            {
                var list = _store.GetRecent(since, cts.Token);
                var r = _scanner.Scan().Where(x => x.SessionId != null).Select(x => x.SessionId!).ToList();
                var missing = list.Select(i => !FolderProbe.Exists(i.ProjectDir)).ToList();
                return (list.Zip(missing).ToList(), r);
            }, cts.Token);
            if (cts != _recentCts) return;

            var runningSet = WithRecentlyLaunched(running);
            _runningIds = runningSet;
            // Read ticks now (not before the await) so boxes ticked while loading are kept.
            var keepChecked = _recentRows.Where(r => r.IsChecked).Select(r => r.Info.SessionId).ToHashSet();
            foreach (var old in _recentRows) old.PropertyChanged -= RecentRow_PropertyChanged;
            _recentRows = rows.Select(x => new RecentRow
            {
                Info = x.First,
                FolderMissing = x.Second,
                IsRunning = runningSet.Contains(x.First.SessionId),
                IsChecked = keepChecked.Contains(x.First.SessionId),
            }).ToList();
            foreach (var r in _recentRows) r.PropertyChanged += RecentRow_PropertyChanged;

            RecentList.ItemsSource = _recentRows;
            RecentEmpty.Visibility = _recentRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _recentStatus = $"{_recentRows.Count} session{(_recentRows.Count == 1 ? "" : "s")} · updated {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _recentLoaded = false; // retry next time the tab is shown
            _recentStatus = "Load failed: " + ex.Message;
        }
        UpdateRecentSelection();
        UpdateStatus();
    }

    private void RecentRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RecentRow.IsChecked)) UpdateRecentSelection();
    }

    private void UpdateRecentSelection()
    {
        int n = _recentRows.Count(r => r.IsChecked);
        RecentSelectionText.Text = n == 0 ? "Tick sessions to reopen or save" : $"{n} selected";
        ReopenButton.IsEnabled = SaveRecentButton.IsEnabled = n > 0;
        ReopenButton.Content = n > 0 ? $"Reopen selected ({n})" : "Reopen selected";
    }

    private void SelectToday_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _recentRows) r.IsChecked = r.IsToday;
    }

    private void ClearSelection_Click(object sender, RoutedEventArgs e)
    {
        foreach (var r in _recentRows) r.IsChecked = false;
    }

    // PreviewKeyDown: ListBox marks Space as handled itself, so a bubbling KeyDown never sees it.
    private void RecentList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && RecentList.SelectedItem is RecentRow row)
        {
            row.IsChecked = !row.IsChecked;
            e.Handled = true;
        }
    }

    private async void RefreshRecent_Click(object sender, RoutedEventArgs e) => await LoadRecentAsync();

    private async void Reopen_Click(object sender, RoutedEventArgs e)
    {
        var items = _recentRows.Where(r => r.IsChecked)
            .Select(r => new LaunchItem(r.Info.ProjectDir ?? "", r.Info.SessionId, r.FolderName))
            .ToList();
        await ReopenAsync(items);
    }

    private void SaveRecent_Click(object sender, RoutedEventArgs e)
    {
        var candidates = _recentRows.Where(r => r.IsChecked && r.Info.ProjectDir != null).Select(r => new SaveCandidate
        {
            SessionId = r.Info.SessionId,
            ProjectDir = r.Info.ProjectDir!,
            Title = r.Info.Title,
            Summary = r.Info.LastPrompt ?? r.Info.FirstPrompt,
            LastActive = r.Info.LastActive,
            IsRunning = r.IsRunning,
            IsChecked = true,
        }).ToList();
        ShowSaveDialog(candidates);
    }

    // ============================================================ snapshots

    private async Task LoadSnapshotsAsync(string? selectPath = null, bool refreshRunning = false)
    {
        selectPath ??= (SnapshotList.SelectedItem as SnapshotListItem)?.File.Path;
        List<SnapshotFile> files;
        bool failed = false;
        try { files = _snapshots.List(); }
        catch (Exception ex)
        {
            App.Log(ex);
            files = new();
            failed = true;
            _snapshotStatus = "Could not read snapshots: " + ex.Message;
        }
        var items = files.Select(f => new SnapshotListItem { File = f }).ToList();
        SnapshotList.ItemsSource = items;
        SnapshotsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotList.SelectedItem = items.FirstOrDefault(i => string.Equals(i.File.Path, selectPath, StringComparison.OrdinalIgnoreCase))
                                    ?? items.FirstOrDefault();
        if (!failed) _snapshotStatus = $"{items.Count} snapshot{(items.Count == 1 ? "" : "s")}";
        UpdateStatus();

        if (refreshRunning)
        {
            try { _runningIds = await ScanRunningIdsAsync(); }
            catch (Exception ex) { App.Log(ex); }
        }
        await ShowSnapshotDetailsAsync();
    }

    private async void SnapshotList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        await ShowSnapshotDetailsAsync();

    private async Task ShowSnapshotDetailsAsync()
    {
        int version = ++_snapshotDetailsVersion;
        var item = SnapshotList.SelectedItem as SnapshotListItem;
        var snap = item?.File.Snapshot;
        RenameButton.IsEnabled = snap != null;
        DeleteButton.IsEnabled = item != null;
        RestoreButton.IsEnabled = snap is { Sessions.Count: > 0 };

        if (item == null || snap == null)
        {
            SnapshotTitle.Text = item == null ? " " : Path.GetFileName(item.File.Path);
            SnapshotMeta.Text = item?.File.Error ?? " ";
            SnapshotSessions.ItemsSource = null;
            return;
        }

        SnapshotTitle.Text = snap.Name;
        var baseMeta = $"Saved {Fmt.When(snap.CreatedAt)} · {snap.Sessions.Count} session{(snap.Sessions.Count == 1 ? "" : "s")}";
        SnapshotMeta.Text = baseMeta;
        var running = _runningIds;
        var sessions = snap.Sessions.ToList();
        // Folder checks can block on unreachable network paths: do them off the UI thread.
        var rows = await Task.Run(() => sessions.Select(s => new SnapshotSessionRow
        {
            Session = s,
            FolderMissing = !FolderProbe.Exists(s.ProjectDir),
            IsRunning = s.SessionId != null && running.Contains(s.SessionId),
        }).ToList());
        if (version != _snapshotDetailsVersion) return; // selection changed meanwhile

        int missing = rows.Count(r => r.FolderMissing);
        SnapshotMeta.Text = baseMeta + (missing > 0 ? $" · {missing} folder{(missing == 1 ? "" : "s")} missing" : "");
        SnapshotSessions.ItemsSource = rows;
    }

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentAsync();
        await SaveSnapshotFromCurrentAsync();
    }

    private async void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if ((SnapshotList.SelectedItem as SnapshotListItem)?.File.Snapshot is not { } snap) return;
        var items = snap.Sessions
            .Select(s => new LaunchItem(s.ProjectDir ?? "", SessionStore.IsValidSessionId(s.SessionId) ? s.SessionId : null,
                                        Fmt.FolderName(s.ProjectDir)))
            .ToList();
        await ReopenAsync(items);
    }

    private async void RenameSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotListItem { File.Snapshot: { } snap } item) return;
        var dlg = new InputDialog("Rename snapshot", "New name:", snap.Name) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value) || dlg.Value.Trim() == snap.Name) return;
        string? path = null;
        try
        {
            path = _snapshots.Rename(item.File, dlg.Value);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Could not rename the snapshot:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await LoadSnapshotsAsync(path);
    }

    private async void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotListItem item) return;
        if (MessageBox.Show(this, $"Delete the snapshot \"{item.Name}\"?\n\nThe file goes to the Recycle Bin. Claude Code's own sessions are not affected.",
                "Delete snapshot", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK)
            return;
        try
        {
            _snapshots.Delete(item.File);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Could not delete the snapshot:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await LoadSnapshotsAsync();
    }

    private void OpenSnapshotFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_snapshots.Dir);
            // Full path: a bare "explorer.exe" is searched for in the app and current folders first.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = Process.Start(new ProcessStartInfo(explorer, Launcher.WinQuote(_snapshots.Dir)) { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    // ============================================================ save dialog

    private async Task SaveSnapshotFromCurrentAsync()
    {
        // Running sessions pre-ticked, plus the last 24h of other sessions unticked.
        var candidates = new List<SaveCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in _currentRows.ToList())
        {
            if (row.ProjectDir == null) continue;
            var id = row.Info != null ? row.SessionId : null; // no transcript yet => restore as a fresh session
            if (id != null && !seen.Add(id)) continue;
            bool confident = row.Running.State is not (RunState.Unmatched or RunState.Starting);
            candidates.Add(new SaveCandidate
            {
                SessionId = id,
                ProjectDir = row.ProjectDir,
                Title = row.Info?.Title,
                Summary = row.Info?.LastPrompt,
                LastActive = row.LastActive,
                IsRunning = true,
                // Unmatched/starting rows have no session id, so restoring them would start a *new*
                // conversation; leave them for the user to tick deliberately.
                IsChecked = confident,
            });
        }

        try
        {
            var recent = await Task.Run(() => _store.GetRecent(DateTimeOffset.Now - TimeSpan.FromHours(24)));
            foreach (var info in recent)
            {
                if (info.ProjectDir == null || !seen.Add(info.SessionId)) continue;
                candidates.Add(new SaveCandidate
                {
                    SessionId = info.SessionId,
                    ProjectDir = info.ProjectDir,
                    Title = info.Title,
                    Summary = info.LastPrompt ?? info.FirstPrompt,
                    LastActive = info.LastActive,
                });
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        ShowSaveDialog(candidates);
    }

    private async void ShowSaveDialog(List<SaveCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            MessageBox.Show(this, "There are no sessions to save.", "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveSnapshotWindow(candidates) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        try
        {
            var path = _snapshots.Save(dlg.Result);
            TabSnapshots.IsChecked = true; // switches tab
            await LoadSnapshotsAsync(path);
            _snapshotStatus = $"Saved \"{dlg.Result.Name}\"";
            UpdateStatus();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Could not save the snapshot:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ============================================================ reopen

    private async Task ReopenAsync(List<LaunchItem> items)
    {
        if (items.Count == 0 || _launching) return;
        _launching = true;
        try
        {
            var running = await ScanRunningIdsAsync();
            var preSkipped = new List<string>();

            var already = items.Where(i => i.SessionId != null && running.Contains(i.SessionId)).ToList();
            if (already.Count > 0)
            {
                var names = string.Join("\n", already.Take(12).Select(a => "  • " + a.Label));
                var answer = MessageBox.Show(this,
                    $"{already.Count} of these sessions {(already.Count == 1 ? "is" : "are")} already open (or just opened):\n\n{names}\n\n" +
                    "Opening a session twice means two terminals writing to the same conversation.\n\n" +
                    "Skip the ones that are already open?\n\nYes = skip them (recommended)\nNo = open them anyway",
                    "Already running", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (answer == MessageBoxResult.Cancel) return;
                if (answer == MessageBoxResult.Yes)
                {
                    items = items.Except(already).ToList();
                    preSkipped.AddRange(already.Select(a => a.Label + ": already open"));
                }
            }

            // The transcript must still exist and belong to this folder, or `claude --resume` would fail
            // (or resume in the wrong place).
            var checks = await Task.Run(() => items.Select(it =>
            {
                if (it.SessionId == null) return (it, (string?)null);
                var file = _store.FindFile(it.SessionId, it.ProjectDir);
                if (file == null) return (it, "conversation file not found");
                var info = _store.GetSessionFromFile(file);
                if (info?.ProjectDir != null && !string.Equals(info.ProjectDir.TrimEnd('\\'), it.ProjectDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return (it, "conversation belongs to a different folder (" + info.ProjectDir + ")");
                return (it, null);
            }).ToList());
            var ready = new List<LaunchItem>();
            foreach (var (it, why) in checks)
            {
                if (why != null) preSkipped.Add(it.Label + ": " + why);
                else ready.Add(it);
            }

            if (ready.Count > 10 &&
                MessageBox.Show(this, $"Open {ready.Count} terminal tabs?", "SessionKeeper", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            var result = ready.Count > 0 ? await Task.Run(() => Launcher.Launch(ready)) : new LaunchResult();
            foreach (var it in result.OpenedItems.Where(r => r.SessionId != null))
                _recentlyLaunched[it.SessionId!] = DateTime.UtcNow;
            result.Skipped.InsertRange(0, preSkipped);
            ReportLaunch(result);

            if (TabRecent.IsChecked == true) await LoadRecentAsync(); // show "running" badges straight away
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Could not open sessions:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _launching = false;
        }
    }

    private void ReportLaunch(LaunchResult r)
    {
        var where = r.UsedPowerShellWindows ? "PowerShell window" : "Windows Terminal tab";
        int n = r.OpenedItems.Count;
        var opened = n == 0 ? "Nothing opened" : $"Opened {n} {where}{(n == 1 ? "" : "s")}";
        var status = opened + (r.Skipped.Count > 0 ? $" · skipped {r.Skipped.Count}" : "");
        _currentStatus = _recentStatus = _snapshotStatus = status;
        UpdateStatus();

        var lines = new List<string>();
        if (r.Error != null) lines.Add("Error: " + r.Error);
        if (r.Skipped.Count > 0) lines.Add("Skipped:\n" + string.Join("\n", r.Skipped.Select(s => "  • " + s)));
        if (r.ClaudeNotFound && n > 0) lines.Add("Note: claude.exe was not found in the usual places, so the tabs run \"claude\" from PATH.");
        if (r.UsedPowerShellWindows && n > 0) lines.Add("Windows Terminal was not found, so separate PowerShell windows were used.");
        if (lines.Count > 0)
            MessageBox.Show(this, opened + ".\n\n" + string.Join("\n\n", lines), "SessionKeeper", MessageBoxButton.OK,
                r.Error != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
