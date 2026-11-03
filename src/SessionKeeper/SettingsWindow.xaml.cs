using System.Windows;
using System.Windows.Interop;
using SessionKeeper.Services;

namespace SessionKeeper;

public partial class SettingsWindow : Window
{
    private readonly WindowsIntegration _integration = new();
    private readonly string _exe = WindowsIntegration.ExePath;
    private bool _loading = true;

    public SettingsWindow()
    {
        InitializeComponent();
        ExePathRun.Text = _exe;
        // The registry and the shortcut file are the source of truth; nothing is stored elsewhere.
        StartWithWindows.IsChecked = _integration.IsStartWithWindowsEnabled;
        StartMenu.IsChecked = _integration.HasStartMenuShortcut;
        UpdateStartupWarning();

        var settings = AppSettings.Load();
        bool hasTerminal = Launcher.FindWindowsTerminal() != null;
        OpenInTerminal.IsEnabled = hasTerminal;
        TerminalMissingText.Visibility = hasTerminal ? Visibility.Collapsed : Visibility.Visible;
        (settings.OpenIn == OpenMode.WindowsTerminalTab && hasTerminal ? OpenInTerminal : OpenInPowerShell).IsChecked = true;
        ShowInstallNotice(settings);
        _loading = false;
    }

    private string? _repo;

    /// <summary>Explains when this copy isn't the installed one (for example the build folder), and how to install.</summary>
    private void ShowInstallNotice(AppSettings settings)
    {
        if (InstallLocation.IsInstalledCopy) return;
        _repo = InstallLocation.SourceRepo(settings, BuildInfo.Current);
        var folder = System.IO.Path.GetDirectoryName(_exe) ?? _exe;
        NotInstalledText.Text =
            $"It's running from {folder}. A build replaces that folder, so updates can fail while it's open, and " +
            "Start with Windows or the Start menu shortcut shouldn't point here.\n" +
            (_repo != null
                ? $"To install it, double-click install.cmd in {_repo}."
                : "To install it, double-click install.cmd in the SessionKeeper repository folder.");
        OpenRepoButton.Visibility = _repo != null ? Visibility.Visible : Visibility.Collapsed;
        NotInstalledNotice.Visibility = Visibility.Visible;
    }

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        if (_repo == null) return;
        Apply(() =>
        {
            using var action = LaunchGuard.UserAction(LaunchGuard.OpenRepoFolder);
            var explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var _ = LaunchGuard.Start(new System.Diagnostics.ProcessStartInfo(explorer, Launcher.WinQuote(_repo)) { UseShellExecute = false },
                "Explorer: repository folder (install.cmd)");
        }, () => { });
    }

    private void OpenIn_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var mode = OpenInTerminal.IsChecked == true ? OpenMode.WindowsTerminalTab : OpenMode.PowerShellWindow;
        Apply(() =>
        {
            var s = AppSettings.Load();
            s.OpenIn = mode;
            s.Save();
        }, () =>
        {
            var current = AppSettings.Load().OpenIn;
            (current == OpenMode.WindowsTerminalTab ? OpenInTerminal : OpenInPowerShell).IsChecked = true;
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Native.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
    }

    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Apply(() => _integration.SetStartWithWindows(StartWithWindows.IsChecked == true, _exe),
              () => StartWithWindows.IsChecked = _integration.IsStartWithWindowsEnabled);
        UpdateStartupWarning();
    }

    private void StartMenu_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Apply(() => _integration.SetStartMenuShortcut(StartMenu.IsChecked == true, _exe),
              () => StartMenu.IsChecked = _integration.HasStartMenuShortcut);
    }

    private void Apply(Action change, Action revert)
    {
        ErrorText.Visibility = Visibility.Collapsed;
        try
        {
            change();
        }
        catch (Exception ex)
        {
            App.Log(ex);
            ErrorText.Text = "Couldn't change that setting: " + ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            _loading = true;
            revert();
            _loading = false;
        }
    }

    private void UpdateStartupWarning()
    {
        if (_integration.StartupPointsElsewhere(_exe))
        {
            StartupWarning.Text = "Currently starts a different copy: " + _integration.GetStartupCommand() +
                                  ". Untick and tick to use this one.";
            StartupWarning.Visibility = Visibility.Visible;
        }
        else
        {
            StartupWarning.Visibility = Visibility.Collapsed;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
