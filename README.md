# SessionKeeper

A small Windows app (C# / .NET 8 WPF) that shows which Claude Code sessions are running, saves them as named snapshots, and reopens them later in Windows Terminal tabs, each resuming the same conversation.

## Build and run

```powershell
dotnet build -c Release
.\src\SessionKeeper\bin\Release\net8.0-windows\win-x64\SessionKeeper.exe
```

Needs the .NET 8 Desktop Runtime. Tests: `dotnet test`.

## The three tabs

- **Current** lists Claude Code sessions running right now. Each row shows the folder, the title and last message, how long ago it was active, and a status: *working*, *needs input*, *idle*, *starting* or *unmatched*. The list refreshes every 10 s, but only while the tab is visible and the window isn't minimised. **Save these as snapshot…** opens the save dialog with every running session ticked.
- **Recent** lists sessions from the session files, newest first, for the last 24 h, 3, 7 or 30 days. Tick rows (or use **Select all from today**), then **Reopen selected** or **Save selected as snapshot…**.
- **Snapshots** lists saved snapshots. Select one to see its sessions and notes. **Restore** reopens them all. You can also rename or delete snapshots (deleted files go to the Recycle Bin).

Hover over a row to see the full path, session ID, and first and last messages.

## How it works

| What | Where it comes from |
|---|---|
| Running sessions | `%USERPROFILE%\.claude\sessions\<pid>.json`, Claude Code's live registry (pid, sessionId, cwd, busy/idle/waiting status, process start time). Each entry is checked against the live process table: the PID must be alive and its start time must match, because a killed process leaves its entry behind. |
| Unregistered processes | `claude.exe` processes with no registry entry are still shown as *unmatched*, with their folder read from the process when Windows allows it. Elevated processes show "folder unknown". |
| Session details | `%USERPROFILE%\.claude\projects\<encoded-folder>\<sessionId>.jsonl`. The app reads the `cwd`, `timestamp`, user messages, and the `ai-title`, `custom-title` and `last-prompt` records. Files are append-only, so each one is parsed incrementally and cached. For a transcript over 8 MB, the first look reads only the first 256 KB and the last 1 MB. |
| Folder checks | Done off the UI thread and cached, so an offline network drive can't freeze the window. |
| Reopen | `wt -w <name> new-tab -d <folder> powershell -NoExit -EncodedCommand …`, which runs `claude --resume <id>` in that folder. If Windows Terminal isn't installed, separate PowerShell windows are used. |
| Snapshots | Readable JSON in `%APPDATA%\SessionKeeper\snapshots\`. |

`CLAUDE_CONFIG_DIR` is respected if set.

## Safety

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
