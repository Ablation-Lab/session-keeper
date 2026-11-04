# SessionKeeper plugin for Claude Code

Lets Claude Code use [SessionKeeper](https://github.com/Ablation-Lab/session-keeper), a small Windows app that shows, saves and reopens your Claude Code sessions. SessionKeeper itself must be installed first (`install.cmd` in the repository).

It contains:

- **The session-keeper skill**, so you can just ask: "what sessions are running?", "save my sessions with the note end of day".
- **Commands:**
  - `/session-keeper:running` lists running sessions.
  - `/session-keeper:recent [days]` lists recent sessions.
  - `/session-keeper:snapshots` lists saved snapshots.
  - `/session-keeper:snapshot [note]` saves the running sessions as a snapshot.
  - `/session-keeper:open` opens the app.

Results show titles, names, folders, times, statuses and session IDs, never what was said in a conversation. Everything is read-only except saving a snapshot (one file in your snapshots folder) and opening the app.

Install steps and a walkthrough: see "Claude Code plugin" in the main [README](https://github.com/Ablation-Lab/session-keeper#claude-code-plugin).
