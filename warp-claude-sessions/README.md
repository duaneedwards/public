# warp-claude-sessions

Snapshot your Warp terminal window - every tab, in on-screen order - and restore it after a reboot with each Claude Code tab resuming its exact session.

Warp's own session restore brings back your tabs and directories after a restart, but every tab that was running Claude Code comes back as a dead shell. This tool closes that gap: one command captures the window (which session was in which tab, in what order, with what permission mode), and one palette action after the reboot brings it all back with every conversation intact via `claude -r <session-id>`.

Cross-platform: macOS and Windows, one codebase, no external tools beyond the .NET SDK.

## Quick install (one prompt)

Paste this into Claude Code on the target machine:

```text
Set up the Warp + Claude Code session snapshot/restore tool from
https://github.com/duaneedwards/public/tree/main/warp-claude-sessions on this machine:

1. Verify prerequisites: `dotnet --version` must be 10 or later and the Warp terminal
   must be installed. Stop and tell me if either is missing.
2. Download these files from
   https://raw.githubusercontent.com/duaneedwards/public/main/warp-claude-sessions/
   creating directories as needed (on Windows, ~ means %USERPROFILE%):
   - warp-session-registry.cs  ->  ~/.claude/lib/warp-session-registry.cs
   - snapshot-warp-sessions.cs ->  ~/.claude/lib/snapshot-warp-sessions.cs
   - warp-snapshot.md          ->  ~/.claude/commands/warp-snapshot.md
3. Merge hooks into ~/.claude/settings.json, preserving everything already there:
   a SessionStart hook and a SessionEnd hook, both type "command" with command
   dotnet run "$HOME/.claude/lib/warp-session-registry.cs" and timeout 30; set
   "async": true on the SessionStart one only. Validate the JSON afterwards.
4. Pre-compile both apps: pipe '{}' into
   dotnet run ~/.claude/lib/warp-session-registry.cs (expect silent success), then run
   dotnet run ~/.claude/lib/snapshot-warp-sessions.cs. On a fresh install,
   "No tabs to snapshot" is success.
5. Windows only: if the snapshot tool reports it cannot find Warp's state DB
   (warp.sqlite) or the launch_configurations directory, search %LOCALAPPDATA% and
   %APPDATA% for them, update the candidate path lists near the top of
   snapshot-warp-sessions.cs to match what you find, then re-run.
6. Summarize what you installed and remind me how to use it: sessions register
   automatically when they start or resume; run /warp-snapshot before shutting down;
   restore afterwards via Warp -> Command Palette -> Open Launch Configuration ->
   "Claude sessions snapshot".
```

## How it works

Three pieces:

| File | Where it lives | What it does |
|------|----------------|--------------|
| `warp-session-registry.cs` | `~/.claude/lib/` | Claude Code hook (SessionStart + SessionEnd). On start/resume/`/clear` it writes `~/.claude/session-registry/<pane-uuid>.json` recording which session lives in which Warp pane (keyed by `WARP_TERMINAL_SESSION_UUID`); on exit it removes the entry. Silent no-op outside Warp. |
| `snapshot-warp-sessions.cs` | `~/.claude/lib/` | The snapshot tool. Reads Warp's state DB (`warp.sqlite`, read-only) for the true on-screen tab order, joins tabs to sessions via pane uuid, pulls each session's title and permission mode from its transcript, and writes a Warp launch configuration with one tab per session running `claude -r <session-id>`. Tabs without a Claude session restore as plain shells in their working directory. |
| `warp-snapshot.md` | `~/.claude/commands/` | The `/warp-snapshot` slash command wrapping the tool. |

The registry is event-driven, so there is no daemon and no polling: a session registers the moment it starts and deregisters when it exits. Entries for tabs that no longer exist are pruned on every snapshot run.

## Usage

Before shutting down or rebooting:

```
/warp-snapshot
```

It prints the tabs it captured, in restore order, with each session's title:

```
 #  title                                          session   cwd
 1  Ventures: business planning                    dcbf4748  ~/Documents/code
 2  migrate from MS SQL Server to PostGres         27b8f949  ~/Documents/code
 3  Kyle iPad App                                  1401b5b2  ~/Documents/code
 4  (shell)                                        -         ~/projects/api
```

After the reboot: open Warp, press **⌘P** (or Ctrl-Shift-P), pick **Open Launch Configuration**, choose **Claude sessions snapshot**. The window opens with all tabs in order, each Claude tab resuming its session with full conversation history. Tab titles reappear within seconds because Claude re-sets the terminal title on resume.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) (the two apps are single-file C# apps run with `dotnet run`)
- [Warp](https://www.warp.dev/) terminal
- [Claude Code](https://claude.ai/claude-code)

## Notes and limitations

- Sessions that were already running before the hook was installed register on their next resume or `/clear`; until then their tab restores as a plain shell.
- The restore step is the one manual action: Warp launch configurations have no working `warp://` URI trigger, so they can only be opened from the Command Palette or File menu.
- Session titles are shown in the snapshot report but deliberately not written into the launch config YAML: a launch-config title acts like a manual rename in Warp and would pin the tab name, blocking Claude's live title updates.
- The permission mode (`--permission-mode auto` etc.) is reconstructed from the session transcript; other CLI flags are not preserved.
- Snapshots are point-in-time. Re-run `/warp-snapshot` before shutdown to capture the current tabs and order.
- macOS Warp state DB location: `~/Library/Group Containers/2BBY89MBSN.dev.warp/Library/Application Support/dev.warp.Warp-Stable/warp.sqlite` (note: not the commonly documented `~/Library/Application Support` path). Windows locations are probed from candidates; the bootstrap prompt has Claude verify and adjust them on first install.

## License

MIT (see repository root)
