# SessionKeeper

A small Windows app (C# / .NET 8 WPF) that shows which Claude Code sessions are running, saves them as named snapshots, and reopens them later in Windows Terminal tabs, each resuming the same conversation.

## Install

You need Windows 10 or 11 and the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). No admin rights are needed. From a clone of this repository:

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

This does three things:
1. Builds the app into `%LOCALAPPDATA%\Programs\SessionKeeper`.
2. Adds a Start menu shortcut.
3. Installs the Claude Code skill (see below).

Options:
- `-Destination <folder>` installs somewhere else.
- `-NoStartMenu` skips the shortcut.
- `-NoSkill` skips the skill.

To update, pull and run it again, after closing the app and its tray icon.

**Uninstall:**
1. In the app's Settings, untick both options.
2. Close the app.
3. Delete `%LOCALAPPDATA%\Programs\SessionKeeper` and `%USERPROFILE%\.claude\skills\session-keeper`.

Snapshots stay in `%APPDATA%\SessionKeeper` until you delete them.

**Developing:** run `dotnet build`, then `dotnet test`. The app is at `src\SessionKeeper\bin\Debug\net8.0-windows\win-x64\SessionKeeper.exe`.

## Version and updates

The bottom bar shows the version, build commit and build date, for example `v1.0.0 · build a1b2c3d · 3 Nov 2026`. `SessionKeeper.exe --version` prints the same. A `+` after the commit means the build had uncommitted changes.

- **Where it comes from:** the version is set in one place, `<Version>` in `src/SessionKeeper/SessionKeeper.csproj`. Bump it with each change: patch for fixes, minor for features, major for breaking changes. The commit, build date and source repository are embedded automatically when you build.
- **Update check:** once at startup, in the background, the app compares its build commit with `HEAD` of the repository it was built from. You can set a different repository as `sourceRepo` in `%APPDATA%\SessionKeeper\settings.json`.
  - Only commits that touch `src/SessionKeeper` count.
  - If there are any, the line turns red with **update available**, and the tooltip says how many.
  - The check only reads (`git rev-list` and `git rev-parse` with optional locks off, hidden, short timeouts, logged). It never pulls, builds or installs.
  - If the repository or git can't be found, the line stays grey.
  - To update, run `install.ps1` in the repository.

## Settings

Open Settings with the gear button at the top right.

- **Open sessions in** applies to Open, Reopen selected and Restore. It's saved in `%APPDATA%\SessionKeeper\settings.json`.
  - **PowerShell window** (the default) starts a separate Windows PowerShell window through `conhost.exe`. That keeps it a real PowerShell window even when Windows' default terminal is set to Windows Terminal.
  - **Windows Terminal tab** opens a tab in a Windows Terminal window. If Terminal isn't installed, PowerShell windows are used and you're told.

- **Start with Windows** (off by default) adds a per-user entry under `HKCU\…\CurrentVersion\Run`. At sign-in the app starts in the system tray, and it doesn't scan anything until you open it. Click the tray icon to open it; right-click for Exit.
- **Start menu shortcut** adds or removes `SessionKeeper.lnk` in your own Start menu folder.

Both settings point at the copy of the app you set them from. If you move it, untick and tick them again.

Starting SessionKeeper while it's already open brings the existing window forward, including from the tray.

## Command line

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

## Claude Code skill

`skills/session-keeper/` lets you ask Claude Code things like:
- "open SessionKeeper"
- "list my running sessions"
- "what did I work on today?"
- "save a snapshot of my sessions with the note waiting on council results"

It runs the command-line options above through `sessionkeeper.ps1`, which finds `SessionKeeper.exe` in this order:
1. The `SESSIONKEEPER_EXE` environment variable.
2. `%LOCALAPPDATA%\Programs\SessionKeeper\SessionKeeper.exe`.
3. `PATH`.

Restoring sessions stays in the app, because that opens terminal tabs.

`install.ps1` installs the skill for you. To install it by hand, copy the folder:

```powershell
Copy-Item -Recurse -Force skills\session-keeper "$env:USERPROFILE\.claude\skills\"
```

Use `%CLAUDE_CONFIG_DIR%\skills\` instead if you've set `CLAUDE_CONFIG_DIR`. Then start a new Claude Code session so it picks the skill up.

## The three tabs

- **Current** lists Claude Code sessions running right now. Each row shows the folder, the title and last message, how long ago it was active, and a status: *working*, *needs input*, *idle*, *starting* or *unmatched*. The list refreshes every 10 s, but only while the tab is visible and the window isn't minimised. **Save these as snapshot…** opens the save dialog with every running session ticked.
- **Recent** lists sessions from the session files, newest first, for the last 24 h, 3, 7 or 30 days. Tick rows (or use **Select all from today**), then **Reopen selected** or **Save selected as snapshot…**.
- **Snapshots** lists saved snapshots. Select one to see its sessions and notes. **Restore** reopens them all. You can also rename or delete snapshots (deleted files go to the Recycle Bin).

Hover over a row to see the full path, session ID, and first and last messages.

**Open:** every row in all three views has an **Open** button. Double-clicking the row, or pressing Enter, does the same. It opens the session in its project folder and runs `claude --resume <session-id>`.
- **Already running:** if the session is already running, you're asked before a second copy opens, because two windows writing to one conversation can mix up its history.
- **Folder gone:** if the folder no longer exists, nothing opens and you're told why.
- **No messages yet:** a running session with no messages has nothing to resume, so its Open button is disabled.
- **Snapshot entries without an ID:** these open a new Claude Code session in that folder.

## How it works

| What | Where it comes from |
|---|---|
| Running sessions | `%USERPROFILE%\.claude\sessions\<pid>.json`, Claude Code's live registry (pid, sessionId, cwd, busy/idle/waiting status, process start time). Each entry is checked against the live process table: the PID must be alive and its start time must match, because a killed process leaves its entry behind. |
| Unregistered processes | `claude.exe` processes with no registry entry are still shown as *unmatched*, with their folder read from the process when Windows allows it. Elevated processes show "folder unknown". |
| Session details | `%USERPROFILE%\.claude\projects\<encoded-folder>\<sessionId>.jsonl`. The app reads the `cwd`, `timestamp`, user messages, and the `ai-title`, `custom-title` and `last-prompt` records. Files are append-only, so each one is parsed incrementally and cached. For a transcript over 8 MB, the first look reads only the first 256 KB and the last 1 MB. |
| Folder checks | Done off the UI thread and cached, so an offline network drive can't freeze the window. |
| Reopen | `wt -w <name> new-tab -d <folder> powershell -NoExit -EncodedCommand …`, which runs `claude --resume <id>` in that folder. If Windows Terminal isn't installed, separate PowerShell windows are used. |
| Snapshots | Readable JSON in `%APPDATA%\SessionKeeper\snapshots\`, with an optional note for the whole snapshot and per-session notes. |

`CLAUDE_CONFIG_DIR` is respected if set.

## Safety

- **Launches only on request.**
  - Every process SessionKeeper starts goes through one gatekeeper (`LaunchGuard`), which refuses anything outside an explicit click on **Open**, **Reopen**, **Restore** or **Open folder**.
  - Background work has no such permission, so it can never launch anything. That covers auto-refresh, session detection, the tray and the command line.
  - Shell execution (URIs such as `ms-settings:`, documents, verbs) is refused, and so is `wt.exe` without a `new-tab` command. A test fails if any other code calls `Process.Start`.
- **Launch log.** Every launch, every refused attempt, each user action, and app start and exit are logged with a timestamp and the calling source line. The logs are in `%APPDATA%\SessionKeeper\logs\launches-yyyy-MM-dd.log` and kept for 14 days. If a window ever opens that you didn't ask for, check the log for that time: no `LAUNCH` line means SessionKeeper didn't open it.
- **Read-only towards Claude Code.** Session files and registry entries are opened read-only with full sharing. Processes are opened with query/read rights only. Nothing is ever written to, deleted from or killed in Claude Code's files or processes.
- **No command injection.** Folder names, titles and notes are never parsed as commands. Each tab runs a base64 `-EncodedCommand` script. Its only dynamic values are single-quoted PowerShell literals, with every quote character doubled (including Unicode ‘ ’ quotes), and a session ID that must parse as a GUID. Tab titles are reduced to letters, digits and `-_.() `. A folder containing `;` or `%` is not passed to `-d` at all, because wt splits on `;` and expands `%VAR%`. Notes are never passed to any process.
- **The right folder, the right claude.**
  - If the script can't enter the folder, it stops instead of starting claude somewhere else.
  - `claude` is resolved to a full path, and relative `PATH` entries are ignored, so a repository can't plant its own `claude.cmd`.
  - `NoDefaultCurrentDirectoryInExePath` is set in each tab, so npm's `claude.cmd` shim can't pick up a `node.exe` from the project folder.
  - A conversation is only resumed in the folder it belongs to.
- **Before reopening:** missing folders are skipped and listed. Sessions whose conversation file has gone are skipped. Sessions that are already running trigger a prompt, because opening one twice means two terminals writing to the same conversation.
- If SessionKeeper itself was started from inside a Claude Code session, the `CLAUDE*` variables it inherited are removed before launching. Otherwise the new `claude` wouldn't start normally.

## Limits

- Claude Code's registry and file formats are internal and could change in a future version. If they do, the Current tab falls back to listing *unmatched* processes.
- A session with no messages yet has no conversation file. Saving it records "start a fresh claude in this folder".
- Node-based installs (`node.exe … claude-code`) are matched through the registry only. They won't appear as *unmatched* when unregistered.
- `claude.exe` is found in `%USERPROFILE%\.local\bin`, then on `PATH`, then as npm's `claude.cmd`. If none of these exist, the tabs run `claude` and rely on `PATH`.
