using System.Diagnostics;
using System.Text.RegularExpressions;
using Stowdeck.Services;

namespace Stowdeck.Tests;

[Collection(nameof(LaunchGuardTests))] // uses the static log-folder override
public sealed class LaunchGuardTests : IDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "sk-logs-" + Guid.NewGuid().ToString("N"));
    private static readonly string Wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");

    public LaunchGuardTests() => LaunchGuard.LogDirOverride = _logs;

    public void Dispose()
    {
        LaunchGuard.LogDirOverride = null;
        try { Directory.Delete(_logs, true); } catch { }
    }

    private string LogText() =>
        Directory.Exists(_logs) ? string.Concat(Directory.GetFiles(_logs, "launches-*.log").Select(File.ReadAllText)) : "";

    // ------------------------------------------------------------------ each button's user action
    // These open real user-action scopes, which log; they live here so the log goes to the temp folder.

    [Theory]
    [InlineData(LaunchGuard.Open, null)]          // Open / double-click: a PowerShell window via conhost
    [InlineData(LaunchGuard.NewSession, "name")]  // New session, with a name
    public void PowerShellWindowLaunchesAreAllowedOnlyInsideTheirUserAction(string action, string? title)
    {
        var psi = Launcher.BuildPowerShellWindowStart(new LaunchItem(@"C:\x", "aaaa1111-0000-4000-8000-000000000001", "x", title), "claude");
        Assert.NotNull(LaunchGuard.Check(psi)); // no user action: refused
        using (LaunchGuard.UserAction(action))
            Assert.Null(LaunchGuard.Check(psi));
        Assert.Contains("action=" + action, LogText()); // logged to the test folder, not the real log
    }

    [Fact]
    public void OpeningTheRepoFolderIsAnExplicitUserAction()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), Path.GetTempPath());
        Assert.NotNull(LaunchGuard.Check(psi));
        using (LaunchGuard.UserAction(LaunchGuard.OpenRepoFolder))
            Assert.Null(LaunchGuard.Check(psi));
        Assert.Contains("action=" + LaunchGuard.OpenRepoFolder, LogText());
    }

    [Fact]
    public void RefusesOutsideAUserAction()
    {
        Assert.Null(LaunchGuard.CurrentAction);
        Assert.Contains("explicit user action", LaunchGuard.Check(new ProcessStartInfo(@"C:\Windows\explorer.exe")));
    }

    [Fact]
    public void RefusesShellExecuteRelativePathsAndBareWt()
    {
        using var _ = LaunchGuard.UserAction(LaunchGuard.Reopen);
        Assert.Null(LaunchGuard.Check(new ProcessStartInfo(@"C:\Windows\explorer.exe", "C:\\")));
        Assert.Contains("shell", LaunchGuard.Check(new ProcessStartInfo("ms-settings:") { UseShellExecute = true }));
        Assert.Contains("full path", LaunchGuard.Check(new ProcessStartInfo("wt.exe", "new-tab x")));
        Assert.Contains("new-tab", LaunchGuard.Check(new ProcessStartInfo(Wt, "")));
        Assert.Contains("new-tab", LaunchGuard.Check(new ProcessStartInfo(Wt, "settings")));
        Assert.Null(LaunchGuard.Check(new ProcessStartInfo(Wt, "-w x new-tab --title t powershell.exe")));
    }

    [Fact]
    public async Task ScopesFlowIntoTasksEndWhenDisposedAndBackgroundClearsThem()
    {
        using (LaunchGuard.UserAction(LaunchGuard.Restore))
        {
            Assert.Equal(LaunchGuard.Restore, await Task.Run(() => LaunchGuard.CurrentAction));
            using (LaunchGuard.Background())
            {
                Assert.Null(LaunchGuard.CurrentAction);
                Assert.Null(await Task.Run(() => LaunchGuard.CurrentAction));
            }
            Assert.Equal(LaunchGuard.Restore, LaunchGuard.CurrentAction);
        }
        Assert.Null(LaunchGuard.CurrentAction);
        Assert.Throws<ArgumentException>(() => LaunchGuard.UserAction("Auto-refresh"));
    }

    [Fact]
    public void BlockedLaunchIsLoggedWithCallerAndNothingStarts()
    {
        var missing = Path.Combine(_logs, "never-started.exe"); // would throw if Process.Start were reached
        var ex = Assert.Throws<InvalidOperationException>(() => LaunchGuard.Start(new ProcessStartInfo(missing), "test"));
        Assert.Contains("Launch blocked", ex.Message);
        var log = LogText();
        Assert.Matches(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} .*BLOCKED .*action=\(none\) .*caller=LaunchGuardTests\.cs:\d+ BlockedLaunchIsLoggedWithCallerAndNothingStarts", log);
        Assert.DoesNotContain("LAUNCH ", log);
    }

    [Fact]
    public void LauncherRefusesWithoutAUserAction()
    {
        // The real launcher path, as auto-refresh would hit it if it ever tried: refused, logged, nothing opened.
        var result = Launcher.Launch(new[] { new LaunchItem(Path.GetTempPath(), null, "temp") });
        Assert.Empty(result.OpenedItems);
        Assert.Contains("Launch blocked", result.Error);
        Assert.Contains("BLOCKED", LogText());
    }

    [Fact]
    public void SessionDetectionNeverLaunches()
    {
        // A full detection pass against the real machine: process listing and file reads only.
        var running = SessionQueries.GetRunning(new RunningScanner(), new SessionStore());
        Assert.NotNull(running);
        var log = LogText();
        Assert.DoesNotContain("LAUNCH", log);
        Assert.DoesNotContain("BLOCKED", log);
    }

    /// <summary>Structural guard: no code in the app may start a process except through LaunchGuard.</summary>
    [Fact]
    public void OnlyLaunchGuardStartsProcesses()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Stowdeck.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        var src = Path.Combine(dir!.FullName, "src", "Stowdeck");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(@"\obj\") || file.Contains(@"\bin\")) continue;
            var text = File.ReadAllText(file);
            var name = Path.GetFileName(file);
            if (name != "LaunchGuard.cs" && Regex.IsMatch(text, @"Process\.Start\s*\(")) offenders.Add(name + ": Process.Start");
            if (Regex.IsMatch(text, @"UseShellExecute\s*=\s*true")) offenders.Add(name + ": UseShellExecute = true");
            if (Regex.IsMatch(text, @"ShellExecute(Ex)?\s*\(")) offenders.Add(name + ": ShellExecute");
        }
        Assert.Empty(offenders);
    }
}
