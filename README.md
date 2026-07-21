# Claude Code Tools

A collection of tools, commands, and utilities for [Claude Code](https://claude.ai/claude-code) - Anthropic's CLI for Claude.

## Tools

### [branch-tool](./branch-tool/)

A cross-platform CLI tool for managing git working copies. Quickly spin up isolated working copies for parallel development on feature branches.

**Features:**
- Fast cloning from local repos when available
- Multi-repo support with short aliases
- Interactive TUI for browsing working copies
- Merged branch cleanup detection
- Terminal integration (Warp, iTerm, Terminal, Windows Terminal)
- Cross-platform (macOS, Windows, Linux)

[View documentation](./branch-tool/README.md)

### [statusline-tool](./statusline-tool/)

A custom statusline script that displays useful context at a glance: context window remaining %, current folder, git branch, and sync status.

**Features:**
- Color-coded context percentage (green/yellow/red based on remaining)
- Git branch with remote sync status (ahead/behind counts)
- Detects branch mismatches, detached HEAD, missing remotes
- Lightweight bash script with jq dependency

[View documentation](./statusline-tool/README.md)

### [coderabbit-queue](./coderabbit-queue/)

A cheap, non-LLM poller that drives every open PR on your GitHub account to a fresh CodeRabbit review, patiently working around CodeRabbit's Fair-Usage rate limit.

**Features:**
- Account-wide PR discovery, with a per-account cooldown model that never wastes the review "trickle"
- Fair round-robin so no PR starves; bumpable priority queue
- Auto-escalates to a full review when an incremental one is skipped ("no new commits")
- Head-anchored review detection; never merges
- Runs as a Claude Code skill or standalone (cron/tmux/nohup) — needs only `gh`, no AI

[View documentation](./coderabbit-queue/README.md)

### [warp-claude-sessions](./warp-claude-sessions/)

Snapshot your Warp terminal window and restore it after a reboot with every Claude Code tab resuming its exact session (`claude -r`), in the same tab order.

**Features:**
- Event-driven session registry via SessionStart/SessionEnd hooks - no daemon, no polling
- True on-screen tab order read from Warp's state DB; plain shell tabs restored too
- Session titles and permission modes recovered from transcripts
- One-prompt bootstrap for new machines
- Cross-platform (macOS, Windows) in a single .NET codebase

[View documentation](./warp-claude-sessions/README.md)

## Installation

Each tool has its own installation instructions. Generally, you'll copy files to your `~/.claude/` directory structure:

```
~/.claude/
├── commands/     # Slash command definitions (.md files)
├── config/       # Configuration files (.json)
├── lib/          # Executable scripts (.cs, .py, etc.)
└── skills/       # Skill definitions
```

## Requirements

Most tools in this collection require:

- [.NET 10 SDK](https://dotnet.microsoft.com/download) - for file-based C# apps
- Git - for version control operations

## Contributing

Feel free to open issues or PRs for improvements, bug fixes, or new tools.

## License

MIT
