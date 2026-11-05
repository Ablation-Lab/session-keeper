using System.IO;

namespace Stowdeck.Services;

/// <summary>
/// One-time move from the app's old name. Before it was renamed, the app was called SessionKeeper and kept its files in
/// %APPDATA%\SessionKeeper. On the first run of Stowdeck that folder is copied to %APPDATA%\Stowdeck, and the original
/// is then renamed to SessionKeeper.backup (kept, never deleted). Nothing happens once %APPDATA%\Stowdeck exists.
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
            if (Directory.Exists(newDir)) return new Result(Outcome.AlreadyMoved, newDir + " already exists");
            if (!Directory.Exists(oldDir)) return new Result(Outcome.NothingToMove, "no " + oldDir);

            // The app, the command line and MCP connectors can all start at once: only one of them moves the folder.
            using var mutex = new Mutex(false, MutexName);
            bool owned;
            try { owned = mutex.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return new Result(Outcome.Failed, "another copy was still moving the folder");
            try
            {
                if (Directory.Exists(newDir)) return new Result(Outcome.AlreadyMoved, newDir + " already exists");
                if (!Directory.Exists(oldDir)) return new Result(Outcome.NothingToMove, "no " + oldDir);

                // Copy into a staging folder first, so an interrupted copy is never mistaken for a finished one.
                var staging = newDir + ".moving";
                if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
                CopyTree(oldDir, staging);
                Directory.Move(staging, newDir);

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
