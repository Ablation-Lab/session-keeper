using System.Diagnostics;
using System.Text;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

/// <summary>How a confirmed terminal entry is turned into a window: validation, escaping, and a real run of the script.</summary>
public sealed class TerminalLaunchTests : IDisposable
{
    private static readonly string System32 = Environment.GetFolderPath(Environment.SpecialFolder.System);
    private static readonly string CmdExe = Path.Combine(System32, "cmd.exe");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sk-term '$(x)' " + Guid.NewGuid().ToString("N")[..8]);

    public TerminalLaunchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static TerminalCommand Run(string exe, string args) => new(exe, args, TerminalCommands.Show(exe, args), false);

    [Fact]
    public void ValidationAcceptsOnlyAnExistingExeAndOneLineOfArguments()
    {
        Assert.Null(Launcher.Validate(new LaunchItem(_dir, null, "t", null, Run(CmdExe, "/c echo hi"))));
        Assert.Null(Launcher.Validate(new LaunchItem(_dir, null, "t", null, new TerminalCommand(null, "", "PowerShell", true))));

        Assert.NotNull(Launcher.Validate(new LaunchItem(_dir, null, "t", null, Run(Path.Combine(_dir, "missing.exe"), ""))));
        Assert.NotNull(Launcher.Validate(new LaunchItem(_dir, null, "t", null, Run(Path.Combine(System32, "notepad.cmd"), ""))));
        Assert.NotNull(Launcher.Validate(new LaunchItem(_dir, null, "t", null, Run("cmd.exe", "/c echo"))));
        Assert.NotNull(Launcher.Validate(new LaunchItem(_dir, null, "t", null, Run(CmdExe, "/c echo a\nRemove-Item x"))));
        Assert.NotNull(Launcher.Validate(new LaunchItem(_dir, null, "t", null, new TerminalCommand(null, "/c x", "x", false))));
        Assert.NotNull(Launcher.Validate(new LaunchItem(_dir, "11111111-2222-3333-4444-555555555555", "t", null, Run(CmdExe, ""))));
    }

    [Fact]
    public void EveryDynamicValueInTheScriptIsASingleQuotedLiteral()
    {
        var args = "/c echo a'b \"c\" $(Get-Date); `whoami` @{x=1}";
        var script = Launcher.BuildTerminalScript(new LaunchItem(_dir, null, "Label '$(y)'", null, Run(CmdExe, args)));
        Assert.Contains("Set-Location -LiteralPath " + Launcher.PsQuote(_dir) + ";", script);
        Assert.Contains("$skStart.FileName = " + Launcher.PsQuote(CmdExe) + ";", script);
        Assert.Contains("$skStart.Arguments = " + Launcher.PsQuote(args) + ";", script);
        Assert.DoesNotContain("Invoke-Expression", script);
        Assert.DoesNotContain("& " + Launcher.PsQuote(CmdExe), script); // never handed to PowerShell's own argument parsing
        Assert.DoesNotContain("--resume", script);
    }

    [Fact]
    public void AShellOnlyEntryRunsNothing()
    {
        var script = Launcher.BuildTerminalScript(new LaunchItem(_dir, null, "t", null, new TerminalCommand(null, "", "PowerShell", true)));
        Assert.DoesNotContain("ProcessStartInfo", script);
        Assert.Contains("Set-Location -LiteralPath", script);
    }

    [Fact]
    public void BuildingTheScriptRefusesUncheckedCommands()
    {
        Assert.Throws<ArgumentException>(() => Launcher.BuildTerminalScript(new LaunchItem(_dir, null, "t", null, Run("cmd.exe", ""))));
        Assert.Throws<ArgumentException>(() => Launcher.BuildTerminalScript(new LaunchItem(_dir, null, "t", null, Run(CmdExe, "a\rb"))));
    }

    [Fact]
    public void TerminalTabsUseTheLabelAsTitleWithoutClaudesTitleLock()
    {
        var tab = Launcher.BuildTabArgs(new LaunchItem(_dir, null, "npm; run", "npm; run", Run(CmdExe, "/c npm run dev")), "claude");
        Assert.StartsWith("new-tab --title \"npm run\"", tab);
        Assert.DoesNotContain("--suppressApplicationTitle", tab);
        Assert.DoesNotContain("/c npm", tab); // the command only travels inside the base64 script
    }

    [Fact]
    public void AMixedRestoreSkipsTheClaudeOnlyChecksForTerminals()
    {
        var r = Launcher.Validate(new LaunchItem(@"\\server\share\x", null, "t", null, new TerminalCommand(null, "", "PowerShell", true)));
        Assert.NotNull(r); // folder doesn't exist here, but the reason isn't about claude.cmd
        Assert.DoesNotContain("claude", r);
    }

    /// <summary>
    /// Runs the generated script in a hidden PowerShell (not through LaunchGuard: nothing user-visible opens) and
    /// checks that cmd received the arguments exactly: no PowerShell expansion of $(...), quotes or backticks.
    /// </summary>
    [Fact]
    public void TheScriptHandsTheArgumentsToWindowsUnchanged()
    {
        var output = Path.Combine(_dir, "out.txt");
        var marker = "SK:a'b;$(whoami)`n@{x}%%";
        var args = "/d /c echo " + marker + "> \"" + output + "\"";
        var script = Launcher.BuildTerminalScript(new LaunchItem(_dir, null, "test", null, Run(CmdExe, args)));

        var psi = new ProcessStartInfo(Launcher.PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Launcher.Encode(script) }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000), "PowerShell didn't finish");
        }

        // Byte for byte what was saved: nothing expanded by PowerShell (cmd leaves %% alone on a command line).
        Assert.Equal(marker, File.ReadAllText(output, Encoding.Default).TrimEnd('\r', '\n', ' '));
    }
}
