---
description: "Snapshot live Claude Code sessions into a restorable Warp launch config"
---

# Warp Session Snapshot Command

Snapshot the current Warp window - every tab, in on-screen order - into a Warp launch configuration so the whole window can be restored after a reboot, with each Claude Code tab resuming its exact session via `claude -r <session-id>` and plain shell tabs reopening in their working directory.

## Task

Run the snapshot generator and report the result.

## Instructions

1. **Execute the snapshot generator** (cross-platform, macOS and Windows):
   ```bash
   dotnet run ~/.claude/lib/snapshot-warp-sessions.cs
   ```

2. **Report the result to the user**:
   - The restore-order table the tool prints: position, session title, short session ID, cwd - relay it as a markdown table in the same order
   - The output file path it printed (inside Warp's launch_configurations directory)
   - Any pruned registry entries (stale entries for closed tabs - normal housekeeping)

3. **Remind the user how to restore**:
   - After reboot: Warp → Command Palette → "Open Launch Configuration" → **Claude sessions snapshot**
   - Launch configs have no working `warp://` URI, so the palette is the one manual step

## How It Works

- A SessionStart/SessionEnd hook (`~/.claude/lib/warp-session-registry.cs`, wired in `~/.claude/settings.json`) maintains `~/.claude/session-registry/<pane-uuid>.json` - one entry per Warp pane (session_id, cwd), keyed by `WARP_TERMINAL_SESSION_UUID`. Sessions register the moment they start, resume, or `/clear` - no warm-up needed.
- The snapshot tool reads Warp's state DB read-only (`warp.sqlite`; macOS: Group Container `2BBY89MBSN.dev.warp`; Windows: probed under `%LOCALAPPDATA%`/`%APPDATA%`) for on-screen tab order (`tabs.id` ascending) and joins tabs to sessions via pane uuid. Tabs without a claude session restore as plain cwd tabs.
- **Session titles** come from the last `ai-title`/`custom-title` event in each session's transcript; the **permission mode** (`--permission-mode auto` etc.) is reconstructed from the transcript's last `permission-mode` event. Titles are shown in the report table only - not written to the YAML, so Claude's live tab titles aren't pinned.
- Registry entries whose pane no longer exists (closed tabs) are pruned on every run. No process inspection, no shell-outs - fully cross-platform .NET.

## Known Limitations

- Sessions that predate the hook install register on their next resume or `/clear`; until then their tab restores as a plain shell
- If Warp's state DB is unreadable, falls back to cwd-sorted order with no pruning (a warning is printed)
- Windows Warp paths are best-guess candidates pending verification on a Windows machine (the tool prints what it probed if nothing is found)
- Snapshot is point-in-time - re-run before shutdown to capture the current set of sessions and tab order

## Examples

**Snapshot before a reboot:**
```bash
/warp-snapshot
```
