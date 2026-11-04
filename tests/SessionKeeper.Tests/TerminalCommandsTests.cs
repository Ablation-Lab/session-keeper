using SessionKeeper.Models;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

/// <summary>Agent recognition, command-line parsing, the destructive-command check and the reopen choices.</summary>
public sealed class TerminalCommandsTests
{
    private const string Node = @"C:\Program Files\nodejs\node.exe";

    // ------------------------------------------------------------------ recognition

    [Theory]
    [InlineData("codex.exe", null, "Codex CLI")]
    [InlineData("CODEX.EXE", null, "Codex CLI")]
    [InlineData("node.exe", @"node C:\npm\node_modules\@openai\codex\bin\codex.js", "Codex CLI")]
    [InlineData("node.exe", "node /usr/lib/node_modules/@google/gemini-cli/dist/index.js", "Gemini CLI")]
    [InlineData("python.exe", "python -m aider --model x", "Aider")]
    [InlineData("aider.exe", "aider", "Aider")]
    [InlineData("node.exe", @"node C:\npm\node_modules\@github\copilot\index.js", "GitHub Copilot CLI")]
    [InlineData("cursor-agent.exe", null, "Cursor Agent")]
    public void RecognisesKnownAgentsByNameOrCommandLine(string exe, string? cmd, string expected) =>
        Assert.Equal(expected, KnownAgents.Recognise(exe, cmd)?.Name);

    [Theory]
    [InlineData("node.exe", @"node D:\Work\codex-notes\app.js")]   // "codex" in a folder name isn't Codex
    [InlineData("codexify.exe", null)]
    [InlineData("python.exe", "python -m aiohttp.web")]
    [InlineData("powershell.exe", "powershell -NoLogo")]
    [InlineData("node.exe", @"node C:\npm\node_modules\npm\bin\npm-cli.js i -g @openai/codex")] // installing isn't running
    [InlineData("node.exe", "node -e \"require('@openai/codex')\"")]
    [InlineData("git.exe", "git commit -m aider")]
    [InlineData(null, null)]
    public void DoesNotRecogniseLookAlikes(string? exe, string? cmd) => Assert.Null(KnownAgents.Recognise(exe, cmd));

    [Fact]
    public void AddingAnAgentIsOneEntryInOneList() =>
        Assert.All(KnownAgents.Agents, a => Assert.True(a.ProcessNames.Length + a.CommandLineMarkers.Length > 0, a.Name));

    [Fact]
    public void NoAgentHasAnUnverifiedResumeOption() =>
        Assert.All(KnownAgents.Agents, a => Assert.Null(a.Resume)); // none could be verified on the build machine

    [Theory]
    [InlineData("claude.exe", @"D:\Tools\claude\claude.exe", null, true)]
    [InlineData("claude.exe", @"C:\Program Files\WindowsApps\Claude_1.0_x64\app\claude.exe", null, false)]
    [InlineData("node.exe", Node, @"node C:\npm\node_modules\@anthropic-ai\claude-code\cli.js", true)]
    [InlineData("node.exe", Node, "node server.js", false)]
    public void IdentifiesClaudeCode(string exe, string path, string? cmd, bool expected) =>
        Assert.Equal(expected, KnownAgents.IsClaudeCode(exe, path, cmd));

    // ------------------------------------------------------------------ parsing

    [Theory]
    [InlineData("\"C:\\a b\\x.exe\" one two", @"C:\a b\x.exe", "one two")]
    [InlineData("C:\\x.exe   one", @"C:\x.exe", "one")]
    [InlineData("  x.exe", "x.exe", "")]
    [InlineData("\"C:\\x.exe\"", @"C:\x.exe", "")]
    public void SplitsTheProgramFromTheRawArguments(string cmd, string program, string args) =>
        Assert.Equal((program, args), TerminalCommands.SplitProgram(cmd));

    [Fact]
    public void TokenizesLikeTheCRuntimeAndKeepsTheRawText()
    {
        var t = TerminalCommands.Tokenize("a \"b c\" d\\\"e \"f\"\"g\" C:\\dir\\ \"h\\\\\"");
        Assert.Equal(new[] { "a", "b c", "d\"e", "f\"g", @"C:\dir\", "h\\" }, t.Select(x => x.Value));
        Assert.Equal("\"b c\"", t[1].Raw);
    }

    [Fact]
    public void ASavedCommandMustBeAnExeByFullPathOnOneLine()
    {
        Assert.NotNull(TerminalCommands.ParseSaved("\"C:\\Tools\\x.exe\" --flag", out var none));
        Assert.Null(none);
        foreach (var bad in new[]
                 {
                     "x.exe --flag",                              // not absolute: would be found via PATH or the folder
                     "\"C:\\Tools\\build.cmd\" all",              // only .exe (no shell-associated files)
                     "\"C:\\Tools\\x.exe\" a\r\nRemove-Item b",   // line break
                     "\"C:\\Tools\\x.exe\" " + new string('a', TerminalCommands.MaxCommandLength),
                     "\"C:\\Tools\\x\".exe a",
                     "",
                 })
        {
            Assert.Null(TerminalCommands.ParseSaved(bad, out var why));
            Assert.NotNull(why);
        }
    }

    [Fact]
    public void WhatIsShownIsExactlyWhatIsRun()
    {
        var run = TerminalCommands.ParseSaved("\"C:\\Tools\\x.exe\"   a 'b' \"c d\" $(e)", out _)!;
        Assert.Equal(@"C:\Tools\x.exe", run.Exe);
        Assert.Equal("a 'b' \"c d\" $(e)", run.Args);
        Assert.Equal("\"C:\\Tools\\x.exe\" a 'b' \"c d\" $(e)", run.Display);
    }

    [Theory]
    [InlineData("C:\\Windows\\system32\\cmd.exe /c \"\"C:\\Program Files\\nodejs\\npm.cmd\" run dev\"", "npm run dev")]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\npm\\node_modules\\x\\cli.js\" --port 3000", "node cli.js --port 3000")]
    [InlineData("python \"my app.py\"", "python \"my app.py\"")]
    [InlineData("C:\\Windows\\system32\\cmd.exe /d /s /c remotion studio", "remotion studio")]
    [InlineData(null, "")]
    public void PrettyShowsAShortReadableCommand(string? cmd, string expected) =>
        Assert.Equal(expected, TerminalCommands.Pretty(cmd));

    [Theory]
    [InlineData("cmd.exe", "cmd.exe /c \"npm run dev\"", true)]
    [InlineData("cmd.exe", "cmd.exe", false)]
    [InlineData("cmd.exe", "cmd.exe /k title x", false)]
    [InlineData("powershell.exe", "powershell.exe -NoLogo", false)]
    [InlineData("powershell.exe", "powershell.exe -NoLogo -NoExit -EncodedCommand AAAA", false)]
    [InlineData("powershell.exe", "powershell.exe -NoProfile -ExecutionPolicy Bypass -Command Get-Date", true)]
    [InlineData("powershell.exe", "powershell.exe -File build.ps1", true)]
    [InlineData("pwsh.exe", "pwsh -NoLogo -enc AAAA", true)]
    [InlineData("powershell.exe", "powershell.exe .\\build.ps1", true)]
    [InlineData("bash.exe", "/usr/bin/bash --login -i", false)]
    [InlineData("bash.exe", "bash -c \"make\"", true)]
    [InlineData("wsl.exe", "wsl.exe -d Ubuntu", false)]
    [InlineData("wsl.exe", "wsl.exe -e htop", true)]
    public void TellsAShellRunningACommandFromOneAtItsPrompt(string exe, string cmd, bool expected) =>
        Assert.Equal(expected, TerminalCommands.RunsCommand(exe, cmd));

    // ------------------------------------------------------------------ destructive commands

    [Theory]
    [InlineData("rm -rf build")]
    [InlineData("\"C:\\Windows\\system32\\cmd.exe\" /c \"del /s /q dist\"")]
    [InlineData("cmd /c rd /s /q node_modules")]
    [InlineData("format D: /q")]
    [InlineData("npm run format")] // flagged on purpose: "format" was on the list; it only unticks the entry
    [InlineData("\"C:\\Program Files\\Git\\cmd\\git.exe\" push origin main")]
    [InlineData("git -C D:\\Work\\api push --force")]
    [InlineData("git reset --hard HEAD~3")]
    [InlineData("git clean -fdx")]
    [InlineData("git branch -D feature")]
    [InlineData("powershell -Command Remove-Item -Recurse x")]
    [InlineData("dd if=image.iso of=/dev/sdb")]
    [InlineData("shutdown /s /t 0")]
    public void FlagsCommandsThatLookDestructive(string cmd) => Assert.NotNull(TerminalCommands.LooksDestructive(cmd));

    [Theory]
    [InlineData("npm run dev")]
    [InlineData("git status")]
    [InlineData("git pull --rebase")]
    [InlineData("git log --format=oneline")]
    [InlineData("python app.py --remove-cache")]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\npm\\node_modules\\@openai\\codex\\bin\\codex.js\"")]
    [InlineData("dotnet build -c Release")]
    [InlineData(null)]
    public void LeavesOrdinaryCommandsAlone(string? cmd) => Assert.Null(TerminalCommands.LooksDestructive(cmd));

    // ------------------------------------------------------------------ reopen choices

    private static SnapshotTerminal Entry(string? command, string? shell = "PowerShell", string? shellExe = null, string? agent = null) => new()
    {
        Label = "Test", ProjectDir = @"D:\Work\api", CommandLine = command, Shell = shell, ShellExe = shellExe, Agent = agent,
    };

    [Fact]
    public void AnIdleShellOffersOnlyTheShellAndOpensWithoutConfirmation()
    {
        var plan = TerminalCommands.Plan(Entry(null));
        var only = Assert.Single(plan.Options);
        Assert.Equal(TerminalOption.ShellOnly, only.Kind);
        Assert.Null(only.Command.Exe);
        Assert.True(plan.IsPlainShell);
        Assert.True(plan.DefaultTicked);
    }

    [Fact]
    public void ACommandIsOfferedFirstAndTheShellAsAFallback()
    {
        var plan = TerminalCommands.Plan(Entry("\"C:\\Tools\\x.exe\" serve"));
        Assert.Equal(new[] { TerminalOption.Run, TerminalOption.ShellOnly }, plan.Options.Select(o => o.Kind));
        Assert.False(plan.IsPlainShell); // needs the confirmation
        Assert.True(plan.DefaultTicked);
    }

    [Fact]
    public void ADestructiveCommandIsNotTickedAndCarriesAWarning()
    {
        var plan = TerminalCommands.Plan(Entry("\"C:\\Program Files\\Git\\cmd\\git.exe\" push --force"));
        Assert.False(plan.DefaultTicked);
        Assert.Contains("git push", plan.Default.Warning);
        Assert.Null(plan.Options.Last().Warning); // the shell-only fallback is safe
    }

    [Fact]
    public void AnUnusableCommandFallsBackToTheShellAndSaysWhy()
    {
        var plan = TerminalCommands.Plan(Entry("build.cmd all"));
        Assert.Equal(TerminalOption.ShellOnly, Assert.Single(plan.Options).Kind);
        Assert.NotNull(plan.Problem);
    }

    [Fact]
    public void SystemShellsReopenFromSystem32WhateverTheFileSays()
    {
        var cmd = TerminalCommands.ShellCommand("Command Prompt", @"D:\Downloads\cmd.exe", @"C:\Windows\System32");
        Assert.Equal(@"C:\Windows\System32\cmd.exe", cmd.Exe);
        Assert.True(cmd.IsPlainShell);
        Assert.Equal("", cmd.Args);
    }

    [Fact]
    public void OtherShellsRunFromTheirSavedPathOnlyAfterConfirmation()
    {
        var bash = TerminalCommands.ShellCommand("Bash", @"C:\Program Files\Git\usr\bin\bash.exe");
        Assert.Equal(@"C:\Program Files\Git\usr\bin\bash.exe", bash.Exe);
        Assert.Equal("--login -i", bash.Args);
        Assert.False(bash.IsPlainShell);
        Assert.False(TerminalCommands.Plan(Entry(null, "Bash", @"C:\Program Files\Git\usr\bin\bash.exe")).IsPlainShell);

        // A path that doesn't match the shell's name (or isn't absolute) is ignored: just PowerShell.
        Assert.Null(TerminalCommands.ShellCommand("Bash", @"D:\Downloads\evil.exe").Exe);
        Assert.Null(TerminalCommands.ShellCommand("Bash", "bash.exe").Exe);
        Assert.Null(TerminalCommands.ShellCommand("Unknown shell", @"C:\Tools\x.exe").Exe);
    }

    [Fact]
    public void AVerifiedResumeKeepsTheProgramAndScriptAndAddsOnlyTheResumeArguments()
    {
        var agent = new KnownAgent("Fake agent", new[] { "fake" }, new[] { "/fake agent/" }, Resume: new[] { "resume", "--last" });
        var node = TerminalCommands.ParseSaved("\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\npm\\fake agent\\cli.js\" --model x", out _);
        Assert.Equal("\"C:\\npm\\fake agent\\cli.js\" resume --last", TerminalCommands.ResumeCommand(agent, node)!.Args);

        var native = TerminalCommands.ParseSaved("\"C:\\Tools\\fake.exe\" --model x", out _);
        Assert.Equal("resume --last", TerminalCommands.ResumeCommand(agent, native)!.Args);

        var module = TerminalCommands.ParseSaved("\"C:\\Python\\python.exe\" -m fake --model x", out _);
        Assert.Equal("-m fake resume --last", TerminalCommands.ResumeCommand(agent, module)!.Args);

        Assert.Null(TerminalCommands.ResumeCommand(agent, TerminalCommands.ParseSaved("\"C:\\Python\\python.exe\" -X utf8 app.py", out _)));
        Assert.Null(TerminalCommands.ResumeCommand(KnownAgents.Agents[0], native)); // no verified option: no resume
        // The snapshot's "agent" can't attach a resume to some other program.
        Assert.Null(TerminalCommands.ResumeCommand(agent, TerminalCommands.ParseSaved("\"C:\\Tools\\other.exe\" x", out _)));
    }

    // ------------------------------------------------------------------ review fixes

    [Theory]
    [InlineData("\"C:\\Tools\\x.exe\" \u202Egnp.exe")]       // right-to-left override
    [InlineData("\"C:\\Tools\\x.exe\" a\u200Bb")]            // zero-width space
    [InlineData("\"C:\\Tools\\x.exe\" \u001b]52;c;AAAA\u0007")] // terminal escape sequence
    [InlineData("\"C:\\Tools\\x.exe\" a\u2028b")]            // line separator
    [InlineData("\"\\\\server\\share\\x.exe\" a")]          // network path
    [InlineData("\"C:\\Windows\\System32\\cmd.exe\\..\\..\\..\\Temp\\x.exe\"")] // reads like cmd.exe
    [InlineData("\"C:\\Temp\\a.txt:s.exe\"")]                 // alternate data stream
    public void CommandsThatCouldLookDifferentFromWhatRunsAreRefused(string cmd)
    {
        Assert.Null(TerminalCommands.ParseSaved(cmd, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    public void TabsInArgumentsAreAllowed() => Assert.NotNull(TerminalCommands.ParseSaved("\"C:\\Tools\\x.exe\" a\tb", out _));

    [Theory]
    [InlineData("cmd.exe /c d^el /q /s *")]
    [InlineData("powershell -Command &{rm -r *}")]
    [InlineData("git.exe push")]
    [InlineData("format.com D: /y")]
    [InlineData("powershell.exe -enc SQBFAFgA")]
    [InlineData("powershell -NoProfile -e SQBFAFgA")]
    [InlineData("powershell -Command iex (irm https://example.invalid/x.ps1)")]
    [InlineData("robocopy src dst /MIR")]
    [InlineData("git stash drop")]
    [InlineData("git checkout -- .")]
    [InlineData("git restore src")]
    [InlineData("docker system prune -af")]
    [InlineData("kubectl delete pod x")]
    [InlineData("terraform destroy")]
    [InlineData("taskkill /im node.exe /f")]
    [InlineData("/usr/bin/rm -r build")]
    public void FlagsObfuscatedAndAccidentallyDestructiveCommands(string cmd) => Assert.NotNull(TerminalCommands.LooksDestructive(cmd));

    [Theory]
    [InlineData("git restore --staged src")]
    [InlineData("docker compose up")]
    [InlineData("robocopy src dst /E")]
    public void LeavesTheirSafeVariantsAlone(string cmd) => Assert.Null(TerminalCommands.LooksDestructive(cmd));

    [Fact]
    public void ARefusedCommandStillShowsTheDialogSoTheUserSeesWhy()
    {
        var plan = TerminalCommands.Plan(Entry("build.cmd all", "Command Prompt"));
        Assert.Equal(TerminalOption.ShellOnly, Assert.Single(plan.Options).Kind);
        Assert.False(plan.IsPlainShell);
    }

    [Fact]
    public void ASavedTerminalThatIsStillRunningIsSpottedAndNotTicked()
    {
        var saved = Entry("\"C:\\Tools\\x.exe\" serve");
        var live = new[] { Entry("\"C:\\Tools\\x.exe\" serve") };
        live[0].ProjectDir = @"D:\Work\API\";
        Assert.True(TerminalCommands.IsStillRunning(saved, live));
        Assert.False(TerminalCommands.IsStillRunning(Entry(null), new[] { Entry(null) })); // a second idle shell is harmless
        Assert.False((TerminalCommands.Plan(saved) with { StillRunning = true }).DefaultTicked);
    }

    [Theory]
    [InlineData("mysql -u root -pHunter2 db", "-p***")]
    [InlineData("curl -H \"Authorization: Bearer abcdefghijklmnop\" https://x", "Bearer ***")]
    [InlineData("tool --api-key abcdefghijklmnop", "--api-key ***")]
    [InlineData("tool sk-abcdefghijklmnopqr", "tool ***")]
    [InlineData("git clone https://user:s3cret@host/repo", "://user:***@host")]
    [InlineData("deploy --token=abc123", "--token=***")]
    public void LikelySecretsAreSpottedAndRedacted(string cmd, string expected)
    {
        Assert.True(TerminalCommands.MayContainSecret(cmd));
        Assert.Contains(expected, TerminalCommands.Redact(cmd));
    }

    [Fact]
    public void OrdinaryCommandsHaveNoSecrets()
    {
        Assert.False(TerminalCommands.MayContainSecret("npm run dev -- --port 3000"));
        Assert.False(TerminalCommands.MayContainSecret("python -m pytest -p no:cacheprovider"));
    }

    [Theory]
    [InlineData("bash.exe", "bash -lc make", true)]
    [InlineData("bash.exe", "bash ./build.sh", true)]
    [InlineData("wsl.exe", "wsl ls -la", true)]
    [InlineData("wsl.exe", "wsl -d Ubuntu --cd ~", false)]
    [InlineData("pwsh.exe", "pwsh -SettingsFile x.json", false)]
    [InlineData("powershell.exe", "powershell -s -NoLogo -NoProfile", true)] // a Start-Job host
    [InlineData("cmd.exe", "cmd /k echo /c", false)]
    public void MoreShellsRunningCommands(string exe, string cmd, bool expected) =>
        Assert.Equal(expected, TerminalCommands.RunsCommand(exe, cmd));

    [Fact]
    public void PrettyUsesTheRealPositionOfCmdsSwitch() =>
        Assert.Equal("foo", TerminalCommands.Pretty("cmd.exe /d /c foo"));

    [Fact]
    public void ADestructiveChoiceUntickesTheRowWhenPicked()
    {
        var plan = TerminalCommands.Plan(Entry("\"C:\\Program Files\\Git\\cmd\\git.exe\" push"));
        var row = new SessionKeeper.ViewModels.TerminalRunRow(plan);
        Assert.False(row.IsChecked);
        row.SelectedOption = plan.Options.Last(); // shell only
        row.IsChecked = true;
        row.SelectedOption = plan.Options[0];     // back to the push
        Assert.False(row.IsChecked);
    }

    [Fact]
    public void ALabelWithEscapeCodesIsCleanedForDisplayAndTheConsole()
    {
        var plan = TerminalCommands.Plan(new SnapshotTerminal { Kind = "terminal", Label = "x\u001b]52;c;AAAA\u0007\u202E", ProjectDir = @"D:\Work" });
        var row = new SessionKeeper.ViewModels.TerminalRunRow(plan);
        Assert.DoesNotContain('\u001b', row.Label);
        Assert.DoesNotContain('\u202E', row.Label);
        var script = Launcher.BuildTerminalScript(new LaunchItem(@"D:\Work", null, "x\u001b[2J", null, new TerminalCommand(null, "", "PowerShell", true)));
        Assert.DoesNotContain('\u001b', script);
    }

    [Fact]
    public void LogLinesCantBeForgedOrHideText()
    {
        Assert.Equal("a\\u000D\\u000Ab\\u202Ec", LaunchGuard.Escape("a\r\nb\u202Ec"));
        Assert.Equal("plain text", LaunchGuard.Escape("plain text"));
    }

    // ------------------------------------------------------------------ snapshot file

    [Fact]
    public void OneMalformedTerminalEntryDoesNotSpoilTheSnapshot()
    {
        var json = """
            { "name": "x", "sessions": [ { "projectDir": "D:\\Work" } ],
              "terminals": [ { "kind": "terminal", "label": "ok", "projectDir": "D:\\Work" },
                             { "kind": "terminal", "startedAt": "yesterday" },
                             null, 5,
                             { "label": "no kind", "projectDir": "D:\\Work" } ] }
            """;
        var snap = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(json, SnapshotStore.JsonOptions)!;
        Assert.Single(snap.Sessions);
        Assert.Equal(new[] { "ok" }, snap.ValidTerminals.Select(t => t.Label)); // the entry without "kind" is never assumed a terminal
        var notAList = System.Text.Json.JsonSerializer.Deserialize<Snapshot>("""{ "name": "x", "terminals": {} }""", SnapshotStore.JsonOptions)!;
        Assert.Empty(notAList.ValidTerminals);
    }

    [Fact]
    public void ASessionsEntryMarkedAsAnotherKindIsNeverAClaudeSession()
    {
        var snap = System.Text.Json.JsonSerializer.Deserialize<Snapshot>(
            """{ "name": "x", "sessions": [ { "projectDir": "D:\\A", "kind": "terminal" }, { "projectDir": "D:\\B" } ] }""",
            SnapshotStore.JsonOptions)!;
        Assert.Equal(new[] { true, false }, snap.Sessions.Select(s => s.IsOtherKind));
    }
}
