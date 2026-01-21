# Custom Statusline for Claude Code

A custom statusline script that displays useful context at a glance.

## What it shows

```
85% | my-project | feature/auth synced +2
```

- **Context remaining %** - Color-coded (green >40%, yellow 15-40%, red <15%)
- **Folder name** - Current working directory
- **Git branch** - Current branch name
- **Sync status** - Remote tracking state:
  - `synced` - Up to date with remote
  - `synced +N` - N commits ahead of remote
  - `synced -N` - N commits behind remote
  - `synced +N -M` - Diverged from remote
  - `mismatch` - Local/remote branch names differ
  - `no remote` - No upstream tracking branch
  - `detached` - Detached HEAD state
  - `no git` - Not a git repository

## Requirements

- `jq` - JSON processor (pre-installed on most systems, or `brew install jq`)
- `git` - For branch/sync status
- Bash shell

## Installation

### 1. Copy the script

```bash
# Create lib directory if needed
mkdir -p ~/.claude/lib

# Copy script
cp statusline-command.sh ~/.claude/lib/

# Make executable
chmod +x ~/.claude/lib/statusline-command.sh
```

### 2. Configure Claude Code

Add to `~/.claude/settings.json`:

```json
{
  "statusLine": {
    "type": "command",
    "command": "/bin/bash ~/.claude/lib/statusline-command.sh"
  }
}
```

Or if you have existing settings:

```json
{
  "existingSetting": "value",
  "statusLine": {
    "type": "command",
    "command": "/bin/bash ~/.claude/lib/statusline-command.sh"
  }
}
```

### 3. Restart Claude Code

The new statusline will appear at the bottom of your terminal.

## Customization

The script receives JSON input from Claude Code with this structure:

```json
{
  "context_window": {
    "total_input_tokens": 12345,
    "total_output_tokens": 6789,
    "context_window_size": 200000,
    "used_percentage": 15,
    "remaining_percentage": 85
  },
  "workspace": {
    "current_dir": "/path/to/project"
  }
}
```

You can modify the script to display different information or change the formatting.

### Color codes

- `\033[32m` - Green
- `\033[33m` - Yellow
- `\033[31m` - Red
- `\033[36m` - Cyan
- `\033[90m` - Gray
- `\033[2m` - Dim
- `\033[0m` - Reset

## Troubleshooting

**Statusline not appearing:**
- Check that the script path is correct in settings.json
- Ensure the script is executable (`chmod +x`)
- Verify jq is installed (`which jq`)

**Git info not showing:**
- Make sure you're in a git repository
- Check that git is in your PATH

**Colors not displaying:**
- Your terminal must support ANSI escape codes (most do)

## License

MIT
