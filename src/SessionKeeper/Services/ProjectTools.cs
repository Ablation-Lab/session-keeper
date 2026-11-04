using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace SessionKeeper.Services;

public enum ToolKind { OpenFolder, VSCode, GitHub, RemotionStudio, DevServer }

/// <summary>One item in a row's Tools menu. <paramref name="Detail"/> is shown faintly on the right (what it opens or runs).</summary>
public sealed record ProjectTool(ToolKind Kind, string Title, string? Detail = null, string? Url = null);

/// <summary>
/// The Tools menu on each row: which items apply to a project folder, and the process starts for the ones that don't
/// go through npm. Every start is by full path with no shell execution, and the folder or URL is the only argument.
/// </summary>
public static class ProjectTools
{
    /// <summary>
    /// The items for a folder, in menu order. Open folder always; VS Code when it's installed; GitHub when the
    /// repository has a github.com remote; Remotion Studio when package.json uses Remotion; Dev server when there's a
    /// dev script that isn't Remotion Studio's. Reads only small local files (package.json, .git\config).
    /// </summary>
    public static List<ProjectTool> For(string dir, string? vsCode)
    {
        var list = new List<ProjectTool> { new(ToolKind.OpenFolder, "Open folder", "File Explorer") };
        if (vsCode != null) list.Add(new(ToolKind.VSCode, "VS Code"));
        if (GitRepo.GitHubPageFor(dir) is { } page)
            list.Add(new(ToolKind.GitHub, "GitHub", page["https://github.com/".Length..], page));
        if (RemotionStudio.ReadPackage(dir) is { } pkg)
        {
            if (pkg.UsesRemotion)
                list.Add(new(ToolKind.RemotionStudio, "Remotion Studio", pkg.DevRunsStudio ? "npm run dev" : "npx remotion studio"));
            if (pkg.DevScript != null && !pkg.DevRunsStudio)
                list.Add(new(ToolKind.DevServer, "Dev server", Shorten(pkg.DevScript, 32)));
        }
        return list;
    }

    private static string Shorten(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..(max - 1)] + "\u2026";
    }

    // ------------------------------------------------------------------ VS Code

    /// <summary>
    /// VS Code's Code.exe: the per-user and per-machine install folders, then the App Paths registration, then the
    /// code.cmd on a fully qualified PATH entry (its bin folder sits next to Code.exe). Null if it isn't installed.
    /// </summary>
    public static string? FindVSCode()
    {
        var candidates = new List<string?>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code", "Code.exe"),
            AppPath(Microsoft.Win32.Registry.CurrentUser),
            AppPath(Microsoft.Win32.Registry.LocalMachine),
        };
        if (Launcher.SearchPath("code.cmd") is { } cmd)
        {
            try { candidates.Add(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cmd)!, "..", "Code.exe"))); }
            catch (Exception) { }
        }
        foreach (var c in candidates)
        {
            try
            {
                if (c != null && CheckVSCode(c) == null && File.Exists(c)) return c;
            }
            catch (Exception) { }
        }
        return null;
    }

    private static string? AppPath(Microsoft.Win32.RegistryKey hive)
    {
        try
        {
            using var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\Code.exe");
            return (key?.GetValue(null) as string)?.Trim().Trim('"');
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A plain local full path whose file is Code.exe.</summary>
    internal static string? CheckVSCode(string path)
    {
        if (path.IndexOfAny(new[] { '"', '*', '?', '<', '>', '|', '%' }) >= 0 || TerminalCommands.HiddenCharacter(path, allowTab: false) != null)
            return "invalid characters";
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return "network path";
        if (path.Replace('/', '\\').Split('\\').Any(seg => seg is "." or "..")) return ". or .. segments";
        try
        {
            if (!Path.IsPathFullyQualified(path)) return "not absolute";
        }
        catch (Exception) { return "not valid"; }
        return Path.GetFileName(path).Equals("Code.exe", StringComparison.OrdinalIgnoreCase) ? null : "not Code.exe";
    }

    // ------------------------------------------------------------------ process starts

    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>File Explorer on the folder: explorer.exe by full path with the quoted folder as its only argument.</summary>
    public static ProcessStartInfo FolderStart(string dir) =>
        new(Explorer, Launcher.WinQuote(TrimSeparator(dir))) { UseShellExecute = false };

    /// <summary>
    /// VS Code on the folder: Code.exe by full path, the quoted folder as its only argument (an absolute path, so it
    /// can't be read as an option). Started in the folder, like Explorer's "Open with Code".
    /// </summary>
    public static ProcessStartInfo VSCodeStart(string codeExe, string dir)
    {
        if (CheckVSCode(codeExe) is { } why) throw new ArgumentException("VS Code path: " + why, nameof(codeExe));
        var psi = new ProcessStartInfo(codeExe, Launcher.WinQuote(TrimSeparator(dir))) { UseShellExecute = false, WorkingDirectory = dir };
        // Inherited from a VS Code or Node process tree, these would change what Code.exe is: ELECTRON_RUN_AS_NODE makes
        // it plain Node, which would run the folder (its package.json "main") as a script.
        foreach (var key in psi.Environment.Keys.Where(k => k.StartsWith("ELECTRON_", StringComparison.OrdinalIgnoreCase) ||
                                                          k.Equals("NODE_OPTIONS", StringComparison.OrdinalIgnoreCase)).ToList())
            psi.Environment.Remove(key);
        return psi;
    }

    /// <summary>
    /// Why a Tools item mustn't open this folder, or null. The path must already be in Windows' normal form: Windows
    /// trims a trailing dot or space from a folder name, so "demo." would open "demo" (a different folder) in Explorer or
    /// VS Code, and forward slashes are read differently by different programs.
    /// </summary>
    public static string? CheckFolderPath(string dir)
    {
        if (dir.Contains('/')) return "the folder path uses forward slashes";
        if (dir.Split('\\').Skip(1).Any(seg => seg.EndsWith('.') || seg.EndsWith(' ')))
            return "a folder name in the path ends with a dot or a space, so Windows would open a different folder";
        try
        {
            var full = Path.GetFullPath(dir);
            if (!string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(dir), StringComparison.Ordinal))
                return "the folder path isn't in its normal form, so Windows would open a different folder";
        }
        catch (Exception)
        {
            return "the folder path is not valid";
        }
        return null;
    }

    private static readonly Regex AllowedUrl = new(
        @"^(http://localhost:\d{1,5}/|https://github\.com/[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9._-]{1,100})$", RegexOptions.CultureInvariant);

    internal static bool IsAllowedUrl(string url) => AllowedUrl.IsMatch(url);

    /// <summary>
    /// The default browser on a URL, through explorer.exe (no shell execution). Only a running server's localhost address
    /// or a github.com repository page is ever passed: anything else is refused.
    /// </summary>
    public static ProcessStartInfo BrowserStart(string url)
    {
        if (!IsAllowedUrl(url)) throw new ArgumentException("Not an address SessionKeeper opens: " + url, nameof(url));
        return new ProcessStartInfo(Explorer, url) { UseShellExecute = false };
    }

    /// <summary>"C:\x\" becomes "C:\x" (a trailing backslash before the closing quote confuses some programs); "C:\" stays.</summary>
    internal static string TrimSeparator(string dir)
    {
        var t = Path.TrimEndingDirectorySeparator(dir);
        return t.Length == 2 && t[1] == ':' ? t + "\\" : t;
    }
}
