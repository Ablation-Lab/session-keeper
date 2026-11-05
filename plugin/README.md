# SessionKeeper plugin for Claude Code

[SessionKeeper](https://github.com/Ablation-Lab/session-keeper) is a small Windows app that saves your whole set of Claude Code sessions as a snapshot and restores it in one click, each session resuming its conversation. It shows your recent sessions and the ones running now, and has a Terminals tab for your other terminal windows, so they can go in a snapshot too. This plugin gives Claude a skill and commands to use it itself: list your running and recent sessions, list snapshots, save a snapshot and open the app. Restoring is done in the app. Install the app first (`install.cmd -NoSkill` in the repository).

> **Unofficial.** SessionKeeper is an unofficial community project from Ablation Lab. It isn't made, endorsed or supported by Anthropic.

It contains:

- **The session-keeper skill**, so you can just ask (see [Example prompts](#example-prompts)).
- **Commands:**
  - `/session-keeper:running [all]` lists running sessions.
  - `/session-keeper:recent [days] [all]` lists recent sessions.
  - `/session-keeper:snapshots` lists saved snapshots.
  - `/session-keeper:snapshot [note]` saves the running sessions as a snapshot.
  - `/session-keeper:open` opens the app.

Results show each session's title (your own custom title, or the title Claude Code generated for it), your SessionKeeper name for it, its folder, times, status and session ID. The text of your messages is always left out (`--no-messages`). Transcripts are only read locally, on your computer, and never sent anywhere. Untitled sessions in temp folders (short throwaway runs) are left out unless you add `all`. Everything is read-only except saving a snapshot (one file in your snapshots folder) and opening the app.

## Example prompts

- "Which Claude Code sessions are running, and which are waiting for me?"
- "What did I work on in the last 3 days?"
- "List my SessionKeeper snapshots."
- "Save my sessions with the note end of day." (or type `/session-keeper:snapshot end of day`)

## Where it works

- **Claude Code on Windows**, with the SessionKeeper app installed. This includes installing the plugin from Anthropic's plugin directory, which also adds it to Claude Code.
- **claude.ai chat and Cowork:** if you add the plugin from the directory in claude.ai, its skill and commands load there too (in chat, commands load as skills). They can't do anything useful there, because they need the SessionKeeper app on your own Windows computer. Use them in Claude Code.
- **Not on macOS or Linux:** SessionKeeper is a Windows app.

## What it runs, reads and writes

- **What it runs:** every command, and the skill, runs the bundled helper script with `powershell -NoProfile -ExecutionPolicy Bypass -File <plugin>/skills/session-keeper/sessionkeeper.ps1 <options>`. The helper starts `SessionKeeper.exe`, which you install separately: `install.cmd` builds it from the source in this repository. The helper looks for it in `%LOCALAPPDATA%\Programs\SessionKeeper\SessionKeeper.exe`, where `install.cmd` puts it, then on `PATH`.
- **Nothing is pre-approved:** neither the skill nor the commands pre-approve any command, so Claude Code shows you each command and asks before it runs, unless your own permission settings already allow it. `/session-keeper:snapshot` and `/session-keeper:open` also run only when you type them.
- **What it reads:** Claude Code's session registry (`~/.claude/sessions`), the session transcripts in `~/.claude/projects` (to find titles, folders and times; under `CLAUDE_CONFIG_DIR` if you've set it), the Windows process list (to find running `claude` processes), your saved snapshots, and your SessionKeeper session names (`%APPDATA%\SessionKeeper\nicknames.json`).
- **What it writes:**
  - **The plugin and its helper** write only a snapshot, as one JSON file in `%APPDATA%\SessionKeeper\snapshots` (or the folder in `SESSIONKEEPER_SNAPSHOTS`), when you ask for one, and a per-user Start menu shortcut if you approve `--install-shortcut`.
  - **The app that `open` starts** keeps its own files in `%APPDATA%\SessionKeeper`: its settings, your session names, snapshots you save in it, a launch log kept for 14 days, `error.log` if something goes wrong, and the .NET check's cache. If you turn on Start with Windows in its settings, it adds a value under your user's `Run` registry key.
  - Neither ever changes Claude Code's files or settings.
- **Network:** the plugin and the helper make no network requests. The app that `open` starts checks Microsoft's .NET release information over HTTPS (`builds.dotnet.microsoft.com`, or `dotnetcli.blob.core.windows.net` as a fallback), cached for 24 hours in `%APPDATA%\SessionKeeper\cache`. It also runs local, read-only `git` commands for its update check and the uncommitted-changes badge.

## Privacy

SessionKeeper has no server, account or telemetry, and collects nothing. Everything above happens on your computer: it reads Claude Code's local session files to list your sessions, and leaves out the text of your messages from what Claude sees. The plugin's only writes are a snapshot you ask for, kept in `%APPDATA%\SessionKeeper\snapshots` (or `SESSIONKEEPER_SNAPSHOTS`) until you delete it in the app, and a Start menu shortcut if you approve one. The app itself keeps its settings, your session names, a launch log (14 days), `error.log` and the .NET check's cache in `%APPDATA%\SessionKeeper`, all on your computer. The only network request is the app's check of Microsoft's .NET release information, which sends nothing about you or your sessions.

## Install

1. Install the SessionKeeper app (1.10.1 or later) with `install.cmd -NoSkill` from the [repository](https://github.com/Ablation-Lab/session-keeper). It needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) to build. `-NoSkill` skips the separate copy of the skill, which the plugin already includes, so Claude Code doesn't load it twice.
2. In Claude Code (2.1.275 or later), add the marketplace and install the plugin in one step:

   ```text
   /plugin install session-keeper --marketplace Ablation-Lab/session-keeper
   ```

   Claude Code asks you to confirm adding the marketplace, then shows the plugin so you can choose where to install it. On older versions, run `/plugin marketplace add Ablation-Lab/session-keeper`, then `/plugin install session-keeper@session-keeper`.
3. Start a new session and ask "which sessions are running?", or type `/session-keeper:running`.

A full walkthrough is in "Claude Code plugin" in the main [README](https://github.com/Ablation-Lab/session-keeper#claude-code-plugin).

## Troubleshooting

- **Exit code 3** means SessionKeeper isn't installed, or the helper can't find it. Run `install.cmd -NoSkill` from the repository. If you installed it in another folder, add that folder to your `PATH`. The plugin needs the app at version 1.10.1 or later.

## Support

Report problems, including security issues, on [GitHub Issues](https://github.com/Ablation-Lab/session-keeper/issues). Licence: [MIT](https://github.com/Ablation-Lab/session-keeper/blob/main/LICENSE).
