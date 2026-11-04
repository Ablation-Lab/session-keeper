namespace SessionKeeper.ViewModels;

/// <summary>A group of points in a help section, optionally under a small heading and numbered (for steps).</summary>
public sealed record HelpBlock(string? Heading, IReadOnlyList<string> Points, bool Numbered = false);

/// <summary>One page of the in-app help. <see cref="Id"/> matches a tab, so Help can open on the tab you're on.</summary>
public sealed record HelpSection(string Id, string Title, string Summary, IReadOnlyList<HelpBlock> Blocks)
{
    public override string ToString() => Title;
}

/// <summary>
/// The text of the Help window (the ? button, or F1). Words between ** are shown in bold: use it for button and
/// tab names. Keep this in step with the README, which has the full detail.
/// </summary>
public static class HelpContent
{
    public const string Overview = "overview", Current = "current", Recent = "recent", Snapshots = "snapshots", Terminals = "terminals", Tools = "tools";

    public static readonly IReadOnlyList<HelpSection> Sections = new HelpSection[]
    {
        new(Overview, "Getting started",
            "SessionKeeper shows which Claude Code sessions are running, saves sets of them as snapshots, and reopens them later, each resuming the same conversation.",
            new HelpBlock[]
            {
                new("The tabs", new[]
                {
                    "**Current:** the Claude Code sessions running right now.",
                    "**Recent:** sessions from the last day to the last month, running or not, to reopen.",
                    "**Snapshots:** saved sets of sessions (and terminals) to restore in one go.",
                    "**Terminals:** your other terminal sessions: other CLI agents, programs such as dev servers, and plain shells.",
                }),
                new("Good to know", new[]
                {
                    "Hover over any row to see its full folder, session ID and last messages.",
                    "The gear button opens **Settings**; the **?** button (or F1) opens this help.",
                    "Each row's **Tools** button opens its project folder, VS Code, GitHub page, Remotion Studio or dev server. A yellow badge means uncommitted or unpushed work.",
                    "Nothing opens unless you click a button. SessionKeeper only reads Claude Code's files; it never changes them.",
                    "Lists refresh every 10 seconds while the window is showing.",
                }),
            }),

        new(Current, "Current tab",
            "The Claude Code sessions running right now, matched to their conversations.",
            new HelpBlock[]
            {
                new("Reading a row", new[]
                {
                    "**Bold label:** your name for the session, or its title, or the folder name if the title is missing.",
                    "**Grey text:** the project folder. **Second line:** the last message you sent.",
                    "**Right-hand side:** when it was last active, and its status.",
                }),
                new("Statuses", new[]
                {
                    "**working:** Claude is busy. **needs input:** it's waiting for you, for example at a permission prompt. **idle:** waiting for your next message.",
                    "**starting:** just launched. **unmatched:** a claude process that isn't in Claude Code's session list (for example at the folder-trust prompt), so it can't be reopened or saved.",
                }),
                new("Buttons", new[]
                {
                    "**Save these as snapshot…** saves the running sessions (see Snapshots).",
                    "**New session…** starts a fresh Claude Code session in a folder you pick.",
                    "**Open** on a row opens a second window on that session. As it's already running, you're asked first: two windows on one conversation can mix up its history.",
                }),
            }),

        new(Recent, "Recent tab",
            "Sessions from Claude Code's history, newest first, including ones that have been closed.",
            new HelpBlock[]
            {
                new("Reopen past sessions", new[]
                {
                    "Next to **Window:**, choose how far back to look: 24 hours, 3 days, 7 days or 30 days.",
                    "Tick the sessions you want (or select a row and press Space). **Select all from today** ticks today's; **Clear** unticks all.",
                    "Click **Reopen selected**. Each opens in its own window and resumes its conversation.",
                }, Numbered: true),
                new("Also", new[]
                {
                    "A green **running** badge means the session is open now. You'll be asked whether to skip running ones.",
                    "**Save selected as snapshot…** keeps the ticked sessions for later instead.",
                    "A red **folder missing** badge means the project folder has gone, so that session can't be reopened.",
                }),
            }),

        new(Snapshots, "Snapshots tab",
            "A snapshot is a named set of sessions, for example everything open at the end of the day, that you can restore with one click.",
            new HelpBlock[]
            {
                new("Save a snapshot", new[]
                {
                    "Click **Save snapshot…** (or **Save these as snapshot…** on the Current tab).",
                    "Untick anything you don't want. Running sessions are ticked; recent ones are listed unticked.",
                    "Check the name, add notes if you like (for the snapshot or each session), and click **Save snapshot**.",
                }, Numbered: true),
                new("Restore it later", new[]
                {
                    "Pick a snapshot on the left. The right side lists its sessions, with **running** and **folder missing** badges.",
                    "Click **Restore** to reopen them all, or **Open** on one row.",
                    "If it holds terminals, a confirmation lists each terminal and the exact command it would run first.",
                }, Numbered: true),
                new("Manage", new[]
                {
                    "**Rename…** and **Delete** (to the Recycle Bin) work on the selected snapshot. **Open folder** shows the snapshot files.",
                    "Snapshots are readable JSON files in %APPDATA%\\SessionKeeper\\snapshots.",
                }),
            }),

        new(Terminals, "Terminals tab",
            "Terminal sessions that aren't Claude Code: other CLI agents (Codex CLI, Gemini CLI, Aider, Copilot CLI and others), programs such as npm run dev, and plain shells.",
            new HelpBlock[]
            {
                new("Reading a row", new[]
                {
                    "**Bold label:** the agent's name if it's recognised, otherwise the shell (PowerShell, Command Prompt, Bash).",
                    "**Grey text:** the folder. **Second line:** the command running in it (hover for the full text); “(at the prompt)” means idle.",
                    "**Right-hand side:** how long it's been open, and its type: agent, program or shell.",
                }),
                new("Save them", new[]
                {
                    "Tick the terminals you want and click **Save snapshot…**. They're saved alongside your running Claude Code sessions.",
                    "Terminals you've ticked here are also offered when you save from the other tabs.",
                    "A command that looks like it contains a password or token is marked in the Save dialog, because snapshots store commands as plain text.",
                }),
                new("Reopening (Snapshots tab)", new[]
                {
                    "A terminal reopens in a new window in its folder. For each one, choose: run the command again, resume the agent (when supported), or open the shell only.",
                    "Nothing runs until you've seen the exact command. Commands that look destructive (deleting, git push, reset --hard…) and ones still running now start unticked.",
                }),
                new("Limits", new[]
                {
                    "An idle PowerShell shows the folder it started in: PowerShell doesn't report later cd commands. A running program's folder is accurate.",
                    "Terminals run as administrator show \"folder unknown\". Folders inside WSL can't be seen.",
                    "Claude Code sessions aren't listed here: they're on the Current tab.",
                }),
            }),

        new("open", "Opening, naming, new sessions",
            "The same actions work on the Current, Recent and Snapshots tabs.",
            new HelpBlock[]
            {
                new("Open a session", new[]
                {
                    "Click **Open** on a row, double-click the row, or select it and press Enter.",
                    "It opens in its project folder and resumes the same conversation, as a PowerShell window or Windows Terminal tab (see Settings).",
                    "If it can't be opened (its folder or conversation file has gone, say), a message tells you why.",
                }),
                new("Name a session", new[]
                {
                    "Right-click a row and choose **Rename…**, or select it and press F2.",
                    "The name shows on every tab and becomes the window or tab title. **Clear name** goes back to the session's own title.",
                }),
                new("Start a new session", new[]
                {
                    "Click **New session…**, pick a recent folder or **Browse…** for one, optionally give it a name, and click **Start**.",
                }),
            }),

        new(Tools, "Tools and badges",
            "Each row on the Current, Recent and Snapshots tabs has a **Tools** button for its project folder, and a badge when the folder has work that isn't committed or pushed.",
            new HelpBlock[]
            {
                new("Use Tools", new[]
                {
                    "Click **Tools** next to a row's Open button.",
                    "The list shows only what applies to that project. Click an item and it happens straight away.",
                }, Numbered: true),
                new("The items", new[]
                {
                    "**Open folder:** the project in File Explorer.",
                    "**VS Code:** the project in VS Code (shown only if VS Code is installed).",
                    "**GitHub:** the repository's page in your browser (shown only if the folder has a GitHub remote).",
                    "**Remotion Studio:** for projects whose package.json uses Remotion. Runs npm.cmd run dev, or npx.cmd remotion studio if the dev script doesn't start Studio.",
                    "**Dev server:** for other projects with a dev script in package.json. Runs npm.cmd run dev.",
                    "Remotion Studio and Dev server open as a PowerShell window or Windows Terminal tab, as set in Settings. If one is already running for that project, you're offered its address in your browser instead of a second copy.",
                }),
                new("The badge", new[]
                {
                    "**uncommitted changes:** the project's git repository has changed, staged or new files. **unpushed commits:** everything is committed, but some commits (or the whole branch) aren't pushed yet.",
                    "Hover over the badge for the details: counts, the first few files, and how far ahead the branch is.",
                    "It's checked in the background and refreshed about once a minute, so it can lag a little behind your last commit.",
                }),
                new("Good to know", new[]
                {
                    "Folders whose path has & | < > ^ or % can't run npm (cmd.exe would misread them), and neither can projects whose .npmrc points npm somewhere else.",
                    "A dev server is recognised as running when it was started with npm run dev, from anywhere. One started another way (npx vite, pnpm, yarn) isn't.",
                }),
            }),

        new("settings", "Settings and shortcuts",
            "Click the gear button at the top right. Changes apply straight away.",
            new HelpBlock[]
            {
                new("Settings", new[]
                {
                    "**Open sessions in:** a separate PowerShell window each, or tabs in one Windows Terminal window.",
                    "**Start with Windows:** SessionKeeper starts in the system tray when you sign in.",
                    "**Start menu shortcut:** adds SessionKeeper to the Start menu.",
                }),
                new("Keyboard", new[]
                {
                    "**F1:** this help. **Enter** or double-click: open the selected row. **Space:** tick the selected row (Recent and Terminals). **F2:** rename the selected session.",
                }),
                new("Footer", new[]
                {
                    "The bottom line shows the version and build. It turns red with **update available** when your copy of the repository has newer app changes than the installed app: run install.cmd again.",
                    "The .NET part turns red when the runtime it uses is out of date or out of support. Hover over it for details.",
                }),
            }),

        new("safety", "Safety and your files",
            "SessionKeeper is careful about what it starts and what it touches.",
            new HelpBlock[]
            {
                new(null, new[]
                {
                    "**Only on your click:** windows open only from Open, Reopen selected, Restore, New session, a Tools item and Open folder. Every launch is logged in %APPDATA%\\SessionKeeper\\logs.",
                    "**Read-only:** Claude Code's files and processes, and the terminals on the Terminals tab, are only ever read, never changed or closed.",
                    "**Your data:** snapshots, settings and session names live in %APPDATA%\\SessionKeeper.",
                    "**More detail:** README.md in the SessionKeeper repository covers everything, including the command line and the Claude Code skill.",
                }),
            }),
    };

    /// <summary>The section to open first: the one for the given tab, else Getting started.</summary>
    public static HelpSection For(string? id) => Sections.FirstOrDefault(s => s.Id == id) ?? Sections[0];

    /// <summary>Splits text on ** into (text, bold) runs.</summary>
    public static IReadOnlyList<(string Text, bool Bold)> Runs(string text)
    {
        var parts = text.Split("**");
        var runs = new List<(string, bool)>();
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0) runs.Add((parts[i], i % 2 == 1 && i < parts.Length - (parts.Length % 2 == 0 ? 1 : 0)));
        return runs;
    }
}
