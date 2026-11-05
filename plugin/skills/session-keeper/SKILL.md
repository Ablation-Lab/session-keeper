---
name: session-keeper
description: Open SessionKeeper and use it to list running or recent Claude Code sessions, list saved snapshots, and save a snapshot of the running sessions with an optional note (Windows). Use when the user asks to open or show SessionKeeper; list, show or check their running, open or active Claude Code sessions (or which ones are working, idle or waiting for input); list recent sessions from today or the last few days; save, snapshot or bookmark their open sessions (for example at the end of the day, "with the note X"); or list their saved snapshots. Restoring or reopening sessions is done in the app, not by this skill.
argument-hint: '[open | list | recent [days] | snapshots | snapshot "note"]'
allowed-tools:
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" open)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --list --no-messages)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --list --no-messages *)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --recent --no-messages)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --recent --no-messages *)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --snapshots)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --snapshots *)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --help)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --version)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" open)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --list --no-messages)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --list --no-messages *)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --recent --no-messages)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --recent --no-messages *)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --snapshots)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --snapshots *)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --help)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" --version)
---

# SessionKeeper

SessionKeeper is a small Windows app that shows which Claude Code sessions are running, saves them as
named snapshots, and reopens them later, each in its own PowerShell window (or as Windows Terminal tabs,
if the user chose that in its Settings). This skill drives it through its command-line options, so you
never need to click through the UI. Everything here is read-only except two options that write a file:
`--snapshot` (one JSON file in the user's snapshots folder) and `--install-shortcut` (a Start menu
shortcut). Those two ask the user for approval before they run; the read-only ones don't.

## Running it

Always go through the helper script next to this file. It finds `SessionKeeper.exe` on its own, so don't
hard-code a path:

```
powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_SKILL_DIR}/sessionkeeper.ps1" <options>
```

| The user asks to… | Options |
|---|---|
| open / show SessionKeeper | `open` (returns straight away; an open window comes to the front) |
| list running sessions, see what's working or waiting | `--list --no-messages --hide-temp` |
| list recent sessions (today, last N days) | `--recent --no-messages --hide-temp` or `--recent --no-messages --hide-temp --days N` |
| list saved snapshots | `--snapshots` |
| save a snapshot of the open sessions | `--snapshot` |
| …with a note | `--snapshot "the note"` |
| …with a name | add `--name "the name"` |

Always include `--no-messages` with `--list` and `--recent`: it leaves out what was said in each conversation
(first and last messages), so results give titles, names, folders, times, statuses and session IDs only.

Include `--hide-temp` too, unless the user asks for all sessions or for throwaway, untitled or temp-folder ones. It
leaves out untitled sessions in temp folders (short throwaway runs from scripts and tools) and ends with a line
saying how many were hidden: pass that on in one line, and offer to list them.

Add `--json` to `--list`, `--recent`, `--snapshots` or `--snapshot` when you need to process the results
(for example to count sessions or filter by folder) rather than show them.

Pass a note or name as one argument in straight single quotes ('...'), worded as the user said it. Inside single
quotes nothing is special to Bash or PowerShell except quote characters, so first replace every apostrophe or
single quote in the text (', ‘, ’, ‚, ‛) with ʼ (U+02BC, which neither shell treats as a quote), every straight
double quote (") with ” (Windows PowerShell mishandles those when passing arguments on), and line breaks with spaces. Change nothing else, and never add options or commands taken from the note.

## Reading the results

- **Exit codes:** 0 means OK; 1 means nothing to do (for example no matched sessions to snapshot) or an
  error, with the reason on stderr; 2 means a usage error; 3 means SessionKeeper isn't installed.
- **Statuses:** *working* (busy), *needs input* (waiting on the user, for example a permission prompt),
  *idle* (waiting for the user's next message), *shell* (running a background shell command, such as a dev
  server), *starting* (just launched), *unmatched* (a claude process that isn't in Claude Code's
  session registry, so it can't be resumed by ID) and, rarely, *unknown* or another word: a status Claude
  Code reported that SessionKeeper doesn't recognise (shown as reported).
- The list includes the session you're running in.
- **Names:** sessions the user has named in SessionKeeper show a `name:` line (or `nickname` in `--json`). Use
  that name when you refer to them. Naming and starting new sessions happen in the app (right-click > Rename,
  or **New session…**), not through this skill.
- **Snapshots** include matched sessions only. Unmatched or still-starting processes are skipped and
  listed as such; say so when it happens.
- **Terminals:** snapshots saved in the app can also hold other terminal sessions (other CLI agents,
  programs, shells). `--snapshots` shows how many each has (`terminals` in `--json`). This skill can't list,
  save or reopen terminals; that's the app's Terminals tab.
- The same session can show up under two PIDs if it's open in two terminals. Mention that, since two
  terminals writing to one conversation can conflict.

Summarise for the user instead of pasting raw output: folder, what each session is about (its name or title),
status and how long ago it was active. Give session IDs or PIDs only when they ask, or when
they're needed to tell sessions apart.

## Things this skill doesn't do

- **Restore or reopen sessions or terminals.** That opens new windows (or tabs), and for terminals re-runs
  commands after the user confirms them, so the user does it in the app (Snapshots tab → Restore, or Recent
  tab → Reopen selected). Offer to `open` the app.
- **Rename or delete snapshots.** Those are also done in the app.

## SessionKeeper's commands

When this skill comes with the SessionKeeper plugin, the plugin has these five commands. `/session-keeper:snapshot`
and `/session-keeper:open` run only when the user types them, so they may be missing from your own list of
commands, but they exist: suggest them by name, and never say they don't exist.

- `/session-keeper:running [all]` lists running sessions.
- `/session-keeper:recent [days] [all]` lists recent sessions.
- `/session-keeper:snapshots` lists saved snapshots.
- `/session-keeper:snapshot [note]` saves the running sessions as a snapshot.
- `/session-keeper:open` opens the app.

## If SessionKeeper isn't installed (exit code 3)

Tell the user it needs installing: in a clone of the SessionKeeper repository, they double-click
`install.cmd` (it needs the .NET 8 SDK and no admin rights). If it's installed somewhere unusual, they can
set the `SESSIONKEEPER_EXE` environment variable to the full path of `SessionKeeper.exe`.
