# Claude Code Status Line Script for Windows
# Displays: Context usage | Rate limits | Folder name | Git branch & status
# Compatible with Windows PowerShell 5.1+

# Read JSON input from stdin
$jsonInput = [Console]::In.ReadToEnd()
$data = $jsonInput | ConvertFrom-Json

# Extract context window data with fallbacks for PS 5.x compatibility
$totalInput = if ($data.context_window.total_input_tokens) { $data.context_window.total_input_tokens } else { 0 }
$totalOutput = if ($data.context_window.total_output_tokens) { $data.context_window.total_output_tokens } else { 0 }
$contextSize = if ($data.context_window.context_window_size) { $data.context_window.context_window_size } else { 200000 }
$usedPct = if ($data.context_window.used_percentage) { [int]$data.context_window.used_percentage } else { 0 }

# Calculate total tokens
$totalTokens = $totalInput + $totalOutput
if ($totalTokens -ge 1000) {
    $tokenDisplay = "{0}K" -f [math]::Floor($totalTokens / 1000)
} else {
    $tokenDisplay = "$totalTokens"
}

# Extract workspace info
$currentDir = $data.workspace.current_dir
$folderName = Split-Path -Leaf $currentDir

# ANSI color codes
$esc = [char]27
$green = "$esc[32m"
$yellow = "$esc[33m"
$orange = "$esc[38;5;208m"
$red = "$esc[31m"
$cyan = "$esc[36m"
$dim = "$esc[2m"
$gray = "$esc[90m"
$reset = "$esc[0m"

# Determine color based on used percentage (context)
# green < 50%, yellow 50-74%, orange 75%+
if ($usedPct -lt 50) {
    $ctxColor = $green
} elseif ($usedPct -lt 75) {
    $ctxColor = $yellow
} else {
    $ctxColor = $orange
}

# Function to get color based on utilization (rate limits)
# gray < 50%, yellow 50-79%, red 80%+
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

# Function to format time remaining
function Format-TimeRemaining {
    param(
        [DateTime]$resetTime,
        [string]$mode  # "session" for hours:mins, "weekly" for days:hours
    )

    $now = [DateTime]::UtcNow
    $diff = $resetTime - $now

    if ($diff.TotalSeconds -le 0) {
        return "0h00"
    }

    if ($mode -eq "session") {
        $hours = [math]::Floor($diff.TotalHours)
        $mins = $diff.Minutes
        return "{0}h{1:D2}" -f $hours, $mins
    } else {
        $days = [math]::Floor($diff.TotalDays)
        $hours = $diff.Hours
        return "{0}d{1:D2}" -f $days, $hours
    }
}

# Function to calculate budget delta
function Get-BudgetDelta {
    param(
        [double]$utilization,
        [DateTime]$resetTime,
        [double]$windowHours  # 5 for session, 168 for weekly
    )

    $now = [DateTime]::UtcNow
    $diff = $resetTime - $now
    $remainingHours = [math]::Max(0, $diff.TotalHours)
    $elapsedHours = $windowHours - $remainingHours

    if ($elapsedHours -le 0) {
        return @{ Delta = 0; IsUnder = $true }
    }

    # Expected usage based on linear consumption
    $expectedUtilization = ($elapsedHours / $windowHours) * 100
    $deltaUtilization = $expectedUtilization - $utilization

    # Convert delta to time
    $deltaHours = ($deltaUtilization / 100) * $windowHours

    return @{
        Delta = [math]::Abs($deltaHours)
        IsUnder = ($deltaUtilization -gt 0)
    }
}

# Function to format budget delta display
function Format-BudgetDelta {
    param(
        [hashtable]$budgetInfo,
        [string]$mode  # "session" or "weekly"
    )

    $hours = $budgetInfo.Delta

    if ($mode -eq "session") {
        $h = [int][math]::Floor($hours)
        $m = [int][math]::Floor(($hours - $h) * 60)
        $timeStr = "${h}h$($m.ToString('D2'))"
    } else {
        $days = [int][math]::Floor($hours / 24)
        $h = [int][math]::Floor($hours - ($days * 24))
        $timeStr = "${days}d$($h.ToString('D2'))"
    }

    # Use Unicode triangles (small filled triangles)
    $downArrow = [char]0x25BE  # Small down-pointing triangle
    $upArrow = [char]0x25B4    # Small up-pointing triangle

    if ($budgetInfo.IsUnder) {
        return "${green}${downArrow}${timeStr}${reset}"
    } else {
        return "${red}${upArrow}${timeStr}${reset}"
    }
}

# Function to fetch usage from API with caching
function Get-UsageData {
    $cachePath = Join-Path $env:USERPROFILE ".claude\statusline-cache.json"
    $cacheTTL = 60  # 1 minute TTL

    # Check cache
    if (Test-Path $cachePath) {
        try {
            $cache = Get-Content $cachePath -Raw | ConvertFrom-Json
            $cacheTime = [DateTime]::Parse($cache.timestamp)
            $age = ([DateTime]::UtcNow - $cacheTime).TotalSeconds

            if ($age -lt $cacheTTL) {
                return $cache.data
            }
        } catch {
            # Cache invalid, continue to fetch
        }
    }

    # Read credentials
    $credsPath = Join-Path $env:USERPROFILE ".claude\.credentials.json"
    if (-not (Test-Path $credsPath)) {
        return $null
    }

    try {
        $creds = Get-Content $credsPath -Raw | ConvertFrom-Json
        $token = $creds.claudeAiOauth.accessToken

        if (-not $token) {
            return $null
        }

        # Check token expiration
        $expiresAt = $creds.claudeAiOauth.expiresAt
        if ($expiresAt) {
            $expiresAtDate = [DateTimeOffset]::FromUnixTimeMilliseconds($expiresAt).UtcDateTime
            if ([DateTime]::UtcNow -gt $expiresAtDate) {
                return $null  # Token expired
            }
        }

        # Fetch from API
        $response = Invoke-RestMethod -Uri "https://api.anthropic.com/api/oauth/usage" -Headers @{
            "Authorization" = "Bearer $token"
            "anthropic-beta" = "oauth-2025-04-20"
        } -TimeoutSec 5

        # Save to cache
        $cacheData = @{
            timestamp = [DateTime]::UtcNow.ToString("o")
            data = $response
        }
        $cacheData | ConvertTo-Json -Depth 10 | Out-File $cachePath -Encoding utf8

        return $response
    } catch {
        return $null
    }
}

# Build rate limit display
$rateLimitDisplay = ""
$usage = Get-UsageData

if ($usage) {
    $parts = @()

    # Five hour (session) limit
    if ($usage.five_hour) {
        $fiveHourUtil = $usage.five_hour.utilization
        $fiveHourReset = [DateTime]::Parse($usage.five_hour.resets_at).ToUniversalTime()
        $fiveHourColor = Get-LimitColor $fiveHourUtil
        $fiveHourTime = Format-TimeRemaining $fiveHourReset "session"
        $fiveHourBudget = Get-BudgetDelta $fiveHourUtil $fiveHourReset 5
        $fiveHourDelta = Format-BudgetDelta $fiveHourBudget "session"

        $parts += "${fiveHourColor}$([math]::Floor($fiveHourUtil))%${reset} ${fiveHourTime} ${fiveHourDelta}"
    }

    # Seven day (weekly) limit
    if ($usage.seven_day) {
        $sevenDayUtil = $usage.seven_day.utilization
        $sevenDayReset = [DateTime]::Parse($usage.seven_day.resets_at).ToUniversalTime()
        $sevenDayColor = Get-LimitColor $sevenDayUtil
        $sevenDayTime = Format-TimeRemaining $sevenDayReset "weekly"
        $sevenDayBudget = Get-BudgetDelta $sevenDayUtil $sevenDayReset 168
        $sevenDayDelta = Format-BudgetDelta $sevenDayBudget "weekly"

        $parts += "${sevenDayColor}$([math]::Floor($sevenDayUtil))%${reset} ${sevenDayTime} ${sevenDayDelta}"
    }

    if ($parts.Count -gt 0) {
        $rateLimitDisplay = " | " + ($parts -join "/")
    }
}

# Git information
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

# Format output
# Context: used% tokens | Rate limits | folder | git
$output = "${ctxColor}${usedPct}%${reset} ${dim}${tokenDisplay}${reset}${rateLimitDisplay} ${dim}|${reset} ${cyan}${folderName}${reset} ${dim}|${reset} ${green}${gitBranch}${reset}${gitStatus}"
Write-Output $output