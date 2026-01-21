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
