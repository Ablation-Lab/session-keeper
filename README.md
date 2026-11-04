A small Windows app (C# / .NET 8 WPF) that shows which Claude Code sessions are running, saves them as named snapshots, and reopens them later, each in its own PowerShell window or Windows Terminal tab, resuming the same conversation. It can also save and reopen your other terminal sessions, such as other CLI agents, dev servers and plain shells.

## What it does

| Feature | What you get | Where |
|---|---|---|
| **See what's running** | Every running Claude Code session with its title, folder, last message and status (*working*, *needs input*, *idle*…), refreshed every 10 seconds. | [Current tab](#current-tab-whats-running-now) |
| **Reopen past sessions** | Sessions from the last 24 hours to 30 days, newest first. Tick some and reopen them, each resuming its conversation. | [Recent tab](#recent-tab-reopen-past-sessions) |
| **Snapshots** | Save a set of sessions (and terminals) with a name and notes, for example at the end of the day, and restore them all with one click later. | [Snapshots](#snapshots-save-and-restore-a-set-of-sessions) |
| **Other terminals** | Lists terminal sessions that aren't Claude Code (Codex CLI, Gemini CLI, Aider, Copilot CLI, `npm run dev`, plain shells) with their folder, command and uptime, so you can save them in a snapshot too. | [Terminals tab](#terminals-tab-other-agents-programs-and-shells) |
| **Safe reopening of terminals** | A saved terminal reopens in its folder. Its command runs again only after you've seen the exact text, and commands that look destructive start unticked with a warning. | [Reopening terminals](#reopening-terminals) |
| **Open one session** | Open any row with its **Open** button, a double-click or Enter. You're warned before a second copy of a running session opens. | [Opening a session](#opening-a-session) |
| **Names** | Give a session your own name. It's shown on every tab and becomes its window or tab title. | [Naming sessions](#naming-sessions) |
| **New session** | Start a fresh Claude Code session in a recent or chosen folder, optionally named. | [Starting a new session](#starting-a-new-session) |
| **Built-in help** | A **?** button next to Settings (or F1) explains each tab and how to use it, opening on the tab you're on. | [Using SessionKeeper](#using-sessionkeeper) |
| **Windows integration** | PowerShell windows or Windows Terminal tabs, Start with Windows (in the tray), a Start menu shortcut. | [Settings](#settings) |
| **Version footer** | Shows the build, warns when your clone has newer app changes than your installed copy, and checks the .NET runtime. | [Reading the version footer](#reading-the-version-footer) |
| **Command line and skill** | List running, recent and saved sessions or save a snapshot from a terminal, or just ask Claude Code. | [Command line](#command-line), [Claude Code skill](#claude-code-skill) |
| **Safety** | Launches only when you click, every launch logged, no command injection, read-only towards Claude Code and towards the processes it inspects. | [Safety](#safety) |

## Install / Update

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No admin rights are needed.

**Install**
1. Get this repository onto your PC, for example with `git clone`.
2. In the repository folder, double-click **install.cmd**.
3. If SessionKeeper is already running, you're asked whether to close it. It's closed cleanly, never killed, and the install carries on.
4. The installer builds the app and installs it for you only, in `%LOCALAPPDATA%\Programs\SessionKeeper`. It also adds a Start menu shortcut and installs the [Claude Code skill](#claude-code-skill) into `%USERPROFILE%\.claude\skills\session-keeper` (or under `CLAUDE_CONFIG_DIR` if you've set it). That's the only thing the installer puts in Claude Code's folder, and it replaces the `SKILL.md` and `sessionkeeper.ps1` of any skill already in a folder with that name.
5. At the end, you're asked whether to open SessionKeeper. The window then waits for a key press, so you can read what happened, including any error.


**Open it:** Start menu > **SessionKeeper**.

**Start it when you sign in:** in SessionKeeper, click the gear button and tick **Start with Windows**. If Start with Windows already points at a different copy of the app, the installer offers to switch it to the installed one.

**Options:** to use these, run `install.cmd` from a terminal in the repository folder, followed by the switch:
- `install.cmd -NoSkill` skips the Claude Code skill.
- `install.cmd -NoStartMenu` skips the Start menu shortcut.
- `install.cmd -Destination D:\Tools\SessionKeeper` installs to another folder you own. If you also install the skill, set `SESSIONKEEPER_EXE` to that copy's `SessionKeeper.exe` so the skill can find it.

**Update:** pull the latest changes (for example `git pull`), then double-click **install.cmd** again. It offers to close the running copy first. The version footer doesn't look online: it compares your installed copy with your local clone, so it turns red with **update available** after you've pulled newer app changes (commits under `src/SessionKeeper`) and haven't reinstalled yet.

**Please don't:**
- **Copy the build folder by hand** (`src\SessionKeeper\bin\...`). Every build replaces it, so a copy running from there blocks updates, and the app will tell you it isn't installed.
- **Install under Program Files.** That needs admin rights and updates would fail, so the installer refuses.

**Uninstall:**
1. In SessionKeeper's Settings, untick **Start with Windows** and **Start menu shortcut**. This removes the startup entry (under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) and the shortcut (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\SessionKeeper.lnk`).
2. Close SessionKeeper: right-click its tray icon and choose **Exit**, or close the window.
3. Delete the app folder, `%LOCALAPPDATA%\Programs\SessionKeeper`.
4. Delete the skill folder, `%USERPROFILE%\.claude\skills\session-keeper`.
5. Optionally, delete `%APPDATA%\SessionKeeper`, which holds your snapshots, session names, settings, logs and cache (the `cache` folder holds Microsoft's .NET release information for the footer).

**Developing:** run `dotnet build`, then `dotnet test`. The development build is at `src\SessionKeeper\bin\Debug\net8.0-windows\win-x64\SessionKeeper.exe`. For everyday use, install it with `install.cmd`. You can also run `install.ps1` directly (`powershell -ExecutionPolicy Bypass -File install.ps1`). Without `-Interactive` it doesn't prompt; it stops with a message if SessionKeeper is running.

## Using SessionKeeper

Open **SessionKeeper** from the Start menu. The window has four tabs at the top, **Current**, **Recent**, **Snapshots** and **Terminals**, a gear button at the top right for Settings, and a **?** button next to it (or F1) for built-in help. The help opens on the topic for the tab you're on and covers each tab, opening and naming sessions, settings, keyboard shortcuts and safety. The first three tabs' bottom bars have a **New session…** button. Hover over any row to see its full folder path and the first 8 characters of its session ID. The tooltip also shows the last message on Current rows, and the first and last messages on Recent rows.

### Current tab: what's running now

1. Open the **Current** tab. It's the one shown when the app starts.
2. Read each row:
   - **Bold label:** your name for the session if you've given it one (see [Naming sessions](#naming-sessions)). Otherwise it's the session's own title, or the folder name if that title is missing or generic (such as "Continuing previous session").
   - **Grey text on the right:** the project folder. Hover over it for the full path.
   - **Second line:** the last message you sent in that session.
   - **Right-hand side:** how long ago the session was active, and its status: *working*, *needs input* (waiting for you, for example at a permission prompt), *idle*, *starting* (just launched) or *unmatched*. Rarely, it's *unknown* or another word: a status Claude Code reported that SessionKeeper doesn't recognise, shown as reported.
3. An *unmatched* row is a `claude` process that isn't in Claude Code's session registry, for example one sitting at the "trust this folder" prompt. It's shown so nothing is hidden, but it has no session ID, so it can't be opened or saved as a resumable session.
4. The list refreshes itself every 10 seconds while this tab is showing and the window isn't minimised. Click **Refresh** to update it straight away. The summary at the top right, such as "6 running · 1 working", shows when it last updated.
5. To open a session from here, see [Opening a session](#opening-a-session). Every session on this tab is already running, so you'll always be asked before a second copy opens.

### Opening a session

The same steps work on the Current, Recent and Snapshots tabs.

1. Find the session's row.
2. Click **Open** on the right of the row, double-click the row, or select it and press Enter.
3. If the session is already running, a **Session already running** box asks whether to open a second copy. The answer defaults to **No**, because two windows writing to one conversation can mix up its history.
4. The session opens in its project folder and runs `claude --resume <session-id>`, so you're back in the same conversation. It opens as a PowerShell window or a Windows Terminal tab, depending on [Settings](#settings). If you've named the session, the window or tab is titled with that name.

When a session is skipped, a message lists it with the reason. The reasons are:
- It's already running and you chose not to open a second copy.
- No folder was recorded for it.
- Its folder no longer exists.
- Its conversation file has gone, so it can't be resumed.
- Its conversation file belongs to a different folder than the one recorded.
- The folder path can't be used safely: it contains a double quote or a control character, isn't a full path, isn't a valid path, or contains invalid Unicode.
- The session ID isn't a valid ID (for example, in a hand-edited snapshot).
- The folder is a network (`\\server\…`) path and Claude Code is installed through npm (`claude.cmd`), which can't start in network folders.

A Current-tab session with no messages yet can't be opened at all: its **Open** button is greyed out (hover over it to see why). If **Windows Terminal tab** is chosen but Terminal isn't installed, sessions open as PowerShell windows instead, and a message says so.

A snapshot entry saved without a session ID opens a new Claude Code session in that folder.

### Naming sessions

Give any session your own name, so it's easy to spot, for example "Login fix" instead of "Continuing previous session".

1. Right-click the session's row (on the Current, Recent or Snapshots tab) and choose **Rename…**, or select the row and press F2.
2. Type a name and click **OK**.
3. The name becomes the row's bold label on every tab. The session's own title moves to the tooltip (hover over the row), and the folder stays as the grey text.
4. When you open, reopen or restore the session, its window or tab is titled with the name. To keep it there, Claude Code's own title updates are switched off for that window, so it doesn't show Claude's busy indicator in the title. Unnamed sessions keep Claude's live title.
5. To go back to the session's own title, right-click the row and choose **Clear name**, or rename it to an empty name.

Names are stored only in SessionKeeper's own file, `%APPDATA%\SessionKeeper\nicknames.json`, keyed by session ID. Claude Code's files are never changed. A snapshot keeps the name each session had when you saved it. A row needs a session ID to be named, so *unmatched* rows can't be.

Names you give a session inside Claude Code itself (its rename command, or `claude --name`) are already shown as that session's title.

### Starting a new session

1. Click **New session…** in the bottom bar (on the Current, Recent or Snapshots tab).
2. Pick the project folder:
   - Click one under **Recent project folders** (folders of running and recent sessions), or
   - Click **Browse…** to choose any folder, or type its full path.
3. Optionally, type a **Name**.
4. Click **Start**. A fresh Claude Code session starts in that folder, as a PowerShell window or Windows Terminal tab depending on [Settings](#settings).
5. If you gave a name, the window or tab is titled with it. As soon as the new session appears under **Current** (usually within a few seconds), the name is attached to it, just as if you'd renamed it.

If the folder doesn't exist, the dialog says so and nothing starts.

### Recent tab: reopen past sessions

1. Open the **Recent** tab. It lists sessions from Claude Code's session files, newest first, including ones that are no longer running.
2. Next to **Window:**, choose how far back to look: **24 hours**, **3 days**, **7 days** or **30 days**.
3. Tick the sessions you want. You can also select a row and press Space, or click **Select all from today**, which ticks today's sessions and unticks the rest. **Clear** unticks everything.
4. Click **Reopen selected (n)**. Each ticked session opens as in [Opening a session](#opening-a-session).
   - If some are already running (they have a green **running** badge), you're asked whether to skip them. **Yes** skips them, which is recommended. **No** opens second copies, and **Cancel** opens nothing.
   - If more than 10 would open, you're asked to confirm first.
5. To keep this selection for later instead, click **Save selected as snapshot…** (see below).

Each row shows the last-active time and the first 8 characters of its session ID, so sessions with the same title can be told apart.

### Snapshots: save and restore a set of sessions

**Save a snapshot**
1. On the **Current** tab, click **Save these as snapshot…**. The **Save snapshot** dialog opens.
2. Running sessions are already ticked, apart from *unmatched* or *starting* ones, which have no session ID to resume. Sessions that were active in the last 24 hours but aren't running are listed too, unticked. Untick anything you don't want, or use **All** and **None**.
3. Check the **Name**. It starts as the date and time, such as "Snapshot Tue 3 Nov 18:40".
4. Optionally, add a **Note** for the whole snapshot, and a note under any ticked session, such as "waiting on council results".
5. Click **Save snapshot**. The app switches to the Snapshots tab and selects the new snapshot.

You can also save from the **Recent** tab: tick sessions, then click **Save selected as snapshot…**. On the **Snapshots** tab, **Save snapshot…** opens the same dialog as on the Current tab.

Terminals you've ticked on the [Terminals tab](#terminals-tab-other-agents-programs-and-shells) are listed too, already ticked. Saving from the Terminals tab itself lists every terminal that can be saved, with your ticked ones ticked. A terminal whose command looks like it contains a password or token is marked **may contain a password or token**, because the command is stored in the snapshot file as plain text.

**What gets saved**
- For each session: its session ID, project folder, your name for it (if any), its title, last message, last-active time and your note.
- For each terminal: its label, type (agent, program or shell), folder, shell, and the command line of the program running in it. They go in a separate `terminals` list, each entry marked `"kind": "terminal"`, so an older SessionKeeper never mistakes one for a Claude session. (An older version shows a snapshot that holds only terminals as "0 sessions", but keeps the terminals if you rename it.) Entries without `"kind": "terminal"`, or that can't be read, are ignored without affecting the rest of the snapshot.
- For the snapshot: its name, when it was saved and its note.
- One readable JSON file per snapshot, in `%APPDATA%\SessionKeeper\snapshots\`, named like `2026-11-03_184000_end-of-day.json`. On the Snapshots tab, **Open folder** opens this folder.
- A running session with no messages yet is saved without a session ID. Restoring it starts a new session in that folder.

**Restore a snapshot**
1. Open the **Snapshots** tab.
2. Click a snapshot in the list on the left. The right side shows its sessions and notes. A green **running** badge marks sessions that are open right now, and a red **folder missing** badge marks folders that no longer exist.
3. Click **Restore**.
4. Each session opens in its own PowerShell window, or as a tab in one new Windows Terminal window, depending on [Settings](#settings). Each resumes the same Claude Code conversation. As with **Reopen selected**:
   - You're asked whether to skip sessions that are already running.
   - You're asked to confirm if more than 10 would open.
5. If any entries were skipped, for example because their folder no longer exists or their conversation file has gone, a message lists them and the reason.

If the snapshot has terminals, **Restore** first shows the **Reopen terminals** dialog, which lists every terminal and the exact command it would run. Only the ticked terminals open. See [Reopening terminals](#reopening-terminals).

You can also open a single session or terminal from a snapshot with its **Open** button.

**Manage snapshots**
- **Rename:** select a snapshot, click **Rename…**, type the new name and click **OK**. The file is renamed to match.
- **Delete:** select a snapshot, click **Delete** and confirm. The file goes to the Recycle Bin, and Claude Code's own sessions aren't affected.
- **Missing folders:** if a session's folder has been moved or deleted, the snapshot still lists it with a **folder missing** badge, and the header counts the missing folders. **Restore** skips those sessions and says so. The rest of the snapshot still opens.

### Terminals tab: other agents, programs and shells

The **Terminals** tab lists the terminal sessions that aren't Claude Code: other CLI agents (Codex CLI, Gemini CLI, Aider, GitHub Copilot CLI and others), programs such as `npm run dev`, and plain shells. Claude Code sessions are left out, because they have their own tabs, and so is SessionKeeper itself.

1. Click the **Terminals** tab.
2. Read each row:
   - **Bold label:** the agent's name if SessionKeeper recognises it, otherwise the shell ("PowerShell", "Command Prompt", "Bash") or, for a tab that runs a program directly, the program.
   - **Grey text on the right:** the folder. Hover over it for the full path.
   - **Second line:** the command running in it, shortened (for example `npm run dev`). Hover over it for the full command line. *(at the prompt)* means the shell is idle.
   - **Right-hand side:** how long the terminal has been open, and its type: *agent*, *program* or *shell*.
3. Hover over a row for the details: the shell, whether it's a Windows Terminal tab or a console window, when the command started, and the process IDs.
4. The list refreshes every 10 seconds while the tab is showing. Click **Refresh** to update it straight away.
5. To save terminals, tick them and click **Save snapshot…**. The Save snapshot dialog opens with those terminals ticked, alongside your running Claude Code sessions. Rows whose folder is unknown can't be ticked. Terminals you've ticked here are also offered when you save from the Current or Snapshots tab.

**What it can and can't see**
- **Found:** Windows Terminal tabs and panes, and classic console windows. A tab is recognised by the hidden window Windows Terminal gives each tab; a console window counts only if it's visible, so the hidden consoles of background tools don't appear.
- **Folder:** for a running program, its own folder, which is reliable. For an idle Command Prompt, its current folder. For an idle PowerShell, only the folder it *started* in, because PowerShell's `cd` doesn't change what Windows reports. Those rows say so in the tooltip, and the snapshot records it. Git Bash and other shells are treated like PowerShell.
- **WSL:** the Linux folder and the programs running inside WSL can't be seen from Windows.
- **Window titles aren't used:** Windows Terminal only exposes the active tab's title, so it can't be matched to the other tabs.
- **Elevated terminals** (Run as administrator) show "folder unknown" and no command, because a normal app can't read them.
- **Several programs at once:** the row shows a recognised agent if there is one, otherwise the most recently started program.
- **Only what runs in that terminal counts.** Windowed programs started from it (`notepad`, `code .`) aren't its command. A new console started from it (`start cmd`) is listed as its own row.
- **Agents** are recognised by their exe name, or by the script that node or python runs. So `npm i -g @openai/codex` is a program, not Codex.
- **Other terminal apps** (VS Code's terminal and so on) aren't listed. Hosts are listed in `KnownAgents.TerminalHosts`.

**Adding an agent:** agents are recognised by one list, `KnownAgents.Agents` in `src/SessionKeeper/Services/KnownAgents.cs`. Add one line with the name, the exe name and, for npm-installed agents, the package name as it appears in node's command line, for example `@openai/codex`.

### Reopening terminals

A saved terminal reopens in a new PowerShell window or Windows Terminal tab (the same [setting](#settings) as Claude sessions), in its folder. Nothing that would run a command starts without the **Reopen terminals** dialog first.

1. On the **Snapshots** tab, click **Restore**, or click **Open** on a terminal row.
2. The **Reopen terminals** dialog lists each terminal with the exact command that will run, built from the same values that are launched. A terminal that's still running right now (the same command or agent in the same folder) says so and starts unticked, because reopening it would start a second copy.
3. For each terminal, choose:
   - **Run the command again:** starts the same program with the same arguments.
   - **Resume …'s last conversation:** shown only for an agent with a resume option SessionKeeper has verified. At the moment no agent has one: none of them was installed where this version was built, so none could be checked against the agent's own `--help`. To add one after checking it, give the agent a `Resume:` entry in `KnownAgents.Agents`.
   - **Open … only:** just the shell, in the folder.
4. Commands that look destructive are shown with a red warning and left **unticked**. Tick one only if you mean to run it. Switching a row to a destructive choice unticks it again. The check covers:
   - delete, format and wipe commands: `rm` (and `rm -rf`), `del`, `rd`, `format`, `Remove-Item`, `Clear-Content`, `diskpart`, `dd of=`, `robocopy /mir`;
   - git commands that lose work: `git push`, `git reset --hard`, `git checkout -- .`, `git restore`, `git clean`, `git stash drop`, `git branch -D`, `git rebase`;
   - `shutdown`, `taskkill`, `Stop-Process`, `reg delete`, `docker … prune`, `kubectl delete`, `terraform destroy` and similar;
   - code you can't read: PowerShell `-EncodedCommand` and `Invoke-Expression`.

   It also sees through `cmd` escapes such as `d^el` and full paths such as `C:\…\git.exe push`. It's a heuristic over the command line: it can't see inside scripts or batch files, and `npm run dev` runs whatever the project's `package.json` says today.
5. Tick the terminals to open, then click **Restore** (or **Open**).

An idle Command Prompt or PowerShell has nothing to run, so its **Open** button opens it straight away. Restore always shows the dialog.

**What re-running means**
- The program is started with its full path and its original arguments, passed to Windows exactly as saved. Nothing is re-typed into a shell. A command that went through `cmd /c` (PowerShell does this for `npm` and other `.cmd` files) runs through `cmd /c` again, as before.
- The environment is the new window's, not the old one's. For example, an activated Python virtual environment or variables set by hand in the old shell aren't there.
- Only an `.exe` given by a plain local full path is ever run: not a network or device path, not one with `..` in it. A saved command that's anything else, or that contains line breaks, control or escape characters, or invisible characters that could make it look different from what runs (such as right-to-left overrides or zero-width spaces), or is longer than 4,000 characters, isn't offered. The dialog says why and offers the shell only.

### Settings

1. Click the gear button at the top right.
2. Choose how sessions open under **Open sessions in**. This applies to **Open**, **Reopen selected**, **Restore**, **New session** and reopened terminals.
   - **PowerShell window** (the default): each session opens in its own Windows PowerShell window.
   - **Windows Terminal tab:** sessions open as tabs in a Windows Terminal window. This option is greyed out if Terminal isn't installed.
3. Optionally, tick **Start with Windows**. SessionKeeper then starts in the system tray when you sign in. Click the tray icon to open it, or right-click it and choose **Exit**. It's off by default.
4. Optionally, tick **Start menu shortcut** to add SessionKeeper to your Start menu. The installer already does this.
5. Click **Close**. Changes take effect immediately, so there's no Save button.

**Running from** shows which copy of the app you're using. **Start with Windows** and **Start menu shortcut** point at that copy. If it says Start with Windows "currently starts a different copy", untick and tick the box to point it at this one.

If you're running a copy that wasn't installed, such as the build folder, Settings shows a **This copy isn't installed** notice at the top. Click **Open repo folder** and double-click **install.cmd** there.

Starting SessionKeeper while it's already open brings the existing window forward, including from the tray.

### Reading the version footer

The bottom of the **Current** tab shows a line like this:

`Auto-refreshes every 10 s · v1.2.1 · build efdb1f5 · 3 Nov 2026 · Runs on .NET 8.0.33`

- **App part** (`v1.2.1 · build efdb1f5 · 3 Nov 2026`): the app's version, the commit it was built from, and the build date. A `+` after the commit means the build had uncommitted changes.
  - **Grey:** up to date, or the check couldn't run.
  - **Red with "update available":** your local clone has newer commits to the app than this copy was built from, typically right after a `git pull`. Hover to see how many, and where: "Double-click install.cmd in … to update". It never checks GitHub, so it can't tell you about changes you haven't pulled.
- **Runtime part** (`Runs on .NET 8.0.33`): the .NET version the app is actually running on. Hover for details:
  - **Running on:** the version, and whether the app uses the PC's shared .NET (*framework-dependent*) or carries its own (*self-contained*).
  - **Installed on this PC:** every .NET and Windows Desktop runtime found on the machine.
  - **Latest patch and support:** the newest patch for that .NET version, and when Microsoft's support for it ends.
  - **Grey:** up to date, or the check couldn't run (for example, offline).
  - **Red:** Microsoft has released a newer patch for this .NET version (the tooltip names it and says whether it has security fixes), or this .NET version is out of support. For a self-contained copy, updating means rebuilding SessionKeeper, not installing a new runtime.

Each part has its own colour, so you can tell which one is out of date. Both checks run once at startup in the background and never slow down opening the app.

## Reference

### Command line

The same exe has command-line options. It never opens the window when you use them:

```text
SessionKeeper.exe --list [--json]                         running sessions
SessionKeeper.exe --recent [--days N] [--json]            sessions active in the last N days (default 1)
SessionKeeper.exe --snapshots [--json]                    saved snapshots (with how many terminals each has)
SessionKeeper.exe --snapshot ["note"] [--name "name"] [--json]   save the running sessions
SessionKeeper.exe --install-shortcut                      add the Start menu shortcut
SessionKeeper.exe --help | --version
```

- `--snapshot` uses the same rules as the Save dialog. Matched sessions are saved, and unmatched or still-starting processes are skipped and listed.
- Exit codes: 0 means OK, 1 means nothing to do or an error, and 2 means a usage error.
- PowerShell doesn't wait for GUI apps, so pipe the output: `SessionKeeper.exe --list | Out-Host`.
- `SESSIONKEEPER_SNAPSHOTS` sets a different snapshots folder.

### Claude Code skill

`skills/session-keeper/` lets you ask Claude Code things like:
- "open SessionKeeper"
- "list my running sessions"
- "what did I work on today?"
- "save a snapshot of my sessions with the note waiting on council results"

It runs the command-line options above through `sessionkeeper.ps1`, which finds `SessionKeeper.exe` in this order:
1. The `SESSIONKEEPER_EXE` environment variable.
2. `%LOCALAPPDATA%\Programs\SessionKeeper\SessionKeeper.exe`.
3. `PATH`.

Restoring sessions stays in the app, because that opens new windows. The skill only pre-approves the read-only commands (`open`, `--list`, `--recent`, `--snapshots`, `--help`, `--version`), so Claude Code asks you before it saves a snapshot or adds the Start menu shortcut.

`install.cmd` installs the skill for you. To install it by hand, copy the folder:

```powershell
Copy-Item -Recurse -Force skills\session-keeper "$env:USERPROFILE\.claude\skills\"
```

Use `%CLAUDE_CONFIG_DIR%\skills\` instead if you've set `CLAUDE_CONFIG_DIR`. Then start a new Claude Code session so it picks the skill up.

### Version and update checks

- **Version:** the version is set in one place, `<Version>` in `src/SessionKeeper/SessionKeeper.csproj`. Bump it with each change: patch for fixes, minor for features, major for breaking changes. The commit, build date and source repository are embedded automatically when you build. `SessionKeeper.exe --version` prints the same line as the footer.
- **App check:** compares the build commit with `HEAD` of the local repository the app was built from. It's offline and local only: nothing is fetched, so the footer turns red only once you've pulled newer commits. You can set a different repository as `sourceRepo` in `%APPDATA%\SessionKeeper\settings.json`. Only commits that touch `src/SessionKeeper` count, so README, test or installer changes don't. A repository under your user profile is embedded in the exe as `%USERPROFILE%\…`, so a copy you pass on doesn't carry your user name. It runs only `git rev-list` and `git rev-parse`, with optional locks off, hidden, with short timeouts, and logged. It never pulls, builds or installs.
- **Runtime check:**
  - The installed runtimes are read from the dotnet install folders and the registered install location. No `dotnet.exe` is run.
  - The latest-patch and support information comes from Microsoft's official release metadata (`releases-index.json`, then the channel's `releases.json`). It uses HTTPS and Microsoft's hosts only, with a short timeout, and is cached for 24 hours in `%APPDATA%\SessionKeeper\cache`.
  - The tooltip warns when support ends within 30 days. Nothing is ever installed or updated.

### Settings file

`%APPDATA%\SessionKeeper\settings.json` stores **Open sessions in** and the optional `sourceRepo`. Your session names are in `%APPDATA%\SessionKeeper\nicknames.json`. Start with Windows is a per-user entry under `HKCU\…\CurrentVersion\Run`, and the Start menu shortcut is `SessionKeeper.lnk` in your own Start menu folder. Neither is stored in the settings file.

### How it works

| What | Where it comes from |
|---|---|
| Running sessions | `%USERPROFILE%\.claude\sessions\<pid>.json`, Claude Code's live registry (pid, sessionId, cwd, busy/idle/waiting status, process start time). Each entry is checked against the live process table: the PID must be alive and its start time must match, because a killed process leaves its entry behind. |
| Unregistered processes | `claude.exe` processes with no registry entry are still shown as *unmatched*, with their folder read from the process when Windows allows it. Elevated processes show "folder unknown". |
| Session details | `%USERPROFILE%\.claude\projects\<encoded-folder>\<sessionId>.jsonl`. The app reads the `cwd`, `timestamp`, user messages, and the `ai-title`, `custom-title` and `last-prompt` records. Files are append-only, so each one is parsed incrementally and cached. For a transcript over 8 MB, the first look reads only the first 256 KB and the last 1 MB. |
| Folder checks | Done off the UI thread and cached, so an offline network drive can't freeze the window. |
| Opening sessions | **PowerShell window:** `conhost.exe powershell -NoExit -EncodedCommand …`. Starting through conhost keeps it a real PowerShell window even when Windows' default terminal is Windows Terminal. **Windows Terminal tab:** `wt -w <name> new-tab -d <folder> powershell -NoExit -EncodedCommand …`. Both run `claude --resume <id>` in the session's folder. |
| Snapshots | Readable JSON in `%APPDATA%\SessionKeeper\snapshots\`, with an optional note for the whole snapshot and per-session notes. |
| Terminal sessions | One process-list snapshot (PID, parent, exe name), one pass over the desktop's console windows (class and owner only), then query-only handles on each terminal's own processes for the command line, start time and folder. Parent links are checked against start times, so a reused process ID can't attach a program to the wrong terminal. A scan takes a few milliseconds. |
| Reopening terminals | The same window or tab as Claude sessions. The script enters the folder, then starts the saved program through .NET's process API with the saved arguments as one string, so neither PowerShell nor SessionKeeper parses them. |

`CLAUDE_CONFIG_DIR` is respected if set.

### Safety

- **Launches only on request.**
  - Every process SessionKeeper starts goes through one gatekeeper (`LaunchGuard`), which refuses anything outside an explicit click on **Open**, **Reopen selected**, **Restore**, **New session** (its **Start** button) or **Open folder**.
  - The gatekeeper stops SessionKeeper's own code from launching things by accident (for example from a timer). It isn't a security boundary: it doesn't protect against other software running as you.
  - Background work has no such permission, so it can never launch anything. That covers auto-refresh, session detection, the tray and the command line.
  - Shell execution (URIs such as `ms-settings:`, documents, verbs) is refused, and so is `wt.exe` without a `new-tab` command. A test fails if any other code calls `Process.Start`.
  - The only background process is the read-only `git` query described above, run under the same rules.
- **Launch log.** Every launch, every refused attempt, each user action, and app start and exit are logged with a timestamp and the calling source line. The logs are in `%APPDATA%\SessionKeeper\logs\launches-yyyy-MM-dd.log` and kept for 14 days. If a window ever opens that you didn't ask for, check the log for that time: no `LAUNCH` line means SessionKeeper didn't open it.
  - **What's in it:** the program started and the first 300 characters of its arguments (which can include a full folder path and, for Windows Terminal, the tab title, so your session name), a purpose line with the folder names of the sessions opened, and, for the version check, the repository folder and git arguments. The session scripts themselves are base64, cut off at 300 characters. Snapshot notes are never logged.
  - `COMMAND` lines for reopened terminals: the label, folder and the full command that ran (at most 4,000 characters), with likely passwords, tokens and keys replaced by `***`. The snapshot file itself stores the command unredacted, so it can be re-run, which is why the Save dialog marks commands that look like they contain one.
  - Every log line is one line: line breaks, control characters and invisible characters in a label, folder or command are written as `\uXXXX`, so nothing can forge or hide a log entry.
  - Also logged: `DEBUG` lines when a registry entry is rejected because its process start time doesn't match (once per process, to make a future registry format change visible), and `ENV` lines naming (never showing the values of) unrecognised `CLAUDE*` variables passed on to a launched session.
- **Read-only detection.** The Terminals tab only reads: a process list, window classes and owners, command lines, start times and folders through query-only handles, and the headers of exe files on disk (to tell windowed programs from console ones). It never kills, signals, modifies or injects into any process.
- **Read-only towards Claude Code.** Session files and registry entries are opened read-only with full sharing. Processes are opened with query/read rights only. The app never writes to, deletes from or kills anything in Claude Code's files or processes. The one exception is the installer (not the app), which adds the skill folder described under [Install](#install--update).
- **No command injection.**
  - Folder names, session names, titles and notes are never parsed as commands.
  - Each window or tab runs a base64 `-EncodedCommand` script. Its only dynamic values are single-quoted PowerShell literals, with every quote character doubled (including Unicode ‘ ’ quotes), and a session ID that must parse as a GUID. A session name reaches the window title only as such a literal.
  - Windows Terminal tab titles are reduced to letters, digits, spaces and harmless punctuation, never `;`, `%`, quotes or backslashes. A title starting with `-` gets a `_` in front, so wt can't read it as one of its options.
  - A folder containing `;` or `%` is not passed to `-d` at all, because wt splits on `;` and expands `%VAR%`.
  - Session names are cleaned of control and invisible characters and capped at 80 characters. Notes are never passed to any process.
  - A reopened terminal's program and arguments go into the script as two such literals and are handed to Windows unchanged, never to PowerShell's own command parsing. A test runs a generated script with `$(...)`, quotes and backticks in the arguments and checks they arrive byte for byte.
  - A terminal command never runs without being shown first, and Restore always lists every terminal. Commands that look destructive start unticked with a warning. An idle shell opens without the dialog only if nothing was saved to run and it's Windows PowerShell (the window itself) or `cmd.exe`/`wsl.exe` from `System32`. Any other shell path saved in a snapshot (which can be hand-edited) is shown in the dialog first.
  - Labels from a snapshot file are cleaned of control, escape and invisible characters before they're shown or written to the console, so a label can't send terminal escape sequences.
- **The right folder, the right claude.**
  - If the script can't enter the folder, it stops instead of starting claude somewhere else.
  - `claude` is resolved to a full path, and relative `PATH` entries are ignored, so a repository can't plant its own `claude.cmd`.
  - `NoDefaultCurrentDirectoryInExePath` is set in each window, so npm's `claude.cmd` shim can't pick up a `node.exe` from the project folder.
  - A conversation is only resumed in the folder it belongs to.
- If SessionKeeper itself was started from inside a Claude Code session, it inherited nine session-scoped variables that would stop the new `claude` from starting normally. They're removed before launching, unless you've set them permanently (user or machine environment): `CLAUDECODE`, `CLAUDE_PID`, `CLAUDE_CODE_ENTRYPOINT`, `CLAUDE_CODE_CHILD_SESSION`, `CLAUDE_CODE_SESSION_ID`, `CLAUDE_CODE_SESSION_ATTENDED`, `CLAUDE_CODE_MESSAGING_SOCKET`, `CLAUDE_CODE_MESSAGING_TOKEN` and `CLAUDE_CODE_SSE_PORT`. Every other `CLAUDE*` variable (such as `CLAUDE_CONFIG_DIR` or `CLAUDE_CODE_USE_BEDROCK`) is passed through, and the names of non-permanent ones are written to the launch log.

### Limits

- Claude Code's registry and file formats are internal and could change in a future version. If they do, the Current tab falls back to listing *unmatched* processes.
- Node-based installs (`node.exe … claude-code`) are matched through the registry only. They won't appear as *unmatched* when unregistered.
- The Terminals tab has its own limits: see [What it can and can't see](#terminals-tab-other-agents-programs-and-shells).
- `claude.exe` is found in `%USERPROFILE%\.local\bin`, then on `PATH`, then as npm's `claude.cmd`. If none of these exist, the windows run `claude` and rely on `PATH`.
