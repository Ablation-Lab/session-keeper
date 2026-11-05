using System.IO;

namespace Stowdeck.Services;

/// <summary>
/// One-time move from the app's old name. Before it was renamed, the app was called SessionKeeper and kept its files in
/// %APPDATA%\SessionKeeper. On the first run of Stowdeck that folder is copied to %APPDATA%\Stowdeck, and the original
/// is then renamed to SessionKeeper.backup (kept, never deleted). Nothing happens once %APPDATA%\Stowdeck holds more
/// than logs and cache.
/// </summary>
public static class RenameMigration
{
    public const string OldName = "SessionKeeper";
    public const string BackupFolderName = OldName + ".backup";
    public const string NoteFileName = "moved-from-SessionKeeper.txt";
    private const string MutexName = @"Local\Stowdeck.RenameMigration";
    /// <summary>Tests that start the real exe set this to 1, so it never moves the real folder.</summary>
    public const string SkipVariable = "STOWDECK_SKIP_MIGRATION";

    public enum Outcome { NothingToMove, AlreadyMoved, Moved, MovedBackupNotRenamed, Failed }

    public sealed record Result(Outcome Outcome, string Message);

    /// <summary>Runs the move for the real %APPDATA%. Never throws; the result is also logged.</summary>
    public static Result Run()
    {
        if (Environment.GetEnvironmentVariable(SkipVariable) == "1")
            return new Result(Outcome.NothingToMove, "skipped (" + SkipVariable + ")");
        var result = MoveDataFolder(AppPaths.AppData);
        if (result.Outcome is not (Outcome.NothingToMove or Outcome.AlreadyMoved))
            LaunchGuard.LogEvent("APP", "rename: " + result.Message);
        return result;
    }

    /// <summary>Copies <paramref name="appData"/>\SessionKeeper to \Stowdeck, then renames the original to a backup.</summary>
    public static Result MoveDataFolder(string appData, DateTime? now = null)
    {
        var oldDir = Path.Combine(appData, OldName);
        var newDir = Path.Combine(appData, AppPaths.DataFolderName);
        try
        {
            if (InUse(newDir)) return new Result(Outcome.AlreadyMoved, newDir + " already exists");
            if (!Directory.Exists(oldDir)) return new Result(Outcome.NothingToMove, "no " + oldDir);

            // The app, the command line and MCP connectors can all start at once: only one of them moves the folder.
            using var mutex = new Mutex(false, MutexName);
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return new Result(Outcome.Failed, "another copy was still moving the folder");
            try
            {
                if (InUse(newDir)) return new Result(Outcome.AlreadyMoved, newDir + " already exists");
                if (!Directory.Exists(oldDir)) return new Result(Outcome.NothingToMove, "no " + oldDir);

                // Copy into a staging folder first, so an interrupted copy is never mistaken for a finished one.
                var staging = newDir + ".moving";
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                CopyTree(oldDir, staging);
                if (Directory.Exists(newDir))
                {
                    // Only logs and cache so far (see InUse): keep the new logs, then swap the folders.
                    MergeLogs(Path.Combine(newDir, "logs"), Path.Combine(staging, "logs"));
                    var aside = newDir + ".before-move";
                    if (Directory.Exists(aside)) Directory.Delete(aside, recursive: true);
                    Directory.Move(newDir, aside);
                    Directory.Move(staging, newDir);
                    try { Directory.Delete(aside, recursive: true); } catch { } // only logs and cache, already merged
                }
                else
                {
                    Directory.Move(staging, newDir);
                }

                var stamp = (now ?? DateTime.Now);
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
                    // Something (such as a running copy of SessionKeeper) still has it open: leave it as it is.
                    backup = oldDir;
                    where = "The original folder was left as it was (" + ex.Message.Trim() + ").";
                    outcome = Outcome.MovedBackupNotRenamed;
                }
                WriteNote(newDir, oldDir, backup, stamp);
                return new Result(outcome, "copied " + oldDir + " to " + newDir + ". " + where);
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
    /// True once the new folder holds anything the user would miss. A folder with only the launch logs and the .NET
    /// check's cache doesn't count: the app writes those even when a move failed or was skipped, and the move should
    /// still happen next time.
    /// </summary>
    private static bool InUse(string newDir) =>
        Directory.Exists(newDir) && Directory.EnumerateFileSystemEntries(newDir).Any(e =>
            !Directory.Exists(e) || !(Path.GetFileName(e).Equals("logs", StringComparison.OrdinalIgnoreCase)
                                      || Path.GetFileName(e).Equals("cache", StringComparison.OrdinalIgnoreCase)));

    /// <summary>Adds the new folder's launch logs to the copied ones (a log for the same day is appended to).</summary>
    private static void MergeLogs(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
        {
            var dest = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(dest)) File.AppendAllText(dest, File.ReadAllText(file));
            else File.Copy(file, dest);
        }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.EnumerateFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: false);
        foreach (var dir in Directory.EnumerateDirectories(from))
        {
            // A junction or symbolic link is not followed (its target isn't ours to copy).
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
        }
    }

    private static void WriteNote(string newDir, string oldDir, string backup, DateTime when)
    {
        try
        {
            File.WriteAllText(Path.Combine(newDir, NoteFileName),
                $"Stowdeck used to be called SessionKeeper. On {when:yyyy-MM-dd HH:mm} its settings, session names, snapshots, " +
                $"logs and cache were copied here from {oldDir}.\r\n" +
                $"The original is kept, unchanged, at {backup}. Delete it once you're happy everything is here.\r\n");
        }
        catch
        {
            // The note is a courtesy: the move itself has already succeeded.
        }
    }
}
