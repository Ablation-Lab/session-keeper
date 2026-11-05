using Microsoft.Win32;
using Stowdeck.Services;

namespace Stowdeck.Tests;

/// <summary>
/// The move from the app's old name (SessionKeeper): the data folder, the Start with Windows value and the Start menu
/// shortcut. Uses a temp folder and a throwaway HKCU key only: never the real %APPDATA%, Run key or Start menu.
/// </summary>
public sealed class RenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sd-rename-" + Guid.NewGuid().ToString("N"));
    // Its own parent key: WindowsIntegrationTests deletes Software\Stowdeck.Tests when it finishes, which could run meanwhile.
    private readonly string _key = @"Software\Stowdeck.RenameTests\" + Guid.NewGuid().ToString("N");

    public RenameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKey(_key, throwOnMissingSubKey: false); } catch { }
        try { Registry.CurrentUser.DeleteSubKey(@"Software\Stowdeck.RenameTests", throwOnMissingSubKey: false); } catch { } // only if empty
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Old => Path.Combine(_root, "SessionKeeper");
    private string New => Path.Combine(_root, "Stowdeck");

    private void MakeOldFolder()
    {
        Directory.CreateDirectory(Path.Combine(Old, "snapshots"));
        Directory.CreateDirectory(Path.Combine(Old, "logs"));
        File.WriteAllText(Path.Combine(Old, "settings.json"), """{ "openIn": "WindowsTerminalTab" }""");
        File.WriteAllText(Path.Combine(Old, "nicknames.json"), "{}");
        File.WriteAllText(Path.Combine(Old, "snapshots", "2026-11-03_184000_end-of-day.json"), """{ "name": "End of day" }""");
        File.WriteAllText(Path.Combine(Old, "logs", "launches-2026-11-03.log"), "x");
    }

    [Fact]
    public void TheOldDataFolderIsCopiedAndTheOriginalKeptAsABackup()
    {
        MakeOldFolder();
        var result = RenameMigration.MoveDataFolder(_root);

        Assert.Equal(RenameMigration.Outcome.Moved, result.Outcome);
        Assert.Equal("""{ "openIn": "WindowsTerminalTab" }""", File.ReadAllText(Path.Combine(New, "settings.json")));
        Assert.True(File.Exists(Path.Combine(New, "nicknames.json")));
        Assert.True(File.Exists(Path.Combine(New, "snapshots", "2026-11-03_184000_end-of-day.json")));
        Assert.True(File.Exists(Path.Combine(New, "logs", "launches-2026-11-03.log")));
        Assert.Contains("SessionKeeper.backup", File.ReadAllText(Path.Combine(New, RenameMigration.NoteFileName)));
        Assert.False(Directory.Exists(New + ".moving"));

        // The original is kept, whole, under the backup name.
        var backup = Path.Combine(_root, "SessionKeeper.backup");
        Assert.False(Directory.Exists(Old));
        Assert.True(File.Exists(Path.Combine(backup, "snapshots", "2026-11-03_184000_end-of-day.json")));
        Assert.True(File.Exists(Path.Combine(backup, "settings.json")));

        // The settings and snapshot stores read the moved files.
        Assert.Equal(OpenMode.WindowsTerminalTab, AppSettings.Load(Path.Combine(New, "settings.json")).OpenIn);
        Assert.Single(new SnapshotStore(Path.Combine(New, "snapshots")).List());
    }

    [Fact]
    public void NothingHappensOnceTheNewFolderExistsOrWithNoOldFolder()
    {
        Assert.Equal(RenameMigration.Outcome.NothingToMove, RenameMigration.MoveDataFolder(_root).Outcome);
        Assert.False(Directory.Exists(New));

        MakeOldFolder();
        Directory.CreateDirectory(New);
        File.WriteAllText(Path.Combine(New, "settings.json"), "{}");
        Assert.Equal(RenameMigration.Outcome.AlreadyMoved, RenameMigration.MoveDataFolder(_root).Outcome);
        Assert.Equal("{}", File.ReadAllText(Path.Combine(New, "settings.json"))); // never merged or overwritten
        Assert.True(Directory.Exists(Old)); // and the old folder is left alone
    }

    [Fact]
    public void AnEarlierBackupIsNeverOverwritten()
    {
        var earlier = Path.Combine(_root, "SessionKeeper.backup");
        Directory.CreateDirectory(earlier);
        File.WriteAllText(Path.Combine(earlier, "keep.txt"), "earlier");
        MakeOldFolder();

        var result = RenameMigration.MoveDataFolder(_root, new DateTime(2026, 11, 5, 9, 30, 0));
        Assert.Equal(RenameMigration.Outcome.Moved, result.Outcome);
        Assert.Equal("earlier", File.ReadAllText(Path.Combine(earlier, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_root, "SessionKeeper.backup-20261105-093000", "settings.json")));
    }

    [Fact]
    public void AnInterruptedCopyIsRedoneAndAnOpenOldFolderIsLeftInPlace()
    {
        MakeOldFolder();
        Directory.CreateDirectory(New + ".moving"); // left over from an interrupted copy
        File.WriteAllText(Path.Combine(New + ".moving", "partial.json"), "x");

        RenameMigration.Result result;
        // A file held open without delete sharing stops the folder being renamed, as a running SessionKeeper might.
        using (new FileStream(Path.Combine(Old, "nicknames.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            result = RenameMigration.MoveDataFolder(_root);

        Assert.Equal(RenameMigration.Outcome.MovedBackupNotRenamed, result.Outcome);
        Assert.False(File.Exists(Path.Combine(New, "partial.json")));
        Assert.True(File.Exists(Path.Combine(New, "settings.json")));
        Assert.True(File.Exists(Path.Combine(Old, "settings.json"))); // still there: it's the backup now
        Assert.Contains(Old, File.ReadAllText(Path.Combine(New, RenameMigration.NoteFileName)));
    }

    [Fact]
    public void TheOldStartupEntryAndShortcutAreReplaced()
    {
        var menu = Path.Combine(_root, "menu");
        Directory.CreateDirectory(menu);
        var oldExe = Path.Combine(_root, "old", "SessionKeeper.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(oldExe)!);
        File.WriteAllBytes(oldExe, Array.Empty<byte>());
        var newExe = Path.Combine(Environment.SystemDirectory, "notepad.exe"); // any existing exe
        using (var k = Registry.CurrentUser.CreateSubKey(_key))
            k.SetValue("SessionKeeper", "\"" + oldExe + "\" --startup");
        var wi = new WindowsIntegration(_key, menu);
        WindowsIntegration.CreateShortcut(wi.LegacyShortcutPath, oldExe, "", "old");

        var changes = wi.TakeOverFromOldName(newExe);

        Assert.Equal(4, changes.Count);
        using (var k = Registry.CurrentUser.OpenSubKey(_key)!)
            Assert.Null(k.GetValue("SessionKeeper"));
        Assert.Equal(WindowsIntegration.StartupCommand(newExe), wi.GetStartupCommand());
        Assert.False(File.Exists(wi.LegacyShortcutPath));
        Assert.Equal(newExe, WindowsIntegration.ReadShortcutTarget(wi.StartMenuShortcutPath), ignoreCase: true);
        Assert.EndsWith("Stowdeck.lnk", wi.StartMenuShortcutPath);

        Assert.Empty(wi.TakeOverFromOldName(newExe)); // a second run changes nothing
    }

    [Fact]
    public void NothingIsTurnedOnThatWasntOnAndOtherShortcutsAreLeftAlone()
    {
        var menu = Path.Combine(_root, "menu");
        Directory.CreateDirectory(menu);
        var wi = new WindowsIntegration(_key, menu);
        var other = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        // A shortcut that happens to be called SessionKeeper.lnk but starts something else isn't ours.
        WindowsIntegration.CreateShortcut(wi.LegacyShortcutPath, other, "", "someone else's");

        Assert.Empty(wi.TakeOverFromOldName(other));
        Assert.False(wi.IsStartWithWindowsEnabled);
        Assert.False(wi.HasStartMenuShortcut);
        Assert.True(File.Exists(wi.LegacyShortcutPath));
    }
}
