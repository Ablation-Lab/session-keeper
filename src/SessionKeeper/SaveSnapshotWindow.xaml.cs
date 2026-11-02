using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using SessionKeeper.Models;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper;

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
        NameBox.Text = "Snapshot " + DateTime.Now.ToString("ddd d MMM HH:mm");
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
            Sessions = picked.Select(c => new SnapshotSession
            {
                SessionId = c.SessionId,
                ProjectDir = c.ProjectDir,
                Title = c.Title,
                Summary = c.Summary,
                LastActive = c.LastActive,
                Note = string.IsNullOrWhiteSpace(c.Note) ? null : c.Note.Trim(),
            }).ToList(),
        };
        DialogResult = true;
    }
}
