# SessionKeeper plugin for Claude Code

Lets Claude Code use [SessionKeeper](https://github.com/Ablation-Lab/session-keeper), a small Windows app that shows, saves and reopens your Claude Code sessions. SessionKeeper itself must be installed first (`install.cmd` in the repository).

It contains:

- **The session-keeper skill**, so you can just ask: "what sessions are running?", "save my sessions with the note end of day".
- **Commands:**
  - `/session-keeper:running [all]` lists running sessions.
  - `/session-keeper:recent [days] [all]` lists recent sessions.
  - `/session-keeper:snapshots` lists saved snapshots.
  - `/session-keeper:snapshot [note]` saves the running sessions as a snapshot.
  - `/session-keeper:open` opens the app.

Results show titles, names, folders, times, statuses and session IDs, never what was said in a conversation. Untitled sessions in temp folders (short throwaway runs) are left out unless you add `all`. Everything is read-only except saving a snapshot (one file in your snapshots folder) and opening the app.

## Install

1. Install the SessionKeeper app (1.10.1 or later) with `install.cmd` from the [repository](https://github.com/Ablation-Lab/session-keeper).
2. In Claude Code (2.1.275 or later), add the marketplace and install the plugin in one step:

   ```text
   /plugin install session-keeper --marketplace Ablation-Lab/session-keeper
   ```

   Claude Code asks you to confirm adding the marketplace, then shows the plugin so you can choose where to install it. On older versions, run `/plugin marketplace add Ablation-Lab/session-keeper`, then `/plugin install session-keeper@session-keeper`.
3. Start a new session and ask "which sessions are running?", or type `/session-keeper:running`.

A full walkthrough is in "Claude Code plugin" in the main [README](https://github.com/Ablation-Lab/session-keeper#claude-code-plugin). Licence: [MIT](https://github.com/Ablation-Lab/session-keeper/blob/main/LICENSE).
