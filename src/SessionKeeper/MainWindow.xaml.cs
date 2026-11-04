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
    private readonly SessionStore _store = new();
    private readonly RunningScanner _scanner = new();
    private readonly SnapshotStore _snapshots = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(10) };

    private readonly ObservableCollection<CurrentRow> _currentRows = new();
    private HashSet<string> _runningIds = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Sessions we just opened count as running until they register (see <see cref="LaunchGrace"/>).</summary>
    private readonly LaunchGrace _launchGrace = new();
    /// <summary>Your names for sessions (SessionKeeper's own file; Claude Code's files are never touched).</summary>
    private readonly NicknameStore _names = new();
    /// <summary>Names given in the New session dialog, waiting for that session to show up under Current.</summary>
    private readonly List<PendingName> _pendingNames = new();
    private bool _pendingWatchRunning;
    private sealed record PendingName(string Folder, DateTimeOffset Since, string Name);
    private bool _badgeRefreshBusy;
    private readonly ObservableCollection<RecentRow> _recentRows = new();
    /// <summary>Terminal sessions that aren't Claude Code (Terminals tab). Read-only detection, like Current.</summary>
    private readonly TerminalScanner _terminalScanner = new();
    private readonly ObservableCollection<TerminalRow> _terminalRows = new();
    private Task? _terminalRefresh;
    private string _terminalStatus = "";
    private bool _saving;
    private Task? _currentRefresh;
    private bool _recentLoaded, _recentLoading, _launching;
    private CancellationTokenSource? _recentCts;
    private int _snapshotDetailsVersion, _snapshotListVersion;
    private string _currentStatus = "", _recentStatus = "", _snapshotStatus = "";
    private DateTime _lastCurrentRefresh = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        CurrentList.ItemsSource = _currentRows;
        RecentList.ItemsSource = _recentRows;
        TerminalList.ItemsSource = _terminalRows;
        ShowVersion(BuildInfo.Current, null);
        ShowRuntime(DotNetRuntime.Current, null, null);
        _timer.Tick += async (_, _) => await AutoRefreshAsync();
        Loaded += async (_, _) =>
        {
            _ = CheckForUpdatesAsync(); // background, once; never delays startup
            _ = CheckRuntimeAsync();    // likewise, in parallel
            await RefreshCurrentAsync();
            _timer.Start();
        };
        Activated += async (_, _) =>
        {
            if (DateTime.UtcNow - _lastCurrentRefresh <= TimeSpan.FromSeconds(3)) return;
            if (TabCurrent.IsChecked == true) await RefreshCurrentAsync();
            else if (TabRecent.IsChecked == true && _recentLoaded) await LoadRecentAsync(quiet: true);
            else if (TabTerminals.IsChecked == true) await RefreshTerminalsAsync();
            else await RefreshRunningBadgesAsync();
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
        bool cur = TabCurrent.IsChecked == true, rec = TabRecent.IsChecked == true, snap = TabSnapshots.IsChecked == true,
             term = TabTerminals.IsChecked == true;
        PanelCurrent.Visibility = BarCurrent.Visibility = cur ? Visibility.Visible : Visibility.Collapsed;
        PanelRecent.Visibility = BarRecent.Visibility = rec ? Visibility.Visible : Visibility.Collapsed;
        PanelSnapshots.Visibility = BarSnapshots.Visibility = snap ? Visibility.Visible : Visibility.Collapsed;
        PanelTerminals.Visibility = BarTerminals.Visibility = term ? Visibility.Visible : Visibility.Collapsed;
        if (term) _ = RefreshTerminalsAsync();

        if (cur) _ = RefreshCurrentAsync();
        if (rec && !_recentLoaded) _ = LoadRecentAsync();
        else if (rec) _ = LoadRecentAsync(quiet: true);
        if (snap) _ = LoadSnapshotsAsync(refreshRunning: true);
        UpdateStatus();
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => new SettingsWindow { Owner = this }.ShowDialog();

    private void Help_Click(object sender, RoutedEventArgs e) => ShowHelp();

    // ============================================================ Remotion Studio

    /// <summary>When Studio was last started for each folder: it takes a few seconds before it can be detected.</summary>
    private readonly Dictionary<string, DateTime> _studioStarted = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan StudioStartupGrace = TimeSpan.FromSeconds(30);

    private async void RemotionStudio_Click(object sender, RoutedEventArgs e)
    {
        if (_launching) { SetAllStatus("Still opening the previous sessions…"); return; }
        // Held from the first click until the launch is done, so a quick second click can't stack dialogs or launches.
        _launching = true;
        try
        {
            await PickAndStartStudioAsync();
        }
        finally
        {
            _launching = false;
        }
    }

    private async Task PickAndStartStudioAsync()
    {
        AppSettings settings;
        List<RemotionProject> projects;
        string root;
        try
        {
            settings = await Task.Run(() => AppSettings.Load());
            root = string.IsNullOrWhiteSpace(settings.RemotionProjectsFolder) ? RemotionStudio.DefaultProjectsFolder : settings.RemotionProjectsFolder;
            projects = await Task.Run(() => RemotionStudio.FindProjects(root));
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Couldn't list the Remotion projects:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new RemotionWindow(projects, root, settings.LastRemotionProject, dirs => RemotionStudio.FindRunning(dirs)) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Project is not { } chosen) return;
        await StartStudioAsync(chosen.Dir);
    }

    /// <summary>How long ago Studio was started from here for this folder, if within the last 30 seconds.</summary>
    private TimeSpan? RecentlyStarted(string dir)
    {
        if (!_studioStarted.TryGetValue(dir.TrimEnd('\\'), out var at)) return null;
        var ago = DateTime.UtcNow - at;
        return ago < StudioStartupGrace ? ago : null;
    }

    /// <summary>
    /// Starts Remotion Studio for a project, unless it's already running there (then offers its address instead).
    /// Everything is checked again at this point, off the UI thread: the folder must still hold a Remotion
    /// package.json, and the running check is fresh rather than what the dialog showed. The caller holds _launching.
    /// </summary>
    private async Task StartStudioAsync(string dir)
    {
        try
        {
            var (project, running, command, problem) = await Task.Run(() =>
            {
                var p = RemotionStudio.ReadProject(dir);
                if (p == null) return (p, (StudioInstance?)null, (StudioCommand?)null, (string?)null);
                var r = RemotionStudio.FindRunning(p.Dir);
                string? why = null;
                var c = r == null ? RemotionStudio.CommandFor(p, RemotionStudio.FindTool, out why) : null;
                return (p, r, c, why);
            });
            var name = project?.Name ?? Fmt.FolderName(dir);
            if (project == null)
            {
                MessageBox.Show(this, $"“{name}” no longer has a package.json that uses Remotion ({dir}).", "Remotion Studio",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (running != null)
            {
                RememberRemotionProject(project.Dir);
                if (running.Port is { } port)
                {
                    var answer = MessageBox.Show(this,
                        $"Remotion Studio is already running for “{name}” at {RemotionStudio.AddressFor(port)}\n\n" +
                        "Open it in your browser instead of starting a second copy?",
                        "Remotion Studio already running", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.Yes);
                    if (answer == MessageBoxResult.Yes) OpenStudioInBrowser(port);
                    else SetAllStatus($"Not started: Remotion Studio is already running for {name}");
                }
                else
                {
                    MessageBox.Show(this,
                        $"Remotion Studio is already running for “{name}” (process {string.Join(", ", running.Pids)}), but it isn't " +
                        "listening on a port yet, so its address isn't known.\n\nA second copy wasn't started. Try again in a moment.",
                        "Remotion Studio already running", MessageBoxButton.OK, MessageBoxImage.Information);
                    SetAllStatus($"Not started: Remotion Studio is already running for {name}");
                }
                return;
            }

            // Started from here moments ago but not detectable yet (npm and Studio take a few seconds to start).
            if (RecentlyStarted(project.Dir) is { } ago)
            {
                var again = MessageBox.Show(this,
                    $"Remotion Studio was started for “{name}” {Math.Max(1, (int)ago.TotalSeconds)} seconds ago and may still be starting.\n\n" +
                    "Start another copy anyway?",
                    "Remotion Studio may be starting", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (again != MessageBoxResult.Yes)
                {
                    SetAllStatus($"Not started: Remotion Studio is still starting for {name}");
                    return;
                }
            }

            if (command == null)
            {
                MessageBox.Show(this, $"Couldn't start Remotion Studio for “{name}”: {problem}", "Remotion Studio",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var mode = (await Task.Run(() => AppSettings.Load())).OpenIn;
            var item = new LaunchItem(project.Dir, null, name, "Remotion Studio: " + name, Studio: command);
            LaunchResult result;
            using (LaunchGuard.UserAction(LaunchGuard.RemotionStudio))
                result = await Task.Run(() => Launcher.Launch(new[] { item }, mode));
            ReportLaunch(result, single: true);
            if (result.OpenedItems.Count > 0)
            {
                _studioStarted[project.Dir.TrimEnd('\\')] = DateTime.UtcNow;
                RememberRemotionProject(project.Dir);
                SetAllStatus($"Started Remotion Studio for {name} ({command.Display})");
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Couldn't start Remotion Studio:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenStudioInBrowser(int port)
    {
        var address = RemotionStudio.AddressFor(port);
        try
        {
            using var action = LaunchGuard.UserAction(LaunchGuard.OpenStudioInBrowser);
            using var _ = LaunchGuard.Start(RemotionStudio.BrowserStart(port), "Browser: Remotion Studio at " + address);
            SetAllStatus("Opened " + address);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, $"Couldn't open the browser:\n{ex.Message}\n\nStudio's address is {address}", "SessionKeeper",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Remembers the project so the Remotion Studio dialog picks it first next time. Best effort, on the UI thread like
    /// the Settings window's own saves (so the two never write at once), and skipped if the settings file couldn't be
    /// read, so real settings are never replaced by defaults.
    /// </summary>
    private static void RememberRemotionProject(string dir)
    {
        try
        {
            if (AppSettings.TryLoad() is not { } s || s.LastRemotionProject == dir) return;
            s.LastRemotionProject = dir;
            s.Save();
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    /// <summary>Opens the help on the topic for the tab you're on.</summary>
    private void ShowHelp()
    {
        var topic = TabRecent.IsChecked == true ? HelpContent.Recent
            : TabSnapshots.IsChecked == true ? HelpContent.Snapshots
            : TabTerminals.IsChecked == true ? HelpContent.Terminals
            : HelpContent.Current;
        new HelpWindow(topic) { Owner = this }.ShowDialog();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.F1 && !e.Handled)
        {
            e.Handled = true;
            ShowHelp();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    // ============================================================ version footer

    /// <summary>
    /// Compares this build with the source repository once, in the background. Read-only, with a short timeout.
    /// If the repo can't be found or checked, the footer simply stays grey.
    /// </summary>
    private async Task CheckForUpdatesAsync()
    {
        using var noLaunches = LaunchGuard.Background(); // git queries are not user launches
        try
        {
            var build = BuildInfo.Current;
            var settings = await Task.Run(() => AppSettings.Load());
            var status = await UpdateChecker.CheckAsync(build, UpdateChecker.DefaultSources(build, settings), TimeSpan.FromSeconds(8));
            ShowVersion(build, status);
        }
        catch (Exception ex)
        {
            App.Log(ex); // quiet: the footer keeps its grey build line
        }
    }

    /// <summary>
    /// Checks the running .NET version against Microsoft's release metadata once (cached 24 h), in the
    /// background. Offline or any failure: the .NET part simply stays grey.
    /// </summary>
    private async Task CheckRuntimeAsync()
    {
        try
        {
            var current = DotNetRuntime.Current;
            // Local and quick (folder listing only), but still off the UI thread.
            var installed = await Task.Run(() => InstalledRuntimes.Find());
            ShowRuntime(current, null, installed);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var status = await Task.Run(() => new DotNetReleaseCheck().CheckAsync(current, cts.Token), cts.Token);
            ShowRuntime(current, status, installed);
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.Net.Http.HttpRequestException)
        {
            // offline or slow: stay grey
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    private void ShowRuntime(Version runtime, RuntimeStatus? status, InstalledRuntimes? installed)
    {
        bool outdated = status?.Outdated == true;
        RuntimeRun.Text = " · Runs on " + DotNetRuntime.Display(runtime);
        RuntimeRun.Foreground = (System.Windows.Media.Brush)FindResource(outdated ? "Warn" : "Faint");
        RuntimeRun.ToolTip = RuntimeTooltip.Build(runtime, DotNetRuntime.IsSelfContained, installed, status, $"{runtime.Major}.{runtime.Minor}");
    }

    private void ShowVersion(BuildInfo build, UpdateStatus? status)
    {
        bool update = status?.State == UpdateState.UpdateAvailable;
        VersionRun.Text = "  ·  " + build.FooterText + (update ? " · update available" : "");
        VersionRun.Foreground = (System.Windows.Media.Brush)FindResource(update ? "Warn" : "Faint");

        var tip = new List<string> { $"SessionKeeper {build.Version}" };
        if (build.Commit != null) tip.Add("Commit " + build.Commit + (build.SourceDirty ? " (built with uncommitted changes)" : ""));
        if (build.BuildDate is { } d) tip.Add("Built " + d.ToLocalTime().ToString("d MMM yyyy HH:mm"));
        if (status != null) tip.Add(status.Detail);
        VersionRun.ToolTip = string.Join("\n", tip);
    }

    private void UpdateStatus()
    {
        StatusText.Text = TabCurrent.IsChecked == true ? _currentStatus
            : TabRecent.IsChecked == true ? _recentStatus
            : TabTerminals.IsChecked == true ? _terminalStatus
            : _snapshotStatus;
    }

    /// <summary>Running session ids from a scan, plus sessions we just opened that haven't registered yet.</summary>
    private HashSet<string> WithRecentlyLaunched(IEnumerable<string> running) => _launchGrace.Apply(running);

    private async Task<HashSet<string>> ScanRunningIdsAsync()
    {
        var ids = await Task.Run(() => _scanner.Scan().Where(r => r.SessionId != null).Select(r => r.SessionId!).ToList());
        return WithRecentlyLaunched(ids);
    }

    // ============================================================ current

    private async Task AutoRefreshAsync()
    {
        // Only scan while the user can actually see the window.
        if (WindowState == WindowState.Minimized || !IsVisible) return;
        if (TabCurrent.IsChecked == true) await RefreshCurrentAsync();
        // Recent: new, changed and closed sessions appear in place (no "Loading…", ticks and scroll kept).
        else if (TabRecent.IsChecked == true && _recentLoaded) await LoadRecentAsync(quiet: true);
        else if (TabTerminals.IsChecked == true) await RefreshTerminalsAsync();
        else await RefreshRunningBadgesAsync(); // Snapshots: keep the "running" badges current
    }

    /// <summary>
    /// Re-checks which sessions are running and updates the "running" badges on the Recent tab and in the
    /// selected snapshot in place (no list reload, so selection, ticks and scroll position stay). Read-only.
    /// </summary>
    private async Task RefreshRunningBadgesAsync()
    {
        if (_badgeRefreshBusy) return;
        _badgeRefreshBusy = true;
        using var noLaunches = LaunchGuard.Background();
        try
        {
            var running = await ScanRunningIdsAsync();
            _runningIds = running;
            foreach (var row in _recentRows) row.IsRunning = running.Contains(row.Info.SessionId);
            if (SnapshotSessions.ItemsSource is System.Collections.IEnumerable snapRows)
                foreach (var row in snapRows.OfType<SnapshotSessionRow>()) row.IsRunning = row.Session.SessionId != null && running.Contains(row.Session.SessionId);
            _lastCurrentRefresh = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _badgeRefreshBusy = false;
        }
    }

    /// <summary>Starts a refresh, or returns the one already in flight so callers always await fresh rows.</summary>
    private Task RefreshCurrentAsync()
    {
        if (_currentRefresh is { IsCompleted: false }) return _currentRefresh;
        return _currentRefresh = RefreshCurrentCoreAsync();
    }

    private async Task RefreshCurrentCoreAsync()
    {
        using var noLaunches = LaunchGuard.Background(); // detection is read-only: it can never launch
        try
        {
            var rows = await Task.Run(() => SessionQueries.GetRunning(_scanner, _store)
                .Select(e => new CurrentRow(e.Running, e.Info, e.FolderMissing, _names.Get(e.Running.SessionId)))
                .ToList());

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

    // ============================================================ terminals

    /// <summary>Starts a terminal scan, or returns the one in flight. Read-only, a few milliseconds.</summary>
    private Task RefreshTerminalsAsync()
    {
        if (_terminalRefresh is { IsCompleted: false }) return _terminalRefresh;
        return _terminalRefresh = RefreshTerminalsCoreAsync();
    }

    private async Task RefreshTerminalsCoreAsync()
    {
        using var noLaunches = LaunchGuard.Background(); // detection only: it can never launch
        try
        {
            var fresh = await Task.Run(() => _terminalScanner.Scan().Select(t => new TerminalRow(t)).ToList());
            TerminalRow.Sync(_terminalRows, fresh,
                added: r => r.PropertyChanged += TerminalRow_PropertyChanged,
                removed: r => r.PropertyChanged -= TerminalRow_PropertyChanged);
            TerminalsEmpty.Visibility = _terminalRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int agents = _terminalRows.Count(r => r.Session.AgentName != null);
            _terminalStatus = $"{_terminalRows.Count} terminal{(_terminalRows.Count == 1 ? "" : "s")}" +
                              (agents > 0 ? $" · {agents} agent{(agents == 1 ? "" : "s")}" : "") + $" · updated {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            App.Log(ex);
            _terminalStatus = "Refresh failed: " + ex.Message;
        }
        finally
        {
            UpdateTerminalSelection();
            UpdateStatus();
        }
    }

    private void TerminalRow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalRow.IsChecked)) UpdateTerminalSelection();
    }

    private void UpdateTerminalSelection()
    {
        int n = _terminalRows.Count(r => r.IsChecked && r.CanSave);
        TerminalSelectionText.Text = n == 0 ? "Auto-refreshes every 10 s · tick terminals to save them in a snapshot" : $"{n} ticked";
    }

    /// <summary>Space ticks the selected terminal.</summary>
    private void TerminalList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && TerminalList.SelectedItem is TerminalRow { CanSave: true } row &&
            !IsInside<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject))
        {
            row.IsChecked = !row.IsChecked;
            e.Handled = true;
        }
    }

    private async void RefreshTerminals_Click(object sender, RoutedEventArgs e) => await RefreshTerminalsAsync();

    private async void SaveTerminals_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentAsync();
        await SaveSnapshotFromCurrentAsync(allTerminals: true);
    }

    /// <summary>
    /// Terminal candidates for the Save dialog. From the Terminals tab every terminal that can be saved is listed
    /// (the ticked ones pre-ticked); from the other tabs only the ones ticked on the Terminals tab.
    /// </summary>
    private async Task<List<SaveCandidate>> TerminalCandidatesAsync(bool all)
    {
        if (!all && !_terminalRows.Any(r => r.IsChecked)) return new();
        try { await RefreshTerminalsAsync(); }
        catch (Exception ex) { App.Log(ex); }
        return _terminalRows.Where(r => r.CanSave && (all || r.IsChecked)).Select(r =>
        {
            var entry = TerminalCommands.ToSnapshot(r.Session);
            return new SaveCandidate
            {
                ProjectDir = entry.ProjectDir,
                Title = entry.Label,
                // A running command whose program path couldn't be read is saved as the shell only: say so.
                Summary = r.Session.CommandLine != null && entry.CommandLine == null ? "its command can't be saved (program path unknown)" : null,
                Terminal = entry,
                IsRunning = true,
                IsChecked = r.IsChecked,
            };
        }).ToList();
    }

    /// <summary>The live terminals as they'd be saved, to spot saved ones that are still running.</summary>
    private async Task<List<SnapshotTerminal>> LiveTerminalsAsync()
    {
        try { await RefreshTerminalsAsync(); }
        catch (Exception ex) { App.Log(ex); }
        return _terminalRows.Select(r => TerminalCommands.ToSnapshot(r.Session)).ToList();
    }

    private List<TerminalPlan> PlansFor(IEnumerable<SnapshotTerminal> saved, List<SnapshotTerminal> live) =>
        saved.Select(t => TerminalCommands.Plan(t) with { StillRunning = TerminalCommands.IsStillRunning(t, live) }).ToList();

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

    /// <summary>
    /// Loads the Recent list. <paramref name="quiet"/> is the timed refresh: it skips if a load is already
    /// running, shows no "Loading…", and leaves the list as it was if it fails.
    /// </summary>
    private async Task LoadRecentAsync(bool quiet = false)
    {
        if (quiet && _recentLoading) return;
        using var noLaunches = LaunchGuard.Background();
        _recentCts?.Cancel();
        var cts = new CancellationTokenSource();
        _recentCts = cts;
        _recentLoaded = true;
        _recentLoading = true;

        var since = DateTimeOffset.Now - RecentWindow;
        if (!quiet)
        {
            _recentStatus = "Loading…";
            UpdateStatus();
        }

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
            // Synced in place after the await, so boxes ticked while loading are kept.
            var fresh = rows.Select(x => new RecentRow
            {
                Info = x.First,
                FolderMissing = x.Second,
                IsRunning = runningSet.Contains(x.First.SessionId),
                Nickname = _names.Get(x.First.SessionId),
            }).ToList();
            RecentRow.Sync(_recentRows, fresh,
                added: r => r.PropertyChanged += RecentRow_PropertyChanged,
                removed: r => r.PropertyChanged -= RecentRow_PropertyChanged);

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
            if (!quiet)
            {
                _recentLoaded = false; // retry next time the tab is shown
                _recentStatus = "Load failed: " + ex.Message;
            }
        }
        finally
        {
            if (cts == _recentCts) _recentLoading = false; // a newer load still running keeps the flag
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

    private async void RefreshRecent_Click(object sender, RoutedEventArgs e) => await LoadRecentAsync();

    private async void Reopen_Click(object sender, RoutedEventArgs e)
    {
        var items = _recentRows.Where(r => r.IsChecked)
            .Select(r => new LaunchItem(r.Info.ProjectDir ?? "", r.Info.SessionId, r.FolderName, r.Nickname))
            .ToList();
        await ReopenAsync(items, LaunchGuard.Reopen);
    }

    private void SaveRecent_Click(object sender, RoutedEventArgs e)
    {
        var candidates = _recentRows.Where(r => r.IsChecked && r.Info.ProjectDir != null).Select(r => new SaveCandidate
        {
            SessionId = r.Info.SessionId,
            ProjectDir = r.Info.ProjectDir!,
            Nickname = r.Nickname,
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
        int version = ++_snapshotListVersion;
        List<SnapshotFile> files;
        bool failed = false;
        // Off the UI thread: reads and parses every snapshot file (the folder may be slow or on a network drive).
        try { files = await Task.Run(() => _snapshots.List()); }
        catch (Exception ex)
        {
            App.Log(ex);
            files = new();
            failed = true;
            _snapshotStatus = "Could not read snapshots: " + ex.Message;
        }
        if (version != _snapshotListVersion) return; // a newer load has started
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
        using var noLaunches = LaunchGuard.Background();
        int version = ++_snapshotDetailsVersion;
        var item = SnapshotList.SelectedItem as SnapshotListItem;
        var snap = item?.File.Snapshot;
        RenameButton.IsEnabled = snap != null;
        DeleteButton.IsEnabled = item != null;
        RestoreButton.IsEnabled = snap != null && (snap.Sessions.Count > 0 || TerminalEntries(snap).Count > 0);

        SnapshotNote.Text = snap?.Note ?? "";
        SnapshotNote.Visibility = string.IsNullOrWhiteSpace(snap?.Note) ? Visibility.Collapsed : Visibility.Visible;

        if (item == null || snap == null)
        {
            SnapshotTitle.Text = item == null ? " " : Path.GetFileName(item.File.Path);
            SnapshotMeta.Text = item?.File.Error ?? " ";
            SnapshotSessions.ItemsSource = null;
            return;
        }

        SnapshotTitle.Text = snap.Name;
        var terminals = TerminalEntries(snap);
        var baseMeta = $"Saved {Fmt.When(snap.CreatedAt)} · {snap.Sessions.Count} session{(snap.Sessions.Count == 1 ? "" : "s")}" +
                       (terminals.Count > 0 ? $" · {terminals.Count} terminal{(terminals.Count == 1 ? "" : "s")}" : "");
        SnapshotMeta.Text = baseMeta;
        var running = _runningIds;
        var sessions = snap.Sessions.ToList();
        // Folder checks can block on unreachable network paths: do them off the UI thread.
        var (rows, terminalRows) = await Task.Run(() => (sessions.Select(s => new SnapshotSessionRow
        {
            Session = s,
            FolderMissing = !FolderProbe.Exists(s.ProjectDir),
            IsRunning = s.SessionId != null && running.Contains(s.SessionId),
            Nickname = _names.Get(s.SessionId), // null: falls back to the name saved with the snapshot
        }).ToList(), terminals.Select(t => new SnapshotTerminalRow { Terminal = t, FolderMissing = !FolderProbe.Exists(t.ProjectDir) }).ToList()));
        if (version != _snapshotDetailsVersion) return; // selection changed meanwhile

        int missing = rows.Count(r => r.FolderMissing) + terminalRows.Count(r => r.FolderMissing);
        SnapshotMeta.Text = baseMeta + (missing > 0 ? $" · {missing} folder{(missing == 1 ? "" : "s")} missing" : "");
        SnapshotSessions.ItemsSource = rows.Cast<object>().Concat(terminalRows).ToList();
    }

    /// <summary>A snapshot's terminal entries (anything not marked "terminal" in a hand-edited file is ignored).</summary>
    private static List<SnapshotTerminal> TerminalEntries(Snapshot snap) => snap.ValidTerminals;

    private async void SaveSnapshot_Click(object sender, RoutedEventArgs e)
    {
        await RefreshCurrentAsync();
        await SaveSnapshotFromCurrentAsync();
    }

    private async void RestoreSnapshot_Click(object sender, RoutedEventArgs e)
    {
        if ((SnapshotList.SelectedItem as SnapshotListItem)?.File.Snapshot is not { } snap) return;
        if (_launching) { SetAllStatus("Still opening the previous sessions\u2026"); return; }
        var items = snap.Sessions
            .Where(s => !s.IsOtherKind) // never restore something else as a Claude session
            .Select(s => new LaunchItem(s.ProjectDir ?? "", SessionStore.IsValidSessionId(s.SessionId) ? s.SessionId : null,
                                        Fmt.FolderName(s.ProjectDir), _names.Get(s.SessionId) ?? NicknameStore.Clean(s.Nickname)))
            .ToList();
        var terminals = TerminalEntries(snap);
        if (terminals.Count > 0)
        {
            // Never re-run anything on Restore without listing it: every terminal and its exact command is shown,
            // and only the ticked ones open.
            var dlg = new TerminalRunWindow(PlansFor(terminals, await LiveTerminalsAsync()), items.Count, "Restore") { Owner = this };
            if (dlg.ShowDialog() != true) return;
            items.AddRange(dlg.Items);
        }
        await ReopenAsync(items, LaunchGuard.Restore);
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
            using var action = LaunchGuard.UserAction(LaunchGuard.OpenSnapshotsFolder);
            Directory.CreateDirectory(_snapshots.Dir);
            // Full path: a bare "explorer.exe" is searched for in the app and current folders first.
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = LaunchGuard.Start(new ProcessStartInfo(explorer, Launcher.WinQuote(_snapshots.Dir)) { UseShellExecute = false },
                "Explorer: snapshots folder");
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
    }

    // ============================================================ save dialog

    private async Task SaveSnapshotFromCurrentAsync(bool allTerminals = false)
    {
        if (_saving) return; // a second click while the first is still gathering candidates
        _saving = true;
        try { await SaveSnapshotFromCurrentCoreAsync(allTerminals); }
        finally { _saving = false; }
    }

    private async Task SaveSnapshotFromCurrentCoreAsync(bool allTerminals)
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
                Nickname = row.Nickname,
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
                    Nickname = _names.Get(info.SessionId),
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
        candidates.AddRange(await TerminalCandidatesAsync(allTerminals));
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

    /// <summary>
    /// The only path that opens sessions (Open, Reopen selected, Restore). <paramref name="userAction"/> names
    /// the button that triggered it; it opens the launch scope that <see cref="LaunchGuard"/> requires, so
    /// nothing else can launch. Sessions open as PowerShell windows or Windows Terminal tabs per Settings.
    /// </summary>
    private async Task<LaunchResult?> ReopenAsync(List<LaunchItem> items, string userAction)
    {
        if (items.Count == 0 || _launching) return null;
        _launching = true;
        bool single = items.Count == 1;
        try
        {
            var running = await ScanRunningIdsAsync();
            var preSkipped = new List<string>();

            var already = items.Where(i => i.SessionId != null && running.Contains(i.SessionId)).ToList();
            if (already.Count > 0 && single)
            {
                var answer = MessageBox.Show(this,
                    $"\u201C{already[0].Label}\u201D is already running (it's listed under Current sessions).\n\n" +
                    "Opening a second copy means two windows writing to the same conversation, which can mix up its history.\n\n" +
                    "Open a second copy anyway?",
                    "Session already running", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes)
                {
                    SetAllStatus("Not opened: that session is already running");
                    return null;
                }
            }
            else if (already.Count > 0)
            {
                var names = string.Join("\n", already.Take(12).Select(a => "  \u2022 " + a.Label));
                var answer = MessageBox.Show(this,
                    $"{already.Count} of these sessions {(already.Count == 1 ? "is" : "are")} already running (or just opened):\n\n{names}\n\n" +
                    "Opening a session twice means two windows writing to the same conversation.\n\n" +
                    "Skip the ones that are already running?\n\nYes = skip them (recommended)\nNo = open second copies anyway",
                    "Already running", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);
                if (answer == MessageBoxResult.Cancel) return null;
                if (answer == MessageBoxResult.Yes)
                {
                    items = items.Except(already).ToList();
                    preSkipped.AddRange(already.Select(a => a.Label + ": already running"));
                }
            }

            // Off the UI thread (folder checks can block on network paths): the folder must still exist, and
            // the transcript must still exist and belong to this folder, or `claude --resume` would fail
            // (or resume in the wrong place).
            var checks = await Task.Run(() => items.Select(it =>
            {
                if (string.IsNullOrWhiteSpace(it.ProjectDir)) return (it, (string?)"no folder recorded");
                if (!FolderProbe.Exists(it.ProjectDir)) return (it, $"the folder no longer exists ({it.ProjectDir})");
                if (it.SessionId == null) return (it, null);
                var file = _store.FindFile(it.SessionId, it.ProjectDir);
                if (file == null) return (it, "its conversation file was not found, so it can't be resumed");
                var info = _store.GetSessionFromFile(file);
                if (info?.ProjectDir != null && !string.Equals(info.ProjectDir.TrimEnd('\\'), it.ProjectDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return (it, "the conversation belongs to a different folder (" + info.ProjectDir + ")");
                return (it, null);
            }).ToList());
            var ready = new List<LaunchItem>();
            foreach (var (it, why) in checks)
            {
                if (why != null) preSkipped.Add(it.Label + ": " + why);
                else ready.Add(it);
            }

            var mode = AppSettings.Load().OpenIn;
            if (ready.Count > 10 &&
                MessageBox.Show(this, $"Open {ready.Count} {(mode == OpenMode.WindowsTerminalTab ? "Windows Terminal tabs" : "PowerShell windows")}?",
                    "SessionKeeper", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
                return null;

            // The launch scope covers only the launch itself: no dialogs (and so no nested message loop that
            // could run a timer tick inside the scope) happen while it is open.
            LaunchResult result;
            if (ready.Count > 0)
            {
                using (LaunchGuard.UserAction(userAction))
                    result = await Task.Run(() => Launcher.Launch(ready, mode));
            }
            else result = new LaunchResult();
            foreach (var it in result.OpenedItems.Where(r => r.SessionId != null))
                _launchGrace.Launched(it.SessionId!);
            result.Skipped.InsertRange(0, preSkipped);
            ReportLaunch(result, single);

            if (TabRecent.IsChecked == true) await LoadRecentAsync(); // show "running" badges straight away
            return result;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Could not open sessions:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            _launching = false;
        }
    }

    private void SetAllStatus(string status)
    {
        _currentStatus = _recentStatus = _snapshotStatus = status;
        UpdateStatus();
    }

    private void ReportLaunch(LaunchResult r, bool single)
    {
        var where = r.UsedPowerShellWindows ? "PowerShell window" : "Windows Terminal tab";
        int n = r.OpenedItems.Count;
        var opened = n == 0 ? "Nothing opened" : $"Opened {n} {where}{(n == 1 ? "" : "s")}";
        SetAllStatus(opened + (r.Skipped.Count > 0 ? $" \u00B7 skipped {r.Skipped.Count}" : ""));

        // One session that couldn't be opened: say exactly why.
        if (single && n == 0 && r.Error == null && r.Skipped.Count == 1)
        {
            MessageBox.Show(this, "Couldn't open " + r.Skipped[0] + ".", "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var lines = new List<string>();
        if (r.Error != null) lines.Add("Error: " + r.Error);
        if (r.Skipped.Count > 0) lines.Add("Skipped:\n" + string.Join("\n", r.Skipped.Select(s => "  \u2022 " + s)));
        if (r.ClaudeNotFound && n > 0) lines.Add("Note: claude.exe was not found in the usual places, so \"claude\" is run from PATH.");
        if (r.TerminalMissing && n > 0) lines.Add("Windows Terminal isn't installed, so PowerShell windows were used instead.");
        if (lines.Count > 0)
            MessageBox.Show(this, opened + ".\n\n" + string.Join("\n\n", lines), "SessionKeeper", MessageBoxButton.OK,
                r.Error != null ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    // ============================================================ open one row

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement { DataContext: { } row }) await OpenRowAsync(row);
    }

    /// <summary>Double-click on a row = its Open button (but not when double-clicking its checkbox or button).</summary>
    private async void Row_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInside<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject))
            return;
        if (sender is System.Windows.Controls.ListBoxItem { DataContext: { } row })
        {
            e.Handled = true;
            await OpenRowAsync(row);
        }
    }

    /// <summary>Enter opens the selected row; Space ticks it on the Recent tab.</summary>
    private async void SessionList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox list || list.SelectedItem is not { } row) return;
        if (e.Key == Key.Space && row is RecentRow recent)
        {
            recent.IsChecked = !recent.IsChecked;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && !IsInside<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject))
        {
            e.Handled = true;
            await OpenRowAsync(row);
        }
        else if (e.Key == Key.F2)
        {
            e.Handled = true;
            RenameRow(row);
        }
    }

    private async Task OpenRowAsync(object row)
    {
        if (row is SnapshotTerminalRow terminalRow)
        {
            await OpenTerminalEntryAsync(terminalRow);
            return;
        }
        var (item, reason) = row switch
        {
            CurrentRow c => (c.ToLaunchItem(), c.OpenTooltip),
            RecentRow r => (r.ToLaunchItem(), r.OpenTooltip),
            SnapshotSessionRow s => (s.ToLaunchItem(), s.OpenTooltip),
            _ => ((LaunchItem?)null, "Can't open this row"),
        };
        if (item == null)
        {
            MessageBox.Show(this, reason + ".", "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await ReopenAsync(new List<LaunchItem> { item }, LaunchGuard.Open);
    }

    /// <summary>
    /// Opens one saved terminal. A plain shell opens straight away; anything that would run a command is shown in
    /// the confirmation first, with the exact text.
    /// </summary>
    private async Task OpenTerminalEntryAsync(SnapshotTerminalRow row)
    {
        if (!row.CanOpen)
        {
            MessageBox.Show(this, row.OpenTooltip + ".", "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_launching) { SetAllStatus("Still opening the previous sessions\u2026"); return; }
        var plan = PlansFor(new[] { row.Terminal }, await LiveTerminalsAsync())[0];
        List<LaunchItem> items;
        if (plan.IsPlainShell && !plan.StillRunning) items = new List<LaunchItem> { new TerminalRunRow(plan).ToLaunchItem() };
        else
        {
            var dlg = new TerminalRunWindow(new[] { plan }, 0, "Open") { Owner = this };
            if (dlg.ShowDialog() != true || dlg.Items.Count == 0) return;
            items = dlg.Items;
        }
        await ReopenAsync(items, LaunchGuard.Open);
    }

    // ============================================================ names (right-click > Rename / F2)

    private static object? MenuRow(object sender) =>
        sender is FrameworkElement { DataContext: { } row } ? row : null;

    private void RowMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ContextMenu menu) return;
        var row = menu.DataContext;
        var nameable = row as INameableRow;
        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            switch (item.Header as string)
            {
                case "Open":
                    item.IsEnabled = row switch
                    {
                        CurrentRow c => c.CanOpen, RecentRow r => r.CanOpen, SnapshotSessionRow s => s.CanOpen, SnapshotTerminalRow t => t.CanOpen, _ => false,
                    };
                    break;
                case "Rename…":
                    item.IsEnabled = nameable?.NameableSessionId != null;
                    break;
                case "Clear name":
                    item.IsEnabled = nameable?.NameableSessionId != null && _names.Get(nameable.NameableSessionId) != null;
                    break;
            }
        }
    }

    private async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row) await OpenRowAsync(row);
    }

    private void MenuRename_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is { } row) RenameRow(row);
    }

    private void MenuClearName_Click(object sender, RoutedEventArgs e)
    {
        if (MenuRow(sender) is INameableRow { NameableSessionId: { } id }) SetNickname(id, null);
    }

    /// <summary>Asks for a new name; an empty name goes back to the session's own title.</summary>
    private void RenameRow(object row)
    {
        if (row is SnapshotTerminalRow)
        {
            SetAllStatus("Terminals keep the label they were saved with"); // only Claude sessions can be named
            return;
        }
        if (row is not INameableRow n || n.NameableSessionId is not { } id)
        {
            MessageBox.Show(this, "This row has no session ID yet, so it can't be named. Unmatched or brand-new sessions get one once Claude Code registers them.",
                "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new InputDialog("Rename session", "Name (leave empty to go back to the session's own title):", _names.Get(id) ?? n.MainLabel) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        SetNickname(id, dlg.Value);
    }

    private void SetNickname(string sessionId, string? name)
    {
        string? stored;
        try
        {
            stored = _names.Set(sessionId, name);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            MessageBox.Show(this, "Couldn't save the name:\n" + ex.Message, "SessionKeeper", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ApplyNickname(sessionId, stored);
        SetAllStatus(stored != null ? $"Named the session \u201C{stored}\u201D" : "Name cleared");
    }

    /// <summary>Updates every visible row for that session (Current, Recent and the open snapshot) in place.</summary>
    private void ApplyNickname(string sessionId, string? name)
    {
        bool Same(string? id) => string.Equals(id, sessionId, StringComparison.OrdinalIgnoreCase);
        foreach (var r in _currentRows.Where(r => Same(r.NameableSessionId))) r.Nickname = name;
        foreach (var r in _recentRows.Where(r => Same(r.NameableSessionId))) r.Nickname = name;
        if (SnapshotSessions.ItemsSource is System.Collections.IEnumerable snapRows)
            foreach (var r in snapRows.OfType<SnapshotSessionRow>().Where(r => Same(r.NameableSessionId))) r.Nickname = name;
    }

    // ============================================================ new session

    private async void NewSession_Click(object sender, RoutedEventArgs e)
    {
        List<string> folders;
        try
        {
            var runningFolders = _currentRows.Select(r => r.ProjectDir).ToList(); // read on the UI thread
            folders = await Task.Run(() => RecentProjectFolders(runningFolders));
        }
        catch (Exception ex)
        {
            App.Log(ex);
            folders = new List<string>();
        }
        var dlg = new NewSessionWindow(folders) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Folder is not { } folder) return;

        var name = dlg.SessionName;
        var since = DateTimeOffset.Now;
        var result = await ReopenAsync(new List<LaunchItem> { new(folder, null, Fmt.FolderName(folder), name) }, LaunchGuard.NewSession);
        if (name != null && result is { OpenedItems.Count: > 0 })
        {
            _pendingNames.Add(new PendingName(folder, since.AddSeconds(-5), name));
            _ = WatchPendingNamesAsync();
        }
    }

    /// <summary>Folders of running sessions, then of sessions active in the last 30 days, that still exist.</summary>
    private List<string> RecentProjectFolders(IReadOnlyList<string?> runningFolders)
    {
        var recent = _store.GetRecent(DateTimeOffset.Now.AddDays(-30)).Select(s => s.ProjectDir);
        return runningFolders.Concat(recent)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(d => FolderProbe.Exists(d))
            .Take(25)
            .ToList();
    }

    /// <summary>
    /// After a named New session, looks for it every few seconds (read-only scans) and, once it shows up with a
    /// session ID in that folder, gives it the name. Gives up after 5 minutes (e.g. if it never got past a prompt).
    /// </summary>
    private async Task WatchPendingNamesAsync()
    {
        if (_pendingWatchRunning) return;
        _pendingWatchRunning = true;
        using var noLaunches = LaunchGuard.Background();
        try
        {
            while (_pendingNames.Count > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(3));
                _pendingNames.RemoveAll(p => DateTimeOffset.Now - p.Since > TimeSpan.FromMinutes(5));
                if (_pendingNames.Count == 0) break;
                var running = await Task.Run(() => _scanner.Scan());
                AttachPendingNames(running);
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _pendingWatchRunning = false;
        }
    }

    private void AttachPendingNames(IReadOnlyList<RunningSession> running)
    {
        foreach (var pending in _pendingNames.ToList())
        {
            var match = PendingNameMatcher.Find(pending.Folder, pending.Since, running, id => _names.Get(id) != null);
            if (match == null) continue;
            _pendingNames.Remove(pending);
            SetNickname(match, pending.Name);
        }
    }

    private static bool IsInside<T>(DependencyObject? d) where T : DependencyObject
    {
        for (; d != null; d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is T) return true;
        return false;
    }
}
