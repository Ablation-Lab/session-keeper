using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using Stowdeck.Models;
using Stowdeck.Services;
using Stowdeck.ViewModels;

namespace Stowdeck;

public partial class SaveSnapshotWindow : Window
{
    private readonly List<SaveCandidate> _candidates;

    public Snapshot? Result { get; private set; }

    public SaveSnapshotWindow(List<SaveCandidate> candidates)
    {
        InitializeComponent();
        _candidates = candidates;
        foreach (var c in _candidates) c.PropertyChanged += Candidate_PropertyChanged;
        List.ItemsSource = _candidates;
        NameBox.Text = SessionQueries.DefaultSnapshotName(DateTimeOffset.Now);
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
        UpdateCount();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void Candidate_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SaveCandidate.IsChecked)) UpdateCount();
    }

    private void UpdateCount()
    {
        int n = _candidates.Count(c => c.IsChecked);
        CountText.Text = $"{n} of {_candidates.Count} selected";
        SaveButton.IsEnabled = n > 0;
    }

    private void SnapshotNoteBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        SnapshotNotePlaceholder.Visibility = SnapshotNoteBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void All_Click(object sender, RoutedEventArgs e) { foreach (var c in _candidates) c.IsChecked = true; }
    private void None_Click(object sender, RoutedEventArgs e) { foreach (var c in _candidates) c.IsChecked = false; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }
        var picked = _candidates.Where(c => c.IsChecked).ToList();
        if (picked.Count == 0) return;

        Result = new Snapshot
        {
            Name = name,
            CreatedAt = DateTimeOffset.Now,
            Note = string.IsNullOrWhiteSpace(SnapshotNoteBox.Text) ? null : SnapshotNoteBox.Text.Trim(),
            Terminals = picked.Any(c => c.Terminal != null)
                ? picked.Where(c => c.Terminal != null).Select(c =>
                {
                    c.Terminal!.Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim();
                    return c.Terminal;
                }).ToList()
                : null,
            Sessions = picked.Where(c => c.Terminal == null).Select(c => new SnapshotSession
            {
                SessionId = c.SessionId,
                ProjectDir = c.ProjectDir,
                Nickname = c.Nickname,
                Title = c.Title,
                Summary = c.Summary,
                LastActive = c.LastActive,
                Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim(),
            }).ToList(),
        };
        DialogResult = true;
    }
}
