using System.Diagnostics;
using SessionKeeper.Services;
using SessionKeeper.ViewModels;

namespace SessionKeeper.Tests;

/// <summary>The Tools menu (which items apply, how each is started) and the "uncommitted changes" badge.</summary>
public sealed class ProjectToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sk-tools " + Guid.NewGuid().ToString("N")[..8]);

    public ProjectToolsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private string Folder(string name, string? packageJson = null, string? gitConfig = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (packageJson != null) File.WriteAllText(Path.Combine(dir, "package.json"), packageJson);
        if (gitConfig != null)
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            File.WriteAllText(Path.Combine(dir, ".git", "config"), gitConfig);
        }
        return dir;
    }

    private const string GitHubOrigin = "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = https://github.com/example-org/demo.git\n\tfetch = +refs/heads/*:refs/remotes/origin/*\n";

    private static IEnumerable<ToolKind> Kinds(IEnumerable<ProjectTool> tools) => tools.Select(t => t.Kind);

    // ------------------------------------------------------------------ which items apply

    [Fact]
    public void APlainFolderOffersOnlyOpenFolder()
    {
        Assert.Equal(new[] { ToolKind.OpenFolder }, Kinds(ProjectTools.For(Folder("plain"), vsCode: null)));
    }

    [Fact]
    public void EachItemAppearsOnlyWhenItApplies()
    {
        var dir = Folder("web", """{"scripts":{"dev":"vite --port 4000"},"dependencies":{"react":"18"}}""", GitHubOrigin);
        var tools = ProjectTools.For(dir, vsCode: @"C:\Tools\VS Code\Code.exe");
        Assert.Equal(new[] { ToolKind.OpenFolder, ToolKind.VSCode, ToolKind.GitHub, ToolKind.DevServer }, Kinds(tools));
        Assert.Equal("https://github.com/example-org/demo", tools.Single(t => t.Kind == ToolKind.GitHub).Url);
        Assert.Equal("example-org/demo", tools.Single(t => t.Kind == ToolKind.GitHub).Detail);
        Assert.Equal("vite --port 4000", tools.Single(t => t.Kind == ToolKind.DevServer).Detail);
        Assert.DoesNotContain(ToolKind.VSCode, Kinds(ProjectTools.For(dir, vsCode: null)));
    }

    [Fact]
    public void ARemotionProjectOffersStudioInsteadOfTheDevServer()
    {
        var studio = Folder("video", """{"scripts":{"dev":"remotion studio"},"dependencies":{"remotion":"4"}}""");
        var kinds = Kinds(ProjectTools.For(studio, null)).ToList();
        Assert.Contains(ToolKind.RemotionStudio, kinds);
        Assert.DoesNotContain(ToolKind.DevServer, kinds);
        Assert.Equal("npm run dev", ProjectTools.For(studio, null).Single(t => t.Kind == ToolKind.RemotionStudio).Detail);

        // Remotion's player in a Next.js app: Studio (through npx) and the dev server are different things, so both show.
        var player = Folder("player", """{"scripts":{"dev":"next dev"},"dependencies":{"@remotion/player":"4"}}""");
        var tools = ProjectTools.For(player, null);
        Assert.Equal("npx remotion studio", tools.Single(t => t.Kind == ToolKind.RemotionStudio).Detail);
        Assert.Equal("next dev", tools.Single(t => t.Kind == ToolKind.DevServer).Detail);
    }

    [Fact]
    public void NoDevScriptMeansNoDevServer()
    {
        var dir = Folder("lib", """{"scripts":{"build":"tsc"}}""");
        Assert.DoesNotContain(ToolKind.DevServer, Kinds(ProjectTools.For(dir, null)));
    }

    [Fact]
    public void ALongDevScriptIsShortenedInTheMenu()
    {
        var dir = Folder("long", """{"scripts":{"dev":"concurrently \"npm:watch-css\"   \"npm:watch-js\" \"npm:serve\" --kill-others"}}""");
        var detail = ProjectTools.For(dir, null).Single(t => t.Kind == ToolKind.DevServer).Detail!;
        Assert.True(detail.Length <= 32, detail);
        Assert.EndsWith("\u2026", detail);
        Assert.DoesNotContain("   ", detail);
    }

    // ------------------------------------------------------------------ GitHub

    [Theory]
    [InlineData("https://github.com/example-org/demo.git", "https://github.com/example-org/demo")]
    [InlineData("https://github.com/example-org/demo", "https://github.com/example-org/demo")]
    [InlineData("https://GitHub.com/example-org/demo/", "https://github.com/example-org/demo")]
    [InlineData("git@github.com:example-org/my.repo.git", "https://github.com/example-org/my.repo")]
    [InlineData("ssh://git@github.com/example-org/demo.git", "https://github.com/example-org/demo")]
    [InlineData("ssh://git@github.com:22/example-org/demo", "https://github.com/example-org/demo")]
    [InlineData("https://user:secret-token@github.com/example-org/demo.git", "https://github.com/example-org/demo")] // the token is dropped
    [InlineData("https://gitlab.com/example-org/demo.git", null)]
    [InlineData("https://github.com.evil.example/example-org/demo", null)]
    [InlineData("https://evil.example/github.com/example-org/demo", null)]
    [InlineData("https://github.com/example-org/demo/tree/main", null)]
    [InlineData("https://github.com/example-org/de mo", null)]
    [InlineData("https://github.com/example-org/demo?x=1", null)]
    [InlineData("https://github.com/-bad/demo", null)]
    [InlineData("https://github.com/example-org/..", null)]
    [InlineData("C:\\local\\mirror.git", null)]
    [InlineData("", null)]
    public void OnlyGitHubRepositoriesGetAPage(string remote, string? page) => Assert.Equal(page, GitRepo.GitHubPage(remote));

    [Fact]
    public void RemotesAreReadFromTheConfigFileWithOriginFirst()
    {
        var config = "[remote \"upstream\"]\n\turl = git@github.com:someone-else/demo.git\n[branch \"main\"]\n\tremote = origin\n" +
                     "[remote \"origin\"]\n  url = \"https://github.com/example-org/demo.git\"\n";
        Assert.Equal(new[] { ("upstream", "git@github.com:someone-else/demo.git"), ("origin", "https://github.com/example-org/demo.git") },
            GitRepo.ParseRemotes(config));
        var dir = Folder("repo", gitConfig: config);
        Assert.Equal("https://github.com/example-org/demo", GitRepo.GitHubPageFor(dir));
        Assert.Equal("https://github.com/example-org/demo", GitRepo.GitHubPageFor(Directory.CreateDirectory(Path.Combine(dir, "src", "app")).FullName));
    }

    [Fact]
    public void AWorktreeReadsTheSharedConfig()
    {
        var main = Folder("main", gitConfig: GitHubOrigin);
        var gitDir = Path.Combine(main, ".git", "worktrees", "feature");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "../..\n");
        var wt = Folder("feature-wt");
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: " + gitDir + "\n");
        Assert.Equal(Path.Combine(main, ".git", "config"), GitRepo.ConfigPath(wt));
        Assert.Equal("https://github.com/example-org/demo", GitRepo.GitHubPageFor(wt));
    }

    [Fact]
    public void FindRootWalksUpAndSkipsNetworkFolders()
    {
        var repo = Folder("repo", gitConfig: "");
        var deep = Directory.CreateDirectory(Path.Combine(repo, "a", "b")).FullName;
        Assert.Equal(repo, GitRepo.FindRoot(deep));
        var outside = GitRepo.FindRoot(Folder("not-a-repo")); // null, unless the temp folder itself is inside a repository
        Assert.True(outside == null || !outside.StartsWith(_root, StringComparison.OrdinalIgnoreCase));
        Assert.Null(GitRepo.FindRoot(@"\\server\share\repo"));
        Assert.Null(GitRepo.FindRoot("relative"));
        Assert.Null(GitRepo.FindRoot(null));
    }

    // ------------------------------------------------------------------ git status

    private static readonly DateTime Now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    private static string Z(params string[] records) => string.Join("\0", records) + "\0";

    [Fact]
    public void ACleanRepositoryInStepWithItsUpstreamHasNoBadge()
    {
        var s = GitState.Parse(Z("# branch.oid abc123", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +0 -0"), hasRemote: true, Now);
        Assert.False(s.HasWork);
    }

    [Fact]
    public void ChangedStagedUntrackedAndConflictedFilesAreCounted()
    {
        var s = GitState.Parse(Z(
            "# branch.oid abc123", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +2 -1",
            "1 .M N... 100644 100644 100644 aaa bbb src/app.cs",
            "1 M. N... 100644 100644 100644 aaa bbb README.md",
            "2 R. N... 100644 100644 100644 aaa bbb R100 docs/new name.md", "docs/old name.md",
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc merge.txt",
            "? notes/todo.txt"), hasRemote: true, Now);
        Assert.Equal(2, s.Staged);   // README.md and the rename
        Assert.Equal(1, s.Modified); // src/app.cs
        Assert.Equal(1, s.Untracked);
        Assert.Equal(1, s.Conflicts);
        Assert.Equal(2, s.Ahead);
        Assert.Equal(1, s.Behind);
        Assert.Equal(new[] { "src/app.cs", "README.md", "docs/new name.md", "merge.txt", "notes/todo.txt" }, s.Paths);
        Assert.Equal("uncommitted changes", s.BadgeText);
        var text = s.Describe();
        Assert.Contains("Uncommitted: 2 staged, 1 modified, 1 untracked, 1 with conflicts", text);
        Assert.Contains("docs/new name.md", text);
        Assert.DoesNotContain("old name", text); // the original path of a rename isn't counted as a file
        Assert.Contains("Unpushed: 2 commits ahead of origin/main", text);
        Assert.Contains("Branch: main (1 behind origin/main)", text);
    }

    [Fact]
    public void OnlyUnpushedCommitsGiveTheUnpushedBadge()
    {
        var s = GitState.Parse(Z("# branch.oid abc", "# branch.head main", "# branch.upstream origin/main", "# branch.ab +1 -0"), true, Now);
        Assert.True(s.HasWork);
        Assert.Equal("unpushed commits", s.BadgeText);
        Assert.StartsWith("Unpushed: 1 commit ahead of origin/main", s.Describe());
    }

    [Fact]
    public void ABranchWithoutUpstreamIsUnpushedOnlyWhenThereIsARemote()
    {
        var records = Z("# branch.oid abc", "# branch.head feature");
        Assert.True(GitState.Parse(records, hasRemote: true, Now).NoUpstream);
        Assert.Contains("branch feature has no upstream branch", GitState.Parse(records, true, Now).Describe());
        Assert.False(GitState.Parse(records, hasRemote: false, Now).HasWork); // a local-only repository
        Assert.False(GitState.Parse(Z("# branch.oid (initial)", "# branch.head main"), true, Now).HasWork); // no commits yet
        Assert.False(GitState.Parse(Z("# branch.oid abc", "# branch.head (detached)"), true, Now).HasWork);
    }

    [Fact]
    public void LongListsShowAFewFilesAndACount()
    {
        var records = new List<string> { "# branch.oid abc", "# branch.head main" };
        for (int i = 0; i < 20; i++) records.Add($"? file{i}.txt");
        var s = GitState.Parse(Z(records.ToArray()), false, Now);
        Assert.Equal(8, s.Paths.Count);
        Assert.Contains("\u2026 and 12 more", s.Describe());
    }

    [Fact]
    public void GitStatusIsAllowedOnlyWithItsFixedOptions()
    {
        var git = @"C:\Program Files\Git\cmd\git.exe";
        Assert.Null(LaunchGuard.CheckReadOnlyGit(git, @"C:\repo", "status", new[] { "--porcelain=v2", "--branch", "-z", "--ignore-submodules=all" }));
        Assert.NotNull(LaunchGuard.CheckReadOnlyGit(git, @"C:\repo", "status", new[] { "--porcelain=v2", "--short" }));
        Assert.NotNull(LaunchGuard.CheckReadOnlyGit(git, @"C:\repo", "status", new[] { "-z", "-z" }));
        Assert.NotNull(LaunchGuard.CheckReadOnlyGit(git, @"C:\repo", "status", new[] { "--", "x" }));
        Assert.NotNull(LaunchGuard.CheckReadOnlyGit(git, @"C:\repo", "stash", Array.Empty<string>()));
    }

    /// <summary>A real hidden git status on a temporary repository (skipped if git isn't installed).</summary>
    [SkippableFact]
    public void TheRealGitStatusReportsAnUntrackedFile()
    {
        var git = LocalRepoUpdateSource.FindGit();
        Skip.If(git == null, "git isn't installed");
        var repo = Folder("real repo");
        var init = new ProcessStartInfo(git!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-C", repo, "init", "-q" }) init.ArgumentList.Add(a);
        using (var p = Process.Start(init)!) { p.WaitForExit(30_000); }
        Assert.False(GitStatusCache.Query(repo)!.HasWork); // empty repository, no remote

        File.WriteAllText(Path.Combine(repo, "new file.txt"), "x");
        var state = GitStatusCache.Query(Directory.CreateDirectory(Path.Combine(repo, "sub")).FullName)!; // a subfolder checks the whole repository
        Assert.Equal(1, state.Untracked);
        Assert.Equal(new[] { "new file.txt" }, state.Paths);
    }

    [Fact]
    public async Task TheCacheChecksEachFolderOnceAMinuteInTheBackground()
    {
        var now = Now;
        int calls = 0;
        var done = new SemaphoreSlim(0);
        var clean = GitState.Parse(Z("# branch.oid a", "# branch.head main", "? x"), false, Now);
        var cache = new GitStatusCache(folder => { Interlocked.Increment(ref calls); return folder.EndsWith("repo") ? clean : null; }, () => now);
        var updated = new List<string>();
        cache.Updated += f => { lock (updated) updated.Add(f); done.Release(); };

        cache.Request(new[] { @"C:\Projects\repo", @"C:\Projects\repo\", @"C:\Projects\plain", null });
        Assert.True(await done.WaitAsync(5000));
        Assert.True(await done.WaitAsync(5000));
        Assert.Equal(2, calls); // "repo" and "repo\" are one folder; null is skipped
        Assert.Same(clean, cache.Get(@"C:\Projects\repo\"));
        Assert.Null(cache.Get(@"C:\Projects\plain"));

        cache.Request(new[] { @"C:\Projects\repo" }); // still fresh: no new check
        await Task.Delay(100);
        Assert.Equal(2, calls);

        now = now.AddSeconds(61);
        cache.Request(new[] { @"C:\Projects\repo" });
        Assert.True(await done.WaitAsync(5000));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task AFailingCheckJustLeavesNoBadge()
    {
        var done = new SemaphoreSlim(0);
        var cache = new GitStatusCache(_ => throw new IOException("boom"), () => Now);
        cache.Updated += _ => done.Release();
        cache.Request(new[] { @"C:\Projects\repo" });
        Assert.True(await done.WaitAsync(5000));
        Assert.Null(cache.Get(@"C:\Projects\repo"));
    }

    [Fact]
    public void TheBadgeShowsOnlyWhenThereIsWork()
    {
        var badge = new GitBadge();
        Assert.Equal(System.Windows.Visibility.Collapsed, badge.Visibility);
        badge.State = GitState.Parse(Z("# branch.oid a", "# branch.head main", "? x.txt"), false, Now);
        Assert.Equal(System.Windows.Visibility.Visible, badge.Visibility);
        Assert.Equal("uncommitted changes", badge.Text);
        Assert.Contains("x.txt", badge.Tooltip);
        badge.State = GitState.Parse(Z("# branch.oid a", "# branch.head main"), false, Now);
        Assert.Equal(System.Windows.Visibility.Collapsed, badge.Visibility);
    }

    [Fact]
    public void ToolsNeedAnExistingProjectFolder()
    {
        var ok = new SnapshotSessionRow { Session = new Models.SnapshotSession { ProjectDir = @"C:\Projects\demo" } };
        Assert.True(ok.CanUseTools);
        var missing = new SnapshotSessionRow { Session = new Models.SnapshotSession { ProjectDir = @"C:\Projects\demo" }, FolderMissing = true };
        Assert.False(missing.CanUseTools);
        Assert.Contains("missing", missing.ToolsTooltip);
        var none = new SnapshotSessionRow { Session = new Models.SnapshotSession { ProjectDir = "" } };
        Assert.False(none.CanUseTools);
    }

    // ------------------------------------------------------------------ how each item starts

    private static readonly string Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    [Fact]
    public void OpenFolderPassesOnlyTheQuotedFolderToExplorer()
    {
        var psi = ProjectTools.FolderStart(@"C:\Projects\it's a (demo)\");
        Assert.Equal(Explorer, psi.FileName);
        Assert.Equal("\"C:\\Projects\\it's a (demo)\"", psi.Arguments);
        Assert.False(psi.UseShellExecute);
        Assert.Equal(@"C:\", ProjectTools.TrimSeparator(@"C:\"));
    }

    [Fact]
    public void VSCodeGetsTheQuotedFolderAndMustBeCodeExe()
    {
        var psi = ProjectTools.VSCodeStart(@"C:\Tools\VS Code\Code.exe", @"C:\Projects\demo app");
        Assert.Equal(@"C:\Tools\VS Code\Code.exe", psi.FileName);
        Assert.Equal("\"C:\\Projects\\demo app\"", psi.Arguments);
        Assert.Equal(@"C:\Projects\demo app", psi.WorkingDirectory);
        Assert.False(psi.UseShellExecute);
        Assert.Throws<ArgumentException>(() => ProjectTools.VSCodeStart(@"C:\Tools\evil.exe", @"C:\Projects\demo"));
        Assert.Throws<ArgumentException>(() => ProjectTools.VSCodeStart(@"\\server\share\Code.exe", @"C:\Projects\demo"));
        Assert.Throws<ArgumentException>(() => ProjectTools.VSCodeStart("Code.exe", @"C:\Projects\demo"));
        Assert.Throws<ArgumentException>(() => ProjectTools.VSCodeStart(@"C:\Tools\..\Code.exe", @"C:\Projects\demo"));
    }

    [Theory]
    [InlineData("http://localhost:3000/", true)]
    [InlineData("https://github.com/example-org/demo", true)]
    [InlineData("https://github.com/example-org/my.repo_1-x", true)]
    [InlineData("http://localhost:3000/../x", false)]
    [InlineData("http://example.com/", false)]
    [InlineData("https://github.com/example-org/demo/settings", false)]
    [InlineData("https://github.com/example-org/demo\" calc", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("C:\\Windows\\System32\\calc.exe", false)]
    [InlineData("", false)]
    public void TheBrowserOpensOnlyKnownAddresses(string url, bool allowed)
    {
        Assert.Equal(allowed, ProjectTools.IsAllowedUrl(url));
        if (allowed)
        {
            var psi = ProjectTools.BrowserStart(url);
            Assert.Equal(Explorer, psi.FileName);
            Assert.Equal(url, psi.Arguments);
            Assert.False(psi.UseShellExecute);
        }
        else Assert.Throws<ArgumentException>(() => ProjectTools.BrowserStart(url));
    }

    [Theory]
    [InlineData(LaunchGuard.OpenProjectFolder)]
    [InlineData(LaunchGuard.OpenInVSCode)]
    [InlineData(LaunchGuard.OpenGitHub)]
    [InlineData(LaunchGuard.OpenInBrowser)]
    public void EachToolOpensOnlyInsideItsUserAction(string action)
    {
        var psi = ProjectTools.FolderStart(@"C:\Projects\demo");
        Assert.NotNull(LaunchGuard.Check(psi));
        using (LaunchGuard.UserAction(action))
            Assert.Null(LaunchGuard.Check(psi));
    }

    [Theory]
    [InlineData(@"C:\Tools\VS Code\Code.exe", true)]
    [InlineData(@"C:\Tools\VS Code\code.EXE", true)]
    [InlineData(@"C:\Tools\VS Code\Code.cmd", false)]
    [InlineData(@"C:\Tools\%X%\Code.exe", false)]
    [InlineData(@"relative\Code.exe", false)]
    public void OnlyAPlainLocalCodeExeCountsAsVSCode(string path, bool ok) => Assert.Equal(ok, ProjectTools.CheckVSCode(path) == null);

    // ------------------------------------------------------------------ review fixes

    [Theory]
    [InlineData("[core]\n\tbare = false\n\tfsmonitor = true\n[remote \"origin\"]\n\turl = https://github.com/example-org/demo.git\n", null)]
    [InlineData("[filter \"lfs\"]\n\tclean = git-lfs clean -- %f\n\tsmudge = git-lfs smudge -- %f\n\tprocess = git-lfs filter-process\n\trequired = true\n", null)]
    [InlineData("[filter \"evil\"]\n\tclean = cmd /c calc\n", "filter")]
    [InlineData("[FILTER \"x\"]\n\tprocess = x.exe\n", "filter")]
    [InlineData("[filter \"lfs\"]\n\tclean = cmd /c calc\n", "LFS")]
    [InlineData("[filter \"lfs\"]\n\tclean = git-lfs clean -- %f\n\textra = 1\n", "LFS")]
    [InlineData("[include]\n\tpath = ../other.cfg\n", "include")]
    [InlineData("[includeIf \"gitdir:~/x/\"]\n\tpath = x.cfg\n", "include")]
    [InlineData("[core]\n\tworktree = \\\\\\\\server\\\\share\n", "worktree")]
    [InlineData("[core]\n\texcludesFile = \\\\\\\\server\\\\x\n", "excludesfile")]
    [InlineData("# [filter \"x\"]\n; [include]\n", null)]
    public void ARepositoryWhoseConfigCanRunOrReachThingsGetsNoStatusCheck(string config, string? reason)
    {
        var why = GitRepo.UnsafeConfig(config);
        if (reason == null) Assert.Null(why);
        else Assert.Contains(reason, why, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AGitFilePointingAtANetworkShareIsNeverFollowed()
    {
        var wt = Folder("crafted");
        File.WriteAllText(Path.Combine(wt, ".git"), "gitdir: \\\\server\\share\\repo.git\n");
        Assert.Null(GitRepo.GitDirs(wt));
        Assert.Null(GitRepo.ConfigPath(wt));
        Assert.NotNull(GitRepo.UnsafeForStatus(wt));
        Assert.Null(GitRepo.GitHubPageFor(wt));
        Assert.Null(GitStatusCache.Query(wt));

        // A local worktree whose commondir points at a share is refused too.
        var gitDir = Path.Combine(_root, "wt-git");
        Directory.CreateDirectory(gitDir);
        File.WriteAllText(Path.Combine(gitDir, "commondir"), "\\\\server\\share\\common\n");
        var wt2 = Folder("crafted2");
        File.WriteAllText(Path.Combine(wt2, ".git"), "gitdir: " + gitDir + "\n");
        Assert.Null(GitRepo.GitDirs(wt2));
    }

    [Fact]
    public void ObjectAlternatesSkipTheStatusCheck()
    {
        var repo = Folder("alt", gitConfig: "[core]\n\tbare = false\n");
        Directory.CreateDirectory(Path.Combine(repo, ".git", "objects", "info"));
        File.WriteAllText(Path.Combine(repo, ".git", "objects", "info", "alternates"), "\\\\server\\share\\objects\n");
        Assert.Contains("alternates", GitRepo.UnsafeForStatus(repo));
    }

    /// <summary>A repository with its own filter driver: git status is never run, so the filter can't run either.</summary>
    [SkippableFact]
    public void ARepositoryFilterNeverRunsFromTheBadge()
    {
        var git = LocalRepoUpdateSource.FindGit();
        Skip.If(git == null, "git isn't installed");
        var repo = Folder("filtered");
        var init = new ProcessStartInfo(git!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-C", repo, "init", "-q" }) init.ArgumentList.Add(a);
        using (var p = Process.Start(init)!) { p.WaitForExit(30_000); }
        var marker = Path.Combine(_root, "filter-ran.txt");
        File.AppendAllText(Path.Combine(repo, ".git", "config"), $"[filter \"x\"]\n\tclean = cmd /c echo ran> \"{marker}\"\n");
        File.WriteAllText(Path.Combine(repo, ".gitattributes"), "* filter=x\n");
        File.WriteAllText(Path.Combine(repo, "a.txt"), "x");
        Assert.Null(GitStatusCache.Query(repo));
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task FoldersWithNoResultAreRetriedOnlyAfterTenMinutes()
    {
        var now = Now;
        int calls = 0;
        var done = new SemaphoreSlim(0);
        var cache = new GitStatusCache(_ => { Interlocked.Increment(ref calls); return null; }, () => now);
        cache.Updated += _ => done.Release();
        cache.Request(new[] { @"C:\Projects\slow" });
        Assert.True(await done.WaitAsync(5000));
        now = now.AddMinutes(5);
        cache.Request(new[] { @"C:\Projects\slow" });
        await Task.Delay(100);
        Assert.Equal(1, calls);
        now = now.AddMinutes(6);
        cache.Request(new[] { @"C:\Projects\slow" });
        Assert.True(await done.WaitAsync(5000));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(@"C:\Projects\demo", true)]
    [InlineData(@"C:\Projects\demo\", true)]
    [InlineData(@"C:\Projects\demo.v2", true)]
    [InlineData(@"C:\Projects\demo.", false)]
    [InlineData(@"C:\Projects\demo ", false)]
    [InlineData(@"C:\Projects\demo. .", false)]
    [InlineData(@"C:\Projects\demo.\src", false)]
    [InlineData(@"C:\Projects\x\..\demo", false)]
    [InlineData(@"C:\Projects\.\demo", false)]
    [InlineData("C:/Projects/demo", false)]
    public void ToolsOpenOnlyFoldersAlreadyInNormalForm(string dir, bool ok) => Assert.Equal(ok, ProjectTools.CheckFolderPath(dir) == null);

    [Fact]
    public void VSCodeDoesntInheritVariablesThatChangeWhatItIs()
    {
        var before = Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE");
        Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", "1");
        try
        {
            var psi = ProjectTools.VSCodeStart(@"C:\Tools\VS Code\Code.exe", @"C:\Projects\demo");
            Assert.False(psi.Environment.ContainsKey("ELECTRON_RUN_AS_NODE"));
            Assert.False(psi.Environment.ContainsKey("NODE_OPTIONS"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", before);
        }
    }

    [Fact]
    public void LookalikeLettersDontMakeAGitHubItem()
    {
        Assert.Null(GitRepo.GitHubPage("https://github.com/o\u212Awner/demo")); // Kelvin sign, not K
        Assert.Equal("https://github.com/Owner/Demo", GitRepo.GitHubPage("HTTPS://GITHUB.COM/Owner/Demo.git"));
    }
}
