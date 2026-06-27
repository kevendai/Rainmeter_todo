param(
    [string]$Version = '1.1.5',
    [string]$OutputRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) 'release-build'),
    [string]$RainmeterInstallerUrl = 'https://github.com/rainmeter/rainmeter/releases/download/v4.5.26.3894/Rainmeter-4.5.26.exe'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path $PSScriptRoot -Parent
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
}

function Convert-IniToUtf16 {
    param([string]$Path)
    $text = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($Path, $text, [Text.UnicodeEncoding]::new($false, $true))
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
$sourceSkins = Join-Path $packageRoot 'Skins'
if ([string]::IsNullOrWhiteSpace($RainmeterRoot)) {
    $RainmeterRoot = Read-Host 'Enter Rainmeter portable directory containing Rainmeter.exe'
}
$RainmeterRoot = $RainmeterRoot.Trim().Trim('"')
if ([string]::IsNullOrWhiteSpace($RainmeterRoot)) {
    throw 'RainmeterRoot cannot be empty.'
}
$rainmeterExe = Join-Path $RainmeterRoot 'Rainmeter.exe'
if (-not (Test-Path -LiteralPath $rainmeterExe)) {
    throw "Rainmeter.exe not found in '$RainmeterRoot'. Enter the portable install directory that contains Rainmeter.exe."
}
if ($WaitForProcessId -gt 0) {
    try { Wait-Process -Id $WaitForProcessId -Timeout 30 -ErrorAction SilentlyContinue } catch {}
}

foreach ($skin in @('Todo', 'Calendar')) {
    $source = Join-Path $sourceSkins $skin
    $target = Join-Path $RainmeterRoot ('Skins\' + $skin)
    New-Item -ItemType Directory -Path $target -Force | Out-Null

    $preserved = @{}
    foreach ($name in @('tasks.json','Generated.inc','calendar-cache.json','calendar-state.json','caldav.secret','translation.secret','paper-sync.secret')) {
        $path = Join-Path $target ('@Resources\' + $name)
        if (Test-Path -LiteralPath $path) { $preserved[$name] = [IO.File]::ReadAllBytes($path) }
    }

    Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force

    foreach ($name in $preserved.Keys) {
        $destination = Join-Path $target ('@Resources\' + $name)
        New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
        [IO.File]::WriteAllBytes($destination, $preserved[$name])
    }
}

$running = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $rainmeterExe })
if ($running.Count -gt 0) {
    & $rainmeterExe '!Quit'
    Start-Sleep -Milliseconds 1200
    $remaining = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $rainmeterExe })
    foreach ($process in $remaining) {
        try { $process.Kill(); $process.WaitForExit(3000) } catch {}
    }
}
Start-Process -FilePath $rainmeterExe | Out-Null
Start-Sleep -Milliseconds 1200
& $rainmeterExe '!RefreshApp'
if ($Activate) {
    Start-Sleep -Milliseconds 800
    & $rainmeterExe '!ActivateConfig' 'Todo' 'Todo.ini'
    & $rainmeterExe '!ActivateConfig' 'Calendar' 'Calendar.ini'
    Start-Sleep -Milliseconds 800
    & $rainmeterExe '!SetWindowPosition' '100%' '0%' '100%' '0%' 'Todo'
}
Write-Host "Installed skins to $RainmeterRoot\Skins"
'@
    Set-Content -LiteralPath $Path -Value $content -Encoding UTF8
}
function New-Package {
    param(
        [string]$Flavor,
        [string]$DisplayName,
        [switch]$NoPaperFeatures
    )

    $packageName = "rainmeter-desktop-widgets-$Flavor-$Version"
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

    if ($NoPaperFeatures) {
        $todoIni = Join-Path $todoRoot 'Todo.ini'
        $ini = [IO.File]::ReadAllText($todoIni, [Text.UTF8Encoding]::new($false))
        $ini = $ini -replace '(?m)^Information=.*$', 'Information=Editable todo board without paper feed integration.'
        $ini = $ini -replace '(?m)^LeftMouseUpAction=\["#@#TodoHost\.exe" "SyncArxiv" "Force"\]$', 'Hidden=1'
        $ini = $ini -replace '(?m)^MouseOverAction=\[!SetOption Sync FontColor.*$', 'Hidden=1'
        $ini = $ini -replace '(?m)^MouseLeaveAction=\[!SetOption Sync FontColor.*$', 'Hidden=1'
        $ini = $ini -replace '(?m)^LeftMouseUpAction=.*"SyncArxiv".*$', 'LeftMouseUpAction=[]'
        [IO.File]::WriteAllText($todoIni, $ini, [Text.UTF8Encoding]::new($false))
    }

    Convert-IniToUtf16 (Join-Path $todoRoot 'Todo.ini')
    Convert-IniToUtf16 (Join-Path $calendarRoot 'Calendar.ini')

    $backendRoot = Join-Path $projectRoot 'backend'
    $common = Join-Path $backendRoot 'Common.cs'
    $todoSource = Join-Path $backendRoot 'TodoApp.cs'
    $calendarSource = Join-Path $backendRoot 'CalendarApp.cs'
    $todoExe = Join-Path $todoRoot '@Resources\TodoHost.exe'
    $calendarExe = Join-Path $calendarRoot '@Resources\CalendarHost.exe'
    $todoCompileArgs = @('/nologo','/target:winexe','/optimize+','/r:System.Web.Extensions.dll','/r:System.Windows.Forms.dll','/r:System.Drawing.dll','/r:System.Security.dll',"/out:$todoExe",$common,$todoSource)
    if ($NoPaperFeatures) { $todoCompileArgs = @('/define:NO_PAPER_FEATURES') + $todoCompileArgs }
    & $csc @todoCompileArgs
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $todoExe)) { throw "Failed to build TodoHost.exe for $DisplayName" }
    & $csc /nologo /target:winexe /optimize+ /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll "/out:$calendarExe" $common $calendarSource
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $calendarExe)) { throw "Failed to build CalendarHost.exe for $DisplayName" }

    Copy-Item -LiteralPath $installer -Destination (Join-Path $packageRoot 'Rainmeter-4.5.26.exe') -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\RELEASE-DEPLOY.md') -Destination (Join-Path $packageRoot 'DEPLOY.md') -Force
    New-InstallScript (Join-Path $packageRoot 'Install-Skins.ps1')

    $manifest = [ordered]@{
        name = $DisplayName
        version = $Version
        rainmeter = '4.5.26.3894'
        paper_features = -not $NoPaperFeatures
        excludes = @('translation.secret','paper-sync.secret','caldav.secret','tasks.json','calendar-cache.json','calendar-state.json')
    } | ConvertTo-Json -Depth 4
    Set-Content -LiteralPath (Join-Path $packageRoot 'manifest.json') -Value $manifest -Encoding UTF8

    $zip = Join-Path $OutputRoot ($packageName + '.zip')
    Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zip -Force
    Write-Host "Created $zip"
}

New-Package -Flavor 'full' -DisplayName 'Rainmeter Desktop Widgets Full'
New-Package -Flavor 'lite' -DisplayName 'Rainmeter Desktop Widgets Lite' -NoPaperFeatures
