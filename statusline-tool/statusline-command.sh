#!/bin/bash

# Claude Code Custom Statusline
# Shows: Context % | Folder | Git branch + sync status
#
# Install:
#   1. Copy this file to ~/.claude/lib/statusline-command.sh
#   2. Make executable: chmod +x ~/.claude/lib/statusline-command.sh
#   3. Add to ~/.claude/settings.json:
#      {
#        "statusLine": {
#          "type": "command",
#          "command": "/bin/bash ~/.claude/lib/statusline-command.sh"
#        }
#      }

# Read JSON input from stdin
input=$(cat)

# Extract context window data
total_input=$(echo "$input" | jq -r '.context_window.total_input_tokens // 0')
total_output=$(echo "$input" | jq -r '.context_window.total_output_tokens // 0')
context_size=$(echo "$input" | jq -r '.context_window.context_window_size // 200000')
used_pct=$(echo "$input" | jq -r '.context_window.used_percentage // 0')
remaining_pct=$(echo "$input" | jq -r '.context_window.remaining_percentage // 100')

# Extract workspace info
current_dir=$(echo "$input" | jq -r '.workspace.current_dir')
folder_name=$(basename "$current_dir")

# Calculate total tokens
total_tokens=$((total_input + total_output))

# Determine color based on remaining percentage
if [ "$remaining_pct" -ge 41 ]; then
    ctx_color="\033[32m"  # Green (>= 41% remaining)
elif [ "$remaining_pct" -ge 15 ]; then
    ctx_color="\033[33m"  # Yellow (15-40% remaining)
else
    ctx_color="\033[31m"  # Red (< 15% remaining)
fi


# Git information (with error suppression)
git_branch=""
git_status=""

if git -C "$current_dir" rev-parse --git-dir >/dev/null 2>&1; then
    # Get local branch
    local_branch=$(git -C "$current_dir" branch --show-current 2>/dev/null)

    if [ -n "$local_branch" ]; then
        git_branch="$local_branch"

        # Get remote tracking branch
        remote_branch=$(git -C "$current_dir" rev-parse --abbrev-ref --symbolic-full-name @{u} 2>/dev/null)

        if [ -n "$remote_branch" ]; then
            # Check for branch name mismatch
            remote_branch_name=$(echo "$remote_branch" | cut -d'/' -f2-)
            if [ "$local_branch" != "$remote_branch_name" ]; then
                git_status=" \033[33mmismatch\033[0m"
            else
                # Get ahead/behind counts
                ahead=$(git -C "$current_dir" rev-list --count @{u}..HEAD 2>/dev/null || echo "0")
                behind=$(git -C "$current_dir" rev-list --count HEAD..@{u} 2>/dev/null || echo "0")

                if [ "$ahead" -eq 0 ] && [ "$behind" -eq 0 ]; then
                    git_status=" \033[32msynced\033[0m"
                elif [ "$ahead" -gt 0 ] && [ "$behind" -eq 0 ]; then
                    git_status=" \033[32msynced\033[0m \033[33m+${ahead}\033[0m"
                elif [ "$ahead" -eq 0 ] && [ "$behind" -gt 0 ]; then
                    git_status=" \033[32msynced\033[0m \033[33m-${behind}\033[0m"
                else
                    git_status=" \033[32msynced\033[0m \033[33m+${ahead} -${behind}\033[0m"
                fi
            fi
        else
            git_status=" \033[33mno remote\033[0m"
        fi
    else
        git_branch="\033[33mdetached\033[0m"
    fi
else
    git_branch="\033[90mno git\033[0m"
fi

# Format output with dimmed colors
printf '\033[2m'  # Start dim
printf "${ctx_color}%d%%\033[0m | " "$remaining_pct"
printf '\033[36m%s\033[0m\033[2m' "$folder_name"  # Cyan folder name
printf " | "
printf "%b\033[2m" "$git_branch"  # Use %b to interpret escape codes
printf "%b" "$git_status"
printf '\033[0m'  # End dim
