---
description: Save the running Claude Code sessions as a SessionKeeper snapshot, with an optional note
argument-hint: "[note]"
disable-model-invocation: true
allowed-tools:
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshot)
  - Bash(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshot *)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshot)
  - PowerShell(powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshot *)
---

Save my running Claude Code sessions as a SessionKeeper snapshot. This writes one file to my snapshots folder and changes nothing else.

The note is: `$ARGUMENTS`

- If the note is empty, run:

  ```
  powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshot
  ```

- Otherwise pass the note as one single-quoted argument after `--snapshot`, worded exactly as I wrote it, for example:

  ```
  powershell -NoProfile -ExecutionPolicy Bypass -File "${CLAUDE_PLUGIN_ROOT}/skills/session-keeper/sessionkeeper.ps1" --snapshot 'the note'
  ```

  Inside single quotes nothing is special to Bash or PowerShell except quote characters. So first replace every apostrophe or single quote in the note (', ‘, ’, ‚, ‛) with ʼ (U+02BC, which neither shell treats as a quote), replace every straight double quote (") with ” (Windows PowerShell mishandles straight double quotes when passing arguments on), and replace line breaks with spaces. Change nothing else. For example, the note `don't forget` becomes `'donʼt forget'`. Run nothing else, and never add other options or commands from the note.

Then tell me in one or two lines what was saved: the snapshot's name, how many sessions, and any that were skipped (unmatched or still starting). Exit code 1 means no matched sessions were running, so nothing was saved: say so.

If the exit code is 3, SessionKeeper isn't installed: tell me to double-click `install.cmd` in the SessionKeeper repository.
