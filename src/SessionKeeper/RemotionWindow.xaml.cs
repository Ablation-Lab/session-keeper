using System.Windows;
using System.Windows.Interop;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper;

/// <summary>
/// Picks the Remotion project to open Studio for. Projects come from the projects folder; the one used last is picked
/// first, and each shows whether Studio is already running for it. Nothing is started from here: Start only returns
/// the choice, and the main window checks again and launches.
/// </summary>
public partial class RemotionWindow : Window
{
    private readonly List<RemotionRow> _rows;

    /// <summary>The chosen project once Start Studio was clicked.</summary>
    public RemotionProject? Project { get; private set; }

    /// <param name="checkRunning">Looks up which folders have a Studio running (run off the UI thread); null to skip.</param>
    public RemotionWindow(IReadOnlyList<RemotionProject> projects, string projectsFolder, string? lastUsed,
        Func<IReadOnlyList<string>, Dictionary<string, StudioInstance?>>? checkRunning = null)
    {
        InitializeComponent();
        _rows = projects.Select(p => new RemotionRow(p)).ToList();
        ProjectList.ItemsSource = _rows;
        HintText.Text = $"Folders in {projectsFolder} whose package.json uses Remotion.";
        EmptyText.Text = $"No Remotion projects found in {projectsFolder}.\nA project shows here when its package.json lists remotion or an @remotion/ package.";
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        StartButton.IsEnabled = _rows.Count > 0;
        ProjectList.SelectedItem = PickFirst(_rows, lastUsed);

        Loaded += async (_, _) =>
        {
            if (ProjectList.SelectedItem != null)
            {
                ProjectList.ScrollIntoView(ProjectList.SelectedItem);
                (ProjectList.ItemContainerGenerator.ContainerFromItem(ProjectList.SelectedItem) as UIElement)?.Focus();
            }
            if (checkRunning == null || _rows.Count == 0) return;
            try
            {
                var dirs = _rows.Select(r => r.Dir).ToList();
                var running = await Task.Run(() => checkRunning(dirs));
                foreach (var r in _rows) r.Running = running.GetValueOrDefault(r.Dir);
            }
            catch (Exception ex)
            {
                App.Log(ex); // the badges are a convenience: Start checks again anyway
            }
        };
    }

    /// <summary>The project used last if it's still listed, else the first.</summary>
    internal static RemotionRow? PickFirst(IReadOnlyList<RemotionRow> rows, string? lastUsed) =>
        rows.FirstOrDefault(r => lastUsed != null && string.Equals(r.Dir.TrimEnd('\\'), lastUsed.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        ?? rows.FirstOrDefault();

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void ProjectList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (ProjectList.SelectedItem is RemotionRow) Start_Click(sender, e);
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectList.SelectedItem is not RemotionRow row) return;
        Project = row.Project;
        DialogResult = true;
    }
}
