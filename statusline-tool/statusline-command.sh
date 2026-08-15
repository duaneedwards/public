#!/bin/bash

# Claude Code Enhanced Statusline (macOS / Linux)
# Shows: Context usage | Model | Usage limits with budget delta | Folder | Git status
#
# Features:
#   - Context usage % and token count (color-coded warnings)
#   - Abbreviated model name (e.g. "O4.8 +1M")
#   - 5-hour session and 7-day usage limits with reset times
#   - Separate 7-day Opus bucket (when the plan exposes it)
#   - Extra-usage / pay-as-you-go credit pool ($used/limit) when enabled
#   - Budget delta: ▼ (under budget / ahead of pace) or ▲ (over budget)
#   - Git branch and sync status
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
#
# Requires: jq, curl, git. Usage-limit segments need Claude Code's OAuth
# credentials in the macOS Keychain (they degrade gracefully if unavailable).

# Read JSON input from stdin
input=$(cat)

# === Configuration ===
CACHE_FILE="$HOME/.claude/statusline-cache.json"
CACHE_TTL_MS=60000  # 1 minute cache

# === ANSI Colors ===
RESET="\033[0m"
DIM="\033[2m"
CYAN="\033[36m"
GREEN="\033[32m"
YELLOW="\033[33m"
RED="\033[31m"
ORANGE="\033[38;5;208m"
GRAY="\033[90m"
MAGENTA="\033[35m"

# === Extract context window data ===
used_pct=$(echo "$input" | jq -r '.context_window.used_percentage // 0' | cut -d. -f1)
total_input=$(echo "$input" | jq -r '.context_window.total_input_tokens // 0')
total_output=$(echo "$input" | jq -r '.context_window.total_output_tokens // 0')
total_tokens=$((total_input + total_output))

# Format tokens (e.g., 150000 -> 150K)
format_tokens() {
    local tokens=$1
    if [ "$tokens" -ge 1000 ]; then
        echo "$((tokens / 1000))K"
    else
        echo "$tokens"
    fi
}

# Context color based on usage (orange at 75%+)
if [ "$used_pct" -ge 75 ]; then
    ctx_color="$ORANGE"
elif [ "$used_pct" -ge 50 ]; then
    ctx_color="$YELLOW"
else
    ctx_color="$GREEN"
fi

# === Workspace info ===
current_dir=$(echo "$input" | jq -r '.workspace.current_dir')
folder_name=$(basename "$current_dir")

# === Model info ===
# Abbreviate the display name: "Opus 4.8 (1M context)" -> "O4.8 +1M",
# "Sonnet 4.6" -> "S4.6", "Haiku 4.5" -> "H4.5". Falls back to the raw
# name if it doesn't match the "<Tier> <version>" shape.
model_raw=$(echo "$input" | jq -r '.model.display_name // empty')
model_name="$model_raw"
if [ -n "$model_raw" ]; then
    tier=$(echo "$model_raw" | sed -n 's/^\([A-Za-z]\).*/\1/p')
    ver=$(echo "$model_raw" | sed -n 's/^[A-Za-z]* *\([0-9][0-9.]*\).*/\1/p')
    if [ -n "$tier" ] && [ -n "$ver" ]; then
        model_name="${tier}${ver}"
        case "$model_raw" in
            *1M*) model_name="${model_name} +1M" ;;
        esac
    fi
fi

# === Usage API Functions ===

get_oauth_token() {
    security find-generic-password -s "Claude Code-credentials" -w 2>/dev/null | jq -r '.claudeAiOauth.accessToken // empty'
}

fetch_usage_from_api() {
    local token=$(get_oauth_token)
    if [ -z "$token" ]; then
        return 1
    fi

    curl -s "https://api.anthropic.com/api/oauth/usage" \
        -H "Authorization: Bearer $token" \
        -H "anthropic-beta: oauth-2025-04-20" 2>/dev/null
}

get_cached_usage() {
    if [ -f "$CACHE_FILE" ]; then
        local cache_time=$(jq -r '.timestamp // 0' "$CACHE_FILE" 2>/dev/null)
        local now_ms=$(($(date +%s) * 1000))
        local age=$((now_ms - cache_time))

        if [ "$age" -lt "$CACHE_TTL_MS" ]; then
            jq -r '.usage' "$CACHE_FILE" 2>/dev/null
            return 0
        fi
    fi
    return 1
}

save_usage_cache() {
    local usage="$1"
    local now_ms=$(($(date +%s) * 1000))
    echo "{\"timestamp\": $now_ms, \"usage\": $usage}" > "$CACHE_FILE" 2>/dev/null
}

get_usage_data() {
    # Try cache first
    local cached=$(get_cached_usage)
    if [ -n "$cached" ] && [ "$cached" != "null" ]; then
        echo "$cached"
        return 0
    fi

    # Fetch fresh data
    local usage=$(fetch_usage_from_api)
    if [ -n "$usage" ] && [ "$usage" != "null" ]; then
        save_usage_cache "$usage"
        echo "$usage"
        return 0
    fi

    return 1
}

# Parse ISO date to epoch seconds (handles timezone offsets)
parse_iso_date() {
    local iso="$1"
    # Remove microseconds and convert +00:00 to +0000 for macOS date
    local cleaned=$(echo "$iso" | sed 's/\.[0-9]*//; s/+\([0-9][0-9]\):\([0-9][0-9]\)$/+\1\2/')
    # macOS date with timezone
    date -j -f "%Y-%m-%dT%H:%M:%S%z" "$cleaned" +%s 2>/dev/null || echo 0
}

# Calculate time remaining in human format
format_time_hours_mins() {
    local reset_iso="$1"
    local reset_epoch=$(parse_iso_date "$reset_iso")
    local now_epoch=$(date +%s)
    local diff_secs=$((reset_epoch - now_epoch))

    if [ "$diff_secs" -le 0 ]; then
        echo "0h00"
        return
    fi

    local hours=$((diff_secs / 3600))
    local mins=$(((diff_secs % 3600) / 60))
    printf "%dh%02d" "$hours" "$mins"
}

format_time_days_hours() {
    local reset_iso="$1"
    local reset_epoch=$(parse_iso_date "$reset_iso")
    local now_epoch=$(date +%s)
    local diff_secs=$((reset_epoch - now_epoch))

    if [ "$diff_secs" -le 0 ]; then
        echo "0d00"
        return
    fi

    local total_hours=$((diff_secs / 3600))
    local days=$((total_hours / 24))
    local hours=$((total_hours % 24))
    printf "%dd%02d" "$days" "$hours"
}

# Calculate budget delta
# Returns: negative = under budget (good), positive = over budget (bad)
calc_delta() {
    local utilization=$1
    local reset_iso="$2"
    local window_hours=$3

    local reset_epoch=$(parse_iso_date "$reset_iso")
    local now_epoch=$(date +%s)
    local time_left_secs=$((reset_epoch - now_epoch))
    local window_secs=$((window_hours * 3600))
    local elapsed_secs=$((window_secs - time_left_secs))

    if [ "$elapsed_secs" -le 0 ]; then
        echo "0"
        return
    fi

    # Expected usage based on time elapsed (integer math, multiply by 100 for precision)
    local expected_pct_x100=$((elapsed_secs * 10000 / window_secs))
    local util_x100=$((utilization * 100))
    local delta_x100=$((util_x100 - expected_pct_x100))

    echo "$delta_x100"
}

format_delta() {
    local delta_x100=$1
    local window_hours=$2

    # delta_x100 is percentage * 100
    local abs_delta=${delta_x100#-}

    # Skip if delta is tiny (< 1%)
    if [ "$abs_delta" -lt 100 ]; then
        return
    fi

    # Convert percentage delta to hours (delta_x100/10000 * window_hours)
    local delta_hours=$((abs_delta * window_hours / 10000))

    local time_str
    if [ "$window_hours" -le 5 ]; then
        # 5-hour window: show hours and minutes
        local hours=$((delta_hours))
        local mins=$(((abs_delta * window_hours * 60 / 10000) % 60))
        time_str=$(printf "%dh%02d" "$hours" "$mins")
    else
        # 7-day window: show days and hours
        local days=$((delta_hours / 24))
        local hours=$((delta_hours % 24))
        time_str=$(printf "%dd%02d" "$days" "$hours")
    fi

    if [ "$delta_x100" -lt 0 ]; then
        printf " ${GREEN}▼${time_str}${RESET}"
    else
        printf " ${RED}▲${time_str}${RESET}"
    fi
}

# Pick a threshold color for a 0-100 utilization integer
pct_color() {
    local pct=$1
    if [ "$pct" -ge 80 ]; then
        echo "$RED"
    elif [ "$pct" -ge 50 ]; then
        echo "$YELLOW"
    else
        echo "$GRAY"
    fi
}

# Format a cents amount as compact USD: 8100 -> 81, 6007 -> 60, 150000 -> 1.5k
# (the usage API reports extra_usage credits in cents)
format_usd() {
    awk -v c="$1" 'BEGIN { d = c / 100; if (d >= 1000) printf "%.1fk", d / 1000; else printf "%.0f", d }'
}

# === Build usage limits string ===
usage_str=""
usage_data=$(get_usage_data 2>/dev/null)

if [ -n "$usage_data" ] && [ "$usage_data" != "null" ]; then
    # Parse session (5-hour) limit
    session_pct=$(echo "$usage_data" | jq -r '.five_hour.utilization // empty' 2>/dev/null)
    session_reset=$(echo "$usage_data" | jq -r '.five_hour.resets_at // empty' 2>/dev/null)

    # Parse weekly (7-day rolling) limit
    weekly_pct=$(echo "$usage_data" | jq -r '.seven_day.utilization // empty' 2>/dev/null)
    weekly_reset=$(echo "$usage_data" | jq -r '.seven_day.resets_at // empty' 2>/dev/null)

    # Parse weekly Opus limit (separate Opus-only bucket; null on plans where Opus
    # consumption rolls into the main seven_day bucket - rendered only when present)
    opus_pct=$(echo "$usage_data" | jq -r '.seven_day_opus.utilization // empty' 2>/dev/null)
    opus_reset=$(echo "$usage_data" | jq -r '.seven_day_opus.resets_at // empty' 2>/dev/null)

    # Parse extra usage (pay-as-you-go monthly credit pool)
    extra_enabled=$(echo "$usage_data" | jq -r '.extra_usage.is_enabled // false' 2>/dev/null)
    extra_pct=$(echo "$usage_data" | jq -r '.extra_usage.utilization // empty' 2>/dev/null)
    extra_used=$(echo "$usage_data" | jq -r '.extra_usage.used_credits // empty' 2>/dev/null)
    extra_limit=$(echo "$usage_data" | jq -r '.extra_usage.monthly_limit // empty' 2>/dev/null)

    if [ -n "$session_pct" ] && [ -n "$session_reset" ]; then
        session_pct_int=${session_pct%.*}
        session_time=$(format_time_hours_mins "$session_reset")
        session_delta=$(calc_delta "$session_pct_int" "$session_reset" 5)
        session_delta_str=$(format_delta "$session_delta" 5)
        session_color=$(pct_color "$session_pct_int")

        usage_str="${session_color}${session_pct_int}%${RESET}${DIM} ${session_time}${RESET}${session_delta_str}"
    fi

    if [ -n "$weekly_pct" ] && [ -n "$weekly_reset" ]; then
        weekly_pct_int=${weekly_pct%.*}
        weekly_time=$(format_time_days_hours "$weekly_reset")
        weekly_delta=$(calc_delta "$weekly_pct_int" "$weekly_reset" 168)  # 7 days = 168 hours
        weekly_delta_str=$(format_delta "$weekly_delta" 168)
        weekly_color=$(pct_color "$weekly_pct_int")

        if [ -n "$usage_str" ]; then
            usage_str="${usage_str}${DIM}/${RESET}${weekly_color}${weekly_pct_int}%${RESET}${DIM} ${weekly_time}${RESET}${weekly_delta_str}"
        else
            usage_str="${weekly_color}${weekly_pct_int}%${RESET}${DIM} ${weekly_time}${RESET}${weekly_delta_str}"
        fi
    fi

    # Weekly Opus bucket (prefixed with dim "o" to distinguish from the all-models weekly)
    if [ -n "$opus_pct" ] && [ "$opus_pct" != "null" ] && [ -n "$opus_reset" ]; then
        opus_pct_int=${opus_pct%.*}
        opus_time=$(format_time_days_hours "$opus_reset")
        opus_delta=$(calc_delta "$opus_pct_int" "$opus_reset" 168)
        opus_delta_str=$(format_delta "$opus_delta" 168)
        opus_color=$(pct_color "$opus_pct_int")

        if [ -n "$usage_str" ]; then
            usage_str="${usage_str}${DIM}/o${RESET}${opus_color}${opus_pct_int}%${RESET}${DIM} ${opus_time}${RESET}${opus_delta_str}"
        else
            usage_str="${DIM}o${RESET}${opus_color}${opus_pct_int}%${RESET}${DIM} ${opus_time}${RESET}${opus_delta_str}"
        fi
    fi

    # Extra-usage credits (pay-as-you-go): dollars used / monthly limit, e.g. $6.0k/8.1k
    # Colored by % of the cap consumed; no reset window (it's a monthly pool).
    if [ "$extra_enabled" = "true" ] && [ -n "$extra_used" ] && [ -n "$extra_limit" ]; then
        extra_pct_int=${extra_pct%.*}
        extra_color=$(pct_color "${extra_pct_int:-0}")
        extra_disp="$(format_usd "$extra_used")/$(format_usd "$extra_limit")"

        if [ -n "$usage_str" ]; then
            usage_str="${usage_str}${DIM} \$${RESET}${extra_color}${extra_disp}${RESET}"
        else
            usage_str="${DIM}\$${RESET}${extra_color}${extra_disp}${RESET}"
        fi
    fi
fi

# === Git information ===
git_branch=""
git_status=""

if git -C "$current_dir" rev-parse --git-dir >/dev/null 2>&1; then
    local_branch=$(git -C "$current_dir" branch --show-current 2>/dev/null)

    if [ -n "$local_branch" ]; then
        git_branch="$local_branch"
        remote_branch=$(git -C "$current_dir" rev-parse --abbrev-ref --symbolic-full-name @{u} 2>/dev/null)

        if [ -n "$remote_branch" ]; then
            remote_branch_name=$(echo "$remote_branch" | cut -d'/' -f2-)
            if [ "$local_branch" != "$remote_branch_name" ]; then
                git_status=" ${YELLOW}mismatch${RESET}"
            else
                ahead=$(git -C "$current_dir" rev-list --count @{u}..HEAD 2>/dev/null || echo "0")
                behind=$(git -C "$current_dir" rev-list --count HEAD..@{u} 2>/dev/null || echo "0")

                if [ "$ahead" -eq 0 ] && [ "$behind" -eq 0 ]; then
                    git_status=" ${GREEN}synced${RESET}"
                elif [ "$ahead" -gt 0 ] && [ "$behind" -eq 0 ]; then
                    git_status=" ${GREEN}synced${RESET} ${YELLOW}+${ahead}${RESET}"
                elif [ "$ahead" -eq 0 ] && [ "$behind" -gt 0 ]; then
                    git_status=" ${GREEN}synced${RESET} ${YELLOW}-${behind}${RESET}"
                else
                    git_status=" ${GREEN}synced${RESET} ${YELLOW}+${ahead} -${behind}${RESET}"
                fi
            fi
        else
            git_status=" ${YELLOW}no remote${RESET}"
        fi
    else
        git_branch="${YELLOW}detached${RESET}"
    fi
else
    git_branch="${GRAY}no git${RESET}"
fi

# === Build output ===
# Format: ctx_used% tokens | model | session%/weekly%[/oOpus%][ $extra%] times deltas | folder | branch status

output=""

# Context usage
output="${ctx_color}${used_pct}%${RESET}${DIM} $(format_tokens $total_tokens)${RESET}"

# Model
if [ -n "$model_name" ]; then
    output="${output} ${DIM}|${RESET} ${MAGENTA}${model_name}${RESET}"
fi

# Usage limits (if available)
if [ -n "$usage_str" ]; then
    output="${output} ${DIM}|${RESET} ${usage_str}"
fi

# Folder
output="${output} ${DIM}|${RESET} ${CYAN}${folder_name}${RESET}"

# Git
output="${output} ${DIM}|${RESET} ${git_branch}${git_status}"

printf "%b" "$output"
