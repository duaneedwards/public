# Branch Manager

A cross-platform CLI tool for managing git working copies. Quickly spin up isolated working copies for parallel development on feature branches.

## Features

- **Fast cloning**: Clones from local repos when available, falls back to GitHub
- **Multi-repo support**: Configure multiple repositories with short aliases
- **Interactive mode**: TUI for browsing and managing working copies
- **Merged branch cleanup**: Detects and offers to clean up working copies for merged branches
- **Terminal integration**: Opens new working copies in your preferred terminal (Warp, iTerm, Terminal, Windows Terminal)
- **Cross-platform**: Works on macOS, Windows, and Linux

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download) or later
- Git

## Installation

### 1. Copy files to your Claude Code config

```bash
# Create directories
mkdir -p ~/.claude/commands
mkdir -p ~/.claude/config
mkdir -p ~/.claude/lib

# Copy files
cp commands/branch.md ~/.claude/commands/
cp commands/branch-wc.md ~/.claude/commands/
cp lib/branch-manager.cs ~/.claude/lib/

# Copy example config (customize for your repos)
cp config/branch-repos.example.json ~/.claude/config/branch-repos.json
```

### 2. Add shell alias (optional, for use outside Claude Code)

**macOS/Linux (~/.zshrc or ~/.bashrc):**
```bash
alias branch='dotnet run ~/.claude/lib/branch-manager.cs --'
```

**Windows (PowerShell profile):**
```powershell
function branch { dotnet run $env:USERPROFILE\.claude\lib\branch-manager.cs -- @args }
```

## Usage

### Within Claude Code

```
/branch-wc                              # Interactive mode
/branch-wc my-project feature/my-feature  # Create working copy
/branch-wc list                         # List all working copies
/branch-wc config                       # Configure repositories
```

Or use `/branch` when already in a repository to create a sister working copy.

### From command line (with alias)

```bash
branch                                  # Interactive mode
branch my-project feature/my-feature    # Create working copy
branch list                             # List all working copies
branch list my-project                  # List working copies for specific repo
branch config                           # Configure repositories
branch help                             # Show help
```

## Configuration

Configuration is stored in `~/.claude/config/branch-repos.json`:

```json
{
  "settings": {
    "rootFolder": {
      "macos": "/Users/you/code",
      "windows": "D:\\Code",
      "linux": "/home/you/code"
    },
    "defaultTerminal": "warp"
  },
  "repositories": [
    {
      "shortName": "my-project",
      "url": "https://github.com/your-org/my-project",
      "displayName": "your-org/my-project",
      "defaultBranch": "main",
      "cloneSource": "auto"
    }
  ]
}
```

### Configuration options

| Setting | Description |
|---------|-------------|
| `rootFolder` | Platform-specific paths where working copies are created |
| `defaultTerminal` | Terminal to open (`warp`, `iterm`, `terminal`, or Windows Terminal) |
| `shortName` | Alias for quick reference (e.g., `mp` for `my-project`) |
| `url` | GitHub repository URL |
| `displayName` | Full org/repo name for display |
| `defaultBranch` | Default branch to base new branches from (`main`, `master`, etc.) |
| `cloneSource` | Where to clone from: `auto` (local if available), `local`, or `github` |

## How it works

1. **Clone source detection**: When creating a working copy, the tool first checks if a local clone exists (faster). If not, it clones from GitHub.

2. **Folder naming**: Working copies are named `{repo}-{branch-name}` with the `feature/` prefix stripped:
   - `feature/phase-4-api` → `my-project-phase-4-api`

3. **Remote configuration**: After cloning locally, the remote is updated to point to GitHub so pushes go to the right place.

4. **Merged branch detection**: The tool checks if the feature branch still exists on the remote. If not (branch was merged/deleted), it's marked for cleanup.

## Workflow example

```bash
# You're working on my-project and need to start a new feature
branch my-project feature/user-auth

# This creates:
# ~/code/my-project-user-auth/
#   - Cloned from local ~/code/my-project (fast!)
#   - Remote set to github.com/your-org/my-project
#   - Checked out to feature/user-auth branch
#   - Opens in Warp terminal

# Later, list all working copies
branch list

# Clean up after features are merged
branch  # Interactive mode shows merged branches for cleanup
```

## License

MIT
