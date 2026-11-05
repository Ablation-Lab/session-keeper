using Stowdeck.Models;
using Stowdeck.Services;

namespace Stowdeck.Tests;

/// <summary>
/// TerminalScanner against a fake process table and fake console windows, so every rule is checked without
/// touching real processes. Paths and names are neutral examples.
/// </summary>
public sealed class TerminalScannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private const string Ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    private const string Cmd = @"C:\Windows\System32\cmd.exe";
    private const string Node = @"C:\Program Files\nodejs\node.exe";
    private const string Wt = @"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.0_x64\WindowsTerminal.exe";

    private readonly List<Native.ProcEntry> _procs = new();
    private readonly Dictionary<int, (string? Path, long? Created, string? Cmd, string? Cwd)> _facts = new();
    private readonly List<Native.ConsoleOwner> _owners = new();
    private readonly HashSet<string> _guiPaths = new(StringComparer.OrdinalIgnoreCase);

    private void P(int pid, int parent, string path, string? cmd = null, string? cwd = null, int minutesAgo = 10, bool unknownStart = false)
    {
        _procs.Add(new Native.ProcEntry(pid, parent, Path.GetFileName(path)));
        _facts[pid] = (path, unknownStart ? null : Now.AddMinutes(-minutesAgo).ToFileTime(), cmd ?? "\"" + path + "\"", cwd);
    }

    private void Terminal(int pid = 100) => P(pid, 4, Wt, minutesAgo: 600);
    private void Tab(int pid, string path, string? cmd = null, string? cwd = null, int minutesAgo = 60, int host = 100)
    {
        P(pid, host, path, cmd, cwd, minutesAgo);
        _owners.Add(new Native.ConsoleOwner(pid, Native.ConsoleKind.Pseudo));
    }

    private List<TerminalSession> Scan(int ownPid = 99999, string? ownPath = null) => new TerminalScanner(new TerminalScanner.Source(
        () => _procs, () => _owners,
        pid => _facts.TryGetValue(pid, out var f) ? new Native.ProcessFacts(f.Path, f.Created, null) : default,
        pid => _facts.TryGetValue(pid, out var f) ? f.Cmd : null,
        pid => _facts.TryGetValue(pid, out var f) ? f.Cwd : null,
        path => path != null && _guiPaths.Contains(path),
        ownPid, ownPath)).Scan();

    [Fact]
    public void AWindowsTerminalTabRunningNpmShowsTheProgramAndItsFolder()
    {
        Terminal();
        Tab(200, Ps, cwd: @"D:\Home\someone");
        P(201, 200, Cmd, "C:\\Windows\\system32\\cmd.exe /c \"\"C:\\Program Files\\nodejs\\npm.cmd\" run dev\"", @"D:\Work\web-app", 30);
        P(202, 201, Node, "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js\" run dev", @"D:\Work\web-app", 29);

        var t = Assert.Single(Scan());
        Assert.Equal("PowerShell", t.Label);
        Assert.Equal("program", t.Type);
        Assert.Equal("Windows Terminal", t.Host);
        Assert.Equal(201, t.CommandPid);              // cmd /c is the command, not a nested shell
        Assert.Equal(@"D:\Work\web-app", t.Folder);   // the program's folder, not PowerShell's start folder
        Assert.False(t.FolderIsStartFolder);
        Assert.Equal("npm run dev", TerminalCommands.Pretty(t.CommandLine));
        Assert.Equal(Now.AddMinutes(-60), t.StartedAt);
    }

    [Fact]
    public void AnIdlePowerShellShowsItsStartFolderMarkedAsSuch()
    {
        Terminal();
        Tab(200, Ps, cwd: @"D:\Home\someone");
        var t = Assert.Single(Scan());
        Assert.Equal("shell", t.Type);
        Assert.Null(t.CommandLine);
        Assert.Equal(@"D:\Home\someone", t.Folder);
        Assert.True(t.FolderIsStartFolder); // PowerShell doesn't report a later cd
    }

    [Fact]
    public void AnIdleCommandPromptReportsItsCurrentFolder()
    {
        Terminal();
        Tab(200, Cmd, cwd: @"D:\Work\api");
        var t = Assert.Single(Scan());
        Assert.Equal("Command Prompt", t.Label);
        Assert.False(t.FolderIsStartFolder); // cmd's cd does change the process folder
    }

    [Theory]
    [InlineData(@"D:\Tools\claude\claude.exe", "claude --resume x")]
    [InlineData(Node, "\"C:\\Program Files\\nodejs\\node.exe\" C:\\npm\\node_modules\\@anthropic-ai\\claude-code\\cli.js")]
    public void ClaudeCodeSessionsAreLeftOut(string path, string cmd)
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, path, cmd, @"D:\Work\api", 5);
        Tab(300, Cmd);
        var t = Assert.Single(Scan());
        Assert.Equal(300, t.RootPid);
    }

    [Fact]
    public void TheClaudeDesktopAppInATabIsNotTreatedAsClaudeCode()
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, @"C:\Program Files\WindowsApps\Claude_1.0_x64\app\claude.exe", null, null, 5);
        Assert.Single(Scan());
    }

    [Fact]
    public void ARecognisedAgentGivesTheLabelAndItsLauncherIsTheCommand()
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, Node, "node C:\\npm\\node_modules\\@openai\\codex\\bin\\codex.js --model x", @"D:\Work\api", 20);
        P(202, 201, @"C:\npm\node_modules\@openai\codex\vendor\codex.exe", null, @"D:\Work\api", 19);
        var t = Assert.Single(Scan());
        Assert.Equal("Codex CLI", t.Label);
        Assert.Equal("agent", t.Type);
        Assert.Equal(201, t.CommandPid);
    }

    [Fact]
    public void AnAgentStartedDirectlyByTheTerminalIsBothRootAndCommand()
    {
        Terminal();
        Tab(200, @"D:\Tools\gemini.exe", cwd: @"D:\Work\api");
        var t = Assert.Single(Scan());
        Assert.Equal("Gemini CLI", t.Label);
        Assert.Null(t.ShellName);
        Assert.Equal(200, t.CommandPid);
        Assert.Equal(@"D:\Work\api", t.Folder);
    }

    [Fact]
    public void ANestedInteractiveShellIsFollowedToTheProgramInside()
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, Cmd, "cmd.exe", @"D:\Work", 30); // cmd typed at the PowerShell prompt
        P(202, 201, @"C:\Python\python.exe", "python app.py", @"D:\Work\tool", 20);
        var t = Assert.Single(Scan());
        Assert.Equal("Command Prompt", t.ShellName);
        Assert.Equal(202, t.CommandPid);
        Assert.Equal(@"D:\Work\tool", t.Folder);
    }

    [Fact]
    public void GitBashsLauncherIsFollowedToTheRealShell()
    {
        Terminal();
        Tab(200, @"C:\Program Files\Git\bin\bash.exe", "\"C:\\Program Files\\Git\\bin\\bash.exe\" -i -l");
        P(201, 200, @"C:\Program Files\Git\usr\bin\bash.exe", "/usr/bin/bash --login -i", @"D:\Work", 59);
        var t = Assert.Single(Scan());
        Assert.Equal("Bash", t.ShellName);
        Assert.Null(t.CommandLine); // at the prompt
        Assert.Equal(@"C:\Program Files\Git\usr\bin\bash.exe", t.ShellPath);
    }

    [Fact]
    public void AClassicConsoleWindowCountsEvenWithConhostAsParent()
    {
        P(500, 4, @"C:\Windows\System32\conhost.exe", minutesAgo: 30);
        P(501, 500, Ps, cwd: @"D:\Work", minutesAgo: 30);
        _owners.Add(new Native.ConsoleOwner(501, Native.ConsoleKind.Classic));
        var t = Assert.Single(Scan());
        Assert.Equal("console window", t.Host);
        Assert.Equal(501, t.RootPid);
    }

    [Fact]
    public void APseudoConsoleOfABackgroundToolIsIgnored()
    {
        P(600, 4, @"C:\Program Files\SomeTool\tool.exe", minutesAgo: 100);
        Tab(601, Ps, host: 600); // ConPTY owned by a process whose parent isn't a terminal
        Assert.Empty(Scan());
    }

    [Fact]
    public void AReusedParentPidDoesNotMakeAProcessATab()
    {
        P(100, 4, Wt, minutesAgo: 5);       // "Windows Terminal" started after the shell: the PID was reused
        Tab(200, Ps, minutesAgo: 60);
        Assert.Empty(Scan());
    }

    [Fact]
    public void AChildOlderThanItsParentIsNotPartOfTheSession()
    {
        Terminal();
        Tab(200, Ps, cwd: @"D:\Home\someone", minutesAgo: 60);
        P(201, 200, @"C:\Python\python.exe", "python old.py", @"D:\Elsewhere", 120); // reused parent PID
        var t = Assert.Single(Scan());
        Assert.Null(t.CommandLine);
    }

    [Fact]
    public void StowdeckAndConsoleHostsAreNeverCommandsOrSessions()
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, @"C:\Windows\System32\conhost.exe", minutesAgo: 59);
        P(202, 200, @"D:\Apps\Stowdeck\Stowdeck.exe", "Stowdeck.exe --list", @"D:\Work", 1);
        Tab(300, @"D:\Apps\Stowdeck\Stowdeck.exe", minutesAgo: 1);
        var t = Assert.Single(Scan(ownPid: 300, ownPath: @"D:\Apps\Stowdeck\Stowdeck.exe"));
        Assert.Equal(200, t.RootPid);
        Assert.Null(t.CommandLine);
    }

    [Fact]
    public void AWindowReportedTwiceIsOneSessionAndOrderIsByStartTime()
    {
        Terminal();
        Tab(300, Cmd, minutesAgo: 10);
        Tab(200, Ps, minutesAgo: 50);
        _owners.Add(new Native.ConsoleOwner(300, Native.ConsoleKind.Pseudo));
        Assert.Equal(new[] { 200, 300 }, Scan().Select(t => t.RootPid));
    }

    [Fact]
    public void TheMostRecentProgramWinsWhenSeveralRunUnlessAnAgentIsAmongThem()
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, @"C:\Python\python.exe", "python a.py", @"D:\A", 40);
        P(202, 200, @"C:\Python\python.exe", "python b.py", @"D:\B", 30);
        Assert.Equal(202, Assert.Single(Scan()).CommandPid);

        P(203, 200, @"D:\Tools\aider.exe", "aider", @"D:\C", 35);
        var t = Assert.Single(Scan());
        Assert.Equal(203, t.CommandPid);
        Assert.Equal("Aider", t.Label);
    }

    [Fact]
    public void WindowedProgramsStartedFromAShellAreNotItsCommand()
    {
        Terminal();
        Tab(200, Ps, cwd: @"D:\Home\someone");
        P(201, 200, @"C:\Windows\notepad.exe", "notepad todo.txt", @"D:\Work", 5);
        P(202, 200, @"D:\Apps\Code\Code.exe", "Code.exe .", @"D:\Work", 4);
        P(203, 202, @"D:\Tools\claude\claude.exe", "claude", @"D:\Work", 3); // e.g. an editor extension's claude
        _guiPaths.Add(@"C:\Windows\notepad.exe");
        _guiPaths.Add(@"D:\Apps\Code\Code.exe");
        var t = Assert.Single(Scan()); // not hidden as Claude Code, and notepad isn't "the command"
        Assert.Null(t.CommandLine);
    }

    [Fact]
    public void AConsoleStartedFromATabIsItsOwnSessionNotTheTabsCommand()
    {
        Terminal();
        Tab(200, Ps, cwd: @"D:\Home\someone", minutesAgo: 60);
        P(201, 200, Cmd, "cmd.exe", @"D:\Work", 10);            // start cmd: a new console window
        _owners.Add(new Native.ConsoleOwner(201, Native.ConsoleKind.Classic));
        P(202, 200, Cmd, "cmd.exe /c build.cmd", @"D:\Work", 5); // a hidden helper console
        _owners.Add(new Native.ConsoleOwner(202, Native.ConsoleKind.Classic, Visible: false));
        var list = Scan();
        Assert.Equal(new[] { 200, 201 }, list.Select(t => t.RootPid));
        Assert.Null(list[0].CommandLine);
    }

    [Fact]
    public void ATabStartedFromAShellWithTerminalAsDefaultIsListed()
    {
        Terminal();
        Tab(200, Ps, minutesAgo: 60);
        Tab(201, Cmd, host: 200, minutesAgo: 5); // pseudo console whose parent is the first tab's shell
        var list = Scan();
        Assert.Equal(2, list.Count);
        Assert.Contains("another terminal", list[1].Host);
    }

    [Fact]
    public void AChildWhoseStartTimeCantBeReadIsNotPartOfTheSession()
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, @"D:\Tools\claude\claude.exe", "claude", null, unknownStart: true); // another user's process on a reused PID
        var t = Assert.Single(Scan());
        Assert.Null(t.CommandLine);
    }

    [Fact]
    public void AParentPidCycleDoesNotHangTheScan()
    {
        Terminal();
        Tab(200, Ps, minutesAgo: 60);
        P(201, 202, Cmd, "cmd.exe", @"D:\A", 30);
        P(202, 201, Cmd, "cmd.exe", @"D:\B", 30); // impossible in reality, but parent PIDs can be stale
        _procs.Add(new Native.ProcEntry(201, 200, "cmd.exe"));
        var task = Task.Run(() => Scan());
        Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "the scan looped");
    }

    [Fact]
    public void RunningUnderDotnetDoesNotHideEveryDotnetProcess()
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, @"C:\Program Files\dotnet\dotnet.exe", "dotnet watch run", @"D:\Work\api", 5);
        var t = Assert.Single(Scan(ownPath: @"C:\Program Files\dotnet\dotnet.exe"));
        Assert.Equal(201, t.CommandPid);
    }

    [Fact]
    public void InstallingAnAgentIsNotRunningIt()
    {
        Terminal();
        Tab(200, Ps, "powershell.exe -NoExit -Command \"cd D:\\src\\aider-chat\"");
        P(201, 200, Node, "node C:\\npm\\node_modules\\npm\\bin\\npm-cli.js i -g @openai/codex", @"D:\Work", 5);
        var t = Assert.Single(Scan());
        Assert.Null(t.AgentName);
        Assert.Equal("program", t.Type);
        Assert.Equal(201, t.CommandPid);
    }

    [Fact]
    public void ABashScriptIsTheCommandNotANestedShell()
    {
        Terminal();
        Tab(200, @"C:\Program Files\Git\usr\bin\bash.exe", "/usr/bin/bash --login -i");
        P(201, 200, @"C:\Program Files\Git\usr\bin\bash.exe", "bash ./build.sh", @"D:\Work", 5);
        P(202, 201, @"C:\Program Files\Git\usr\bin\sleep.exe", "sleep 5", @"D:\Work", 4);
        Assert.Equal(201, Assert.Single(Scan()).CommandPid);
    }

    [Fact]
    public void SavingKeepsTheRealExePathAndTheOriginalArguments()
    {
        Terminal();
        Tab(200, Ps);
        P(201, 200, Node, "\"node\"   \"D:\\Work\\app\\node_modules\\.bin\\..\\cli.js\" studio --port 3000", @"D:\Work\app", 20);
        var entry = TerminalCommands.ToSnapshot(Assert.Single(Scan()));
        Assert.Equal(SnapshotTerminal.TerminalKind, entry.Kind);
        Assert.Equal("program", entry.Type);
        Assert.Equal(@"D:\Work\app", entry.ProjectDir);
        Assert.Equal("\"C:\\Program Files\\nodejs\\node.exe\" \"D:\\Work\\app\\node_modules\\.bin\\..\\cli.js\" studio --port 3000", entry.CommandLine);
        Assert.Equal(Ps, entry.ShellExe);
    }
}
