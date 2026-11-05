using System.Diagnostics;
using System.IO;

namespace Stowdeck.Services;

/// <summary>
/// One-time move from the app's old name. Before it was renamed, the app was called SessionKeeper and kept its files in
/// %APPDATA%\SessionKeeper. On the first run of Stowdeck the files in that folder are copied into %APPDATA%\Stowdeck
/// (a file Stowdeck already has is kept, never overwritten), and the original folder is then renamed to
/// SessionKeeper.backup (kept, never deleted). The move is done once the note file is written; until then a move that
/// failed or was put off (because SessionKeeper was still running) is tried again on the next run.
/// </summary>
public static class RenameMigration
{
    public const string OldName = "SessionKeeper";
    public const string BackupFolderName = OldName + ".backup";
    public const string NoteFileName = "moved-from-SessionKeeper.txt";
    /// <summary>The old exe's description (its Comments field), which, with its product name, tells it apart from other programs.</summary>
    private const string OldDescription = "See, save and reopen Claude Code sessions.";
    private const string OldExitEventName = @"Local\" + OldName + ".Exit";
    private const string OldMcpStopEventName = @"Local\" + OldName + ".McpStop";
    private const string MutexName = @"Local\Stowdeck.RenameMigration";
    /// <summary>Tests that start the real exe set this to 1, so it never moves the real folder.</summary>
    public const string SkipVariable = "STOWDECK_SKIP_MIGRATION";

    public enum Outcome { NothingToMove, AlreadyMoved, Moved, MovedBackupNotRenamed, Deferred, Failed }

    public sealed record Result(Outcome Outcome, string Message);

    /// <summary>Runs the move for the real %APPDATA%. Never throws; the result is also logged.</summary>
    public static Result Run()
    {
        if (Environment.GetEnvironmentVariable(SkipVariable) == "1")
            return new Result(Outcome.NothingToMove, "skipped (" + SkipVariable + ")");
        var result = MoveDataFolder(AppPaths.AppData, oldAppRunning: () => OldAppProcessIds().Count > 0);
        if (result.Outcome is not (Outcome.NothingToMove or Outcome.AlreadyMoved))
            LaunchGuard.LogEvent("APP", "rename: " + result.Message);
        return result;
    }

    /// <summary>
    /// Copies the files in <paramref name="appData"/>\SessionKeeper into \Stowdeck, then renames the original to a backup.
    /// <paramref name="oldAppRunning"/> puts the move off while the old app still runs (it would keep writing to the old folder).
    /// </summary>
    public static Result MoveDataFolder(string appData, DateTime? now = null, Func<bool>? oldAppRunning = null)
    {
        var oldDir = Path.Combine(appData, OldName);
        var newDir = Path.Combine(appData, AppPaths.DataFolderName);
        var note = Path.Combine(newDir, NoteFileName);
        try
        {
            if (File.Exists(note)) return new Result(Outcome.AlreadyMoved, "moved earlier (" + note + ")");
            if (!Directory.Exists(oldDir)) return new Result(Outcome.NothingToMove, "no " + oldDir);
            if (!LooksLikeOurs(oldDir)) return new Result(Outcome.NothingToMove, oldDir + " doesn't hold this app's files");
            if (oldAppRunning?.Invoke() == true)
                return new Result(Outcome.Deferred, "put off: SessionKeeper is still running and using " + oldDir);

            // The app, the command line and MCP connectors can all start at once: only one of them moves the folder.
            using var mutex = new Mutex(false, MutexName);
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return new Result(Outcome.Failed, "another copy of Stowdeck was still moving the folder");
            try
            {
                if (File.Exists(note)) return new Result(Outcome.AlreadyMoved, "moved earlier (" + note + ")");
                if (!Directory.Exists(oldDir)) return new Result(Outcome.NothingToMove, "no " + oldDir);

                // File by file, each copied under a temporary name first, so an interrupted copy never leaves a
                // half-written file; the next run carries on where it stopped.
                var keptNew = new List<string>();
                var links = new List<string>();
                MergeTree(oldDir, newDir, "", keptNew, links);

                var stamp = now ?? DateTime.Now;
                var backup = Path.Combine(appData, BackupFolderName);
                if (Directory.Exists(backup) || File.Exists(backup))
                    backup = Path.Combine(appData, BackupFolderName + "-" + stamp.ToString("yyyyMMdd-HHmmss"));
                string where;
                Outcome outcome;
                try
                {
                    Directory.Move(oldDir, backup);
                    where = "The original folder was renamed to " + backup + ".";
                    outcome = Outcome.Moved;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Something still has a file in it open: leave the folder as it is. It's the backup now.
                    backup = oldDir;
                    where = "The original folder was left as it was (" + ex.Message.Trim() + ").";
                    outcome = Outcome.MovedBackupNotRenamed;
                }
                WriteNote(note, oldDir, backup, stamp, keptNew, links);
                return new Result(outcome, "copied " + oldDir + " to " + newDir + ". " + where
                    + (keptNew.Count > 0 ? " Kept Stowdeck's own " + string.Join(", ", keptNew) + "." : "")
                    + (links.Count > 0 ? " Not copied (links): " + string.Join(", ", links) + "." : ""));
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
        catch (Exception ex)
        {
            return new Result(Outcome.Failed, "couldn't move " + oldDir + ": " + ex.Message);
        }
    }

    /// <summary>
    /// True if the folder holds this app's files. Another company sells a different product called SessionKeeper, so
    /// a folder with that name is only moved if it has the launch logs, session names, snapshots or settings this app wrote.
    /// </summary>
    internal static bool LooksLikeOurs(string dir)
    {
        if (File.Exists(Path.Combine(dir, "nicknames.json"))) return true;
        var logs = Path.Combine(dir, "logs");
        if (Directory.Exists(logs) && Directory.EnumerateFiles(logs, "launches-*.log").Any()) return true;
        var snapshots = Path.Combine(dir, "snapshots");
        if (Directory.Exists(snapshots) && Directory.EnumerateFiles(snapshots, "*.json").Any()) return true;
        var settings = Path.Combine(dir, "settings.json");
        return File.Exists(settings) && File.ReadAllText(settings).Contains("\"openIn\"", StringComparison.Ordinal);
    }

    /// <summary>True if <paramref name="path"/> is the old app's exe (by its name, product name and description).</summary>
    internal static bool IsOldExe(string? path)
    {
        if (string.IsNullOrEmpty(path) || !string.Equals(Path.GetFileName(path), OldName + ".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)) return false;
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.ProductName == OldName && info.Comments == OldDescription;
    }

    /// <summary>Running copies of the old app: its window, its command line or its MCP connector.</summary>
    public static List<int> OldAppProcessIds()
    {
        var ids = new List<int>();
        foreach (var p in Process.GetProcessesByName(OldName))
        {
            using (p)
            {
                try
                {
                    if (IsOldExe(Native.QueryProcess(p.Id).ImagePath)) ids.Add(p.Id);
                }
                catch
                {
                    // Gone, or not ours to look at.
                }
            }
        }
        return ids;
    }

    /// <summary>
    /// Asks the old app to exit cleanly, as its installer did: the window and tray app through its exit signal, its MCP
    /// connectors through their stop signal. Never kills it. Returns true once none is left running.
    /// </summary>
    public static bool CloseOldApp(TimeSpan wait)
    {
        using var mcpStop = new EventWaitHandle(false, EventResetMode.ManualReset, OldMcpStopEventName);
        mcpStop.Set();
        try
        {
            if (EventWaitHandle.TryOpenExisting(OldExitEventName, out var exit))
                using (exit) exit.Set();
        }
        catch
        {
            // Older copies without the signal still get CloseMainWindow below.
        }
        foreach (var id in OldAppProcessIds())
        {
            try { using var p = Process.GetProcessById(id); p.CloseMainWindow(); } catch { }
        }
        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            if (OldAppProcessIds().Count == 0) return true;
            Thread.Sleep(300);
        }
        return OldAppProcessIds().Count == 0;
    }

    private static void MergeTree(string from, string to, string rel, List<string> keptNew, List<string> links)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(to, name);
            if (File.Exists(dest))
            {
                // Stowdeck's own copy wins; the old one stays in the backup. Logs and cache aren't worth mentioning.
                var relPath = Path.Combine(rel, name);
                if (!(rel.StartsWith("logs", StringComparison.OrdinalIgnoreCase) || rel.StartsWith("cache", StringComparison.OrdinalIgnoreCase)))
                    keptNew.Add(relPath);
                continue;
            }
            var temp = dest + ".moving";
            File.Copy(file, temp, overwrite: true);
            File.Move(temp, dest);
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            var name = Path.GetFileName(dir);
            // A junction or symbolic link is not followed (its target isn't ours to copy), but the note names it.
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                links.Add(Path.Combine(rel, name));
                continue;
            }
            MergeTree(dir, Path.Combine(to, name), Path.Combine(rel, name), keptNew, links);
        }
    }

    private static void WriteNote(string note, string oldDir, string backup, DateTime when, List<string> keptNew, List<string> links)
    {
        try
        {
            var text = $"Stowdeck used to be called SessionKeeper. On {when:yyyy-MM-dd HH:mm} its settings, session names, snapshots, " +
                       $"logs and cache were copied here from {oldDir}.\r\n" +
                       $"The original is kept, unchanged, at {backup}. Delete it once you're happy everything is here.\r\n";
            if (keptNew.Count > 0)
                text += "\r\nThese were already here, so Stowdeck's own copies were kept (the old ones are in the backup):\r\n"
                        + string.Concat(keptNew.Select(f => "  " + f + "\r\n"));
            if (links.Count > 0)
                text += "\r\nThese are links to folders elsewhere, so they weren't copied. Their files are still where the links point:\r\n"
                        + string.Concat(links.Select(f => "  " + f + "\r\n"));
            File.WriteAllText(note, text);
        }
        catch
        {
            // The files are copied either way. Without the note, a later run finds nothing new to copy.
        }
    }
}
