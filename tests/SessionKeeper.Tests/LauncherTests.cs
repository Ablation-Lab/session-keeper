using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SessionKeeper.Services;

namespace SessionKeeper.Tests;

public sealed class LauncherTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    [DllImport("shell32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CommandLineToArgvW(string cmdLine, out int argc);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);

    private static string[] SplitArgs(string cmdLine)
    {
        var p = CommandLineToArgvW("x.exe " + cmdLine, out var argc);
        try
        {
            var result = new string[argc - 1];
            for (int i = 1; i < argc; i++) result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size))!;
            return result;
        }
        finally { LocalFree(p); }
    }

    [Theory]
    [InlineData(@"C:\Projects\a b")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\trailing\")]
    [InlineData("plain")]
    [InlineData("has \"quote\" and \\\" mix")]
    [InlineData(@"a\\b\\")]
    [InlineData("")]
    public void WinQuoteRoundTripsThroughCommandLineToArgvW(string arg)
    {
        var args = SplitArgs(Launcher.WinQuote(arg) + " next");
        Assert.Equal(new[] { arg, "next" }, args);
    }

    [Fact]
    public void PsQuoteDoublesAllSingleQuoteVariants()
    {
        Assert.Equal("'it''s'", Launcher.PsQuote("it's"));
        Assert.Equal("'a\u2019\u2019b'", Launcher.PsQuote("a\u2019b"));
        Assert.Equal("'$(x); & \"y\"'", Launcher.PsQuote("$(x); & \"y\""));
    }

    [Fact]
    public void BuildScriptRejectsNonGuidSessionIds()
    {
        Assert.Throws<ArgumentException>(() =>
            Launcher.BuildScript(new LaunchItem(@"C:\x", "abc; Remove-Item x", "x"), "claude"));
    }

    [Fact]
    public void TabArgsOmitDirectoryFlagWhenPathHasSemicolon()
    {
        var withSemi = Launcher.BuildTabArgs(new LaunchItem(@"C:\a;b", Id, "a;b"), "claude");
        Assert.DoesNotContain(" -d ", withSemi);
        Assert.DoesNotContain(";", withSemi); // title is sanitised, script is base64
        var normal = Launcher.BuildTabArgs(new LaunchItem(@"C:\Projects\x y", Id, "x y"), "claude");
        Assert.Contains("-d \"C:\\Projects\\x y\"", normal);
        // wt expands %VAR% in -d, so such folders rely on the script's Set-Location instead.
        Assert.DoesNotContain(" -d ", Launcher.BuildTabArgs(new LaunchItem(@"C:\p\%OS%", Id, "x"), "claude"));
    }

    [Fact]
    public void CleanEnvironmentRemovesOnlySessionScopedVariables()
    {
        var psi = new ProcessStartInfo("x.exe");
        psi.Environment["CLAUDECODE"] = "1";
        psi.Environment["CLAUDE_CODE_SESSION_ID"] = "abc";
        psi.Environment["CLAUDE_CONFIG_DIR"] = @"D:\cc";
        psi.Environment["CLAUDE_CODE_USE_BEDROCK"] = "1";
        Launcher.CleanEnvironment(psi);
        Assert.False(psi.Environment.ContainsKey("CLAUDECODE"));
        Assert.False(psi.Environment.ContainsKey("CLAUDE_CODE_SESSION_ID"));
        Assert.Equal(@"D:\cc", psi.Environment["CLAUDE_CONFIG_DIR"]);
        Assert.Equal("1", psi.Environment["CLAUDE_CODE_USE_BEDROCK"]);
    }

    [Fact]
    public void ScriptForwardsConfigDirAsQuotedLiteral()
    {
        var s = Launcher.BuildScript(new LaunchItem(@"C:\x", Id, "x"), "claude", @"D:\it's cc");
        Assert.Contains("$env:CLAUDE_CONFIG_DIR = 'D:\\it''s cc';", s);
        Assert.DoesNotContain("CLAUDE_CONFIG_DIR", Launcher.BuildScript(new LaunchItem(@"C:\x", Id, "x"), "claude"));
    }

    [Fact]
    public void ValidateRejectsLoneSurrogates()
    {
        Assert.NotNull(Launcher.Validate(new LaunchItem(Path.GetTempPath() + "a\uD83Db", Id, "x")));
    }

    /// <summary>If the folder can't be entered, claude must not start (in whatever folder the shell is in).</summary>
    [Fact]
    public void ScriptStopsWhenFolderCannotBeEntered()
    {
        var root = Path.Combine(Path.GetTempPath(), "sk-stop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var fake = Path.Combine(root, "fake.cmd");
        var marker = Path.Combine(root, "ran.txt");
        File.WriteAllText(fake, $"@echo ran> \"{marker}\"\r\n");
        try
        {
            var script = Launcher.BuildScript(new LaunchItem(Path.Combine(root, "gone"), Id, "gone"), fake);
            var (_, _) = RunPowerShell(script, root);
            Assert.False(File.Exists(marker), "claude ran even though Set-Location failed");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static (string StdOut, string StdErr) RunPowerShell(string script, string workingDir)
    {
        var psi = new ProcessStartInfo(Launcher.PowerShellPath, "-NoProfile -NonInteractive -EncodedCommand " + Launcher.Encode(script))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = workingDir,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (stdout, stderr);
    }

    [Fact]
    public void WtCommandLinesAreChunkedUnderLimit()
    {
        var items = Enumerable.Range(0, 120).Select(i => new LaunchItem(@"C:\Projects\folder-" + i, Id, "folder " + i)).ToList();
        var chunks = Launcher.BuildWtCommandLines(items, @"D:\Tools\claude\claude.exe", "sessionkeeper-test");
        var lines = chunks.Select(c => c.CommandLine).ToList();
        Assert.True(lines.Count > 1);
        Assert.All(lines, l => Assert.True(l.Length < 32_000));
        Assert.All(lines, l => Assert.StartsWith("-w sessionkeeper-test new-tab", l));
        Assert.Equal(120, lines.Sum(l => System.Text.RegularExpressions.Regex.Matches(l, "new-tab --title").Count));
        Assert.Equal(120, chunks.Sum(c => c.Items.Count));
        Assert.All(chunks, c => Assert.Equal(c.Items.Count, System.Text.RegularExpressions.Regex.Matches(c.CommandLine, "new-tab --title").Count));
    }

    [Fact]
    public void SafeTitleStripsMetacharacters()
    {
        Assert.Equal("ab cd", Launcher.SafeTitle("a;b \"cd\""));
        Assert.Equal("claude", Launcher.SafeTitle(";;;"));
    }

    [Fact]
    public void ValidateRejectsBadItems()
    {
        Assert.NotNull(Launcher.Validate(new LaunchItem(@"C:\definitely\not\here-" + Guid.NewGuid(), Id, "x")));
        Assert.NotNull(Launcher.Validate(new LaunchItem(@"relative\path", Id, "x")));
        Assert.NotNull(Launcher.Validate(new LaunchItem("C:\\a\"b", Id, "x")));
        Assert.NotNull(Launcher.Validate(new LaunchItem(Path.GetTempPath(), "nope", "x")));
        Assert.Null(Launcher.Validate(new LaunchItem(Path.GetTempPath(), Id, "x")));
        Assert.Null(Launcher.Validate(new LaunchItem(Path.GetTempPath(), null, "x")));
    }

    /// <summary>
    /// End-to-end: run the generated encoded script in real PowerShell against a hostile folder name,
    /// with a fake "claude" that just reports its cwd and arguments.
    /// </summary>
    [Fact]
    public void EncodedScriptHandlesHostileFolderNames()
    {
        var root = Path.Combine(Path.GetTempPath(), "sk-inj-" + Guid.NewGuid().ToString("N"));
        var evil = Path.Combine(root, "it's; $(Set-Content pwned.txt x) & echo ‘hi’ `n");
        Directory.CreateDirectory(evil);
        var fake = Path.Combine(root, "fake claude.cmd");
        File.WriteAllText(fake, "@echo CD=%CD%\r\n@echo ARGS=%*\r\n");
        try
        {
            var script = Launcher.BuildScript(new LaunchItem(evil, Id, "it's; $(x)"), fake);
            var psi = new ProcessStartInfo(Launcher.PowerShellPath,
                "-NoProfile -NonInteractive -EncodedCommand " + Launcher.Encode(script))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = root,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(30_000);

            // Redirected stderr carries CLIXML for progress/host output; only error records matter.
            Assert.DoesNotContain("S=\"Error\"", stderr);
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("ARGS=--resume " + Id, stdout);
            Assert.Contains("CD=" + root, stdout); // cmd may mangle the odd chars in display; the prefix must match
            Assert.False(File.Exists(Path.Combine(root, "pwned.txt")));
            Assert.False(File.Exists(Path.Combine(evil, "pwned.txt")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
