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
    private readonly SessionStore _store = new();
    private readonly RunningScanner _scanner = new();
    private readonly SnapshotStore _snapshots = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

    private List<CurrentRow> _currentRows = new();
    private HashSet<string> _runningIds = new(StringComparer.OrdinalIgnoreCase);
    private List<RecentRow> _recentRows = new();
    private bool _currentBusy, _recentLoaded, _launching;
    private CancellationTokenSource? _recentCts;
    private string _currentStatus = "", _recentStatus = "", _snapshotStatus = "";
    private DateTime _lastCurrentRefresh = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
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
        if (snap) LoadSnapshots();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        StatusText.Text = TabCurrent.IsChecked == true ? _currentStatus
            : TabRecent.IsChecked == true ? _recentStatus
            : _snapshotStatus;
    }

    // ============================================================ current

    private async Task AutoRefreshAsync()
    {
        // Only scan while the user can actually see the list.
        if (TabCurrent.IsChecked != true || WindowState == WindowState.Minimized || !IsVisible) return;
        await RefreshCurrentAsync();
    }

    private async Task RefreshCurrentAsync()
    {
        if (_currentBusy) return;
        _currentBusy = true;
        try
        {
            var rows = await Task.Run(() =>
            {
                var running = _scanner.Scan();
                return running.Select(r => new CurrentRow
                {
                    Running = r,
                    Info = r.SessionId != null ? _store.GetSession(r.SessionId, r.Cwd) : null,
                }).ToList();
            });

            rows = rows
                .OrderBy(r => r.Running.State is RunState.Unmatched or RunState.Starting ? 1 : 0)
                .ThenByDescending(r => r.LastActive ?? DateTimeOffset.MinValue)
                .ToList();

            var selectedPid = (CurrentList.SelectedItem as CurrentRow)?.Running.Pid;
            _currentRows = rows;
            _runningIds = rows.Where(r => r.SessionId != null).Select(r => r.SessionId!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            CurrentList.ItemsSource = rows;
            if (selectedPid != null) CurrentList.SelectedItem = rows.FirstOrDefault(r => r.Running.Pid == selectedPid);
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
            _currentBusy = false;
            UpdateStatus();
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

        var keepChecked = _recentRows.Where(r => r.IsChecked).Select(r => r.Info.SessionId).ToHashSet();
        var since = DateTimeOffset.Now - RecentWindow;
        _recentStatus = "Loading…";
        UpdateStatus();

        try
        {
            var (list, running) = await Task.Run(() =>
            {
                var l = _store.GetRecent(since, cts.Token);
                var r = _scanner.Scan().Where(x => x.SessionId != null).Select(x => x.SessionId!)
                                .ToHashSet(StringComparer.OrdinalIgnoreCase);
                return (l, r);
            }, cts.Token);
            if (cts != _recentCts) return;

            _runningIds = running;
            foreach (var old in _recentRows) old.PropertyChanged -= RecentRow_PropertyChanged;
            _recentRows = list.Select(i => new RecentRow
            {
                Info = i,
                IsRunning = running.Contains(i.SessionId),
                IsChecked = keepChecked.Contains(i.SessionId),
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

    private void RecentList_KeyDown(object sender, KeyEventArgs e)
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

    private void LoadSnapshots(string? selectPath = null)
    {
        selectPath ??= (SnapshotList.SelectedItem as SnapshotListItem)?.File.Path;
        List<SnapshotFile> files;
        try { files = _snapshots.List(); }
        catch (Exception ex)
        {
            App.Log(ex);
            files = new();
            _snapshotStatus = "Could not read snapshots: " + ex.Message;
        }
        var items = files.Select(f => new SnapshotListItem { File = f }).ToList();
        SnapshotList.ItemsSource = items;
        SnapshotsEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SnapshotList.SelectedItem = items.FirstOrDefault(i => string.Equals(i.File.Path, selectPath, StringComparison.OrdinalIgnoreCase))
                                    ?? items.FirstOrDefault();
        if (items.Count > 0 || _snapshotStatus.Length == 0)
            _snapshotStatus = $"{items.Count} snapshot{(items.Count == 1 ? "" : "s")}";
        ShowSnapshotDetails();
        UpdateStatus();
    }

    private void SnapshotList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => ShowSnapshotDetails();

    private void ShowSnapshotDetails()
    {
        var item = SnapshotList.SelectedItem as SnapshotListItem;
        var snap = item?.File.Snapshot;
        RenameButton.IsEnabled = snap != null;
        DeleteButton.IsEnabled = item != null;
        RestoreButton.IsEnabled = snap is { Sessions.Count: > 0 };

        if (item == null)
        {
            SnapshotTitle.Text = " ";
            SnapshotMeta.Text = " ";
            SnapshotSessions.ItemsSource = null;
            return;
        }
        if (snap == null)
        {
            SnapshotTitle.Text = Path.GetFileName(item.File.Path);
            SnapshotMeta.Text = item.File.Error ?? "";
            SnapshotSessions.ItemsSource = null;
            return;
        }
        SnapshotTitle.Text = snap.Name;
        int missing = snap.Sessions.Count(s => !Fmt.FolderExists(s.ProjectDir));
        SnapshotMeta.Text = $"Saved {Fmt.When(snap.CreatedAt)} · {snap.Sessions.Count} session{(snap.Sessions.Count == 1 ? "" : "s")}" +
                            (missing > 0 ? $" · {missing} folder{(missing == 1 ? "" : "s")} missing" : "");
        SnapshotSessions.ItemsSource = snap.Sessions.Select(s => new SnapshotSessionRow
        {
            Session = s,
            IsRunning = s.SessionId != null && _runningIds.Contains(s.SessionId),
        }).ToList();
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

    private void RenameSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if (SnapshotList.SelectedItem is not SnapshotListItem { File.Snapshot: { } snap } item) return;
        var dlg = new InputDialog("Rename snapshot", "New name:", snap.Name) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value) || dlg.Value.Trim() == snap.Name) return;
        try
        {
            var path = _snapshots.Rename(item.File, dlg.Value);
            LoadSnapshots(path);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Could not rename the snapshot:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
            LoadSnapshots();
        }
    }

    private void DeleteSnapshot_Click(object sender, RoutedEventArgs e)
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
        LoadSnapshots();
    }

    private void OpenSnapshotFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_snapshots.Dir);
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", Launcher.WinQuote(_snapshots.Dir)) { UseShellExecute = false });
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
        foreach (var row in _currentRows)
        {
            if (row.ProjectDir == null) continue;
            var id = row.Info != null ? row.SessionId : null; // no transcript yet => restore as a fresh session
            if (id != null && !seen.Add(id)) continue;
            candidates.Add(new SaveCandidate
            {
                SessionId = id,
                ProjectDir = row.ProjectDir,
                Title = row.Info?.Title,
                Summary = row.Info?.LastPrompt,
                LastActive = row.LastActive,
                IsRunning = true,
                IsChecked = true,
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

    private void ShowSaveDialog(List<SaveCandidate> candidates)
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
            TabSnapshots.IsChecked = true; // switches tab and loads the list
            LoadSnapshots(path);
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
            var running = await Task.Run(() => _scanner.Scan().Where(r => r.SessionId != null).Select(r => r.SessionId!)
                                                       .ToHashSet(StringComparer.OrdinalIgnoreCase));
            var preSkipped = new List<string>();

            var already = items.Where(i => i.SessionId != null && running.Contains(i.SessionId)).ToList();
            if (already.Count > 0)
            {
                var names = string.Join("\n", already.Take(12).Select(a => "  • " + a.Label));
                var answer = MessageBox.Show(this,
                    $"{already.Count} of these sessions {(already.Count == 1 ? "is" : "are")} already open:\n\n{names}\n\n" +
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

            // A session id whose transcript no longer exists can't be resumed.
            var ready = new List<LaunchItem>();
            foreach (var it in items)
            {
                if (it.SessionId != null && await Task.Run(() => _store.FindFile(it.SessionId, it.ProjectDir)) == null)
                    preSkipped.Add(it.Label + ": conversation file not found");
                else ready.Add(it);
            }

            if (ready.Count > 10 &&
                MessageBox.Show(this, $"Open {ready.Count} terminal tabs?", "SessionKeeper", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK)
                return;

            var result = ready.Count > 0 ? await Task.Run(() => Launcher.Launch(ready)) : new LaunchResult();
            result.Skipped.InsertRange(0, preSkipped);
            ReportLaunch(result);
            if (TabCurrent.IsChecked != true)
            {
                // Show "running" badges once the new processes have registered.
                await Task.Delay(4000);
                if (TabRecent.IsChecked == true) await LoadRecentAsync();
            }
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
        var opened = r.Opened.Count == 0 ? "Nothing opened" : $"Opened {r.Opened.Count} {where}{(r.Opened.Count == 1 ? "" : "s")}";
        var status = opened + (r.Skipped.Count > 0 ? $" · skipped {r.Skipped.Count}" : "");
        _currentStatus = _recentStatus = _snapshotStatus = status;
        UpdateStatus();

        var lines = new List<string>();
        if (r.Error != null) lines.Add("Error: " + r.Error);
        if (r.Skipped.Count > 0) lines.Add("Skipped:\n" + string.Join("\n", r.Skipped.Select(s => "  • " + s)));
        if (r.ClaudeNotFound && r.Opened.Count > 0) lines.Add("Note: claude.exe was not found in the usual places, so the tabs run \"claude\" from PATH.");
        if (r.UsedPowerShellWindows && r.Opened.Count > 0) lines.Add("Windows Terminal was not found, so separate PowerShell windows were used.");
        if (lines.Count > 0)
            MessageBox.Show(this, opened + ".\n\n" + string.Join("\n\n", lines), "SessionKeeper", MessageBoxButton.OK,
                r.Error != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }
}
