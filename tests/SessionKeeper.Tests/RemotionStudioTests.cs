using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper.Tests;

/// <summary>The Remotion Studio button: finding projects, the command, spotting a running Studio, and launching safely.</summary>
public sealed class RemotionStudioTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sk-remotion " + Guid.NewGuid().ToString("N")[..8]);

    public RemotionStudioTests() => Directory.CreateDirectory(_root);
    // The \?\ form, so a test folder whose name ends in a dot can be deleted too.
    public void Dispose() { try { Directory.Delete(@"\\?\" + _root, true); } catch { } }

    private string Project(string name, string packageJson, bool installed = false)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "package.json"), packageJson);
        if (installed) Directory.CreateDirectory(Path.Combine(dir, "node_modules", "@remotion", "cli"));
        return dir;
    }

    private string Tool(string name)
    {
        var dir = Path.Combine(_root, "tools");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "@echo off\r\n");
        return path;
    }

    private static bool Uses(string json) => RemotionStudio.UsesRemotion(JsonDocument.Parse(json).RootElement);

    // ------------------------------------------------------------------ projects

    [Fact]
    public void AProjectUsesRemotionWhenADependencyListNamesIt()
    {
        Assert.True(Uses("""{"dependencies":{"remotion":"4.0.0"}}"""));
        Assert.True(Uses("""{"devDependencies":{"@remotion/cli":"4.0.0"}}"""));
        Assert.True(Uses("""{"peerDependencies":{"@remotion/player":"4.0.0"}}"""));
        Assert.False(Uses("""{"dependencies":{"remotion-like":"1.0.0","react":"18"}}"""));
        Assert.False(Uses("""{"scripts":{"dev":"remotion studio"}}""")); // only a script, no dependency
        Assert.False(Uses("""{"dependencies":["remotion"]}"""));
        Assert.False(Uses("""["remotion"]"""));
    }

    [Fact]
    public void FindProjectsListsOnlyRemotionFoldersByName()
    {
        Project("beta", """{"devDependencies":{"@remotion/cli":"4.0.0"}}""");
        Project("alpha", """{"scripts":{"dev":"remotion studio"},"dependencies":{"remotion":"4.0.0"}}""", installed: true);
        Project("plain", """{"dependencies":{"react":"18"}}""");
        Project("broken", "{ not json");
        Project(".hidden", """{"dependencies":{"remotion":"4.0.0"}}""");
        Directory.CreateDirectory(Path.Combine(_root, "empty"));

        var found = RemotionStudio.FindProjects(_root);
        Assert.Equal(new[] { "alpha", "beta" }, found.Select(p => p.Name));
        Assert.Equal("remotion studio", found[0].DevScript);
        Assert.True(found[0].Installed);
        Assert.Null(found[1].DevScript);
        Assert.False(found[1].Installed);
        Assert.True(found[0].DevRunsStudio);
        Assert.False(found[1].DevRunsStudio);
        Assert.Equal(Path.Combine(_root, "alpha"), found[0].Dir);
    }

    [Fact]
    public void AMissingOrRelativeProjectsFolderFindsNothing()
    {
        Assert.Empty(RemotionStudio.FindProjects(Path.Combine(_root, "nope")));
        Assert.Empty(RemotionStudio.FindProjects("relative"));
    }

    [Fact]
    public void AnEmptyDevScriptCountsAsNone()
    {
        var dir = Project("demo", """{"scripts":{"dev":"  "},"dependencies":{"remotion":"4"}}""");
        Assert.Null(RemotionStudio.ReadProject(dir)!.DevScript);
    }

    // ------------------------------------------------------------------ the command

    [Fact]
    public void TheDevScriptIsRunWithNpmCmdElseNpxCmdRemotionStudio()
    {
        var npm = Tool("npm.cmd");
        var npx = Tool("npx.cmd");
        string? Find(string name) => name == "npm.cmd" ? npm : name == "npx.cmd" ? npx : null;
        var withDev = new RemotionProject("demo", Path.Combine(_root, "demo"), "remotion studio", true);
        var withoutDev = withDev with { DevScript = null };

        var a = RemotionStudio.CommandFor(withDev, Find, out var p1);
        Assert.Null(p1);
        Assert.Equal(npm, a!.Exe);
        Assert.Equal(new[] { "run", "dev" }, a.Args);
        Assert.Equal("npm.cmd run dev", a.Display);

        var b = RemotionStudio.CommandFor(withoutDev, Find, out _);
        Assert.Equal(npx, b!.Exe);
        Assert.Equal(new[] { "remotion", "studio" }, b.Args);

        Assert.Null(RemotionStudio.CommandFor(withDev, _ => null, out var p3));
        Assert.Contains("npm.cmd wasn't found", p3);

        // A dev script that isn't Studio (Remotion's Next.js template: "next dev") isn't used.
        var next = RemotionStudio.CommandFor(withDev with { DevScript = "next dev" }, Find, out _);
        Assert.Equal(npx, next!.Exe);
    }

    [Theory]
    [InlineData("remotion studio", true)]
    [InlineData("remotion studio src/index.ts --port 3100", true)]
    [InlineData("npx remotion studio", true)]
    [InlineData("REMOTION Preview", true)]
    [InlineData("node scripts/x.mjs && remotion studio", true)]
    [InlineData("node_modules/.bin/remotion studio", true)]
    [InlineData("next dev", false)]
    [InlineData("remotion render Main out.mp4", false)]
    [InlineData("remotion studio-helper", false)]
    [InlineData("myremotion studio", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TheDevScriptCountsOnlyWhenItRunsStudio(string? script, bool runs) => Assert.Equal(runs, RemotionStudio.RunsStudio(script));

    [Theory]
    [InlineData("demo&calc&")]
    [InlineData("a|b")]
    [InlineData("a^b")]
    [InlineData("100%")]
    [InlineData("a>b")]
    public void FoldersWithCmdCommandCharactersAreRefused(string name)
    {
        var dir = @"C:\Projects\" + name;
        Assert.Contains("command character", RemotionStudio.CheckFolder(dir));
        Assert.Throws<ArgumentException>(() => Launcher.BuildStudioScript(new LaunchItem(dir, null, "demo", Studio: NpmDev())));
    }

    [Theory]
    [InlineData("prefix=fake", true)]
    [InlineData(@"  prefix = C:\x  ", true)]
    [InlineData("\"prefix\"=fake", true)]
    [InlineData("PREFIX=fake", true)]
    [InlineData("globalconfig=other.npmrc", true)]
    [InlineData("userconfig=other.npmrc", true)]
    [InlineData("# prefix=fake", false)]
    [InlineData("; prefix=fake", false)]
    [InlineData("registry=https://registry.npmjs.org/\nsave-exact=true", false)]
    public void AnNpmrcThatRedirectsNpmIsRefused(string npmrc, bool refused)
    {
        var dir = Project("demo", """{"scripts":{"dev":"remotion studio"},"dependencies":{"remotion":"4"}}""");
        File.WriteAllText(Path.Combine(dir, ".npmrc"), npmrc);
        Assert.Equal(refused, RemotionStudio.CheckFolder(dir) != null);
        Assert.Equal(refused, Launcher.Validate(new LaunchItem(dir, null, "demo", Studio: NpmDev())) != null);
    }

    [Fact]
    public void FolderNamesEndingInADotAreSkipped()
    {
        Project("demo", """{"dependencies":{"remotion":"4"}}""");
        // Windows APIs trim a trailing dot, so "demo." would read demo's package.json and share its status.
        var trailing = @"\\?\" + Path.Combine(_root, "demo.");
        Directory.CreateDirectory(trailing);
        File.WriteAllText(trailing + @"\package.json", """{"dependencies":{"remotion":"4"}}""");
        Assert.Equal(new[] { "demo" }, RemotionStudio.FindProjects(_root).Select(p => p.Name));
        Assert.NotNull(RemotionStudio.CheckFolder(Path.Combine(_root, "demo.")));
    }

    [Fact]
    public void OnlyTheTwoFixedCommandsFromAPlainLocalPathAreAllowed()
    {
        var dir = Path.Combine(_root, "demo");
        var npm = Tool("npm.cmd");
        var npx = Tool("npx.cmd");
        Assert.Null(RemotionStudio.Check(new StudioCommand(npm, new[] { "run", "dev" }), dir));
        Assert.Null(RemotionStudio.Check(new StudioCommand(npx, new[] { "remotion", "studio" }), dir));

        Assert.NotNull(RemotionStudio.Check(new StudioCommand(npm, new[] { "run", "build" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand(npm, new[] { "remotion", "studio" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand(npx, new[] { "run", "dev" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand(npm, new[] { "run", "dev", "--", "x" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand(Tool("npm.exe"), new[] { "run", "dev" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand("npm.cmd", new[] { "run", "dev" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand(@"\\server\share\npm.cmd", new[] { "run", "dev" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand(@"C:\Tools\..\x\npm.cmd", new[] { "run", "dev" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand(@"C:\%TEMP%\npm.cmd", new[] { "run", "dev" }), dir));
        Assert.NotNull(RemotionStudio.Check(new StudioCommand("C:\\Tools\u202E\\npm.cmd", new[] { "run", "dev" }), dir));
    }

    [Fact]
    public void AProjectCantSupplyItsOwnNpm()
    {
        var dir = Project("demo", """{"scripts":{"dev":"x"},"dependencies":{"remotion":"4"}}""");
        var planted = Path.Combine(dir, "npm.cmd");
        File.WriteAllText(planted, "");
        var why = RemotionStudio.Check(new StudioCommand(planted, new[] { "run", "dev" }), dir);
        Assert.Contains("inside the project", why);
        // A similarly named sibling folder isn't "inside".
        Assert.Null(RemotionStudio.Check(new StudioCommand(Path.Combine(_root, "demo-2", "npm.cmd"), new[] { "run", "dev" }), dir));
    }

    // ------------------------------------------------------------------ already running?

    private const string Demo = @"C:\Projects\demo";

    [Theory]
    [InlineData("\"node\"   \"C:\\Projects\\demo\\node_modules\\.bin\\\\..\\@remotion\\cli\\remotion-cli.js\" studio", true)]
    [InlineData("node C:\\Projects\\demo\\node_modules\\@remotion\\cli\\remotion-cli.js --log=verbose studio src/index.ts", true)]
    [InlineData("node C:\\Projects\\demo\\node_modules\\@remotion\\cli\\remotion-cli.js preview", true)]
    [InlineData("node \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npx-cli.js\" remotion studio", true)]
    [InlineData("node C:\\Projects\\demo\\node_modules\\@remotion\\cli\\remotion-cli.js render Main out.mp4", false)]
    [InlineData("node C:\\Projects\\demo\\node_modules\\@remotion\\cli\\remotion-cli.js render studio", false)]
    [InlineData("node C:\\Projects\\demo\\server.js studio", false)]
    [InlineData("node \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js\" run dev", false)]
    [InlineData("node", false)]
    public void StudioCommandLinesAreRecognised(string commandLine, bool isStudio) =>
        Assert.Equal(isStudio, RemotionStudio.StudioScript(commandLine) != null);

    [Theory]
    // The working folder decides.
    [InlineData(@"C:\Projects\demo\node_modules\@remotion\cli\remotion-cli.js", @"C:\Projects\demo", true)]
    [InlineData(@"C:\Projects\demo\node_modules\@remotion\cli\remotion-cli.js", @"C:\Projects\demo\", true)]
    [InlineData("remotion", @"c:\projects\DEMO", true)]
    [InlineData("remotion", @"C:\Projects\demo-2", false)]
    [InlineData(@"C:\Projects\demo\node_modules\@remotion\cli\remotion-cli.js", @"C:\Projects\other", false)]
    // A subfolder only counts with the project's own CLI.
    [InlineData(@"C:\Projects\demo\node_modules\@remotion\cli\remotion-cli.js", @"C:\Projects\demo\src", true)]
    [InlineData(@"D:\Tools\npm\node_modules\@remotion\cli\remotion-cli.js", @"C:\Projects\demo\src", false)]
    [InlineData(@"node_modules\@remotion\cli\remotion-cli.js", @"C:\Projects\demo\sub", false)]
    // Working folder unreadable: the CLI's location is the evidence.
    [InlineData(@"C:\Projects\demo\node_modules\.bin\..\@remotion\cli\remotion-cli.js", null, true)]
    [InlineData(@"C:\Projects\demo-2\node_modules\@remotion\cli\remotion-cli.js", null, false)]
    [InlineData(@"C:\Projects\demo\sub\node_modules\@remotion\cli\remotion-cli.js", null, false)]
    [InlineData(@"C:\Projects\demo\node_modules\..\..\other\node_modules\@remotion\cli\remotion-cli.js", null, false)]
    [InlineData("remotion", null, false)]
    public void AStudioBelongsOnlyToItsOwnFolder(string script, string? cwd, bool belongs) =>
        Assert.Equal(belongs, RemotionStudio.BelongsTo(Demo, script, cwd));

    private sealed class FakeSystem
    {
        public readonly List<Native.ProcEntry> Processes = new();
        public readonly Dictionary<int, string> CommandLines = new();
        public readonly Dictionary<int, string> Folders = new();
        public readonly List<Native.Listener> Listeners = new();
        public int CommandLineReads;

        public void Add(int pid, int parent, string exe, string cmd, string? cwd)
        {
            Processes.Add(new Native.ProcEntry(pid, parent, exe));
            CommandLines[pid] = cmd;
            if (cwd != null) Folders[pid] = cwd;
        }

        public RemotionStudio.Source Source => new(
            () => Processes,
            pid => { CommandLineReads++; return CommandLines.GetValueOrDefault(pid); },
            pid => Folders.GetValueOrDefault(pid),
            () => Listeners);
    }

    private static string Cli(string project) => $"\"node\" \"{project}\\node_modules\\.bin\\\\..\\@remotion\\cli\\remotion-cli.js\" studio";

    [Fact]
    public void ARunningStudioIsFoundWithItsLowestPort()
    {
        var sys = new FakeSystem();
        // The npm run dev chain for demo: npm (node), cmd /c remotion studio, then Remotion's CLI, which listens.
        sys.Add(10, 1, "node.exe", "\"C:\\Program Files\\nodejs\\node.exe\" npm-cli.js run dev", Demo);
        sys.Add(11, 10, "cmd.exe", "C:\\WINDOWS\\system32\\cmd.exe /d /s /c remotion studio", Demo);
        sys.Add(12, 11, "node.exe", Cli(Demo), Demo);
        // Another project's Studio on the next port.
        sys.Add(20, 1, "node.exe", Cli(@"C:\Projects\demo-2"), @"C:\Projects\demo-2");
        sys.Listeners.AddRange(new Native.Listener[] { new(12, 63756), new(12, 3000), new(20, 3001), new(99, 80) });

        var demo = RemotionStudio.FindRunning(Demo, sys.Source);
        Assert.Equal(new[] { 12 }, demo!.Pids);
        Assert.Equal(3000, demo.Port);
        Assert.Equal("http://localhost:3000/", demo.Address);

        Assert.Equal(3001, RemotionStudio.FindRunning(@"C:\Projects\demo-2", sys.Source)!.Port);
        Assert.Null(RemotionStudio.FindRunning(@"C:\Projects\demo-3", sys.Source));
    }

    [Fact]
    public void AStudioThatIsntListeningYetIsStillRunning()
    {
        var sys = new FakeSystem();
        sys.Add(12, 1, "node.exe", Cli(Demo), Demo);
        var found = RemotionStudio.FindRunning(Demo, sys.Source);
        Assert.NotNull(found);
        Assert.Null(found!.Port);
        Assert.Null(found.Address);
    }

    [Fact]
    public void ThePortOfAChildServerCounts()
    {
        var sys = new FakeSystem();
        sys.Add(12, 1, "node.exe", Cli(Demo), Demo);
        sys.Add(13, 12, "node.exe", "node server-worker.js", Demo);
        sys.Listeners.Add(new Native.Listener(13, 3002));
        Assert.Equal(3002, RemotionStudio.FindRunning(Demo, sys.Source)!.Port);
    }

    [Fact]
    public void OnlyNodeAndBunProcessesAreRead()
    {
        var sys = new FakeSystem();
        sys.Add(11, 1, "cmd.exe", "cmd.exe /d /s /c remotion studio", Demo);
        sys.Add(12, 1, "powershell.exe", Cli(Demo), Demo);
        Assert.Null(RemotionStudio.FindRunning(Demo, sys.Source));
        Assert.Equal(0, sys.CommandLineReads);
    }

    [Fact]
    public void CheckingSeveralProjectsReadsEachProcessOnce()
    {
        var sys = new FakeSystem();
        sys.Add(12, 1, "node.exe", Cli(Demo), Demo);
        sys.Add(20, 1, "node.exe", "node other.js", @"C:\Projects\x");
        sys.Listeners.Add(new Native.Listener(12, 3000));
        var all = RemotionStudio.FindRunning(new[] { Demo, @"C:\Projects\demo-2", @"C:\Projects\demo-3" }, sys.Source);
        Assert.Equal(3000, all[Demo]!.Port);
        Assert.Null(all[@"C:\Projects\demo-2"]);
        Assert.Equal(2, sys.CommandLineReads);
    }

    // ------------------------------------------------------------------ launching

    private StudioCommand NpmDev() => new(Tool("npm.cmd"), new[] { "run", "dev" });

    [Fact]
    public void TheScriptEntersTheFolderBeforeRunningAndQuotesEverything()
    {
        var dir = @"C:\Projects\it's a $(test) demo";
        var cmd = NpmDev();
        var script = Launcher.BuildStudioScript(new LaunchItem(dir, null, "demo", "Remotion Studio: demo", Studio: cmd));
        var enter = script.IndexOf("Set-Location -LiteralPath 'C:\\Projects\\it''s a $(test) demo'; ", StringComparison.Ordinal);
        var run = script.IndexOf("& " + Launcher.PsQuote(cmd.Exe) + " 'run' 'dev'", StringComparison.Ordinal);
        Assert.True(enter > 0, script);
        Assert.True(run > enter, script);
        Assert.True(script.IndexOf("$ErrorActionPreference = 'Stop'", StringComparison.Ordinal) < enter);
        Assert.EndsWith("'run' 'dev'", script);
        Assert.Contains("$env:NoDefaultCurrentDirectoryInExePath = '1'", script);
    }

    [Fact]
    public void TheScriptRefusesAnythingButTheFixedCommands()
    {
        var bad = new StudioCommand(Tool("npm.cmd"), new[] { "run", "build" });
        Assert.Throws<ArgumentException>(() => Launcher.BuildStudioScript(new LaunchItem(_root, null, "demo", Studio: bad)));
    }

    [Fact]
    public void ValidationNeedsALocalFolderWithAPackageJson()
    {
        var dir = Project("demo", """{"scripts":{"dev":"x"},"dependencies":{"remotion":"4"}}""");
        var cmd = NpmDev();
        Assert.Null(Launcher.Validate(new LaunchItem(dir, null, "demo", Studio: cmd)));

        var bare = Path.Combine(_root, "bare");
        Directory.CreateDirectory(bare);
        Assert.Contains("package.json", Launcher.Validate(new LaunchItem(bare, null, "bare", Studio: cmd)));
        Assert.Contains("missing", Launcher.Validate(new LaunchItem(Path.Combine(_root, "gone"), null, "gone", Studio: cmd)));
        Assert.Contains("UNC", Launcher.ValidateStudio(cmd, @"\\server\share\demo", null));
        Assert.NotNull(Launcher.Validate(new LaunchItem(dir, "11111111-2222-3333-4444-555555555555", "demo", Studio: cmd)));
        Assert.NotNull(Launcher.Validate(new LaunchItem(dir, null, "demo", null, new TerminalCommand(null, "", "PowerShell", true), cmd)));
        Assert.NotNull(Launcher.Validate(new LaunchItem(dir, null, "demo", Studio: new StudioCommand(Path.Combine(_root, "tools", "gone", "npm.cmd"), new[] { "run", "dev" }))));
    }

    [Fact]
    public void AStudioTabKeepsItsTitleAndIsntAClaudeSession()
    {
        var dir = Project("demo", """{"dependencies":{"remotion":"4"}}""");
        var item = new LaunchItem(dir, null, "demo", "Remotion Studio: demo", Studio: NpmDev());
        Assert.False(item.IsClaude);
        var tab = Launcher.BuildTabArgs(item, "claude");
        Assert.StartsWith("new-tab --title \"Remotion Studio: demo\" --suppressApplicationTitle -d ", tab);
        Assert.DoesNotContain("--resume", Launcher.BuildScript(item, "claude"));
        Assert.DoesNotContain("claude", Launcher.BuildScript(item, @"C:\x\claude.exe"));
    }

    /// <summary>
    /// Runs the real generated script in a hidden PowerShell (not through LaunchGuard: nothing user-visible opens),
    /// with a stand-in npm.cmd that records where it ran and with what. The folder name has the characters that could
    /// go wrong and are allowed: spaces, straight and curly quotes, ;, =, brackets, $(...) and a backtick. (&amp;, ^, %, |
    /// and angle brackets are refused before this point.)
    /// </summary>
    [Fact]
    public void TheToolRunsInTheChosenFolderWithTheFixedArguments()
    {
        var dir = Project("it's a (b); $(x) `y ‘z’ =1", """{"dependencies":{"remotion":"4"}}""");
        var output = RunWithFakeNpm(dir);
        Assert.NotNull(output);
        var lines = output!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        // The marker file proves the working folder exactly (cmd prints its folder in the console code page, which
        // can't show curly quotes, so the printed path is only compared for ASCII folders).
        Assert.True(File.Exists(Path.Combine(dir, "sk-ran-here.txt")), "npm.cmd didn't run in " + dir);
        Assert.Single(Directory.EnumerateFiles(_root, "sk-ran-here.txt", SearchOption.AllDirectories));
        Assert.Equal("run dev", lines[1].Trim());
    }

    [Fact]
    public void IfTheFolderCantBeEnteredNothingRuns()
    {
        var output = RunWithFakeNpm(Path.Combine(_root, "gone"));
        Assert.Null(output);
    }

    /// <summary>The fake npm.cmd writes its working folder and arguments next to itself; returns them, or null if it didn't run.</summary>
    private string? RunWithFakeNpm(string dir)
    {
        var toolDir = Path.Combine(_root, "fake tools");
        Directory.CreateDirectory(toolDir);
        var npm = Path.Combine(toolDir, "npm.cmd");
        var outFile = Path.Combine(toolDir, "out.txt");
        File.WriteAllText(npm, "@echo off\r\ncd > \"%~dp0out.txt\"\r\necho %*>> \"%~dp0out.txt\"\r\necho ran> sk-ran-here.txt\r\n", Encoding.ASCII);
        var script = Launcher.BuildStudioScript(new LaunchItem(dir, null, "demo", "Remotion Studio: demo",
            Studio: new StudioCommand(npm, new[] { "run", "dev" })));

        var psi = new ProcessStartInfo(Launcher.PowerShellPath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = _root, // not the project, so the test proves Set-Location moved there
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Launcher.Encode(script) }) psi.ArgumentList.Add(a);
        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000), "PowerShell didn't finish");
        }
        return File.Exists(outFile) ? File.ReadAllText(outFile, Encoding.Default) : null;
    }

    // ------------------------------------------------------------------ guard, browser, settings, dialog

    [Fact]
    public void StudioAndItsBrowserAddressOpenOnlyInsideTheirUserActions()
    {
        var psi = RemotionStudio.BrowserStart(3000);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), psi.FileName);
        Assert.Equal("http://localhost:3000/", psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.NotNull(LaunchGuard.Check(psi));
        using (LaunchGuard.UserAction(LaunchGuard.OpenStudioInBrowser))
            Assert.Null(LaunchGuard.Check(psi));
        using (LaunchGuard.UserAction(LaunchGuard.RemotionStudio))
            Assert.Null(LaunchGuard.Check(new ProcessStartInfo(Launcher.ConhostPath, "x") { UseShellExecute = false }));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemotionStudio.BrowserStart(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RemotionStudio.BrowserStart(70000));
    }

    [Fact]
    public void SettingsThatCantBeReadArentReplacedByDefaults()
    {
        var path = Path.Combine(_root, "settings.json");
        Assert.NotNull(AppSettings.TryLoad(path)); // missing: defaults, fine to save
        File.WriteAllText(path, "{ not json");
        Assert.Null(AppSettings.TryLoad(path));
        new AppSettings { OpenIn = OpenMode.WindowsTerminalTab, LastRemotionProject = "x" }.Save(path);
        Assert.Equal(OpenMode.WindowsTerminalTab, AppSettings.TryLoad(path)!.OpenIn);
    }

    [Fact]
    public void TheLastProjectIsRememberedInSettings()
    {
        var path = Path.Combine(_root, "settings.json");
        new AppSettings { LastRemotionProject = @"C:\Projects\demo" }.Save(path);
        Assert.Equal(@"C:\Projects\demo", AppSettings.Load(path).LastRemotionProject);
        Assert.DoesNotContain("remotion", File.ReadAllText(Path.Combine(_root, "settings.json")).Replace("lastRemotionProject", ""),
            StringComparison.OrdinalIgnoreCase); // nothing else is written for it
        new AppSettings().Save(path);
        Assert.DoesNotContain("Remotion", File.ReadAllText(path));
    }

    [Fact]
    public void TheDialogPicksTheLastUsedProjectFirst()
    {
        var rows = new[] { "alpha", "beta" }.Select(n => new RemotionRow(new RemotionProject(n, @"C:\Projects\" + n, null, true))).ToList();
        Assert.Equal("beta", RemotionWindow.PickFirst(rows, @"c:\projects\BETA\")!.Name);
        Assert.Equal("alpha", RemotionWindow.PickFirst(rows, @"C:\Projects\gone")!.Name);
        Assert.Equal("alpha", RemotionWindow.PickFirst(rows, null)!.Name);
        Assert.Null(RemotionWindow.PickFirst(new List<RemotionRow>(), "x"));
    }

    [Fact]
    public void RowsShowTheRealDevScriptAndStatus()
    {
        var row = new RemotionRow(new RemotionProject("demo", Demo, "remotion studio --port 3100", false));
        Assert.Contains("npm.cmd run dev", row.CommandText);
        Assert.Contains("remotion studio --port 3100", row.CommandText);
        Assert.Contains("npm install", row.Tooltip);
        Assert.Equal(System.Windows.Visibility.Collapsed, row.RunningVisibility);
        row.Running = new StudioInstance(new[] { 12 }, 3100);
        Assert.Equal("running \u00B7 localhost:3100", row.RunningText);
        Assert.Contains("http://localhost:3100/", row.Tooltip);
        Assert.Contains("npx.cmd remotion studio", new RemotionRow(new RemotionProject("x", Demo, null, true)).CommandText);
        var next = new RemotionRow(new RemotionProject("x", Demo, "next dev", true)).CommandText;
        Assert.StartsWith("npx.cmd remotion studio", next);
        Assert.Contains("next dev", next);
    }
}
