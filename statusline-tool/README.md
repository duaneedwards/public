# Enhanced Statusline for Claude Code

A custom statusline script that displays context usage, rate limits, budget tracking, and git status at a glance.

## What it shows

```
45% 90K | 4% 4h10 ▼0h37/16% 3d18 ▼2d02 | my-project | main synced
```

| Section | Meaning |
|---------|---------|
| `45% 90K` | Context used (45%), total tokens (90K) |
| `4% 4h10 ▼0h37` | 5-hour session: 4% used, 4h10 until reset, 37min under budget |
| `16% 3d18 ▼2d02` | 7-day limit: 16% used, 3d18h until reset, 2d02h under budget |
| `my-project` | Current folder name |
| `main synced` | Git branch and sync status |

### Context Usage
- **Green** - Under 50% used
- **Yellow** - 50-74% used
- **Orange** - 75%+ used (warning)

### Usage Limits
- **5-hour session limit** - Rolling window, resets continuously
- **7-day weekly limit** - Rolling window for sustained usage
- Percentages show how much of each limit you've consumed

### Budget Delta
The delta shows if you're pacing ahead or behind your expected usage rate:
- `▼` (green) = **Under budget** - Using less than expected for elapsed time
- `▲` (red) = **Over budget** - Using more than expected for elapsed time

Example: If 50% of your 5-hour window has elapsed but you've only used 25%, you're under budget by the equivalent time shown.

### Git Status
- `synced` - Up to date with remote
- `synced +N` - N commits ahead of remote
- `synced -N` - N commits behind remote
- `synced +N -M` - Diverged from remote
- `mismatch` - Local/remote branch names differ
- `no remote` - No upstream tracking branch
- `detached` - Detached HEAD state
- `no git` - Not a git repository

## Requirements

- **macOS** (uses macOS Keychain for OAuth token)
- `jq` - JSON processor (`brew install jq`)
- `curl` - For API calls (pre-installed)
- `git` - For branch/sync status
- Bash shell

## Installation

### 1. Copy the script

```bash
# Create lib directory if needed
mkdir -p ~/.claude/lib

# Copy script (or download from this repo)
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

## How It Works

### OAuth Token
The script reads your Claude Code OAuth token from the macOS Keychain (where Claude Code stores it automatically). No manual token configuration needed.

### API Caching
Usage data is fetched from Anthropic's API and cached for 1 minute at `~/.claude/statusline-cache.json` to keep the statusline responsive.

### Graceful Degradation
If the usage API is unavailable (network issues, token expired, etc.), the statusline still shows context usage and git status - only the usage limits section is omitted.

## Customization

### Cache Duration
Edit `CACHE_TTL_MS` at the top of the script (default: 60000ms = 1 minute):

```bash
CACHE_TTL_MS=120000  # 2 minutes
```

### Color Thresholds
Modify the threshold checks in the script:

```bash
# Context usage thresholds
if [ "$used_pct" -ge 75 ]; then    # Orange warning
elif [ "$used_pct" -ge 50 ]; then  # Yellow caution

# Usage limit thresholds
if [ "$session_pct_int" -ge 80 ]; then  # Red
elif [ "$session_pct_int" -ge 50 ]; then  # Yellow
```

### Color Codes Reference
- `\033[32m` - Green
- `\033[33m` - Yellow
- `\033[31m` - Red
- `\033[36m` - Cyan
- `\033[90m` - Gray
- `\033[38;5;208m` - Orange
- `\033[2m` - Dim
- `\033[0m` - Reset

## Troubleshooting

**Statusline not appearing:**
- Check that the script path is correct in settings.json
- Ensure the script is executable (`chmod +x`)
- Verify jq is installed (`which jq`)

**Usage limits not showing:**
- This feature requires macOS (uses Keychain)
- Check you're logged into Claude Code
- Try clearing cache: `rm ~/.claude/statusline-cache.json`

**Git info not showing:**
- Make sure you're in a git repository
- Check that git is in your PATH

**Colors not displaying:**
- Your terminal must support ANSI escape codes (most do)

## Credits

Inspired by [cc-statusline](https://github.com/daliovic/cc-statusline) by @daliovic.

## License

MIT
