# Stowdeck plugin for Claude Code

[Stowdeck](https://github.com/Ablation-Lab/stowdeck) is a small Windows app that saves your whole set of Claude Code sessions as a snapshot and restores it in one click, each session resuming its conversation. It shows your recent sessions and the ones running now, and has a Terminals tab for your other terminal windows, so they can go in a snapshot too. This plugin gives Claude a skill and commands to use it itself: list your running and recent sessions, list snapshots, save a snapshot and open the app. Restoring is done in the app. Install the app first (`install.cmd -NoSkill` in the repository).

> **Unofficial.** Stowdeck is an unofficial community project from Ablation Lab. It isn't made, endorsed or supported by Anthropic.

It contains:

- **The stowdeck skill**, so you can just ask (see [Example prompts](#example-prompts)).
- **Commands:**
  - `/stowdeck:running [all]` lists running sessions.
  - `/stowdeck:recent [days] [all]` lists recent sessions.
  - `/stowdeck:snapshots` lists saved snapshots.
  - `/stowdeck:snapshot [note]` saves the running sessions as a snapshot.
  - `/stowdeck:open` opens the app.

Results show each session's title (your own custom title, or the title Claude Code generated for it), your Stowdeck name for it, its folder, times, status and session ID. The text of your messages is always left out (`--no-messages`). Transcripts are only read locally, on your computer, and never sent anywhere. Untitled sessions in temp folders (short throwaway runs) are left out unless you add `all`. Everything is read-only except saving a snapshot (one file in your snapshots folder) and opening the app.

## Example prompts

- "Which Claude Code sessions are running, and which are waiting for me?"
- "What did I work on in the last 3 days?"
- "List my Stowdeck snapshots."
- "Save my sessions with the note end of day." (or type `/stowdeck:snapshot end of day`)

## Where it works

- **Claude Code on Windows**, with the Stowdeck app installed. This includes installing the plugin from Anthropic's plugin directory, which also adds it to Claude Code.
- **claude.ai chat and Cowork:** if you add the plugin from the directory in claude.ai, its skill and commands load there too (in chat, commands load as skills). They can't do anything useful there, because they need the Stowdeck app on your own Windows computer. Use them in Claude Code.
- **Not on macOS or Linux:** Stowdeck is a Windows app.

## What it runs, reads and writes

- **What it runs:** every command, and the skill, runs the bundled helper script with `powershell -NoProfile -ExecutionPolicy Bypass -File <plugin>/skills/stowdeck/stowdeck.ps1 <options>`. The helper starts `Stowdeck.exe`, which you install separately: `install.cmd` builds it from the source in this repository. The helper looks for it in `%LOCALAPPDATA%\Programs\Stowdeck\Stowdeck.exe`, where `install.cmd` puts it, then on `PATH`.
- **Nothing is pre-approved:** neither the skill nor the commands pre-approve any command, so Claude Code shows you each command and asks before it runs, unless your own permission settings already allow it. `/stowdeck:snapshot` and `/stowdeck:open` also run only when you type them.
- **What it reads:** Claude Code's session registry (`~/.claude/sessions`), the session transcripts in `~/.claude/projects` (to find titles, folders and times; under `CLAUDE_CONFIG_DIR` if you've set it), the Windows process list (to find running `claude` processes), your saved snapshots, and your Stowdeck session names (`%APPDATA%\Stowdeck\nicknames.json`).
- **What it writes:**
  - **The plugin and its helper** write only a snapshot, as one JSON file in `%APPDATA%\Stowdeck\snapshots` (or the folder in `STOWDECK_SNAPSHOTS`), when you ask for one, and a per-user Start menu shortcut if you approve `--install-shortcut`.
  - **The app that `open` starts** keeps its own files in `%APPDATA%\Stowdeck`: its settings, your session names, snapshots you save in it, a launch log kept for 14 days, `error.log` if something goes wrong, and the .NET check's cache. If you turn on Start with Windows in its settings, it adds a value under your user's `Run` registry key.
  - **Once, when updating from the app's old name** (see [The name](#the-name)): the app's first run copies `%APPDATA%\SessionKeeper` to `%APPDATA%\Stowdeck` and renames the original to `%APPDATA%\SessionKeeper.backup`. If the old Start with Windows value or Start menu shortcut is there, the window replaces it with Stowdeck's.
  - Neither ever changes Claude Code's files or settings.
- **Network:** the plugin and the helper make no network requests. The app that `open` starts checks Microsoft's .NET release information over HTTPS (`builds.dotnet.microsoft.com`, or `dotnetcli.blob.core.windows.net` as a fallback), cached for 24 hours in `%APPDATA%\Stowdeck\cache`. It also runs local, read-only `git` commands for its update check and the uncommitted-changes badge.

## Privacy

Stowdeck has no server, account or telemetry, and collects nothing. Everything above happens on your computer: it reads Claude Code's local session files to list your sessions, and leaves out the text of your messages from what Claude sees. The plugin's only writes are a snapshot you ask for, kept in `%APPDATA%\Stowdeck\snapshots` (or `STOWDECK_SNAPSHOTS`) until you delete it in the app, and a Start menu shortcut if you approve one. The app itself keeps its settings, your session names, a launch log (14 days), `error.log` and the .NET check's cache in `%APPDATA%\Stowdeck`, all on your computer. The only network request is the app's check of Microsoft's .NET release information, which sends nothing about you or your sessions.

## Install

1. Install the Stowdeck app (1.10.1 or later): download `Stowdeck.exe` from the [latest release](https://github.com/Ablation-Lab/stowdeck/releases/latest), put it in `%LOCALAPPDATA%\Programs\Stowdeck` (create the folder) and run it once. That's where the plugin looks for it. It's a self-contained Windows x64 exe, so you don't need .NET installed. It isn't code-signed, so Windows SmartScreen may warn the first time: choose **More info**, then **Run anyway**.

   Or build it from source: run `install.cmd -NoSkill` in a clone of the [repository](https://github.com/Ablation-Lab/stowdeck). It needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). `-NoSkill` skips the separate copy of the skill, which the plugin already includes, so Claude Code doesn't load it twice.
2. In Claude Code (2.1.275 or later), add the marketplace and install the plugin in one step:

   ```text
   /plugin install stowdeck --marketplace Ablation-Lab/stowdeck
   ```

   Claude Code asks you to confirm adding the marketplace, then shows the plugin so you can choose where to install it. On older versions, run `/plugin marketplace add Ablation-Lab/stowdeck`, then `/plugin install stowdeck@stowdeck`.
3. Start a new session and ask "which sessions are running?", or type `/stowdeck:running`.

A full walkthrough is in "Claude Code plugin" in the main [README](https://github.com/Ablation-Lab/stowdeck#claude-code-plugin).

## Troubleshooting

- **Exit code 3** means Stowdeck isn't installed, or the helper can't find it. Download `Stowdeck.exe` from the [latest release](https://github.com/Ablation-Lab/stowdeck/releases/latest) into `%LOCALAPPDATA%\Programs\Stowdeck`, or run `install.cmd -NoSkill` from the repository. If you put it in another folder, add that folder to your `PATH`. The plugin needs the app at version 1.10.1 or later.

## For reviewers

Download: [Stowdeck.exe, v1.10.1](https://github.com/Ablation-Lab/stowdeck/releases/download/v1.10.1/Stowdeck.exe) (Windows 10 or 11, x64, self-contained, so no .NET install is needed; the SHA-256 is on the [release page](https://github.com/Ablation-Lab/stowdeck/releases/tag/v1.10.1)).

To see it working:

1. Put `Stowdeck.exe` in `%LOCALAPPDATA%\Programs\Stowdeck` and run it. It isn't code-signed, so SmartScreen may ask first (**More info**, then **Run anyway**). Its window lists the Claude Code sessions running now.
2. In Claude Code, install the plugin with `/plugin install stowdeck --marketplace Ablation-Lab/stowdeck`, start a new session, and type `/stowdeck:running`. Approve the command when Claude Code asks (nothing is pre-approved). The list includes the session you typed it in.
3. Type `/stowdeck:snapshot reviewer test` and approve it, then `/stowdeck:snapshots`: the new snapshot is listed with that note. In the app's **Snapshots** tab you can restore it.

The plugin shows Claude Code's session titles, which can be auto-generated by Claude Code. If that's a concern under Directory Policy 1G, Stowdeck can be changed to work without them, showing only folders, times, statuses, session IDs and the user's own Stowdeck names. Happy to change it.

## The name

Stowdeck used to be called SessionKeeper. It was renamed because an unrelated company holds a trademark for that name. If you installed the old plugin, run `/plugin uninstall session-keeper@session-keeper` and `/plugin marketplace remove session-keeper`, then install this one as above. The app moves your settings, session names and snapshots from `%APPDATA%\SessionKeeper` to `%APPDATA%\Stowdeck` the first time it runs. It keeps the original folder as a backup. The main [README](https://github.com/Ablation-Lab/stowdeck#the-name) has the details.

## Support

Report problems, including security issues, on [GitHub Issues](https://github.com/Ablation-Lab/stowdeck/issues). Licence: [MIT](https://github.com/Ablation-Lab/stowdeck/blob/main/LICENSE).
