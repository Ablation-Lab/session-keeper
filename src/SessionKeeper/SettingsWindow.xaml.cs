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
        _loading = false;
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
