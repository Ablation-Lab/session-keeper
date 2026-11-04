---
description: List saved SessionKeeper snapshots
allowed-tools:
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshots)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshots)
---

List my saved SessionKeeper snapshots. Run exactly this command (read-only):

```
powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshots
```

Then summarise for me, newest first: each snapshot's name, when it was saved, how many sessions (and terminals) it holds, and its note. Don't paste the raw output. Restoring a snapshot is done in the app (Snapshots tab, Restore); offer `/session-keeper:open` if I want to.

If the exit code is 3, SessionKeeper isn't installed: tell me to double-click `install.cmd` in the SessionKeeper repository.
