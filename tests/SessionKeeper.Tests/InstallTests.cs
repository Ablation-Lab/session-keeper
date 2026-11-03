using System.Diagnostics;
using System.Text;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

public sealed class InstallTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sk-install-" + Guid.NewGuid().ToString("N"));

    public InstallTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "SessionKeeper.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void InstalledCopiesAreRecognisedByTheMarkerFile()
    {
        Assert.False(InstallLocation.IsInstalledFolder(_dir));
        File.WriteAllText(Path.Combine(_dir, InstallLocation.MarkerFileName), "{}");
        Assert.True(InstallLocation.IsInstalledFolder(_dir));
        Assert.False(InstallLocation.IsInstalledFolder(null));
        Assert.False(InstallLocation.IsInstalledCopy); // the test host runs from a build folder
    }

    [Fact]
    public void SourceRepoPrefersTheSettingAndIgnoresMissingFolders()
    {
        var build = new BuildInfo("1.0.0", null, null, Path.Combine(_dir, "missing-repo"), false);
        Assert.Null(InstallLocation.SourceRepo(new AppSettings(), build));
        Assert.Equal(_dir.TrimEnd('\\'), InstallLocation.SourceRepo(new AppSettings { SourceRepo = _dir }, build));
        Assert.Equal(_dir.TrimEnd('\\'), InstallLocation.SourceRepo(new AppSettings(), build with { SourceRepo = _dir }));
    }

    [Fact]
    public void OpeningTheRepoFolderIsAnExplicitUserAction()
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), _dir);
        Assert.NotNull(LaunchGuard.Check(psi));
        using (LaunchGuard.UserAction(LaunchGuard.OpenRepoFolder))
            Assert.Null(LaunchGuard.Check(psi));
    }

    [Fact]
    public void InstallCmdRunsTheScriptWithBypassAndPauses()
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "install.cmd"));
        var text = Encoding.ASCII.GetString(bytes);
        Assert.DoesNotContain("\n", text.Replace("\r\n", "")); // CRLF only: cmd.exe mishandles LF-only batch files
        Assert.DoesNotContain(bytes, b => b < 32 && b != 10 && b != 13); // no stray control characters (once, "\v" became one)
        Assert.Contains(@"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe", text);
        Assert.Contains("-ExecutionPolicy Bypass -File \"%~dp0install.ps1\" -Interactive", text);
        Assert.Contains("pause", text);
        Assert.Contains("exit /b %RC%", text);
    }

    private static (int Code, string Out) RunPowerShell(params string[] args)
    {
        var psi = new ProcessStartInfo(Launcher.PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(60_000), "powershell did not exit");
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    [Fact]
    public void InstallScriptParsesWithoutErrors()
    {
        var script = Path.Combine(RepoRoot(), "install.ps1");
        // -Command doesn't pass extra arguments as $args, so the path goes in as a quoted literal.
        var (code, output) = RunPowerShell("-NoProfile", "-NonInteractive", "-Command",
            "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseFile(" + Launcher.PsQuote(script) + ", [ref]$null, [ref]$e); " +
            "$e | ForEach-Object { $_.Message }; exit $e.Count");
        Assert.True(code == 0, output);
    }

    [Fact]
    public void InstallRefusesProgramFilesBeforeBuildingAnything()
    {
        var script = Path.Combine(RepoRoot(), "install.ps1");
        var target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SessionKeeper");
        var sw = Stopwatch.StartNew();
        var (code, output) = RunPowerShell("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
            "-File", script, "-Destination", target, "-NoStartMenu", "-NoSkill");
        Assert.NotEqual(0, code);
        Assert.Contains("Program Files", output);
        Assert.DoesNotContain("Building", output);
        Assert.False(Directory.Exists(target) && File.Exists(Path.Combine(target, "SessionKeeper.exe")));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30), "it should stop before building");
    }
}
