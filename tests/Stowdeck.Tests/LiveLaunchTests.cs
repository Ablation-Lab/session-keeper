using Stowdeck.Models;
using Stowdeck.Services;
using Xunit.Abstractions;

namespace Stowdeck.Tests;

/// <summary>
/// Opens real Windows Terminal tabs with a fresh claude in two scratch folders (one with ';' and an
/// apostrophe in its name), then checks the scanner sees them. No existing conversation is touched.
/// Runs only when SK_LAUNCH=1. Close the two tabs afterwards.
/// </summary>
public sealed class LiveLaunchTests(ITestOutputHelper output)
{
    [SkippableFact]
    public void LiveLaunch()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("SK_LAUNCH") == "1", "Opens real terminal tabs; set SK_LAUNCH=1 to run.");

        var baseDir = Path.Combine(Path.GetTempPath(), "sk-launch-test");
        var a = Path.Combine(baseDir, "plain folder");
        var b = Path.Combine(baseDir, "semi;colon it's");
        Directory.CreateDirectory(a);
        Directory.CreateDirectory(b);

        LaunchResult result;
        using (LaunchGuard.UserAction(LaunchGuard.Reopen))
            result = Launcher.Launch(new[] { new LaunchItem(a, null, "plain folder"), new LaunchItem(b, null, "semi;colon it's") });
        output.WriteLine($"opened={result.OpenedItems.Count} skipped={result.Skipped.Count} error={result.Error} psFallback={result.UsedPowerShellWindows}");
        Assert.Null(result.Error);
        Assert.Equal(2, result.OpenedItems.Count);

        List<RunningSession> found = new();
        for (int i = 0; i < 20 && found.Count < 2; i++)
        {
            Thread.Sleep(1000);
            found = new RunningScanner().Scan()
                .Where(r => r.Cwd != null && (r.Cwd.Equals(a, StringComparison.OrdinalIgnoreCase) || r.Cwd.Equals(b, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        foreach (var r in found) output.WriteLine($"pid {r.Pid} {r.State} {r.SessionId} {r.Cwd}");
        Assert.Equal(2, found.Count);
    }
}
