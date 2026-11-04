using System.IO;
using System.Text.Json;

namespace SessionKeeper.Services;

/// <summary>What a project's package.json says, as far as the Tools menu cares.</summary>
/// <param name="DevScript">The "dev" script, or null when there isn't one.</param>
/// <param name="RemotionInstalled">Whether node_modules has Remotion's CLI (if not, the project needs npm install first).</param>
public sealed record PackageInfo(bool UsesRemotion, string? DevScript, bool RemotionInstalled)
{
    /// <summary>Whether npm run dev starts Remotion Studio (the dev script runs remotion studio or preview).</summary>
    public bool DevRunsStudio => RemotionStudio.RunsStudio(DevScript);
}

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
/// An npm launch from the Tools menu: npm.cmd or npx.cmd by full path, with one of two fixed argument lists. Nothing in
/// it comes from the project or the folder name; the folder only reaches the new shell as a quoted literal (see
/// Launcher). <paramref name="What"/> names it in the window ("Remotion Studio" or "dev server") and is a constant.
/// </summary>
public sealed record NpmCommand(string Exe, IReadOnlyList<string> Args, string What = RemotionStudio.StudioName)
{
    public string Display => Path.GetFileName(Exe) + " " + string.Join(' ', Args);
}

/// <summary>A Studio or dev server already running for a project: its processes and, once it's listening, its port.</summary>
public sealed record RunningServer(IReadOnlyList<int> Pids, int? Port)
{
    public string? Address => Port is { } p ? RemotionStudio.AddressFor(p) : null;
}

/// <summary>
/// The npm launchers in the Tools menu (Remotion Studio and Dev server): reads package.json, works out the command,
/// and spots a Studio or dev server that's already running. Detection is read-only: it reads the process list,
/// command lines, start times, working folders and the system's TCP listener table, and never opens a connection or
/// touches a process.
/// </summary>
public static class RemotionStudio
{
    public const string StudioName = "Remotion Studio", DevServerName = "dev server";
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
    /// Characters cmd.exe treats specially. npm.cmd and the .cmd shims npm runs use the folder path in cmd lines, some
    /// unquoted (SET dp0=%~dp0), so a folder named "demo&amp;calc" would run calc. Such folders are refused.
    /// </summary>
    internal static readonly char[] CmdSpecial = { '&', '|', '<', '>', '^', '%' };

    /// <summary>Why npm can't be started in this folder, or null. Checked just before launching.</summary>
    public static string? CheckFolder(string dir)
    {
        var bad = dir.IndexOfAny(CmdSpecial);
        if (bad >= 0)
            return $"the folder path contains “{dir[bad]}”, which cmd.exe (used by npm) treats as a command character. Rename the folder to start it from here";
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

    // ------------------------------------------------------------------ package.json

    /// <summary>The folder's package.json, or null if there's none (or it can't be read as JSON).</summary>
    public static PackageInfo? ReadPackage(string dir)
    {
        try
        {
            var file = Path.Combine(dir, "package.json");
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxPackageJson) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var uses = UsesRemotion(doc.RootElement);
            var installed = uses && Directory.Exists(Path.Combine(dir, "node_modules", "@remotion", "cli"));
            return new PackageInfo(uses, DevScript(doc.RootElement), installed);
        }
        catch (Exception)
        {
            return null; // unreadable or not JSON: no npm tools offered
        }
    }

    /// <summary>The project in this folder, or null if it has no readable package.json that uses Remotion.</summary>
    public static RemotionProject? ReadProject(string dir) =>
        ReadPackage(dir) is { UsesRemotion: true } p
            ? new RemotionProject(Path.GetFileName(Path.TrimEndingDirectorySeparator(dir)), dir, p.DevScript, p.RemotionInstalled)
            : null;

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
    public static NpmCommand? CommandFor(RemotionProject project, Func<string, string?> findTool, out string? problem)
    {
        var (tool, args) = project.DevRunsStudio ? (Npm, DevArgs) : (Npx, StudioArgs);
        return Build(tool, args, StudioName, project.Dir, findTool, out problem);
    }

    /// <summary>npm.cmd run dev for the Dev server item. Null (with the reason) if npm.cmd isn't found.</summary>
    public static NpmCommand? DevServerCommand(string dir, Func<string, string?> findTool, out string? problem) =>
        Build(Npm, DevArgs, DevServerName, dir, findTool, out problem);

    private static NpmCommand? Build(string tool, IReadOnlyList<string> args, string what, string dir, Func<string, string?> findTool, out string? problem)
    {
        var exe = findTool(tool);
        if (exe == null)
        {
            problem = tool + " wasn't found. Is Node.js installed?";
            return null;
        }
        var cmd = new NpmCommand(exe, args, what);
        problem = Check(cmd, dir);
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
    public static string? Check(NpmCommand cmd, string projectDir)
    {
        if (CheckTool(cmd.Exe) is { } why) return why;
        if (cmd.What is not (StudioName or DevServerName)) return "unknown npm launch";
        var name = Path.GetFileName(cmd.Exe);
        bool allowed = name.Equals(Npm, StringComparison.OrdinalIgnoreCase) ? cmd.Args.SequenceEqual(DevArgs)
            : name.Equals(Npx, StringComparison.OrdinalIgnoreCase) && cmd.Args.SequenceEqual(StudioArgs) && cmd.What == StudioName;
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
        Func<IReadOnlyList<Native.Listener>> Listeners,
        Func<int, long?> Created);

    internal static readonly Source Live = new(
        () => Native.SnapshotProcesses(), Native.TryGetCommandLine, Native.TryGetCurrentDirectory, () => Native.ListeningTcpPorts(),
        pid => Native.QueryProcess(pid).CreationFileTimeUtc);

    private static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase) { "node.exe", "bun.exe" };

    /// <summary>
    /// A Studio running for this project, or null. A process counts when it runs Remotion's studio (or older
    /// preview) command and belongs to the folder (see <see cref="BelongsTo"/>). Its address is its lowest
    /// listening port (Studio's own port; others it opens are high, temporary ones).
    /// </summary>
    public static RunningServer? FindRunning(string projectDir) => FindRunning(projectDir, Live);

    internal static RunningServer? FindRunning(string projectDir, Source source)
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
        var byParent = processes.ToLookup(p => p.ParentPid);
        foreach (var pid in pids)
            foreach (var child in byParent[pid])
                if (Runtimes.Contains(child.ExeName) && IsChildOf(child.Pid, pid, source)) owners.Add(child.Pid);
        return new RunningServer(pids, LowestPort(owners, source));
    }

    /// <summary>
    /// A dev server running for this project, or null: an npm process running "run dev" (from any terminal) whose
    /// working folder is the project, or one of whose own processes runs in the project folder (npm runs the script in
    /// the package's folder). Its address is the lowest port listened on anywhere in that npm process's tree.
    /// Servers started without npm run dev (npx vite, pnpm, yarn) aren't recognised.
    /// </summary>
    public static RunningServer? FindDevServer(string projectDir) => FindDevServer(projectDir, Live);

    internal static RunningServer? FindDevServer(string projectDir, Source source)
    {
        string project;
        try { project = Normalise(projectDir); }
        catch (Exception) { return null; }
        bool InProject(int pid)
        {
            try { return source.CurrentDirectory(pid) is { } cwd && Normalise(cwd).Equals(project, StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        var processes = source.Processes();
        var byParent = processes.ToLookup(p => p.ParentPid);
        var roots = new List<int>();
        var tree = new HashSet<int>();
        foreach (var p in processes)
        {
            if (!Runtimes.Contains(p.ExeName) || source.CommandLine(p.Pid) is not { } cmd || !IsNpmRunDev(cmd)) continue;
            var members = Descendants(p.Pid, byParent, source);
            if (InProject(p.Pid))
            {
                tree.Add(p.Pid);
                tree.UnionWith(members);
            }
            else
            {
                // npm started elsewhere (a subfolder, or a workspace root running several projects): only the processes
                // running in this project, and theirs, count, so another workspace's port is never offered.
                var inProject = members.Where(InProject).ToList();
                if (inProject.Count == 0) continue;
                foreach (var m in inProject)
                {
                    tree.Add(m);
                    tree.UnionWith(Descendants(m, byParent, source));
                }
            }
            roots.Add(p.Pid);
        }
        return roots.Count == 0 ? null : new RunningServer(roots, LowestPort(tree, source));
    }

    /// <summary>Whether a command line is npm running the "dev" script (npm-cli.js run dev, or run-script dev).</summary>
    internal static bool IsNpmRunDev(string commandLine)
    {
        var (_, args) = TerminalCommands.SplitProgram(commandLine);
        var tokens = TerminalCommands.Tokenize(args).Select(t => t.Value).ToList();
        int cli = tokens.FindIndex(t => Path.GetFileName(t.Replace('/', '\\')).Equals("npm-cli.js", StringComparison.OrdinalIgnoreCase));
        if (cli < 0) return false;
        var words = tokens.Skip(cli + 1).Where(t => !t.StartsWith('-')).Take(2).ToList();
        return words.Count == 2 && words[0] is "run" or "run-script" or "rum" or "urn" && words[1] == "dev";
    }

    /// <summary>
    /// The processes below <paramref name="root"/>. A child counts only when both start times are known and it started
    /// after its parent, so a reused process ID can't attach someone else's process (and its port) to the tree.
    /// </summary>
    private static List<int> Descendants(int root, ILookup<int, Native.ProcEntry> byParent, Source source)
    {
        var found = new List<int>();
        var seen = new HashSet<int> { root };
        var queue = new Queue<(int Pid, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && found.Count < 256)
        {
            var (pid, depth) = queue.Dequeue();
            if (depth >= 8) continue;
            foreach (var child in byParent[pid])
            {
                if (!seen.Add(child.Pid) || !IsChildOf(child.Pid, pid, source)) continue;
                found.Add(child.Pid);
                queue.Enqueue((child.Pid, depth + 1));
            }
        }
        return found;
    }

    private static bool IsChildOf(int child, int parent, Source source) =>
        source.Created(child) is { } c && source.Created(parent) is { } p && c >= p;

    private static int? LowestPort(IReadOnlySet<int> pids, Source source)
    {
        var ports = source.Listeners().Where(l => pids.Contains(l.Pid)).Select(l => l.Port).ToList();
        return ports.Count > 0 ? ports.Min() : null;
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
            // A subfolder counts only when it isn't a project of its own (no package.json between it and this one):
            // with Remotion hoisted to a monorepo root, a Studio in apps\video belongs to apps\video, not the root.
            return cliInProject && dir.StartsWith(project + @"\", StringComparison.OrdinalIgnoreCase) && NoPackageBetween(dir, project);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool NoPackageBetween(string dir, string project)
    {
        for (var d = dir; d.Length > project.Length; d = Path.GetDirectoryName(d) ?? project)
            if (File.Exists(Path.Combine(d, "package.json"))) return false;
        return true;
    }

    internal static string Normalise(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Replace('/', '\\')));

    /// <summary>Whether <paramref name="path"/> is the folder or inside it.</summary>
    internal static bool IsInside(string path, string folder)
    {
        var f = Normalise(folder);
        var p = Normalise(path);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase) || p.StartsWith(f + @"\", StringComparison.OrdinalIgnoreCase);
    }

    public static string AddressFor(int port) => $"http://localhost:{port}/";
}
