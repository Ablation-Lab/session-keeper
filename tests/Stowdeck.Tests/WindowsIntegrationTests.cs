using Microsoft.Win32;
using Stowdeck.Services;

namespace Stowdeck.Tests;

/// <summary>Uses a throwaway HKCU key and a temp folder: never touches the real Run key or Start menu.</summary>
public sealed class WindowsIntegrationTests : IDisposable
{
    private readonly string _key = @"Software\Stowdeck.Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _menu = Path.Combine(Path.GetTempPath(), "sk-menu-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(@"Software\Stowdeck.Tests", throwOnMissingSubKey: false); } catch { }
        try { Directory.Delete(_menu, true); } catch { }
    }

    [Fact]
    public void StartWithWindowsWritesQuotedCommandAndRemovesIt()
    {
        var wi = new WindowsIntegration(_key, _menu);
        var exe = @"C:\Program Files\Stowdeck\Stowdeck.exe";
        Assert.False(wi.IsStartWithWindowsEnabled);

        wi.SetStartWithWindows(true, exe);
        Assert.Equal("\"" + exe + "\" --startup", wi.GetStartupCommand());
        Assert.False(wi.StartupPointsElsewhere(exe));
        Assert.True(wi.StartupPointsElsewhere(@"D:\Other\Stowdeck.exe"));

        wi.SetStartWithWindows(false, exe);
        Assert.False(wi.IsStartWithWindowsEnabled);
        wi.SetStartWithWindows(false, exe); // idempotent
    }

    [Fact]
    public void StartMenuShortcutPointsAtExe()
    {
        var wi = new WindowsIntegration(_key, _menu);
        var exe = Path.Combine(Environment.SystemDirectory, "notepad.exe"); // any existing exe
        wi.SetStartMenuShortcut(true, exe);
        Assert.True(wi.HasStartMenuShortcut);
        Assert.Equal(exe, WindowsIntegration.ReadShortcutTarget(wi.StartMenuShortcutPath), ignoreCase: true);

        wi.SetStartMenuShortcut(false, exe);
        Assert.False(wi.HasStartMenuShortcut);
    }

    [Fact]
    public void RejectsQuoteInExePath()
    {
        Assert.Throws<ArgumentException>(() => new WindowsIntegration(_key, _menu).SetStartWithWindows(true, "C:\\a\"b.exe"));
    }
}
