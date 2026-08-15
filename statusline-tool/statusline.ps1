# Claude Code Enhanced Statusline (Windows)
# Shows: Context usage | Model | Usage limits with budget delta | Folder | Git status
# Compatible with Windows PowerShell 5.1+
#
# Feature parity with statusline-command.sh, EXCEPT the budget-delta markers use
# ASCII "v" (under budget / ahead of pace) and "^" (over budget) instead of the
# Unicode triangles the bash version uses - the Windows console does not render
# those glyphs reliably.
#
# Install:
#   1. Copy this file to %USERPROFILE%\.claude\lib\statusline.ps1
#   2. Add to %USERPROFILE%\.claude\settings.json:
#        {
#          "statusLine": {
#            "type": "command",
#            "command": "powershell -NoProfile -ExecutionPolicy Bypass -File %USERPROFILE%\\.claude\\lib\\statusline.ps1"
#          }
#        }
#
# Requires: git. Usage-limit segments read Claude Code's OAuth credentials from
# %USERPROFILE%\.claude\.credentials.json (they degrade gracefully if absent).

# Read JSON input from stdin
$jsonInput = [Console]::In.ReadToEnd()
$data = $jsonInput | ConvertFrom-Json

# Extract context window data with fallbacks for PS 5.x compatibility
$totalInput = if ($data.context_window.total_input_tokens) { $data.context_window.total_input_tokens } else { 0 }
$totalOutput = if ($data.context_window.total_output_tokens) { $data.context_window.total_output_tokens } else { 0 }
$usedPct = if ($data.context_window.used_percentage) { [int]$data.context_window.used_percentage } else { 0 }

# Total tokens (e.g. 150000 -> 150K)
$totalTokens = $totalInput + $totalOutput
if ($totalTokens -ge 1000) {
    $tokenDisplay = "{0}K" -f [math]::Floor($totalTokens / 1000)
} else {
    $tokenDisplay = "$totalTokens"
}

# Workspace info
$currentDir = $data.workspace.current_dir
$folderName = Split-Path -Leaf $currentDir

# ANSI colors
$esc = [char]27
$green = "$esc[32m"
$yellow = "$esc[33m"
$orange = "$esc[38;5;208m"
$red = "$esc[31m"
$cyan = "$esc[36m"
$dim = "$esc[2m"
$gray = "$esc[90m"
$magenta = "$esc[35m"
$reset = "$esc[0m"

# Context color: green < 50%, yellow 50-74%, orange 75%+
if ($usedPct -lt 50) {
    $ctxColor = $green
} elseif ($usedPct -lt 75) {
    $ctxColor = $yellow
} else {
    $ctxColor = $orange
}

# Model info: abbreviate "Opus 4.8 (1M context)" -> "O4.8 +1M", "Sonnet 4.6" -> "S4.6".
# Falls back to the raw name if it doesn't match the "<Tier> <version>" shape.
$modelRaw = $data.model.display_name
$modelName = $modelRaw
if ($modelRaw) {
    $m = [regex]::Match($modelRaw, '^([A-Za-z])[A-Za-z]*\s*([0-9][0-9.]*)')
    if ($m.Success) {
        $modelName = $m.Groups[1].Value + $m.Groups[2].Value
        if ($modelRaw -match '1M') { $modelName = "$modelName +1M" }
    }
}

# Threshold color for a utilization percentage: gray < 50, yellow 50-79, red 80+
function Get-LimitColor {
    param([double]$utilization)
    if ($utilization -lt 50) {
        return $gray
    } elseif ($utilization -lt 80) {
        return $yellow
    } else {
        return $red
    }
}

# Compact USD from a cents amount: 8100 -> 81, 150000 -> 1.5k
# (the usage API reports extra_usage credits in cents)
function Format-Usd {
    param([double]$cents)
    $d = $cents / 100
    if ($d -ge 1000) {
        return ("{0:0.0}k" -f ($d / 1000))
    } else {
        return ("{0:0}" -f $d)
    }
}

# Time remaining: "session" -> hours:mins, else days:hours
function Format-TimeRemaining {
    param(
        [DateTime]$resetTime,
        [string]$mode
    )
    $diff = $resetTime - [DateTime]::UtcNow
    if ($diff.TotalSeconds -le 0) {
        if ($mode -eq "session") { return "0h00" } else { return "0d00" }
    }
    if ($mode -eq "session") {
        return "{0}h{1:D2}" -f [math]::Floor($diff.TotalHours), $diff.Minutes
    } else {
        return "{0}d{1:D2}" -f [math]::Floor($diff.TotalDays), $diff.Hours
    }
}

# Budget delta: how far ahead/behind linear pace this bucket is.
function Get-BudgetDelta {
    param(
        [double]$utilization,
        [DateTime]$resetTime,
        [double]$windowHours  # 5 for session, 168 for weekly/opus
    )
    $diff = $resetTime - [DateTime]::UtcNow
    $remainingHours = [math]::Max(0, $diff.TotalHours)
    $elapsedHours = $windowHours - $remainingHours
    if ($elapsedHours -le 0) {
        return @{ AbsHours = 0; AbsPct = 0; IsUnder = $true }
    }
    $expectedUtilization = ($elapsedHours / $windowHours) * 100
    $deltaUtilization = $expectedUtilization - $utilization  # >0 = under (good)
    return @{
        AbsHours = [math]::Abs(($deltaUtilization / 100) * $windowHours)
        AbsPct = [math]::Abs($deltaUtilization)
        IsUnder = ($deltaUtilization -gt 0)
    }
}

# Render a budget delta (or "" when under ~1%, matching the bash version).
# ASCII markers: "v" = under budget (green), "^" = over budget (red).
function Format-BudgetDelta {
    param(
        [hashtable]$budgetInfo,
        [string]$mode
    )
    if ($budgetInfo.AbsPct -lt 1) { return "" }
    $hours = $budgetInfo.AbsHours
    if ($mode -eq "session") {
        $h = [int][math]::Floor($hours)
        $mins = [int][math]::Floor(($hours - $h) * 60)
        $timeStr = "${h}h$($mins.ToString('D2'))"
    } else {
        $days = [int][math]::Floor($hours / 24)
        $h = [int][math]::Floor($hours - ($days * 24))
        $timeStr = "${days}d$($h.ToString('D2'))"
    }
    if ($budgetInfo.IsUnder) {
        return " ${green}v${timeStr}${reset}"
    } else {
        return " ${red}^${timeStr}${reset}"
    }
}

# Fetch usage from the OAuth usage API, cached 1 min.
function Get-UsageData {
    if (-not $env:USERPROFILE) { return $null }  # no home dir -> skip usage segment
    $cachePath = Join-Path $env:USERPROFILE ".claude\statusline-cache.json"
    $cacheTTL = 60

    if (Test-Path $cachePath) {
        try {
            $cache = Get-Content $cachePath -Raw | ConvertFrom-Json
            $cacheTime = [DateTime]::Parse($cache.timestamp)
            if (([DateTime]::UtcNow - $cacheTime).TotalSeconds -lt $cacheTTL) {
                return $cache.data
            }
        } catch { }
    }

    $credsPath = Join-Path $env:USERPROFILE ".claude\.credentials.json"
    if (-not (Test-Path $credsPath)) { return $null }

    try {
        $creds = Get-Content $credsPath -Raw | ConvertFrom-Json
        $token = $creds.claudeAiOauth.accessToken
        if (-not $token) { return $null }

        $expiresAt = $creds.claudeAiOauth.expiresAt
        if ($expiresAt) {
            $expiresAtDate = [DateTimeOffset]::FromUnixTimeMilliseconds($expiresAt).UtcDateTime
            if ([DateTime]::UtcNow -gt $expiresAtDate) { return $null }
        }

        $response = Invoke-RestMethod -Uri "https://api.anthropic.com/api/oauth/usage" -Headers @{
            "Authorization" = "Bearer $token"
            "anthropic-beta" = "oauth-2025-04-20"
        } -TimeoutSec 5

        $cacheData = @{ timestamp = [DateTime]::UtcNow.ToString("o"); data = $response }
        $cacheData | ConvertTo-Json -Depth 10 | Out-File $cachePath -Encoding utf8
        return $response
    } catch {
        return $null
    }
}

# === Build usage limits string (mirrors the bash join: session/weekly[/oOpus][ $extra]) ===
$usageStr = ""
$usage = Get-UsageData

if ($usage) {
    # Session (5-hour)
    if ($usage.five_hour -and $usage.five_hour.utilization -ne $null) {
        $u = [int]$usage.five_hour.utilization
        $reset = [DateTime]::Parse($usage.five_hour.resets_at).ToUniversalTime()
        $c = Get-LimitColor $u
        $t = Format-TimeRemaining $reset "session"
        $d = Format-BudgetDelta (Get-BudgetDelta $u $reset 5) "session"
        $usageStr = "${c}${u}%${reset}${dim} ${t}${reset}${d}"
    }

    # Weekly (7-day, all models)
    if ($usage.seven_day -and $usage.seven_day.utilization -ne $null) {
        $u = [int]$usage.seven_day.utilization
        $reset = [DateTime]::Parse($usage.seven_day.resets_at).ToUniversalTime()
        $c = Get-LimitColor $u
        $t = Format-TimeRemaining $reset "weekly"
        $d = Format-BudgetDelta (Get-BudgetDelta $u $reset 168) "weekly"
        $seg = "${c}${u}%${reset}${dim} ${t}${reset}${d}"
        if ($usageStr) { $usageStr = "${usageStr}${dim}/${reset}${seg}" } else { $usageStr = $seg }
    }

    # Weekly Opus bucket (separate Opus-only cap; only present on some plans). Prefix "o".
    if ($usage.seven_day_opus -and $usage.seven_day_opus.utilization -ne $null) {
        $u = [int]$usage.seven_day_opus.utilization
        $reset = [DateTime]::Parse($usage.seven_day_opus.resets_at).ToUniversalTime()
        $c = Get-LimitColor $u
        $t = Format-TimeRemaining $reset "weekly"
        $d = Format-BudgetDelta (Get-BudgetDelta $u $reset 168) "weekly"
        $seg = "${c}${u}%${reset}${dim} ${t}${reset}${d}"
        if ($usageStr) { $usageStr = "${usageStr}${dim}/o${reset}${seg}" } else { $usageStr = "${dim}o${reset}${seg}" }
    }

    # Extra-usage credits (pay-as-you-go): $used/limit, colored by % of cap consumed.
    if ($usage.extra_usage -and $usage.extra_usage.is_enabled -and $usage.extra_usage.used_credits -ne $null -and $usage.extra_usage.monthly_limit -ne $null) {
        $ePct = if ($usage.extra_usage.utilization) { [int]$usage.extra_usage.utilization } else { 0 }
        $eColor = Get-LimitColor $ePct
        $eDisp = "$(Format-Usd $usage.extra_usage.used_credits)/$(Format-Usd $usage.extra_usage.monthly_limit)"
        if ($usageStr) { $usageStr = "${usageStr}${dim} `$${reset}${eColor}${eDisp}${reset}" } else { $usageStr = "${dim}`$${reset}${eColor}${eDisp}${reset}" }
    }
}

# === Git information ===
$gitBranch = ""
$gitStatus = ""

try {
    Push-Location $currentDir
    $isGitRepo = git rev-parse --git-dir 2>$null

    if ($isGitRepo) {
        $localBranch = git branch --show-current 2>$null

        if ($localBranch) {
            $gitBranch = $localBranch
            $remoteBranch = git rev-parse --abbrev-ref --symbolic-full-name '@{u}' 2>$null

            if ($remoteBranch) {
                $remoteBranchName = ($remoteBranch -split '/', 2)[1]

                if ($localBranch -ne $remoteBranchName) {
                    $gitStatus = " ${yellow}mismatch${reset}"
                } else {
                    $ahead = git rev-list --count '@{u}..HEAD' 2>$null
                    $behind = git rev-list --count 'HEAD..@{u}' 2>$null
                    if (-not $ahead) { $ahead = 0 }
                    if (-not $behind) { $behind = 0 }
                    $ahead = [int]$ahead
                    $behind = [int]$behind

                    if ($ahead -eq 0 -and $behind -eq 0) {
                        $gitStatus = " ${green}synced${reset}"
                    } elseif ($ahead -gt 0 -and $behind -eq 0) {
                        $gitStatus = " ${green}synced${reset} ${yellow}+${ahead}${reset}"
                    } elseif ($ahead -eq 0 -and $behind -gt 0) {
                        $gitStatus = " ${green}synced${reset} ${yellow}-${behind}${reset}"
                    } else {
                        $gitStatus = " ${green}synced${reset} ${yellow}+${ahead} -${behind}${reset}"
                    }
                }
            } else {
                $gitStatus = " ${yellow}no remote${reset}"
            }
        } else {
            $gitBranch = "${yellow}detached${reset}"
        }
    } else {
        $gitBranch = "${gray}no git${reset}"
    }
} catch {
    $gitBranch = "${gray}no git${reset}"
} finally {
    Pop-Location
}

# === Build output: ctx% tokens | model | usage | folder | branch status ===
$output = "${ctxColor}${usedPct}%${reset}${dim} ${tokenDisplay}${reset}"
if ($modelName) { $output = "${output} ${dim}|${reset} ${magenta}${modelName}${reset}" }
if ($usageStr) { $output = "${output} ${dim}|${reset} ${usageStr}" }
$output = "${output} ${dim}|${reset} ${cyan}${folderName}${reset} ${dim}|${reset} ${gitBranch}${gitStatus}"

Write-Output $output
