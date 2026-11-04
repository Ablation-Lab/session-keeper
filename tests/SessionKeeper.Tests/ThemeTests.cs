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
                    new NewSessionWindow(new List<string> { @"C:\Projects\demo" }),
                    new HelpWindow(HelpContent.Terminals),
                    new TerminalRunWindow(new[] { Services.TerminalCommands.Plan(new Models.SnapshotTerminal
                        { Kind = "terminal", Label = "t", ProjectDir = @"C:\x", CommandLine = "\"C:\\Tools\\x.exe\" a" }) }, 1, "Restore"),
                };
                foreach (var w in windows)
                {
                    var wbg = (w.Background as SolidColorBrush)?.Color;
                    var wfg = (w.Foreground as SolidColorBrush)?.Color;
                    if (wbg != bg || wfg != fg) results.Add($"{w.GetType().Name}: background {wbg}, foreground {wfg}");
                    w.Close();
                }

                // Clicked controls must not keep an outline: focus is shown only for keyboard focus (FocusVisualStyle),
                // never by an IsKeyboardFocused template trigger (which also fires after a mouse click).
                var ring = app.Resources["KeyboardFocusRing"];
                foreach (var key in new object[] { typeof(System.Windows.Controls.Button), typeof(System.Windows.Controls.CheckBox),
                                                   typeof(System.Windows.Controls.RadioButton), "TabButton" })
                {
                    var style = (Style)app.Resources[key];
                    var focusStyle = style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == FrameworkElement.FocusVisualStyleProperty)?.Value;
                    if (!ReferenceEquals(focusStyle, ring)) results.Add($"{key}: not using the keyboard-only focus ring");
                    var template = style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == System.Windows.Controls.Control.TemplateProperty)?.Value
                        as System.Windows.Controls.ControlTemplate;
                    if (template?.Triggers.OfType<Trigger>().Any(t => t.Property == UIElement.IsKeyboardFocusedProperty) == true)
                        results.Add($"{key}: has a sticky IsKeyboardFocused trigger");
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
