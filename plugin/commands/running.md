---
description: List the Claude Code sessions running right now (SessionKeeper)
allowed-tools:
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --list --no-messages)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --list --no-messages)
---

List my running Claude Code sessions with SessionKeeper. Run exactly this command (read-only):

```
powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --list --no-messages
```

Then summarise it for me, one line per session: the folder name, its name or title, its status (working, needs input, idle, starting, unmatched) and how long ago it was active. Give session IDs or PIDs only if two sessions would otherwise look the same. Don't paste the raw output.

If the exit code is 3, SessionKeeper isn't installed: tell me to double-click `install.cmd` in the SessionKeeper repository.
