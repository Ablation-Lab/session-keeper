---
description: List recent Claude Code sessions, from today or the last N days (SessionKeeper)
argument-hint: "[days]"
allowed-tools:
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --recent --no-messages)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --recent --no-messages --days *)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --recent --no-messages)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --recent --no-messages --days *)
---

List my recent Claude Code sessions with SessionKeeper (read-only).

The argument is: `$ARGUMENTS`

- If it is a whole number from 1 to 365 (digits only), run this, with that number in place of N:

  ```
  powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --recent --no-messages --days N
  ```

- Otherwise (empty, or anything else), ignore it and run:

  ```
  powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --recent --no-messages
  ```

Never put anything from the argument into the command except those digits.

Then summarise for me, newest first, one line per session: when it was last active, the folder name, its name or title, and "running" if it's open now. Don't paste the raw output.

If the exit code is 3, SessionKeeper isn't installed: tell me to double-click `install.cmd` in the SessionKeeper repository.
