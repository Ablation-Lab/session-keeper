using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace SessionKeeper.Services;

/// <summary>
/// Read-only facts about a project's git repository: where it is and its remotes (read straight from the git config
/// file, no git process), the GitHub page for the Tools menu, and its status for the "uncommitted changes" badge.
/// </summary>
public static class GitRepo
{
    /// <summary>
    /// The repository root for a folder (the folder or the nearest parent with .git), or null. Network folders are
    /// skipped, and so are a drive root and the user profile folder as roots: a .git there is usually a dotfiles
    /// repository, whose changes have nothing to do with the project.
    /// </summary>
    public static string? FindRoot(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !IsLocal(dir)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(dir)) return null;
            var profile = Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            var current = new DirectoryInfo(dir);
            for (int i = 0; current != null && i < 64; i++, current = current.Parent)
            {
                var dotGit = Path.Combine(current.FullName, ".git");
                if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) continue;
                if (current.Parent == null || string.Equals(Path.TrimEndingDirectorySeparator(current.FullName), profile, StringComparison.OrdinalIgnoreCase))
                    return null;
                return current.FullName;
            }
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>A fully qualified local path: not a network share or device path (\\server, \\?\, //).</summary>
    internal static bool IsLocal(string path)
    {
        try
        {
            return !path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith("//", StringComparison.Ordinal) &&
                   Path.IsPathFullyQualified(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The repository's git folder: root\.git, or for a worktree (.git is a file) the folder it points to, and its
    /// shared folder (commondir). Null unless every one of those is a local path: a crafted .git file pointing at
    /// \\server\share would otherwise make SessionKeeper, or git, connect to that share in the background.
    /// </summary>
    internal static (string GitDir, string CommonDir)? GitDirs(string root)
    {
        try
        {
            var dotGit = Path.Combine(root, ".git");
            if (Directory.Exists(dotGit)) return (dotGit, dotGit);
            if (!File.Exists(dotGit) || new FileInfo(dotGit).Length > 4096) return null;
            var line = File.ReadLines(dotGit).FirstOrDefault(l => l.StartsWith("gitdir:", StringComparison.Ordinal));
            if (line == null) return null;
            var target = line["gitdir:".Length..].Trim();
            if (!IsLocalOrRelative(target)) return null;
            var gitDir = Path.GetFullPath(Path.Combine(root, target));
            if (!IsLocal(gitDir)) return null;
            var common = gitDir;
            var commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                var c = File.ReadAllText(commonFile).Trim();
                if (!IsLocalOrRelative(c)) return null;
                common = Path.GetFullPath(Path.Combine(gitDir, c));
                if (!IsLocal(common)) return null;
            }
            return (gitDir, common);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A local full path, or a relative one (no network path, and no drive-relative "C:x" or stream ':').</summary>
    private static bool IsLocalOrRelative(string p)
    {
        if (p.Length == 0 || p.StartsWith(@"\\", StringComparison.Ordinal) || p.StartsWith("//", StringComparison.Ordinal)) return false;
        return Path.IsPathFullyQualified(p) ? IsLocal(p) : !p.Contains(':');
    }

    /// <summary>The repository's config file: .git\config, or for a worktree the shared one. Null if it isn't local.</summary>
    internal static string? ConfigPath(string root) => GitDirs(root) is { } d ? Path.Combine(d.CommonDir, "config") : null;

    /// <summary>
    /// Why git status shouldn't run in this repository in the background, or null. git status can run commands the
    /// repository's own config names: filter drivers (clean/process, run when a file's timestamp changed), and it
    /// follows include files, a separate work tree and object alternates, which could point anywhere. A repository
    /// using any of these simply gets no badge. Git LFS's standard filter is allowed.
    /// </summary>
    internal static string? UnsafeForStatus(string root)
    {
        if (GitDirs(root) is not { } dirs) return "the git folder isn't local";
        try
        {
            if (File.Exists(Path.Combine(dirs.CommonDir, "objects", "info", "alternates"))) return "object alternates";
            var config = Path.Combine(dirs.CommonDir, "config");
            if (!File.Exists(config)) return null;
            if (new FileInfo(config).Length > 1024 * 1024) return "config too big to check";
            var why = UnsafeConfig(File.ReadAllText(config));
            if (why != null) return why;
            var worktreeConfig = Path.Combine(dirs.GitDir, "config.worktree");
            return File.Exists(worktreeConfig) ? UnsafeConfig(File.ReadAllText(worktreeConfig)) : null;
        }
        catch (Exception)
        {
            return "config unreadable";
        }
    }

    private static readonly HashSet<string> LfsFilterValues = new(StringComparer.Ordinal)
        { "git-lfs clean -- %f", "git-lfs smudge -- %f", "git-lfs filter-process", "true" };

    internal static string? UnsafeConfig(string configText)
    {
        string section = "";
        foreach (var raw in configText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';') continue;
            if (line[0] == '[')
            {
                var end = line.IndexOf(']');
                section = (end > 0 ? line[1..end] : line[1..]).Trim().ToLowerInvariant();
                if (section.StartsWith("include", StringComparison.Ordinal)) return "it includes other config files";
                if (section.StartsWith("filter", StringComparison.Ordinal) && !section.StartsWith("filter \"lfs\"", StringComparison.Ordinal))
                    return "it defines a filter";
                continue;
            }
            var eq = line.IndexOf('=');
            var key = (eq >= 0 ? line[..eq] : line).Trim().ToLowerInvariant();
            var value = eq >= 0 ? line[(eq + 1)..].Trim().Trim('"') : "";
            // (core.fsmonitor is always switched off on the command line, so it doesn't matter here.)
            if (section == "core" && key is "worktree" or "attributesfile" or "excludesfile") return "core." + key + " is set";
            if (section.StartsWith("filter \"lfs\"", StringComparison.Ordinal) && key is "clean" or "smudge" or "process" && !LfsFilterValues.Contains(value))
                return "its LFS filter isn't the standard one";
            if (section.StartsWith("filter \"lfs\"", StringComparison.Ordinal) && key is not ("clean" or "smudge" or "process" or "required"))
                return "its LFS filter has unexpected settings";
        }
        return null;
    }

    /// <summary>The remotes (name, url) in a git config file's text.</summary>
    internal static List<(string Name, string Url)> ParseRemotes(string configText)
    {
        var list = new List<(string, string)>();
        string? remote = null;
        foreach (var raw in configText.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var m = Regex.Match(line, @"^\[\s*remote\s+""([^""]+)""\s*\]");
                remote = m.Success ? m.Groups[1].Value : null;
                continue;
            }
            if (remote == null) continue;
            var kv = Regex.Match(line, @"^url\s*=\s*(.+)$", RegexOptions.IgnoreCase);
            if (kv.Success) list.Add((remote, kv.Groups[1].Value.Trim().Trim('"')));
        }
        return list;
    }

    public static List<(string Name, string Url)> Remotes(string root)
    {
        try
        {
            var config = ConfigPath(root);
            if (config == null || !File.Exists(config) || new FileInfo(config).Length > 1024 * 1024) return new();
            return ParseRemotes(File.ReadAllText(config));
        }
        catch (Exception)
        {
            return new();
        }
    }

    // Only the scheme and host ignore case (written out, not IgnoreCase: that would let [A-Za-z] match the Kelvin sign).
    private static readonly Regex GitHubRemote = new(
        @"^(?:(?:[Hh][Tt][Tt][Pp][Ss]?|[Ss][Ss][Hh]|[Gg][Ii][Tt])://(?:[^@/\s]+@)?[Gg][Ii][Tt][Hh][Uu][Bb]\.[Cc][Oo][Mm](?::[0-9]{1,5})?/|git@[Gg][Ii][Tt][Hh][Uu][Bb]\.[Cc][Oo][Mm]:)" +
        @"(?<owner>[A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/(?<repo>[A-Za-z0-9._-]{1,100}?)(?:\.git)?/?$",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The https://github.com/owner/repo page for a remote URL (https, ssh or git@ form), or null if it isn't a
    /// github.com repository. Owner and repository are limited to GitHub's own characters, so the page address can't
    /// carry anything else (and any user name or token in the remote is dropped).
    /// </summary>
    public static string? GitHubPage(string? remoteUrl)
    {
        if (remoteUrl == null) return null;
        var m = GitHubRemote.Match(remoteUrl.Trim());
        if (!m.Success) return null;
        var repo = m.Groups["repo"].Value;
        if (repo is "." or "..") return null;
        return "https://github.com/" + m.Groups["owner"].Value + "/" + repo;
    }

    /// <summary>The GitHub page of a folder's repository (origin first), or null.</summary>
    public static string? GitHubPageFor(string dir)
    {
        if (FindRoot(dir) is not { } root) return null;
        var remotes = Remotes(root);
        return remotes.OrderBy(r => r.Name == "origin" ? 0 : 1).Select(r => GitHubPage(r.Url)).FirstOrDefault(u => u != null);
    }
}

/// <summary>A repository's state for the badge: what's uncommitted and what isn't pushed.</summary>
public sealed record GitState(
    string? Branch, string? Upstream, int Ahead, int Behind, bool NoUpstream,
    int Staged, int Modified, int Untracked, int Conflicts, IReadOnlyList<string> Paths, DateTime CheckedAt)
{
    public bool Uncommitted => Staged + Modified + Untracked + Conflicts > 0;
    public bool Unpushed => Ahead > 0 || NoUpstream;
    public bool HasWork => Uncommitted || Unpushed;

    /// <summary>The badge's own text.</summary>
    public string BadgeText => Uncommitted ? "uncommitted changes" : "unpushed commits";

    /// <summary>The hover text: counts, a few file names and the push state.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        if (Uncommitted)
        {
            var parts = new List<string>();
            if (Staged > 0) parts.Add($"{Staged} staged");
            if (Modified > 0) parts.Add($"{Modified} modified");
            if (Untracked > 0) parts.Add($"{Untracked} untracked");
            if (Conflicts > 0) parts.Add($"{Conflicts} with conflicts");
            sb.Append("Uncommitted: ").AppendJoin(", ", parts);
            foreach (var p in Paths) sb.Append("\n  ").Append(p);
            int total = Staged + Modified + Untracked + Conflicts;
            if (total > Paths.Count && Paths.Count > 0) sb.Append("\n  … and ").Append(total - Paths.Count).Append(" more");
        }
        if (Ahead > 0)
            sb.Append(sb.Length > 0 ? "\n" : "").Append($"Unpushed: {Ahead} commit{(Ahead == 1 ? "" : "s")} ahead of {Upstream}");
        else if (NoUpstream)
            sb.Append(sb.Length > 0 ? "\n" : "").Append($"Unpushed: branch {Branch} has no upstream branch");
        if (Branch != null) sb.Append("\nBranch: ").Append(Branch).Append(Behind > 0 ? $" ({Behind} behind {Upstream})" : "");
        sb.Append($"\nChecked at {CheckedAt.ToLocalTime():HH:mm:ss}");
        return sb.ToString();
    }

    /// <summary>
    /// Parses <c>git status --porcelain=v2 --branch -z</c>. A branch without an upstream counts as unpushed only when the
    /// repository has a remote to push to and the branch has commits.
    /// </summary>
    public static GitState Parse(string output, bool hasRemote, DateTime checkedAt)
    {
        string? oid = null, head = null, upstream = null;
        int ahead = 0, behind = 0, staged = 0, modified = 0, untracked = 0, conflicts = 0;
        var paths = new List<string>();
        var records = output.Split('\0');
        for (int i = 0; i < records.Length; i++)
        {
            var r = records[i];
            if (r.Length < 2) continue;
            if (r.StartsWith("# ", StringComparison.Ordinal))
            {
                var parts = r.Split(' ', 3);
                if (parts.Length < 3) continue;
                switch (parts[1])
                {
                    case "branch.oid": oid = parts[2]; break;
                    case "branch.head": head = parts[2]; break;
                    case "branch.upstream": upstream = parts[2]; break;
                    case "branch.ab":
                        var ab = parts[2].Split(' ');
                        if (ab.Length == 2)
                        {
                            int.TryParse(ab[0].TrimStart('+'), out ahead);
                            int.TryParse(ab[1].TrimStart('-'), out behind);
                        }
                        break;
                }
                continue;
            }
            string? path = null;
            switch (r[0])
            {
                case '1':
                case '2':
                {
                    var f = r.Split(' ', r[0] == '1' ? 9 : 10);
                    if (f.Length < (r[0] == '1' ? 9 : 10)) continue;
                    if (f[1][0] != '.') staged++;
                    if (f[1].Length > 1 && f[1][1] != '.') modified++;
                    path = f[^1];
                    if (r[0] == '2') i++; // a rename or copy is followed by its original path
                    break;
                }
                case 'u':
                {
                    var f = r.Split(' ', 11);
                    if (f.Length < 11) continue;
                    conflicts++;
                    path = f[^1];
                    break;
                }
                case '?':
                    untracked++;
                    path = r[2..];
                    break;
            }
            if (path != null && paths.Count < 8) paths.Add(path);
        }
        var branch = head is null or "(detached)" ? null : head;
        bool noUpstream = upstream == null && branch != null && hasRemote && oid != null && oid != "(initial)";
        return new GitState(head, upstream, ahead, behind, noUpstream, staged, modified, untracked, conflicts, paths, checkedAt);
    }
}

/// <summary>
/// The badge's background checks: git status per project folder, at most two at a time, each result kept for a minute
/// (ten minutes when there's no result: not a repository, skipped as unsafe, timed out or failed).
/// Nothing here ever runs on the UI thread, and a slow or failing repository just has no badge.
/// </summary>
public sealed class GitStatusCache
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(60);
    /// <summary>Not a repository, skipped, timed out or failed: looked at again only after this long.</summary>
    public static readonly TimeSpan RetryNoState = TimeSpan.FromMinutes(10);
    private static readonly string[] StatusArgs = { "--porcelain=v2", "--branch", "-z", "--ignore-submodules=all" };

    private sealed record Entry(GitState? State, DateTime CheckedAt);

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(2);
    private readonly Func<string, GitState?> _query;
    private readonly Func<DateTime> _now;

    /// <summary>Raised (on a background thread) with the folder whose state was just checked.</summary>
    public event Action<string>? Updated;

    public GitStatusCache() : this(Query, () => DateTime.UtcNow) { }

    internal GitStatusCache(Func<string, GitState?> query, Func<DateTime> now)
    {
        _query = query;
        _now = now;
    }

    /// <summary>The last known state for a folder (null: not a repo, not checked yet, or the check failed).</summary>
    public GitState? Get(string? folder) => folder != null && _cache.TryGetValue(Key(folder), out var e) ? e.State : null;

    /// <summary>Checks, in the background, each folder whose result is missing or older than a minute.</summary>
    public void Request(IEnumerable<string?> folders)
    {
        foreach (var folder in folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => Key(f!)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_cache.TryGetValue(folder, out var e) && _now() - e.CheckedAt < (e.State == null ? RetryNoState : Fresh)) continue;
            if (!_pending.TryAdd(folder, 0)) continue;
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync().ConfigureAwait(false);
                GitState? state = null;
                try
                {
                    using (LaunchGuard.Background()) state = _query(folder);
                }
                catch (Exception) { }
                finally
                {
                    _gate.Release();
                }
                _cache[folder] = new Entry(state, _now());
                _pending.TryRemove(folder, out _);
                try { Updated?.Invoke(folder); } catch (Exception) { }
            });
        }
    }

    internal static string Key(string folder) => folder.TrimEnd('\\', '/');

    private static string? _git;
    private static bool _gitLooked;

    /// <summary>git status for the folder's repository, or null if it isn't one, git isn't installed, or it fails.</summary>
    internal static GitState? Query(string folder)
    {
        if (GitRepo.FindRoot(folder) is not { } root) return null;
        if (GitRepo.UnsafeForStatus(root) is { } why)
        {
            LaunchGuard.LogEvent("GITSKIP", $"repo={root}  reason={why} (no uncommitted changes badge for it)");
            return null;
        }
        if (!_gitLooked) { _git = LocalRepoUpdateSource.FindGit(); _gitLooked = true; }
        if (_git == null) return null;
        var result = LaunchGuard.RunReadOnlyGit(_git, root, "status", StatusArgs, TimeSpan.FromSeconds(10), logRoutine: false);
        if (result is not { ExitCode: 0, TimedOut: false }) return null;
        return GitState.Parse(result.StdOut, GitRepo.Remotes(root).Count > 0, DateTime.UtcNow);
    }
}
