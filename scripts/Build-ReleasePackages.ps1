param(
    [string]$Version = '',
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-build'),
    [string]$RainmeterInstallerUrl = 'https://github.com/rainmeter/rainmeter/releases/download/v4.5.26.3894/Rainmeter-4.5.26.exe'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path $PSScriptRoot -Parent
$UpdaterVersion = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'updater-version.txt'), [Text.UTF8Encoding]::new($false)).Trim()
if ($UpdaterVersion -notmatch '^\d+\.\d+$') { throw 'Updater version must use major.minor format.' }
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION'), [Text.UTF8Encoding]::new($false)).Trim()
}
$cacheRoot = Join-Path $projectRoot '.release-cache'
$installer = Join-Path $cacheRoot 'Rainmeter-4.5.26.exe'
New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $installer)) {
    Write-Host "Downloading Rainmeter installer..."
    Invoke-WebRequest -Uri $RainmeterInstallerUrl -OutFile $installer
}

if (Test-Path -LiteralPath $OutputRoot) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$updaterBuild = Join-Path $OutputRoot '.updater-build'
& (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Updater -OutputDirectory $updaterBuild | Out-Null

function Copy-Tree {
    param([string]$Source, [string]$Destination)
    $excludedNames = @(
        'translation.secret',
        'paper-sync.secret',
        'caldav.secret',
        'PluginValues.inc',
        'ui-scale.txt',
        'ui-window-scale.txt',
        'ui-theme.txt',
        'tasks.json',
        'calendar-cache.json',
        'calendar-state.json',
        '.refresh-guard'
    )
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $Source -Recurse -Force) {
        if ($excludedNames -contains $item.Name) { continue }
        if ($item.Name -like '*.tmp' -or $item.Name -like '*.log' -or $item.Name -like '*.build.exe' -or $item.Name -like '*.pdb') { continue }
        $relative = $item.FullName.Substring($Source.Length).TrimStart('\', '/')
        if ($relative -match '(^|[\\/])PaperCache([\\/]|$)') { continue }
        $target = Join-Path $Destination $relative
        if ($item.PSIsContainer) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
        } else {
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $item.FullName -Destination $target -Force
        }
    }
}

function Remove-ReleaseSecrets {
    param([string]$Root)
    $names = @(
        'translation.secret',
        'paper-sync.secret',
        'caldav.secret',
        'ui-scale.txt',
        'ui-window-scale.txt',
        'ui-theme.txt',
        'tasks.json',
        'calendar-cache.json',
        'calendar-state.json',
        '.refresh-guard'
    )
    foreach ($name in $names) {
        foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -Force -Filter $name -ErrorAction SilentlyContinue) {
            if ($null -ne $file) { Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue }
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -Force -File -ErrorAction SilentlyContinue) {
        if ($file.Name -like '*.tmp' -or $file.Name -like '*.log' -or $file.Name -like '*.build.exe' -or $file.Name -like '*.pdb') {
            Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($directory in Get-ChildItem -LiteralPath $Root -Recurse -Force -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -eq 'PaperCache' } | Sort-Object FullName -Descending) {
        Remove-Item -LiteralPath $directory.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Convert-IniToUtf16 {
    param([string]$Path)
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($Path, $text, [Text.UnicodeEncoding]::new($false, $true))
}

function Set-SkinVersion {
    param([string]$SkinRoot, [string]$IniName)
    $iniPath = Join-Path $SkinRoot $IniName
    $text = [IO.File]::ReadAllText($iniPath, [Text.UTF8Encoding]::new($false))
    if ($text -match '(?m)^Version=') {
        $text = $text -replace '(?m)^Version=.*$', "Version=$Version"
    } else {
        $text = $text -replace '(?m)^Information=.*$', "`$0`r`nVersion=$Version"
    }
    [IO.File]::WriteAllText($iniPath, $text, [Text.UTF8Encoding]::new($false))
    $resources = Join-Path $SkinRoot '@Resources'
    New-Item -ItemType Directory -Path $resources -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $resources 'app-version.txt'), $Version, [Text.UTF8Encoding]::new($false))
}

function New-UpdaterScript {
    param([string]$Path)
    $target = Split-Path $Path -Parent
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RainmeterDesktopWidgetsUpdater.ps1') -Destination $Path -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'updater-version.txt') -Destination (Join-Path $target 'updater-version.txt') -Force
    Copy-Item -LiteralPath (Join-Path $updaterBuild 'UpdaterHost.exe') -Destination (Join-Path $target 'UpdaterHost.exe') -Force
}

function New-InstallScript {
    param([string]$Path)
$content = @'
param(
    [string]$RainmeterRoot,
    [switch]$Activate,
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = 'Stop'
$packageRoot = $PSScriptRoot
$updater = Join-Path $packageRoot 'Updater\UpdaterHost.exe'
if (-not (Test-Path -LiteralPath $updater)) { throw 'UpdaterHost.exe not found in package.' }
$args = @('-Mode','InstallPackage','-PackageRoot',$packageRoot)
if (-not [string]::IsNullOrWhiteSpace($RainmeterRoot)) { $args += @('-RainmeterRoot',$RainmeterRoot) }
if ($Activate) { $args += '-Activate' }
$escaped = @($args | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } })
$process = Start-Process -FilePath $updater -ArgumentList $escaped -Wait -PassThru
exit $process.ExitCode
'@
    Set-Content -LiteralPath $Path -Value $content -Encoding UTF8
}

function Add-RmskinFooter {
    param([string]$Path)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $archiveSize = [Int64]$stream.Length
        $stream.Position = $archiveSize
        $writer = [IO.BinaryWriter]::new($stream, [Text.Encoding]::ASCII, $true)
        try {
            $writer.Write($archiveSize)
            $writer.Write([byte]0)
            $writer.Write([Text.Encoding]::ASCII.GetBytes("RMSKIN`0"))
            $writer.Flush()
        }
        finally { $writer.Dispose() }
    }
    finally { $stream.Dispose() }

    $stream = [IO.File]::OpenRead($Path)
    try {
        if ($stream.Length -lt 16) { throw 'Generated rmskin is too small.' }
        $stream.Position = $stream.Length - 16
        $reader = [IO.BinaryReader]::new($stream, [Text.Encoding]::ASCII, $true)
        try {
            $declaredSize = $reader.ReadInt64()
            $flags = $reader.ReadByte()
            $key = [Text.Encoding]::ASCII.GetString($reader.ReadBytes(7))
            if ($declaredSize -ne $stream.Length - 16 -or $flags -ne 0 -or $key -ne "RMSKIN`0") {
                throw 'Generated rmskin footer validation failed.'
            }
        }
        finally { $reader.Dispose() }
    }
    finally { $stream.Dispose() }
}

function New-RmskinPackage {
    param(
        [string]$DisplayName,
        [string]$PackageRoot
    )

    $rmskinRoot = Join-Path $OutputRoot ("rmskin-standard-$Version")
    if (Test-Path -LiteralPath $rmskinRoot) { Remove-Item -LiteralPath $rmskinRoot -Recurse -Force }
    New-Item -ItemType Directory -Path $rmskinRoot -Force | Out-Null
    Copy-Item -Path (Join-Path $PackageRoot 'Skins') -Destination (Join-Path $rmskinRoot 'Skins') -Recurse -Force

    $targetUpdater = Join-Path $rmskinRoot 'Skins\Todo\@Resources\Updater'
    New-UpdaterScript (Join-Path $targetUpdater 'RainmeterDesktopWidgetsUpdater.ps1')
    if (-not (Test-Path -LiteralPath (Join-Path $targetUpdater 'UpdaterHost.exe'))) { throw 'UpdaterHost.exe was not staged in the rmskin package.' }

    $rmskinIni = @"
[rmskin]
Name=$DisplayName
Author=Rainmeter Desktop Widgets
Version=$Version
LoadType=Skin
Load=Todo\Todo.ini|Calendar\Calendar.ini
MinimumRainmeter=4.5.26
MinimumWindows=10.0
"@
    [IO.File]::WriteAllText((Join-Path $rmskinRoot 'RMSKIN.ini'), ($rmskinIni.Trim() + "`r`n"), [Text.UTF8Encoding]::new($false))

    $rmskin = Join-Path $OutputRoot ("rainmeter-desktop-widgets-$Version.rmskin")
    $rmskinZip = Join-Path $OutputRoot ("rainmeter-desktop-widgets-$Version.rmskin.zip")
    Compress-Archive -Path (Join-Path $rmskinRoot '*') -DestinationPath $rmskinZip -Force
    if (Test-Path -LiteralPath $rmskin) { Remove-Item -LiteralPath $rmskin -Force }
    Move-Item -LiteralPath $rmskinZip -Destination $rmskin -Force
    Add-RmskinFooter $rmskin
    Write-Host "Created $rmskin"
}

function New-Package {
    param([string]$DisplayName)

    $packageName = "rainmeter-desktop-widgets-$Version"
    $packageRoot = Join-Path $OutputRoot $packageName
    $skinsRoot = Join-Path $packageRoot 'Skins'
    $todoRoot = Join-Path $skinsRoot 'Todo'
    $calendarRoot = Join-Path $skinsRoot 'Calendar'
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

    Copy-Tree (Join-Path $projectRoot 'skins\Todo') $todoRoot
    Copy-Tree (Join-Path $projectRoot 'skins\Calendar') $calendarRoot
    # PluginValues.inc is user/runtime data. Never copy the working-tree file
    # into a release; create a known-empty bridge for first startup instead.
    [IO.File]::WriteAllText(
        (Join-Path $todoRoot '@Resources\PluginValues.inc'),
        "[Variables]`r`n",
        [Text.UnicodeEncoding]::new($false, $true)
    )
    Remove-ReleaseSecrets $skinsRoot

    & (Join-Path $PSScriptRoot 'New-RefreshArrow.ps1') -OutputDirectory (Join-Path $todoRoot '@Resources\RefreshFrames')
    & (Join-Path $PSScriptRoot 'New-RefreshArrow.ps1') -OutputDirectory (Join-Path $calendarRoot '@Resources\RefreshFrames')

    Set-SkinVersion $todoRoot 'Todo.ini'
    Set-SkinVersion $calendarRoot 'Calendar.ini'
    Convert-IniToUtf16 (Join-Path $todoRoot 'Todo.ini')
    Convert-IniToUtf16 (Join-Path $calendarRoot 'Calendar.ini')

    $todoExe = Join-Path $todoRoot '@Resources\TodoHost.exe'
    $calendarExe = Join-Path $calendarRoot '@Resources\CalendarHost.exe'
    $pluginExe = Join-Path $todoRoot '@Resources\PluginHost.exe'
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Todo -OutputDirectory (Split-Path $todoExe -Parent) | Out-Null
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Calendar -OutputDirectory (Split-Path $calendarExe -Parent) | Out-Null
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Plugin -OutputDirectory (Split-Path $pluginExe -Parent) | Out-Null
    & (Join-Path $PSScriptRoot 'Build-OfficialPlugins.ps1') -OutputDirectory (Join-Path $todoRoot '@Resources\BundledPlugins') -PackageDirectory (Join-Path $todoRoot '@Resources\BundledPluginPackages') -LockPath (Join-Path $todoRoot '@Resources\bundled-plugins.lock.json') | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot 'plugin-registry-template\index-v1.json') -Destination (Join-Path $todoRoot '@Resources\plugin-registry-v1.json') -Force

    Copy-Item -LiteralPath $installer -Destination (Join-Path $packageRoot 'Rainmeter-4.5.26.exe') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\RELEASE-DEPLOY.md') -Destination (Join-Path $packageRoot 'DEPLOY.md') -Force
    $updaterRoot = Join-Path $packageRoot 'Updater'
    New-Item -ItemType Directory -Path $updaterRoot -Force | Out-Null
    New-UpdaterScript (Join-Path $updaterRoot 'RainmeterDesktopWidgetsUpdater.ps1')

    $manifest = [ordered]@{
        name = $DisplayName
        version = $Version
        updater_version = $UpdaterVersion
        rainmeter = '4.5.26.3894'
        paper_features = $true
        paper_features_runtime_switch = $true
        plugin_api = 1
        excludes = @('translation.secret','paper-sync.secret','caldav.secret','ui-scale.txt','ui-window-scale.txt','ui-theme.txt','tasks.json','calendar-cache.json','calendar-state.json','PaperCache','PluginData','PluginLogs','PluginJobs')
    } | ConvertTo-Json -Depth 4
    Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Value $manifest -Encoding UTF8

    $zip = Join-Path $OutputRoot ($packageName + '.zip')
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zip -Force
    Write-Host "Created $zip"
    New-RmskinPackage -DisplayName $DisplayName -PackageRoot $packageRoot
}

New-Package -DisplayName 'Rainmeter Desktop Widgets'

# Legacy raw bootstrap channel -------------------------------------------------
# Pre-2.0 clients select the newest numeric tag and download a compatibility
# archive from raw.githubusercontent.com/<tag>/releases/<tag>/.  Such an archive
# carries no payload at all: it only contains the compatibility updater that
# installs the canonical release package.  Build-LegacyCompatPackages.ps1 owns
# that archive, so a release build can never drift back to the retired two-step
# v1.4.4 bridge or publish a bootstrap that loops back onto itself.
& (Join-Path $PSScriptRoot 'Build-LegacyCompatPackages.ps1') -Version $Version -Repository 'kevendai/Rainmeter_todo'
Get-ChildItem -LiteralPath $OutputRoot -File | Where-Object { $_.Extension -in @('.zip', '.rmskin') } | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $checksumPath = $_.FullName + '.sha256'
    [IO.File]::WriteAllText($checksumPath, "$hash  $($_.Name)`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Created $checksumPath"
}
