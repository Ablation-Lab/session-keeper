using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using SessionKeeper.Models;
using SessionKeeper.Services;

namespace SessionKeeper.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }
    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal static class Fmt
{
    public static string Ago(DateTimeOffset? t)
    {
        if (t is not { } v) return "";
        var d = DateTimeOffset.Now - v;
        if (d < TimeSpan.FromSeconds(45)) return "just now";
        if (d < TimeSpan.FromMinutes(60)) return $"{Math.Max(1, (int)d.TotalMinutes)}m ago";
        if (d < TimeSpan.FromHours(24)) return $"{(int)d.TotalHours}h ago";
        if (d < TimeSpan.FromDays(7)) return $"{(int)d.TotalDays}d ago";
        return v.ToLocalTime().ToString("d MMM");
    }

    public static string When(DateTimeOffset? t)
    {
        if (t is not { } v) return "";
        var local = v.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return "Today " + local.ToString("HH:mm");
        if (local.Date == today.AddDays(-1)) return "Yesterday " + local.ToString("HH:mm");
        if (local.Date > today.AddDays(-7)) return local.ToString("ddd HH:mm");
        return local.ToString("d MMM HH:mm");
    }

    public static string Trim(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    public static string FolderName(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return "(unknown folder)";
        var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(name) ? dir : name;
    }

    public static string ShortId(string? id) => string.IsNullOrEmpty(id) ? "" : id.Length > 8 ? id[..8] : id;
}

internal static class Brushes2
{
    private static SolidColorBrush B(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
    public static readonly SolidColorBrush Working = B("#E5B567");
    public static readonly SolidColorBrush Waiting = B("#7AA7E0");
    public static readonly SolidColorBrush Idle = B("#7FB685");
    public static readonly SolidColorBrush Other = B("#8A9099");
    public static readonly SolidColorBrush Unmatched = B("#E07A7A");
}

/// <summary>
/// A row on the "Current" tab. Rows are updated in place on each refresh (keyed by PID) so tooltips,
/// scroll position and focus survive the 10-second auto-refresh.
/// </summary>
public sealed class CurrentRow : Observable
{
    private RunningSession _running;
    private SessionInfo? _info;
    private bool _folderMissing;

    /// <summary>Build off the UI thread: <paramref name="folderMissing"/> comes from <see cref="Services.FolderProbe"/>.</summary>
    public CurrentRow(RunningSession running, SessionInfo? info, bool folderMissing)
    {
        _running = running;
        _info = info;
        _folderMissing = folderMissing;
    }

    public void UpdateFrom(CurrentRow newer)
    {
        _running = newer._running;
        _info = newer._info;
        _folderMissing = newer._folderMissing;
        Raise(string.Empty); // refresh every binding on this row
    }

    public void RefreshTimes() => Raise(nameof(AgoText));

    public RunningSession Running => _running;
    public SessionInfo? Info => _info;

    public string? SessionId => Running.SessionId;
    public string? ProjectDir => Info?.ProjectDir ?? Running.Cwd;
    public string FolderName => Fmt.FolderName(ProjectDir);
    public bool FolderMissing => _folderMissing;
    public Visibility MissingVisibility => FolderMissing ? Visibility.Visible : Visibility.Collapsed;

    public string TitleText => Running.Name ?? Info?.Title ?? "";
    public Visibility TitleVisibility => TitleText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Only a matched session with a conversation file can be resumed by ID.</summary>
    public bool CanOpen => ProjectDir != null && SessionId != null && Info != null;
    public string OpenTooltip => CanOpen
        ? "Open this session in a new window (it's already running, so you'll be asked first)"
        : Running.State is RunState.Unmatched or RunState.Starting
            ? "Can't open: this process isn't matched to a session ID"
            : "Can't open: the session has no messages yet, so there's nothing to resume";
    public LaunchItem? ToLaunchItem() => CanOpen ? new LaunchItem(ProjectDir!, SessionId, FolderName) : null;

    public string Summary =>
        Running.State == RunState.Unmatched ? "Unmatched: " + (Running.Cwd == null ? "folder unknown" : "not in Claude's session registry")
        : Running.State == RunState.Starting ? "Starting…"
        : Info == null ? "New session (no messages yet)"
        : Fmt.Trim(Info.LastPrompt, 160);

    public DateTimeOffset? LastActive => Info?.LastActive ?? Running.StatusUpdatedAt ?? Running.StartedAt;
    public string AgoText => Fmt.Ago(LastActive);

    public string StatusText => Running.State switch
    {
        RunState.Working => "working",
        RunState.Waiting => "needs input",
        RunState.Idle => "idle",
        RunState.Unmatched => "unmatched",
        RunState.Starting => "starting",
        _ => Running.RawStatus ?? "unknown",
    };

    public Brush StatusBrush => Running.State switch
    {
        RunState.Working => Brushes2.Working,
        RunState.Waiting => Brushes2.Waiting,
        RunState.Idle => Brushes2.Idle,
        RunState.Unmatched => Brushes2.Unmatched,
        _ => Brushes2.Other,
    };

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { ProjectDir ?? "(folder unknown)" };
            if (Info?.Title is { } t) lines.Add("Title: " + t);
            if (SessionId != null) lines.Add("Session: " + SessionId);
            lines.Add("PID: " + Running.Pid + (Running.StartedAt is { } s ? "   started " + Fmt.When(s) : ""));
            if (Info?.LastPrompt is { } lp) lines.Add("Last message: " + Fmt.Trim(lp, 300));
            if (Running.Note != null) lines.Add(Running.Note);
            if (FolderMissing) lines.Add("Folder no longer exists.");
            return string.Join("\n", lines);
        }
    }
}

/// <summary>A row on the "Recent" tab.</summary>
public sealed class RecentRow : Observable
{
    private bool _isChecked;
    public required SessionInfo Info { get; init; }
    public bool IsRunning { get; init; }

    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    public string FolderName => Fmt.FolderName(Info.ProjectDir);
    /// <summary>Set off the UI thread via <see cref="Services.FolderProbe"/>.</summary>
    public bool FolderMissing { get; init; }
    public Visibility MissingVisibility => FolderMissing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;
    public string TitleText => Info.Title ?? "";
    public Visibility TitleVisibility => TitleText.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Summary => Fmt.Trim(Info.LastPrompt ?? Info.FirstPrompt, 160);
    public string WhenText => Fmt.When(Info.LastActive);
    public string ShortId => Fmt.ShortId(Info.SessionId);
    public bool CanOpen => Info.ProjectDir != null;
    public string OpenTooltip => CanOpen ? "Open this session in a new window" : "Can't open: the session's folder isn't known";
    public LaunchItem? ToLaunchItem() => CanOpen ? new LaunchItem(Info.ProjectDir!, Info.SessionId, FolderName) : null;
    public bool IsToday => Info.LastActive.ToLocalTime().Date == DateTime.Today;

    public string Tooltip
    {
        get
        {
            var lines = new List<string> { Info.ProjectDir ?? "(folder unknown)", "Session: " + Info.SessionId };
            if (Info.Title != null) lines.Add("Title: " + Info.Title);
            if (Info.Started is { } s) lines.Add("Started: " + Fmt.When(s));
            lines.Add("Last active: " + Fmt.When(Info.LastActive));
            if (Info.FirstPrompt != null) lines.Add("First message: " + Fmt.Trim(Info.FirstPrompt, 300));
            if (Info.LastPrompt != null && Info.LastPrompt != Info.FirstPrompt) lines.Add("Last message: " + Fmt.Trim(Info.LastPrompt, 300));
            if (Info.ContinuedIn != null) lines.Add("Continued in session " + Info.ContinuedIn);
            if (IsRunning) lines.Add("Currently running.");
            if (FolderMissing) lines.Add("Folder no longer exists; will be skipped on reopen.");
            return string.Join("\n", lines);
        }
    }
}

public sealed class SnapshotListItem
{
    public required SnapshotFile File { get; init; }
    public string Name => File.Snapshot?.Name ?? Path.GetFileName(File.Path);
    public string Meta => File.Snapshot is { } s
        ? $"{Fmt.When(s.CreatedAt)}  ·  {s.Sessions.Count} session{(s.Sessions.Count == 1 ? "" : "s")}"
        : File.Error ?? "";
}

public sealed class SnapshotSessionRow
{
    public required SnapshotSession Session { get; init; }
    public bool IsRunning { get; init; }
    public string FolderName => Fmt.FolderName(Session.ProjectDir);
    /// <summary>Set off the UI thread via <see cref="Services.FolderProbe"/>.</summary>
    public bool FolderMissing { get; init; }
    public Visibility MissingVisibility => FolderMissing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;
    public string Summary => Session.SessionId == null
        ? "New session in this folder"
        : Fmt.Trim(Session.Title is { } t && Session.Summary is { } s ? $"{t} — {s}" : Session.Title ?? Session.Summary, 160);
    public bool CanOpen => !string.IsNullOrWhiteSpace(Session.ProjectDir);
    public string OpenTooltip => !CanOpen ? "Can't open: no folder recorded"
        : Session.SessionId == null ? "Start a new Claude Code session in this folder (no session ID was saved)"
        : "Open this session in a new window";
    public LaunchItem? ToLaunchItem() => CanOpen
        ? new LaunchItem(Session.ProjectDir, Services.SessionStore.IsValidSessionId(Session.SessionId) ? Session.SessionId : null, FolderName)
        : null;
    public string Note => Session.Note ?? "";
    public Visibility NoteVisibility => string.IsNullOrWhiteSpace(Session.Note) ? Visibility.Collapsed : Visibility.Visible;
    public string Tooltip => string.Join("\n", new[]
    {
        Session.ProjectDir,
        Session.SessionId != null ? "Session: " + Session.SessionId : "No session id: restores as a fresh claude in this folder",
        Session.LastActive is { } la ? "Last active: " + Fmt.When(la) : null,
        FolderMissing ? "Folder no longer exists; will be skipped on restore." : null,
    }.Where(x => x != null));
}

/// <summary>A candidate row in the Save Snapshot dialog.</summary>
public sealed class SaveCandidate : Observable
{
    private bool _isChecked;
    private string _note = "";
    public string? SessionId { get; init; }
    public required string ProjectDir { get; init; }
    public string? Title { get; init; }
    public string? Summary { get; init; }
    public DateTimeOffset? LastActive { get; init; }
    public bool IsRunning { get; init; }

    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
    public string Note
    {
        get => _note;
        set { Set(ref _note, value); Raise(nameof(NotePlaceholderVisibility)); }
    }
    public Visibility NotePlaceholderVisibility => string.IsNullOrEmpty(_note) ? Visibility.Visible : Visibility.Collapsed;

    public string FolderName => Fmt.FolderName(ProjectDir);
    public string Detail => (IsRunning ? "running · " : "") + Fmt.When(LastActive) +
                            (SessionId == null ? " · new session" : "") +
                            (string.IsNullOrEmpty(Title ?? Summary) ? "" : "  —  " + Fmt.Trim(Title ?? Summary, 90));
    public string Tooltip => ProjectDir + (SessionId != null ? "\nSession: " + SessionId : "") +
                             (Summary != null ? "\n" + Fmt.Trim(Summary, 300) : "");
}
