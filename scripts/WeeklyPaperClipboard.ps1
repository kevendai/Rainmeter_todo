param(
    [string]$TaskJsonPath,
    [string]$RainmeterRoot = ''
)

$ErrorActionPreference = 'Stop'

$Text = @{
    PaperLabel = -join @([char]0x8BBA, [char]0x6587)
    ReadLabel = -join @([char]0x5DF2, [char]0x8BFB)
    OriginalTitleLabel = -join @([char]0x8BBA, [char]0x6587, [char]0x539F, [char]0x6807, [char]0x9898)
    WeeklyPrefix = -join @([char]0x672C, [char]0x5468, [char]0x9605, [char]0x8BFB, [char]0x4E86, [char]0x8BBA, [char]0x6587)
    NoWeeklyPapers = -join @([char]0x672C, [char]0x5468, [char]0x6CA1, [char]0x6709, [char]0x8BB0, [char]0x5F55, [char]0x5DF2, [char]0x8BFB, [char]0x8BBA, [char]0x6587)
    LeftQuote = [string][char]0x300A
    RightQuote = [string][char]0x300B
    Separator = [string][char]0x3001
}

function Get-ExistingTaskPath {
    param([string]$PreferredPath)

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($PreferredPath)) {
        $candidates += $PreferredPath
    }

    $scriptDir = Split-Path -Parent $PSCommandPath
    $projectRoot = Split-Path $scriptDir -Parent
    $rainmeterTaskPath = $null
    $pathHelper = Join-Path $scriptDir 'Rainmeter-Paths.ps1'
    if (Test-Path -LiteralPath $pathHelper) {
        try { . $pathHelper; $rainmeterTaskPath = Join-Path (Resolve-RainmeterEnvironment -RainmeterRoot $RainmeterRoot).SkinsRoot 'Todo\@Resources\tasks.json' } catch {}
    }
    $candidates += @(
        $rainmeterTaskPath,
        (Join-Path $projectRoot 'skins\Todo\@Resources\tasks.json'),
        (Join-Path $projectRoot 'skins\Todo\@Resources\task.json')
    )

    foreach ($path in $candidates) {
        if (-not [string]::IsNullOrWhiteSpace($path) -and (Test-Path -LiteralPath $path -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $path).Path
        }
    }

    throw "Could not find task.json/tasks.json. Pass -TaskJsonPath or -RainmeterRoot explicitly."
}

function Get-LabelList {
    param($Task)

    if ($null -eq $Task.labels) {
        return @()
    }

    if ($Task.labels -is [System.Array]) {
        return @($Task.labels | ForEach-Object { [string]$_ })
    }

    return @([string]$Task.labels)
}

function Get-OriginalPaperTitle {
    param($Task)

    $note = [string]$Task.note
    if ([string]::IsNullOrWhiteSpace($note)) {
        return ''
    }

    $pattern = '(?m)^\s*' + [regex]::Escape($Text.OriginalTitleLabel) + '\s*[:' + [char]0xFF1A + ']\s*(.+?)\s*$'
    $match = [regex]::Match($note, $pattern)
    if ($match.Success) {
        return $match.Groups[1].Value.Trim()
    }

    $match = [regex]::Match($note, '(?m)^\s*(?:Original\s+Title|Paper\s+Title|Title)\s*[:：]\s*(.+?)\s*$', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($match.Success) {
        return $match.Groups[1].Value.Trim()
    }

    return ''
}

$resolvedPath = Get-ExistingTaskPath -PreferredPath $TaskJsonPath
$jsonText = [System.IO.File]::ReadAllText($resolvedPath, [System.Text.Encoding]::UTF8)
$state = $jsonText | ConvertFrom-Json

$today = Get-Date
$daysSinceMonday = (([int]$today.DayOfWeek + 6) % 7)
$monday = $today.Date.AddDays(-$daysSinceMonday)
$saturday = $monday.AddDays(5)

$titles = @(
    @($state.tasks) |
        Where-Object {
            $labels = Get-LabelList $_
            $completedAt = $null
            $hasCompletedAt = $false
            if (-not [string]::IsNullOrWhiteSpace([string]$_.completed_at)) {
                try {
                    $completedAt = [datetime]::Parse([string]$_.completed_at)
                    $hasCompletedAt = $true
                } catch {
                    $hasCompletedAt = $false
                }
            }

            $labels -contains $Text.PaperLabel -and
            $labels -contains $Text.ReadLabel -and
            $hasCompletedAt -and
            $completedAt -ge $monday -and
            $completedAt -lt $saturday
        } |
        Sort-Object { [datetime]$_.completed_at } |
        ForEach-Object { Get-OriginalPaperTitle $_ } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -Unique
)

if ($titles.Count -gt 0) {
    $message = $Text.WeeklyPrefix + (($titles | ForEach-Object { $Text.LeftQuote + $_ + $Text.RightQuote }) -join $Text.Separator)
} else {
    $message = $Text.NoWeeklyPapers
}

Set-Clipboard -Value $message
Write-Host $message
Write-Host "Copied to clipboard. Source: $resolvedPath"
