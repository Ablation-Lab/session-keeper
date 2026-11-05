using System.Windows;
using System.Windows.Media;
using Stowdeck.Models;
using Stowdeck.Services;

namespace Stowdeck.ViewModels;

/// <summary>A row on the "Terminals" tab: a terminal session that isn't Claude Code. Updated in place on refresh.</summary>
public sealed class TerminalRow : Observable
{
    private TerminalSession _session;
    private bool _isChecked;

    public TerminalRow(TerminalSession session) => _session = session;

    public TerminalSession Session => _session;
    /// <summary>Identity across refreshes: the root process (PID + start time, so a reused PID is a new row).</summary>
    public (int, long?) Key => (_session.RootPid, _session.RootCreated);

    public void UpdateFrom(TerminalRow newer)
    {
        _session = newer._session;
        if (!CanSave) _isChecked = false; // its folder can't be read any more, so it can't be saved
        Raise(string.Empty);
    }

    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    public string Label => _session.Label;
    public string FolderText => _session.Folder == null ? "(folder unknown)" : Fmt.FolderName(_session.Folder);
    public string FolderTooltip => _session.Folder == null ? "The folder couldn't be read (the process may be elevated)."
        : _session.Folder + (_session.FolderIsStartFolder ? "\nThe folder the shell started in: PowerShell doesn't report a later cd." : "");
    public string CommandText => _session.CommandLine != null ? TerminalCommands.Pretty(_session.CommandLine)
        : _session.ShellName != null ? "(at the prompt)" : "";
    public string CommandTooltip => _session.CommandLine ?? "No program is running: the shell is at its prompt.";
    public string UptimeText => _session.StartedAt is { } s ? "up " + Fmt.Duration(DateTimeOffset.Now - s) : "";
    public string TypeText => _session.Type;
    public Brush TypeBrush => _session.Type switch { "agent" => Brushes2.Waiting, "program" => Brushes2.Working, _ => Brushes2.Other };
    public bool CanSave => _session.Folder != null;

    public string Tooltip
    {
        get
        {
            var s = _session;
            var lines = new List<string> { s.Label };
            if (s.AgentName != null) lines.Add("Agent: " + s.AgentName);
            if (s.ShellName != null) lines.Add("Shell: " + s.ShellName);
            lines.Add("In: " + (s.Host ?? "terminal"));
            lines.Add(FolderTooltip);
            if (s.CommandLine != null) lines.Add("Running: " + Fmt.Trim(s.CommandLine, 400));
            if (s.StartedAt is { } st)
                lines.Add("Opened " + Fmt.When(st) + (s.CommandStarted is { } cs && s.CommandPid != s.RootPid ? " · command started " + Fmt.When(cs) : ""));
            lines.Add("PID " + s.RootPid + (s.CommandPid is { } cp && cp != s.RootPid ? " · command PID " + cp : ""));
            if (!CanSave) lines.Add("Can't be saved: its folder is unknown.");
            return string.Join("\n", lines);
        }
    }

    /// <summary>Updates <paramref name="rows"/> in place (keyed by root process), keeping ticks, scroll and focus.</summary>
    public static void Sync(System.Collections.ObjectModel.ObservableCollection<TerminalRow> rows, IReadOnlyList<TerminalRow> fresh,
        Action<TerminalRow>? added = null, Action<TerminalRow>? removed = null)
    {
        var keys = fresh.Select(r => r.Key).ToHashSet();
        for (int i = rows.Count - 1; i >= 0; i--)
            if (!keys.Contains(rows[i].Key)) { removed?.Invoke(rows[i]); rows.RemoveAt(i); }
        for (int target = 0; target < fresh.Count; target++)
        {
            int existing = -1;
            for (int j = target; j < rows.Count; j++)
                if (rows[j].Key == fresh[target].Key) { existing = j; break; }
            if (existing < 0) { rows.Insert(target, fresh[target]); added?.Invoke(fresh[target]); continue; }
            if (existing != target) rows.Move(existing, target);
            rows[target].UpdateFrom(fresh[target]);
        }
    }
}

/// <summary>A terminal entry in a snapshot (shown alongside the Claude sessions).</summary>
public sealed class SnapshotTerminalRow : Observable, IProjectRow
{
    public required SnapshotTerminal Terminal { get; init; }
    /// <summary>Set off the UI thread via <see cref="Services.FolderProbe"/>.</summary>
    public bool FolderMissing { get; init; }

    /// <summary>Cleaned of control and invisible characters (the file may have been edited), at most 80 characters.</summary>
    public string MainLabel => NicknameStore.Clean(Terminal.Label) ?? "Terminal";
    public string SecondaryText => Fmt.FolderName(Terminal.ProjectDir);
    public Visibility MissingVisibility => FolderMissing ? Visibility.Visible : Visibility.Collapsed;
    public string Summary => Terminal.CommandLine != null ? TerminalCommands.Pretty(Terminal.CommandLine) : (Terminal.Shell ?? "Shell") + " at the prompt";
    public string Note => Fmt.Trim(Terminal.Note, 1000);
    public Visibility NoteVisibility => string.IsNullOrWhiteSpace(Terminal.Note) ? Visibility.Collapsed : Visibility.Visible;
    public bool CanOpen => !string.IsNullOrWhiteSpace(Terminal.ProjectDir);
    // Tools button and "uncommitted changes" badge (IProjectRow).
    public GitBadge Git { get; } = new();
    public string? ProjectFolder => Terminal.ProjectDir;
    public bool CanUseTools => ProjectRow.CanUseTools(this);
    public string ToolsTooltip => ProjectRow.ToolsTooltip(this);
    public string OpenTooltip => !CanOpen ? "Can't open: no folder recorded"
        : Terminal.CommandLine == null ? "Open the shell in this folder"
        : "Open a shell in this folder; you'll see the exact command before anything runs";

    public string Tooltip => string.Join("\n", new[]
    {
        MainLabel + "  (terminal)",
        Terminal.Agent != null ? "Agent: " + Terminal.Agent : null,
        Terminal.Shell != null ? "Shell: " + Terminal.Shell : null,
        string.IsNullOrWhiteSpace(Terminal.ProjectDir) ? "(folder unknown)" : Terminal.ProjectDir,
        Terminal.FolderIsStartFolder == true ? "(the folder the shell started in; it may have moved on with cd)" : null,
        Terminal.CommandLine != null ? "Command: " + Fmt.Trim(Terminal.CommandLine, 400) : "No command: reopens the shell only",
        FolderMissing ? "Folder no longer exists; will be skipped on restore." : null,
    }.Where(x => x != null));
}

/// <summary>A terminal in the Reopen confirmation: tick it to open, pick what it runs, see the exact command.</summary>
public sealed class TerminalRunRow : Observable
{
    private bool _isChecked;
    private TerminalOption _selected;

    public TerminalRunRow(TerminalPlan plan)
    {
        Plan = plan;
        _selected = plan.Default;
        _isChecked = plan.DefaultTicked;
        Choices = plan.Options.Select(o => new Choice(this, o)).ToList();
    }

    /// <summary>One of the row's options, as a radio button (the group name keeps each row's buttons separate).</summary>
    public sealed class Choice(TerminalRunRow row, TerminalOption option) : Observable
    {
        public TerminalOption Option => option;
        public string Title => option.Title;
        public string Group => row.Group;
        public bool IsSelected
        {
            get => ReferenceEquals(row.SelectedOption, option);
            set { if (value) row.SelectedOption = option; }
        }
        internal void Refresh() => Raise(nameof(IsSelected));
    }

    public TerminalPlan Plan { get; }
    public IReadOnlyList<TerminalOption> Options => Plan.Options;
    public IReadOnlyList<Choice> Choices { get; }
    public string Group { get; } = "run-" + Guid.NewGuid().ToString("N");
    public Visibility ChoiceVisibility => Plan.Options.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }

    public TerminalOption SelectedOption
    {
        get => _selected;
        set
        {
            if (value == null || ReferenceEquals(value, _selected)) return;
            // Moving to a choice that looks destructive unticks the row, so it never runs on one click.
            if (value.Warning != null && _selected.Warning == null) IsChecked = false;
            _selected = value;
            Raise(nameof(SelectedOption)); Raise(nameof(CommandText)); Raise(nameof(WarningText)); Raise(nameof(WarningVisibility));
            foreach (var c in Choices) c.Refresh();
        }
    }

    public string Label => NicknameStore.Clean(Plan.Entry.Label) ?? "Terminal";
    public string Folder => Plan.Entry.ProjectDir;
    public string CommandText => _selected.Command.Exe == null ? "(no command: PowerShell opens in the folder)" : _selected.Command.Display;
    public string? WarningText => _selected.Warning == null ? null
        : $"\u26A0 This looks destructive: {_selected.Warning}. Tick it only if you mean to run it.";
    public Visibility WarningVisibility => _selected.Warning == null ? Visibility.Collapsed : Visibility.Visible;
    public string? ProblemText => Plan.StillRunning
        ? "It's still running in a terminal now, so reopening it starts a second copy. Not ticked."
        : Plan.Problem == null ? null : "The saved command can't be re-run: " + Plan.Problem + ".";
    public Visibility ProblemVisibility => ProblemText == null ? Visibility.Collapsed : Visibility.Visible;

    public LaunchItem ToLaunchItem() => new(Plan.Entry.ProjectDir, null, Label, Label, _selected.Command);
}
