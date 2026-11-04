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
