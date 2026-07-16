param(
    [string]$Version = '',
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-build'),
    [string]$RainmeterInstallerUrl = 'https://github.com/rainmeter/rainmeter/releases/download/v4.5.26.3894/Rainmeter-4.5.26.exe'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path $PSScriptRoot -Parent
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION'), [Text.UTF8Encoding]::new($false)).Trim()
}
$cacheRoot = Join-Path $projectRoot '.release-cache'
$installer = Join-Path $cacheRoot 'Rainmeter-4.5.26.exe'
$csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw 'C# compiler not found' }

New-Item -ItemType Directory -Path $cacheRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $installer)) {
    Write-Host "Downloading Rainmeter installer..."
    Invoke-WebRequest -Uri $RainmeterInstallerUrl -OutFile $installer
}

if (Test-Path -LiteralPath $OutputRoot) { Remove-Item -LiteralPath $OutputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

function Copy-Tree {
    param([string]$Source, [string]$Destination)
    $excludedNames = @(
        'translation.secret',
        'paper-sync.secret',
        'caldav.secret',
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
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RainmeterDesktopWidgetsUpdater.ps1') -Destination $Path -Force
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
$updater = Join-Path $packageRoot 'Updater\RainmeterDesktopWidgetsUpdater.ps1'
if (-not (Test-Path -LiteralPath $updater)) { throw 'Updater script not found in package.' }
$args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$updater,'-Mode','InstallPackage','-PackageRoot',$packageRoot,'-RainmeterRoot',$RainmeterRoot,'-WaitForProcessId',$WaitForProcessId)
if ($Activate) { $args += '-Activate' }
& powershell @args
'@
    Set-Content -LiteralPath $Path -Value $content -Encoding UTF8
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
    New-Item -ItemType Directory -Path $targetUpdater -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RainmeterDesktopWidgetsUpdater.ps1') -Destination (Join-Path $targetUpdater 'RainmeterDesktopWidgetsUpdater.ps1') -Force

    $rmskinIni = @"
[rmskin]
Name=$DisplayName
Author=Codex
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
    Remove-ReleaseSecrets $skinsRoot

    & (Join-Path $PSScriptRoot 'New-RefreshArrow.ps1') -OutputDirectory (Join-Path $todoRoot '@Resources\RefreshFrames')
    & (Join-Path $PSScriptRoot 'New-RefreshArrow.ps1') -OutputDirectory (Join-Path $calendarRoot '@Resources\RefreshFrames')

    Set-SkinVersion $todoRoot 'Todo.ini'
    Set-SkinVersion $calendarRoot 'Calendar.ini'
    Convert-IniToUtf16 (Join-Path $todoRoot 'Todo.ini')
    Convert-IniToUtf16 (Join-Path $calendarRoot 'Calendar.ini')

    $backendRoot = Join-Path $projectRoot 'backend'
    $common = Join-Path $backendRoot 'Common.cs'
    $todoSources = @(Get-ChildItem -LiteralPath $backendRoot -Filter 'Todo*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
    $calendarSources = @(Get-ChildItem -LiteralPath $backendRoot -Filter 'Calendar*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
    $todoExe = Join-Path $todoRoot '@Resources\TodoHost.exe'
    $calendarExe = Join-Path $calendarRoot '@Resources\CalendarHost.exe'
    $todoCompileArgs = @('/nologo','/target:winexe','/optimize+','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Security.dll',"/out:$todoExe",$common) + $todoSources
    & $csc @todoCompileArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $todoExe)) { throw "Failed to build TodoHost.exe for $DisplayName" }
    & $csc /nologo /target:winexe /optimize+ /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll "/out:$calendarExe" $common $calendarSources
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $calendarExe)) { throw "Failed to build CalendarHost.exe for $DisplayName" }

    Copy-Item -LiteralPath $installer -Destination (Join-Path $packageRoot 'Rainmeter-4.5.26.exe') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\RELEASE-DEPLOY.md') -Destination (Join-Path $packageRoot 'DEPLOY.md') -Force
    $updaterRoot = Join-Path $packageRoot 'Updater'
    New-Item -ItemType Directory -Path $updaterRoot -Force | Out-Null
    New-UpdaterScript (Join-Path $updaterRoot 'RainmeterDesktopWidgetsUpdater.ps1')
    New-InstallScript (Join-Path $packageRoot 'Install-Skins.ps1')

    $manifest = [ordered]@{
        name = $DisplayName
        version = $Version
        updater_version = 1
        rainmeter = '4.5.26.3894'
        paper_features = $true
        paper_features_runtime_switch = $true
        excludes = @('translation.secret','paper-sync.secret','caldav.secret','tasks.json','calendar-cache.json','calendar-state.json','PaperCache')
    } | ConvertTo-Json -Depth 4
    Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Value $manifest -Encoding UTF8

    $zip = Join-Path $OutputRoot ($packageName + '.zip')
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zip -Force
    Write-Host "Created $zip"
    New-RmskinPackage -DisplayName $DisplayName -PackageRoot $packageRoot
}

New-Package -DisplayName 'Rainmeter Desktop Widgets'

$canonicalZip = Join-Path $OutputRoot "rainmeter-desktop-widgets-$Version.zip"
$canonicalRmskin = Join-Path $OutputRoot "rainmeter-desktop-widgets-$Version.rmskin"
foreach ($legacyFlavor in @('full','lite')) {
    Copy-Item -LiteralPath $canonicalZip -Destination (Join-Path $OutputRoot "rainmeter-desktop-widgets-$legacyFlavor-$Version.zip") -Force
    Copy-Item -LiteralPath $canonicalRmskin -Destination (Join-Path $OutputRoot "rainmeter-desktop-widgets-$legacyFlavor-$Version.rmskin") -Force
}
Write-Host 'Created full/lite compatibility aliases from the unified package.'
