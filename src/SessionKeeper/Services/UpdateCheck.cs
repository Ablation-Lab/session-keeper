using System.IO;

namespace SessionKeeper.Services;

public enum UpdateState { UpToDate, UpdateAvailable }

/// <param name="NewerCommits">Commits touching the app source that this build doesn't have.</param>
/// <param name="Detail">One line for the tooltip.</param>
public sealed record UpdateStatus(UpdateState State, int NewerCommits, string Detail);

/// <summary>
/// Somewhere a newer version might come from. Today: the local source repository. Later, a GitHub Releases
/// source can implement this too and be added to <see cref="UpdateChecker.DefaultSources"/>.
/// Implementations must be read-only and quiet: return null when they can't tell, never throw.
/// </summary>
public interface IUpdateSource
{
    string Name { get; }
    UpdateStatus? Check(BuildInfo build, CancellationToken ct);
}

/// <summary>
/// Compares the commit this build was made from with HEAD of the source repository, counting only commits
/// that touch the app source (src/SessionKeeper). Only read-only git queries are run.
/// Never pulls, builds or installs anything.
/// </summary>
public sealed class LocalRepoUpdateSource : IUpdateSource
{
    public const string AppSourcePath = "src/SessionKeeper";
    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(3);
    private readonly string? _repo;
    private readonly string? _git;

    public LocalRepoUpdateSource(string? repoDir, string? gitExe)
    {
        _repo = Normalise(repoDir);
        _git = gitExe;
    }

    public string Name => "local repository";

    public UpdateStatus? Check(BuildInfo build, CancellationToken ct)
    {
        if (build.Commit == null || _repo == null || _git == null) return null;
        var dotGit = Path.Combine(_repo, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit)) return null; // not a repository (any more)
        ct.ThrowIfCancellationRequested();

        var count = LaunchGuard.RunReadOnlyGit(_git, _repo, "rev-list",
            new[] { "--count", build.Commit + "..HEAD", "--", AppSourcePath }, GitTimeout);
        if (count is not { ExitCode: 0, TimedOut: false } || !int.TryParse(count.StdOut.Trim(), out var n) || n < 0)
            return null; // unknown commit (e.g. built elsewhere), git error or timeout: stay quiet
        ct.ThrowIfCancellationRequested();

        var head = LaunchGuard.RunReadOnlyGit(_git, _repo, "rev-parse", new[] { "--short", "HEAD" }, GitTimeout);
        var headText = head is { ExitCode: 0, TimedOut: false } ? head.StdOut.Trim() : "?";

        return n > 0
            ? new UpdateStatus(UpdateState.UpdateAvailable, n,
                $"{n} newer commit{(n == 1 ? "" : "s")} to the app source (HEAD {headText}).\nDouble-click install.cmd in {_repo} to update.")
            : new UpdateStatus(UpdateState.UpToDate, 0, $"Up to date with {_repo} (HEAD {headText}).");
    }

    private static string? Normalise(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return null;
        try
        {
            var full = Path.GetFullPath(dir.Trim());
            return Path.IsPathFullyQualified(full) ? full.TrimEnd('\\', '/') : null;
        }
        catch { return null; }
    }

    /// <summary>git.exe from fully qualified PATH entries, else the usual Git for Windows locations.</summary>
    public static string? FindGit()
    {
        if (Launcher.SearchPath("git.exe") is { } onPath) return onPath;
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) is { Length: > 0 } l ? Path.Combine(l, "Programs") : "",
                 })
        {
            if (root.Length == 0) continue;
            var p = Path.Combine(root, "Git", "cmd", "git.exe");
            if (File.Exists(p)) return p;
        }
        return null;
    }
}

public static class UpdateChecker
{
    /// <summary>Where to look, in order. A GitHub Releases source would be appended here later.</summary>
    public static IEnumerable<IUpdateSource> DefaultSources(BuildInfo build, AppSettings settings)
    {
        yield return new LocalRepoUpdateSource(settings.SourceRepo ?? build.SourceRepo, LocalRepoUpdateSource.FindGit());
    }

    /// <summary>First source with an answer wins. Gives up quietly after <paramref name="overall"/>; never throws.</summary>
    public static async Task<UpdateStatus?> CheckAsync(BuildInfo build, IEnumerable<IUpdateSource> sources, TimeSpan overall)
    {
        using var cts = new CancellationTokenSource(overall);
        try
        {
            var work = Task.Run(() =>
            {
                foreach (var s in sources)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        if (s.Check(build, cts.Token) is { } status) return status;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch { /* a broken source is skipped */ }
                }
                return null;
            }, cts.Token);
            var done = await Task.WhenAny(work, Task.Delay(overall)).ConfigureAwait(false);
            return done == work && work.IsCompletedSuccessfully ? work.Result : null;
        }
        catch
        {
            return null;
        }
    }
}
