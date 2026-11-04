using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper;

/// <summary>
/// The confirmation shown before any saved terminal is reopened: every terminal, the exact command each would run
/// (built from the same values that are launched), a choice of run / resume / shell only, and a warning (with the
/// box left unticked) for commands that look destructive. Nothing runs unless it's ticked here.
/// </summary>
public partial class TerminalRunWindow : Window
{
    private readonly List<TerminalRunRow> _rows;
    private readonly int _claudeCount;

    /// <summary>What to open once the user confirms (only the ticked terminals, each with its chosen command).</summary>
    public List<LaunchItem> Items { get; } = new();

    public TerminalRunWindow(IReadOnlyList<TerminalPlan> plans, int claudeCount, string confirmText)
    {
        InitializeComponent();
        _claudeCount = claudeCount;
        _rows = plans.Select(p => new TerminalRunRow(p)).ToList();
        foreach (var r in _rows) r.PropertyChanged += Row_PropertyChanged;
        List.ItemsSource = _rows;
        if (claudeCount > 0)
        {
            ClaudeText.Text = $"{claudeCount} Claude Code session{(claudeCount == 1 ? "" : "s")} from this snapshot will reopen as well.";
            ClaudeText.Visibility = Visibility.Visible;
        }
        OpenButton.Content = confirmText;
        UpdateCount();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalRunRow.IsChecked)) UpdateCount();
    }

    private void UpdateCount()
    {
        int n = _rows.Count(r => r.IsChecked);
        CountText.Text = $"{n} of {_rows.Count} terminal{(_rows.Count == 1 ? "" : "s")} ticked";
        OpenButton.IsEnabled = n > 0 || _claudeCount > 0;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        Items.AddRange(_rows.Where(r => r.IsChecked).Select(r => r.ToLaunchItem()));
        DialogResult = true;
    }
}
