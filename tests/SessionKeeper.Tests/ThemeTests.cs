using System.Windows;
using System.Windows.Media;
using SessionKeeper.ViewModels;

namespace SessionKeeper.Tests;

/// <summary>
/// Every window must use the dark theme. An implicit TargetType="Window" style silently doesn't apply to
/// Window subclasses, which once left all windows white with near-invisible light text.
/// Only one WPF Application can exist per process, so all windows are checked in this single test.
/// </summary>
public sealed class ThemeTests
{
    [Fact]
    public void AllWindowsUseTheDarkTheme()
    {
        var results = new List<string>();
        Exception? error = null;
        var t = new Thread(() =>
        {
            try
            {
                var app = new App();
                app.InitializeComponent();
                var bg = ((SolidColorBrush)app.Resources["Bg"]).Color;
                var fg = ((SolidColorBrush)app.Resources["Text"]).Color;
                var windows = new Window[]
                {
                    new MainWindow(),
                    new SettingsWindow(),
                    new InputDialog("t", "p", "v"),
                    new SaveSnapshotWindow(new List<SaveCandidate> { new() { ProjectDir = @"C:\x" } }),
                };
                foreach (var w in windows)
                {
                    var wbg = (w.Background as SolidColorBrush)?.Color;
                    var wfg = (w.Foreground as SolidColorBrush)?.Color;
                    if (wbg != bg || wfg != fg) results.Add($"{w.GetType().Name}: background {wbg}, foreground {wfg}");
                    w.Close();
                }
                app.Shutdown();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(TimeSpan.FromSeconds(60)), "window construction hung");
        Assert.Null(error);
        Assert.Empty(results);
    }
}
