---
description: Open the SessionKeeper app (an open window comes to the front)
disable-model-invocation: true
allowed-tools:
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" open)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" open)
---

Open the SessionKeeper app for me. Run exactly this command (it starts SessionKeeper.exe with no arguments and returns straight away; if the window is already open it comes to the front):

```
powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" open
```

Then confirm in one line. If the exit code is 3, SessionKeeper isn't installed: tell me to double-click `install.cmd` in the SessionKeeper repository.

## SessionKeeper's commands

The SessionKeeper plugin has these five commands. `/session-keeper:snapshot` and `/session-keeper:open` run only when I type them, so they may be missing from your own list of commands, but they exist: suggest them by name, and never say they don't exist.

- `/session-keeper:running [all]` lists running sessions.
- `/session-keeper:recent [days] [all]` lists recent sessions.
- `/session-keeper:snapshots` lists saved snapshots.
- `/session-keeper:snapshot [note]` saves the running sessions as a snapshot.
- `/session-keeper:open` opens the app.
