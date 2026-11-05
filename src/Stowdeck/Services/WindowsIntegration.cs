using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32;

namespace Stowdeck.Services;

/// <summary>
/// Per-user Windows integration; nothing here needs admin rights.
/// - Start with Windows: a value under HKCU\Software\Microsoft\Windows\CurrentVersion\Run that starts
///   the app with --startup (tray). Off unless the user turns it on.
/// - Start menu: a shortcut in %APPDATA%\Microsoft\Windows\Start Menu\Programs.
/// </summary>
public sealed class WindowsIntegration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Stowdeck";

    /// <summary>Where Task Manager's Startup apps page records entries the user turned off.</summary>
    public const string StartupApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    private readonly string _runKeyPath;
    private readonly string _startMenuDir;
    private readonly string _approvedKeyPath;
    private readonly Func<string?, bool> _isOldExe;

    /// <param name="runKeyPath">HKCU subkey; overridable so tests never touch the real Run key.</param>
    /// <param name="startMenuDir">Folder for the Start menu shortcut; overridable for tests.</param>
    /// <param name="approvedKeyPath">HKCU subkey for Task Manager's on/off state; overridable for tests.</param>
    /// <param name="isOldExe">Tells the old app's exe apart from others; overridable for tests.</param>
    public WindowsIntegration(string? runKeyPath = null, string? startMenuDir = null, string? approvedKeyPath = null,
                              Func<string?, bool>? isOldExe = null)
    {
        _isOldExe = isOldExe ?? RenameMigration.IsOldExe;
        _runKeyPath = runKeyPath ?? RunKeyPath;
        _startMenuDir = startMenuDir ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        _approvedKeyPath = approvedKeyPath ?? StartupApprovedKeyPath;
    }

    /// <summary>The running executable (the apphost .exe, not the .dll).</summary>
    public static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path.");

    public static string StartupCommand(string exePath) => "\"" + exePath + "\" " + Program.StartupFlag;

    // ------------------------------------------------------------------ start with Windows

    /// <summary>The registered start-up command, or null when start with Windows is off.</summary>
    public string? GetStartupCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public bool IsStartWithWindowsEnabled => GetStartupCommand() != null;

    /// <summary>True when start-up is on but points at a different copy of the app (e.g. a moved build).</summary>
    public bool StartupPointsElsewhere(string exePath) =>
        GetStartupCommand() is { } cmd && !string.Equals(cmd, StartupCommand(exePath), StringComparison.OrdinalIgnoreCase);

    public void SetStartWithWindows(bool enabled, string exePath)
    {
        if (exePath.Contains('"')) throw new ArgumentException("Executable path contains a quote.", nameof(exePath));
        using var key = Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true)
                        ?? throw new InvalidOperationException("Cannot open " + _runKeyPath);
        if (enabled) key.SetValue(ValueName, StartupCommand(exePath), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    // ------------------------------------------------------------------ Start menu shortcut

    public string StartMenuShortcutPath => Path.Combine(_startMenuDir, "Stowdeck.lnk");

    public bool HasStartMenuShortcut => File.Exists(StartMenuShortcutPath);

    public void SetStartMenuShortcut(bool enabled, string exePath)
    {
        if (enabled)
        {
            Directory.CreateDirectory(_startMenuDir);
            CreateShortcut(StartMenuShortcutPath, exePath, "", "See, save and reopen Claude Code sessions");
        }
        else if (File.Exists(StartMenuShortcutPath))
        {
            File.Delete(StartMenuShortcutPath); // our own shortcut only
        }
    }

    // ------------------------------------------------------------------ the app's old name

    /// <summary>The Start with Windows value and Start menu shortcut the app used before it was renamed.</summary>
    public const string LegacyValueName = RenameMigration.OldName;
    public const string LegacyShortcutName = RenameMigration.OldName + ".lnk";

    public string LegacyShortcutPath => Path.Combine(_startMenuDir, LegacyShortcutName);

    /// <summary>
    /// Replaces SessionKeeper's Start with Windows value and Start menu shortcut with Stowdeck's, pointing at
    /// <paramref name="exePath"/>. Each is moved over only if it exists, so nothing is turned on that wasn't on before:
    /// an old value the user turned off in Task Manager is removed without adding a new one. The old value is touched
    /// only when it starts the old app's exe, and the old shortcut only when it points at it (checked by its version
    /// details, as another company's product shares the name). Returns what was changed.
    /// </summary>
    public List<string> TakeOverFromOldName(string exePath)
    {
        var done = new List<string>();
        bool hadStartup;
        using (var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true))
        {
            hadStartup = key?.GetValue(LegacyValueName) is string command && _isOldExe(ExeFromCommand(command));
            if (hadStartup) key!.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        bool turnedOff = false;
        if (hadStartup)
        {
            using var approved = Registry.CurrentUser.OpenSubKey(_approvedKeyPath, writable: true);
            // Task Manager stores 12 bytes per entry; an odd first byte means the user turned it off.
            turnedOff = approved?.GetValue(LegacyValueName) is byte[] { Length: > 0 } state && (state[0] & 1) == 1;
            approved?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
        }
        if (hadStartup)
        {
            done.Add("removed the old " + LegacyValueName + " Start with Windows entry" + (turnedOff ? " (it was turned off)" : ""));
            if (!turnedOff && !IsStartWithWindowsEnabled)
            {
                SetStartWithWindows(true, exePath);
                done.Add("Start with Windows now starts " + exePath);
            }
        }

        if (ReadShortcutTarget(LegacyShortcutPath) is { } target && _isOldExe(target))
        {
            File.Delete(LegacyShortcutPath);
            done.Add("removed the old Start menu shortcut " + LegacyShortcutName);
            if (!HasStartMenuShortcut)
            {
                SetStartMenuShortcut(true, exePath);
                done.Add("added the Start menu shortcut " + StartMenuShortcutPath);
            }
        }
        return done;
    }

    /// <summary>The program a Run value starts: the quoted path at its start, or everything up to the first space.</summary>
    internal static string ExeFromCommand(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            var end = command.IndexOf('"', 1);
            return end > 0 ? command[1..end] : command[1..];
        }
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    /// <summary>Target path of an existing .lnk (used by tests and the settings window).</summary>
    public static string? ReadShortcutTarget(string lnkPath)
    {
        if (!File.Exists(lnkPath)) return null;
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(lnkPath, 0);
            var sb = new StringBuilder(32768);
            link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
            return sb.ToString();
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    public static void CreateShortcut(string lnkPath, string target, string arguments, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetArguments(arguments);
            link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
            link.SetDescription(description);
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(lnkPath, true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }
}
