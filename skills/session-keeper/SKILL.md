---
name: session-keeper
description: Open SessionKeeper and use it to list running or recent Claude Code sessions, list saved snapshots, and save a snapshot of the running sessions with an optional note (Windows). Use when the user asks to open or show SessionKeeper; list, show or check their running, open or active Claude Code sessions (or which ones are working, idle or waiting for input); list recent sessions from today or the last few days; save, snapshot or bookmark their open sessions (for example at the end of the day, "with the note X"); or list their saved snapshots. Restoring or reopening sessions is done in the app, not by this skill.
argument-hint: [open | list | recent [days] | snapshots | snapshot "note"]
allowed-tools: Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" *) PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" *)
---

# SessionKeeper

SessionKeeper is a small Windows app that shows which Claude Code sessions are running, saves them as
named snapshots, and reopens them later in Windows Terminal tabs. This skill drives it through its
command-line options, so you never need to click through the UI. Everything here is read-only except
saving a snapshot, which writes one JSON file to the user's snapshots folder.

## Running it

Always go through the helper script next to this file. It finds `SessionKeeper.exe` on its own, so don't
hard-code a path:

```
powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" <options>
```

| The user asks to… | Options |
|---|---|
| open / show SessionKeeper | `open` (returns straight away; an open window comes to the front) |
| list running sessions, see what's working or waiting | `--list` |
| list recent sessions (today, last N days) | `--recent` or `--recent --days N` |
| list saved snapshots | `--snapshots` |
| save a snapshot of the open sessions | `--snapshot` |
| …with a note | `--snapshot "the note"` |
| …with a name | add `--name "the name"` |

Add `--json` to `--list`, `--recent`, `--snapshots` or `--snapshot` when you need to process the results
(for example to count sessions or filter by folder) rather than show them.

Pass a note or name as a single quoted argument, exactly as the user worded it. If it contains double
quotes, change them to single quotes first so the command line stays intact.

## Reading the results

- **Exit codes:** 0 means OK; 1 means nothing to do (for example no matched sessions to snapshot) or an
  error, with the reason on stderr; 2 means a usage error; 3 means SessionKeeper isn't installed.
- **Statuses:** *working* (busy), *needs input* (waiting on the user, for example a permission prompt),
  *idle*, *starting* (just launched) and *unmatched* (a claude process that isn't in Claude Code's
  session registry, so it can't be resumed by ID).
- The list includes the session you're running in.
- **Names:** sessions the user has named in SessionKeeper show a `name:` line (or `nickname` in `--json`). Use
  that name when you refer to them. Naming and starting new sessions happen in the app (right-click > Rename,
  or **New session…**), not through this skill.
- **Snapshots** include matched sessions only. Unmatched or still-starting processes are skipped and
  listed as such; say so when it happens.
- The same session can show up under two PIDs if it's open in two terminals. Mention that, since two
  terminals writing to one conversation can conflict.

Summarise for the user instead of pasting raw output: folder, what each session is about (title or last
message), status and how long ago it was active. Give session IDs or PIDs only when they ask, or when
they're needed to tell sessions apart.

## Things this skill doesn't do

- **Restore or reopen sessions.** That opens terminal tabs, so the user does it in the app (Snapshots
  tab → Restore, or Recent tab → Reopen selected). Offer to `open` the app.
- **Rename or delete snapshots.** Those are also done in the app.

## If SessionKeeper isn't installed (exit code 3)

Tell the user it needs installing: in a clone of the SessionKeeper repository, they double-click
`install.cmd` (it needs the .NET 8 SDK and no admin rights). If it's installed somewhere unusual, they can
set the `SESSIONKEEPER_EXE` environment variable to the full path of `SessionKeeper.exe`.
