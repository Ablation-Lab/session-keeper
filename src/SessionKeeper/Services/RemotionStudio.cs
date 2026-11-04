using System.IO;
using System.Text.Json;

namespace SessionKeeper.Services;

/// <summary>A folder whose package.json uses Remotion.</summary>
/// <param name="DevScript">The package.json "dev" script, or null when there isn't one.</param>
/// <param name="Installed">Whether node_modules has Remotion's CLI (if not, the project needs npm install first).</param>
public sealed record RemotionProject(string Name, string Dir, string? DevScript, bool Installed)
{
    /// <summary>
    /// Whether npm run dev starts Studio. Only when the dev script runs remotion studio (or preview): a project that
    /// only uses Remotion's player may have "dev": "next dev", which isn't Studio, so npx remotion studio is used.
    /// </summary>
    public bool DevRunsStudio => RemotionStudio.RunsStudio(DevScript);
}

/// <summary>
/// How Studio is started: npm.cmd or npx.cmd by full path, with one of two fixed argument lists. Nothing in it comes
/// from the project or the folder name; the folder only reaches the new shell as a quoted literal (see Launcher).
/// </summary>
public sealed record StudioCommand(string Exe, IReadOnlyList<string> Args)
{
    public string Display => Path.GetFileName(Exe) + " " + string.Join(' ', Args);
}

/// <summary>A Studio already running for a project: its processes and, once it's listening, its port.</summary>
public sealed record StudioInstance(IReadOnlyList<int> Pids, int? Port)
{
    public string? Address => Port is { } p ? RemotionStudio.AddressFor(p) : null;
}

/// <summary>
/// The Remotion Studio button: finds Remotion projects, works out the command that starts Studio, and spots a Studio
/// that's already running. Detection is read-only: it reads the process list, command lines, working folders and
/// the system's TCP listener table, and never opens a connection or touches a process.
/// </summary>
public static class RemotionStudio
{
    public const string DefaultProjectsFolder = @"C:\Projects";
    public const string Npm = "npm.cmd", Npx = "npx.cmd";

    /// <summary>The only two commands ever run: the project's dev script, or Remotion's own studio command.</summary>
    public static readonly IReadOnlyList<string> DevArgs = new[] { "run", "dev" };
    public static readonly IReadOnlyList<string> StudioArgs = new[] { "remotion", "studio" };

    private const long MaxPackageJson = 1024 * 1024;

    private static readonly System.Text.RegularExpressions.Regex StudioInScript = new(
        @"(^|[\s/\\&|;(""'])remotion(\.cmd)?\s+(studio|preview)(\s|$|[&|;)""'])",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>Whether a package.json script runs remotion studio (or the older remotion preview).</summary>
    public static bool RunsStudio(string? script) => script != null && StudioInScript.IsMatch(script);

    /// <summary>
    /// Characters cmd.exe treats specially. npm.cmd and the remotion.cmd shim npm runs use the folder path in cmd
    /// lines, some unquoted (SET dp0=%~dp0), so a folder named "demo&amp;calc" would run calc. Such folders are refused.
    /// </summary>
    private static readonly char[] CmdSpecial = { '&', '|', '<', '>', '^', '%' };

    /// <summary>Why Studio can't be started in this folder, or null. Checked just before launching.</summary>
    public static string? CheckFolder(string dir)
    {
        var bad = dir.IndexOfAny(CmdSpecial);
        if (bad >= 0)
            return $"the folder path contains \u201C{dir[bad]}\u201D, which cmd.exe (used by npm) treats as a command character. Rename the folder to start Studio from here";
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(dir));
        if (name.EndsWith('.') || name.EndsWith(' ')) return "the folder name ends with a dot or a space";
        if (NpmrcSetsPrefix(dir)) return "the project's .npmrc sets prefix (or globalconfig/userconfig), which would make npm.cmd run a different npm than the installed one";
        return null;
    }

    /// <summary>
    /// npm.cmd and npx.cmd first ask npm for its global prefix, which reads the project's .npmrc, and then run the
    /// npm found under that prefix if there is one. A project .npmrc that sets prefix (or points at other config files)
    /// could therefore make them run an npm from the project, so such projects are refused.
    /// </summary>
    internal static bool NpmrcSetsPrefix(string dir)
    {
        try
        {
            var file = Path.Combine(dir, ".npmrc");
            var info = new FileInfo(file);
            if (!info.Exists) return false;
            if (info.Length > MaxPackageJson) return true; // too big to check: refuse rather than guess
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim().TrimStart('"', '\'');
                var eq = line.IndexOf('=');
                if (eq <= 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                var key = line[..eq].Trim().Trim('"', '\'').Replace('_', '-');
                if (key.Equals("prefix", StringComparison.OrdinalIgnoreCase) || key.Equals("globalconfig", StringComparison.OrdinalIgnoreCase) ||
                    key.Equals("userconfig", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception)
        {
            return true; // can't read it: refuse rather than guess
        }
    }

    // ------------------------------------------------------------------ projects

    /// <summary>Folders directly under <paramref name="root"/> whose package.json uses Remotion, by name.</summary>
    public static List<RemotionProject> FindProjects(string? root = null)
    {
        root = string.IsNullOrWhiteSpace(root) ? DefaultProjectsFolder : root;
        var list = new List<RemotionProject>();
        IEnumerable<string> dirs;
        try
        {
            if (!Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return list;
            dirs = Directory.EnumerateDirectories(root).ToList();
        }
        catch (Exception)
        {
            return list;
        }
        foreach (var dir in dirs)
        {
            var name = Path.GetFileName(dir);
            // A name ending in a dot or space is skipped: Windows APIs trim it, so "demo." would read demo's files.
            if (name.StartsWith('.') || name.EndsWith('.') || name.EndsWith(' ') ||
                name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
            if (ReadProject(dir) is { } p) list.Add(p);
        }
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The project in this folder, or null if it has no readable package.json that uses Remotion.</summary>
    public static RemotionProject? ReadProject(string dir)
    {
        try
        {
            var file = Path.Combine(dir, "package.json");
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxPackageJson) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!UsesRemotion(doc.RootElement)) return null;
            var installed = Directory.Exists(Path.Combine(dir, "node_modules", "@remotion", "cli"));
            return new RemotionProject(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), dir, DevScript(doc.RootElement), installed);
        }
        catch (Exception)
        {
            return null; // unreadable or not JSON: not offered
        }
    }

    /// <summary>Whether any dependency list names "remotion" or an "@remotion/" package.</summary>
    public static bool UsesRemotion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies", "optionalDependencies" })
        {
            if (!root.TryGetProperty(section, out var deps) || deps.ValueKind != JsonValueKind.Object) continue;
            foreach (var dep in deps.EnumerateObject())
                if (dep.Name == "remotion" || dep.Name.StartsWith("@remotion/", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>The "dev" script, or null if there's none (or it's empty).</summary>
    public static string? DevScript(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object &&
        scripts.TryGetProperty("dev", out var dev) && dev.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(dev.GetString())
            ? dev.GetString()
            : null;

    // ------------------------------------------------------------------ the command

    /// <summary>
    /// npm.cmd run dev when the project's dev script runs Studio, else npx.cmd remotion studio. The .cmd shims are used, not
    /// npm/npx, because PowerShell's script policy can block npm.ps1. Null (with the reason) if the tool isn't found.
    /// </summary>
    public static StudioCommand? CommandFor(RemotionProject project, Func<string, string?> findTool, out string? problem)
    {
        var (tool, args) = project.DevRunsStudio ? (Npm, DevArgs) : (Npx, StudioArgs);
        problem = null;
        var exe = findTool(tool);
        if (exe == null)
        {
            problem = tool + " wasn't found. Is Node.js installed?";
            return null;
        }
        var cmd = new StudioCommand(exe, args);
        problem = Check(cmd, project.Dir);
        return problem == null ? cmd : null;
    }

    /// <summary>The Node.js install folder first, then the fully qualified PATH entries.</summary>
    public static string? FindTool(string name)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", name),
            Launcher.SearchPath(name),
        };
        foreach (var c in candidates)
        {
            try
            {
                if (c != null && CheckTool(c) == null && File.Exists(c)) return c;
            }
            catch (Exception) { }
        }
        return null;
    }

    /// <summary>
    /// Why this command must not run in that folder, or null. Only npm.cmd run dev or npx.cmd remotion studio; the
    /// tool must be a plain local full path outside the project, so the project can't supply its own npm.cmd. (Its
    /// .npmrc could still redirect npm.cmd to another npm: <see cref="CheckFolder"/> refuses that.)
    /// </summary>
    public static string? Check(StudioCommand cmd, string projectDir)
    {
        if (CheckTool(cmd.Exe) is { } why) return why;
        var name = Path.GetFileName(cmd.Exe);
        bool allowed = name.Equals(Npm, StringComparison.OrdinalIgnoreCase) ? cmd.Args.SequenceEqual(DevArgs)
            : name.Equals(Npx, StringComparison.OrdinalIgnoreCase) && cmd.Args.SequenceEqual(StudioArgs);
        if (!allowed) return "only npm.cmd run dev or npx.cmd remotion studio can be run";
        try
        {
            if (IsInside(cmd.Exe, projectDir)) return "npm.cmd or npx.cmd inside the project folder isn't used";
        }
        catch (Exception)
        {
            return "the folder path is not valid";
        }
        return null;
    }

    /// <summary>A plain local full path to npm.cmd or npx.cmd: no network or device path, no . or .. segments.</summary>
    internal static string? CheckTool(string? exe)
    {
        if (string.IsNullOrWhiteSpace(exe)) return "no program";
        if (exe.IndexOfAny(new[] { '"', '*', '?', '<', '>', '|', '%', '&', '^' }) >= 0 || TerminalCommands.HiddenCharacter(exe, allowTab: false) != null)
            return "the program path contains invalid characters";
        if (exe.StartsWith(@"\\", StringComparison.Ordinal) || exe.StartsWith("//", StringComparison.Ordinal))
            return "programs on network or device paths aren't run";
        if (exe.Replace('/', '\\').Split('\\').Any(seg => seg is "." or "..")) return "the program path contains . or .. segments";
        if (exe.IndexOf(':', 2) >= 0) return "the program path is not valid";
        try
        {
            if (!Path.IsPathFullyQualified(exe)) return "the program path is not absolute";
        }
        catch (Exception) { return "the program path is not valid"; }
        var name = Path.GetFileName(exe);
        if (!name.Equals(Npm, StringComparison.OrdinalIgnoreCase) && !name.Equals(Npx, StringComparison.OrdinalIgnoreCase))
            return "only npm.cmd or npx.cmd can be run";
        return null;
    }

    // ------------------------------------------------------------------ already running?

    /// <summary>What detection reads. Tests pass fakes; <see cref="Live"/> reads the real system.</summary>
    internal sealed record Source(
        Func<IReadOnlyList<Native.ProcEntry>> Processes,
        Func<int, string?> CommandLine,
        Func<int, string?> CurrentDirectory,
        Func<IReadOnlyList<Native.Listener>> Listeners);

    internal static readonly Source Live = new(
        () => Native.SnapshotProcesses(), Native.TryGetCommandLine, Native.TryGetCurrentDirectory, () => Native.ListeningTcpPorts());

    private static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase) { "node.exe", "bun.exe" };

    /// <summary>
    /// A Studio running for this project, or null. A process counts when it runs Remotion's studio (or older
    /// preview) command and belongs to the folder (see <see cref="BelongsTo"/>). Its address is its lowest
    /// listening port (Studio's own port; others it opens are high, temporary ones).
    /// </summary>
    public static StudioInstance? FindRunning(string projectDir) => FindRunning(projectDir, Live);

    /// <summary>The running Studio (or null) for each folder, from one look at the system.</summary>
    public static Dictionary<string, StudioInstance?> FindRunning(IEnumerable<string> projectDirs) => FindRunning(projectDirs, Live);

    internal static Dictionary<string, StudioInstance?> FindRunning(IEnumerable<string> projectDirs, Source source)
    {
        var processes = source.Processes();
        var listeners = new Lazy<IReadOnlyList<Native.Listener>>(source.Listeners);
        var commandLines = new Dictionary<int, string?>();
        var folders = new Dictionary<int, string?>();
        var cached = new Source(
            () => processes,
            pid => commandLines.TryGetValue(pid, out var c) ? c : commandLines[pid] = source.CommandLine(pid),
            pid => folders.TryGetValue(pid, out var f) ? f : folders[pid] = source.CurrentDirectory(pid),
            () => listeners.Value);
        var result = new Dictionary<string, StudioInstance?>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in projectDirs) result[dir] = FindRunning(dir, cached);
        return result;
    }

    internal static StudioInstance? FindRunning(string projectDir, Source source)
    {
        var processes = source.Processes();
        var pids = new List<int>();
        foreach (var p in processes)
        {
            if (!Runtimes.Contains(p.ExeName)) continue;
            if (source.CommandLine(p.Pid) is not { } cmd || StudioScript(cmd) is not { } script) continue;
            if (BelongsTo(projectDir, script, source.CurrentDirectory(p.Pid))) pids.Add(p.Pid);
        }
        if (pids.Count == 0) return null;

        // Studio's server might be a child of the matched process, so its direct children's ports count too.
        var owners = new HashSet<int>(pids);
        foreach (var p in processes)
            if (pids.Contains(p.ParentPid) && Runtimes.Contains(p.ExeName)) owners.Add(p.Pid);
        var ports = source.Listeners().Where(l => owners.Contains(l.Pid)).Select(l => l.Port).ToList();
        return new StudioInstance(pids, ports.Count > 0 ? ports.Min() : null);
    }

    /// <summary>
    /// For a command line running Remotion's studio, the token naming Remotion (its CLI script path, or "remotion"
    /// itself); null otherwise. The first argument after it that isn't an option must be "studio" (or the older
    /// "preview"), so "remotion render" and the like don't count.
    /// </summary>
    internal static string? StudioScript(string commandLine)
    {
        var (_, args) = TerminalCommands.SplitProgram(commandLine);
        var tokens = TerminalCommands.Tokenize(args);
        for (int i = 0; i < tokens.Count; i++)
        {
            var name = Path.GetFileName(tokens[i].Value.Replace('/', '\\'));
            bool isRemotion = name.Equals("remotion", StringComparison.OrdinalIgnoreCase) ||
                              name.Equals("remotion-cli.js", StringComparison.OrdinalIgnoreCase) ||
                              name.Equals("remotion.js", StringComparison.OrdinalIgnoreCase);
            if (!isRemotion) continue;
            var next = tokens.Skip(i + 1).Select(t => t.Value).FirstOrDefault(v => !v.StartsWith('-'));
            return next is not null && (next.Equals("studio", StringComparison.OrdinalIgnoreCase) || next.Equals("preview", StringComparison.OrdinalIgnoreCase))
                ? tokens[i].Value
                : null;
        }
        return null;
    }

    /// <summary>
    /// Whether a Studio process belongs to this project folder. Remotion serves the project it was started in, so
    /// the working folder decides: the project folder itself, or a folder inside it when Remotion's CLI also comes
    /// from the project's own node_modules. When the working folder can't be read, a CLI in the project's
    /// node_modules is the evidence. A similarly named folder (demo vs demo-2) never matches.
    /// </summary>
    internal static bool BelongsTo(string projectDir, string script, string? cwd)
    {
        try
        {
            var project = Normalise(projectDir);
            string? scriptPath = null;
            var s = script.Replace('/', '\\');
            if (Path.IsPathFullyQualified(s)) scriptPath = Path.GetFullPath(s);
            else if (cwd != null && s.Contains('\\')) scriptPath = Path.GetFullPath(Path.Combine(cwd, s));
            bool cliInProject = scriptPath != null &&
                scriptPath.StartsWith(project + @"\node_modules\", StringComparison.OrdinalIgnoreCase);

            if (cwd == null) return cliInProject;
            var dir = Normalise(cwd);
            if (dir.Equals(project, StringComparison.OrdinalIgnoreCase)) return true;
            return cliInProject && dir.StartsWith(project + @"\", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', '\\')));

    /// <summary>Whether <paramref name="path"/> is the folder or inside it.</summary>
    private static bool IsInside(string path, string folder)
    {
        var f = Normalise(folder);
        var p = Normalise(path);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + @"\", StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ the browser

    public static string AddressFor(int port) => $"http://localhost:{port}/";

    /// <summary>
    /// Opens Studio's address in the default browser through explorer.exe (by full path, no shell execution). The
    /// only argument is a URL built from a port number, so nothing else can be passed.
    /// </summary>
    public static System.Diagnostics.ProcessStartInfo BrowserStart(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        return new System.Diagnostics.ProcessStartInfo(explorer, AddressFor(port)) { UseShellExecute = false };
    }
}
