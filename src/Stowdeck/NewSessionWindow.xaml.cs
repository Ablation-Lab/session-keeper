using System.IO;
using System.Windows;
using System.Windows.Interop;
using Stowdeck.Services;

namespace Stowdeck;

/// <summary>Picks a project folder (folder picker or a recent one) and an optional name for a new session.</summary>
public partial class NewSessionWindow : Window
{
    public sealed record FolderChoice(string Path, string Name);

    /// <summary>The chosen folder (full path) once Start was clicked.</summary>
    public string? Folder { get; private set; }

    /// <summary>The optional name, already cleaned (null when left empty).</summary>
    public string? SessionName { get; private set; }

    public NewSessionWindow(IReadOnlyList<string> recentFolders)
    {
        InitializeComponent();
        var choices = recentFolders.Select(p => new FolderChoice(p, ViewModels.Fmt.FolderName(p))).ToList();
        RecentFolders.ItemsSource = choices;
        NoRecentText.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (choices.Count > 0) FolderBox.Text = choices[0].Path;
        Loaded += (_, _) => { FolderBox.Focus(); FolderBox.SelectAll(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the project folder for the new session" };
        try
        {
            var current = FolderBox.Text.Trim();
            if (current.Length > 0 && Path.IsPathFullyQualified(current) && Directory.Exists(current)) dialog.InitialDirectory = current;
        }
        catch { /* odd text in the box: just open the picker at its default place */ }
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void RecentFolders_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (RecentFolders.SelectedItem is FolderChoice c) FolderBox.Text = c.Path;
    }

    private void RecentFolders_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecentFolders.SelectedItem is FolderChoice) Start_Click(sender, e);
    }

    private void FolderBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        ErrorText.Visibility = Visibility.Collapsed;

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var why = CheckFolder(FolderBox.Text, out var full);
        if (why != null)
        {
            ErrorText.Text = why;
            ErrorText.Visibility = Visibility.Visible;
            FolderBox.Focus();
            return;
        }
        Folder = full;
        SessionName = NicknameStore.Clean(NameBox.Text);
        DialogResult = true;
    }

    /// <summary>Why a folder can't be used, or null (with its full, normalised path).</summary>
    internal static string? CheckFolder(string? text, out string full)
    {
        full = "";
        var t = (text ?? "").Trim().Trim('"');
        if (t.Length == 0) return "Choose a project folder.";
        try
        {
            if (!Path.IsPathFullyQualified(t)) return "Enter a full folder path, such as C:\\Projects\\my-app, or use Browse….";
            full = Path.GetFullPath(t);
            if (full.Length > 3) full = full.TrimEnd('\\', '/');
        }
        catch
        {
            return "That isn't a valid folder path.";
        }
        if (Launcher.Validate(new LaunchItem(full, null, "new session")) is { } problem) return "Can't start there: " + problem + ".";
        return null;
    }
}
