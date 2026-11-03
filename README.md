# SessionKeeper

A small Windows app (C# / .NET 8 WPF) that shows which Claude Code sessions are running, saves them as named snapshots, and reopens them later, each in its own PowerShell window or Windows Terminal tab, resuming the same conversation.

## Install / Update

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No admin rights are needed.

**Install**
1. Get this repository onto your PC, for example with `git clone`.
2. In the repository folder, double-click **install.cmd**.
3. If SessionKeeper is already running, you're asked whether to close it. It's closed cleanly, never killed, and the install carries on.
4. The installer builds the app and installs it for you only, in `%LOCALAPPDATA%\Programs\SessionKeeper`. It also adds a Start menu shortcut and installs the [Claude Code skill](#claude-code-skill).
5. At the end, you're asked whether to open SessionKeeper. The window then waits for a key press, so you can read what happened, including any error.

**Open it:** Start menu > **SessionKeeper**.

**Start it when you sign in:** in SessionKeeper, click the gear button and tick **Start with Windows**. If Start with Windows already points at a different copy of the app, the installer offers to switch it to the installed one.

**Options:** to use these, run `install.cmd` from a terminal in the repository folder, followed by the switch:
- `install.cmd -NoSkill` skips the Claude Code skill.
- `install.cmd -NoStartMenu` skips the Start menu shortcut.
- `install.cmd -Destination D:\Tools\SessionKeeper` installs to another folder you own. If you also install the skill, set `SESSIONKEEPER_EXE` to that copy's `SessionKeeper.exe` so the skill can find it.

**Update:** pull the latest changes (for example `git pull`), then double-click **install.cmd** again. It offers to close the running copy first. The version footer turns red with **update available** when the repository has newer app changes.

**Please don't:**
- **Copy the build folder by hand** (`src\SessionKeeper\bin\...`). Every build replaces it, so a copy running from there blocks updates, and the app will tell you it isn't installed.
- **Install under Program Files.** That needs admin rights and updates would fail, so the installer refuses.

**Uninstall:**
1. In SessionKeeper's Settings, untick **Start with Windows** and **Start menu shortcut**. This removes the startup entry (under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`) and the shortcut (`%APPDATA%\Microsoft\Windows\Start Menu\Programs\SessionKeeper.lnk`).
2. Close SessionKeeper: right-click its tray icon and choose **Exit**, or close the window.
3. Delete the app folder, `%LOCALAPPDATA%\Programs\SessionKeeper`.
4. Delete the skill folder, `%USERPROFILE%\.claude\skills\session-keeper`.
5. Optionally, delete `%APPDATA%\SessionKeeper`, which holds your snapshots, settings, logs and cache.

**Developing:** run `dotnet build`, then `dotnet test`. The development build is at `src\SessionKeeper\bin\Debug\net8.0-windows\win-x64\SessionKeeper.exe`. For everyday use, install it with `install.cmd`. You can also run `install.ps1` directly (`powershell -ExecutionPolicy Bypass -File install.ps1`). Without `-Interactive` it doesn't prompt; it stops with a message if SessionKeeper is running.

## Using SessionKeeper

Open **SessionKeeper** from the Start menu. The window has three tabs at the top, **Current**, **Recent** and **Snapshots**, and a gear button at the top right for Settings. Hover over any row to see its full folder path and the first 8 characters of its session ID. The tooltip also shows the last message on Current rows, and the first and last messages on Recent rows.

### Current tab: what's running now

1. Open the **Current** tab. It's the one shown when the app starts.
2. Read each row:
   - **Bold label:** the session's title. If the title is missing or generic (such as "Continuing previous session"), the folder name is shown instead.
   - **Grey text on the right:** the project folder. Hover over it for the full path.
   - **Second line:** the last message you sent in that session.
   - **Right-hand side:** how long ago the session was active, and its status: *working*, *needs input* (waiting for you, for example at a permission prompt), *idle*, *starting* (just launched) or *unmatched*.
3. An *unmatched* row is a `claude` process that isn't in Claude Code's session registry, for example one sitting at the "trust this folder" prompt. It's shown so nothing is hidden, but it has no session ID, so it can't be opened or saved as a resumable session.
4. The list refreshes itself every 10 seconds while this tab is showing and the window isn't minimised. Click **Refresh** to update it straight away. The summary at the top right, such as "6 running · 1 working", shows when it last updated.
5. To open a session from here, see [Opening a session](#opening-a-session). Every session on this tab is already running, so you'll always be asked before a second copy opens.

### Opening a session

The same steps work on the Current, Recent and Snapshots tabs.

1. Find the session's row.
2. Click **Open** on the right of the row, double-click the row, or select it and press Enter.
3. If the session is already running, a **Session already running** box asks whether to open a second copy. The answer defaults to **No**, because two windows writing to one conversation can mix up its history.
4. The session opens in its project folder and runs `claude --resume <session-id>`, so you're back in the same conversation. It opens as a PowerShell window or a Windows Terminal tab, depending on [Settings](#settings).

When nothing opens, a message says why. The cases are:
- The folder no longer exists.
- The conversation file has gone.
- A Current-tab session has no messages yet (its **Open** button is greyed out; hover over it to see why).

A snapshot entry saved without a session ID opens a new Claude Code session in that folder.

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

**What gets saved**
- For each session: its session ID, project folder, title, last message, last-active time and your note.
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

You can also open a single session from a snapshot with its **Open** button.

**Manage snapshots**
- **Rename:** select a snapshot, click **Rename…**, type the new name and click **OK**. The file is renamed to match.
- **Delete:** select a snapshot, click **Delete** and confirm. The file goes to the Recycle Bin, and Claude Code's own sessions aren't affected.
- **Missing folders:** if a session's folder has been moved or deleted, the snapshot still lists it with a **folder missing** badge, and the header counts the missing folders. **Restore** skips those sessions and says so. The rest of the snapshot still opens.

### Settings

1. Click the gear button at the top right.
2. Choose how sessions open under **Open sessions in**. This applies to **Open**, **Reopen selected** and **Restore**.
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
  - **Red with "update available":** the source repository has newer commits to the app. Hover to see how many, and where: "Double-click install.cmd in … to update".
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
SessionKeeper.exe --snapshots [--json]                    saved snapshots
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

Restoring sessions stays in the app, because that opens new windows.

`install.cmd` installs the skill for you. To install it by hand, copy the folder:

```powershell
Copy-Item -Recurse -Force skills\session-keeper "$env:USERPROFILE\.claude\skills\"
```

Use `%CLAUDE_CONFIG_DIR%\skills\` instead if you've set `CLAUDE_CONFIG_DIR`. Then start a new Claude Code session so it picks the skill up.

### Version and update checks

- **Version:** the version is set in one place, `<Version>` in `src/SessionKeeper/SessionKeeper.csproj`. Bump it with each change: patch for fixes, minor for features, major for breaking changes. The commit, build date and source repository are embedded automatically when you build. `SessionKeeper.exe --version` prints the same line as the footer.
- **App check:** compares the build commit with `HEAD` of the repository the app was built from. You can set a different repository as `sourceRepo` in `%APPDATA%\SessionKeeper\settings.json`. Only commits that touch `src/SessionKeeper` count. It runs only `git rev-list` and `git rev-parse`, with optional locks off, hidden, with short timeouts, and logged. It never pulls, builds or installs.
- **Runtime check:**
  - The installed runtimes are read from the dotnet install folders and the registered install location. No `dotnet.exe` is run.
  - The latest-patch and support information comes from Microsoft's official release metadata (`releases-index.json`, then the channel's `releases.json`). It uses HTTPS and Microsoft's hosts only, with a short timeout, and is cached for 24 hours in `%APPDATA%\SessionKeeper\cache`.
  - The tooltip warns when support ends within 30 days. Nothing is ever installed or updated.

### Settings file

`%APPDATA%\SessionKeeper\settings.json` stores **Open sessions in** and the optional `sourceRepo`. Start with Windows is a per-user entry under `HKCU\…\CurrentVersion\Run`, and the Start menu shortcut is `SessionKeeper.lnk` in your own Start menu folder. Neither is stored in the settings file.

### How it works

| What | Where it comes from |
|---|---|
| Running sessions | `%USERPROFILE%\.claude\sessions\<pid>.json`, Claude Code's live registry (pid, sessionId, cwd, busy/idle/waiting status, process start time). Each entry is checked against the live process table: the PID must be alive and its start time must match, because a killed process leaves its entry behind. |
| Unregistered processes | `claude.exe` processes with no registry entry are still shown as *unmatched*, with their folder read from the process when Windows allows it. Elevated processes show "folder unknown". |
| Session details | `%USERPROFILE%\.claude\projects\<encoded-folder>\<sessionId>.jsonl`. The app reads the `cwd`, `timestamp`, user messages, and the `ai-title`, `custom-title` and `last-prompt` records. Files are append-only, so each one is parsed incrementally and cached. For a transcript over 8 MB, the first look reads only the first 256 KB and the last 1 MB. |
| Folder checks | Done off the UI thread and cached, so an offline network drive can't freeze the window. |
| Opening sessions | **PowerShell window:** `conhost.exe powershell -NoExit -EncodedCommand …`. Starting through conhost keeps it a real PowerShell window even when Windows' default terminal is Windows Terminal. **Windows Terminal tab:** `wt -w <name> new-tab -d <folder> powershell -NoExit -EncodedCommand …`. Both run `claude --resume <id>` in the session's folder. |
| Snapshots | Readable JSON in `%APPDATA%\SessionKeeper\snapshots\`, with an optional note for the whole snapshot and per-session notes. |

`CLAUDE_CONFIG_DIR` is respected if set.

### Safety

- **Launches only on request.**
  - Every process SessionKeeper starts goes through one gatekeeper (`LaunchGuard`), which refuses anything outside an explicit click on **Open**, **Reopen selected**, **Restore** or **Open folder**.
  - Background work has no such permission, so it can never launch anything. That covers auto-refresh, session detection, the tray and the command line.
  - Shell execution (URIs such as `ms-settings:`, documents, verbs) is refused, and so is `wt.exe` without a `new-tab` command. A test fails if any other code calls `Process.Start`.
  - The only background process is the read-only `git` query described above, run under the same rules.
- **Launch log.** Every launch, every refused attempt, each user action, and app start and exit are logged with a timestamp and the calling source line. The logs are in `%APPDATA%\SessionKeeper\logs\launches-yyyy-MM-dd.log` and kept for 14 days. If a window ever opens that you didn't ask for, check the log for that time: no `LAUNCH` line means SessionKeeper didn't open it.
- **Read-only towards Claude Code.** Session files and registry entries are opened read-only with full sharing. Processes are opened with query/read rights only. Nothing is ever written to, deleted from or killed in Claude Code's files or processes.
- **No command injection.** Folder names, titles and notes are never parsed as commands. Each window or tab runs a base64 `-EncodedCommand` script. Its only dynamic values are single-quoted PowerShell literals, with every quote character doubled (including Unicode ‘ ’ quotes), and a session ID that must parse as a GUID. Tab titles are reduced to letters, digits and `-_.() `. A folder containing `;` or `%` is not passed to `-d` at all, because wt splits on `;` and expands `%VAR%`. Notes are never passed to any process.
- **The right folder, the right claude.**
  - If the script can't enter the folder, it stops instead of starting claude somewhere else.
  - `claude` is resolved to a full path, and relative `PATH` entries are ignored, so a repository can't plant its own `claude.cmd`.
  - `NoDefaultCurrentDirectoryInExePath` is set in each window, so npm's `claude.cmd` shim can't pick up a `node.exe` from the project folder.
  - A conversation is only resumed in the folder it belongs to.
- If SessionKeeper itself was started from inside a Claude Code session, the `CLAUDE*` variables it inherited are removed before launching. Otherwise the new `claude` wouldn't start normally.

### Limits

- Claude Code's registry and file formats are internal and could change in a future version. If they do, the Current tab falls back to listing *unmatched* processes.
- Node-based installs (`node.exe … claude-code`) are matched through the registry only. They won't appear as *unmatched* when unregistered.
- `claude.exe` is found in `%USERPROFILE%\.local\bin`, then on `PATH`, then as npm's `claude.cmd`. If none of these exist, the windows run `claude` and rely on `PATH`.
