param(
    [ValidateSet('CheckAndInstall','InstallPackage','UpdateUpdater')]
    [string]$Mode = 'InstallPackage',
    [string]$Repository = 'kevendai/Rainmeter_todo',
    [string]$CurrentVersion = '',
    [string]$Flavor = 'full',
    [string]$FlavorName = '',
    [string]$PackageRoot,
    [string]$RainmeterRoot,
    [switch]$Activate,
    [switch]$AssumeYes,
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = 'Stop'
$UpdaterVersion = 1
$UserAgent = "RainmeterDesktopWidgetsUpdater/$UpdaterVersion"

function Normalize-Version {
    param([string]$Value)
    $text = if ($null -eq $Value) { '' } else { $Value.Trim() }
    if ($text.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { $text = $text.Substring(1) }
    $match = [regex]::Match($text, '\d+(?:\.\d+){0,3}')
    if ($match.Success) { return $match.Value }
    return $text
}

function Get-VersionParts {
    param([string]$Value)
    $normalized = Normalize-Version $Value
    if ([string]::IsNullOrWhiteSpace($normalized)) { return @(0) }
    return @($normalized.Split('.') | ForEach-Object {
        $part = 0
        if ([int]::TryParse($_, [ref]$part)) { $part } else { 0 }
    })
}

function Compare-VersionText {
    param([string]$Left, [string]$Right)
    $a = Get-VersionParts $Left
    $b = Get-VersionParts $Right
    $count = [Math]::Max($a.Count, $b.Count)
    for ($i = 0; $i -lt $count; $i++) {
        $av = if ($i -lt $a.Count) { $a[$i] } else { 0 }
        $bv = if ($i -lt $b.Count) { $b[$i] } else { 0 }
        if ($av -ne $bv) { return ($av.CompareTo($bv)) }
    }
    return 0
}

function Get-SkinsRoot {
    param([string]$Root)
    $value = if ($null -eq $Root) { '' } else { $Root.Trim().Trim('"') }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = Read-Host 'Enter Rainmeter skin library directory, for example Documents\Rainmeter'
    }
    $value = $value.Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'RainmeterRoot cannot be empty.' }
    $skins = Join-Path $value 'Skins'
    $rainmeter = $value
    if ((Split-Path -Leaf $value) -eq 'Skins') {
        $skins = $value
        $rainmeter = Split-Path $value -Parent
    }
    New-Item -ItemType Directory -Path $skins -Force | Out-Null
    return [pscustomobject]@{ RainmeterRoot = $rainmeter; SkinsRoot = $skins }
}

function Find-RainmeterExe {
    param([string]$Root)
    $rainmeterExe = Join-Path $Root 'Rainmeter.exe'
    if (Test-Path -LiteralPath $rainmeterExe) { return $rainmeterExe }
    $runningRainmeter = Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path } | Select-Object -First 1
    if ($null -ne $runningRainmeter) { return $runningRainmeter.Path }
    foreach ($key in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Rainmeter.exe',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\Rainmeter.exe'
    )) {
        $appPath = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($null -ne $appPath -and -not [string]::IsNullOrWhiteSpace($appPath.'(default)')) { return $appPath.'(default)' }
    }
    return $rainmeterExe
}

function Install-UpdaterFiles {
    param([string]$SourcePackageRoot, [string]$TargetSkinsRoot)
    $sourceUpdater = Join-Path $SourcePackageRoot 'Updater'
    if (-not (Test-Path -LiteralPath $sourceUpdater)) { return }
    $targetUpdater = Join-Path $TargetSkinsRoot 'Todo\@Resources\Updater'
    New-Item -ItemType Directory -Path $targetUpdater -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceUpdater '*') -Destination $targetUpdater -Recurse -Force
    Get-ChildItem -LiteralPath $targetUpdater -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
}

function Install-Package {
    param([string]$SourcePackageRoot, [string]$Root, [switch]$ShouldActivate, [int]$WaitPid)
    if ([string]::IsNullOrWhiteSpace($SourcePackageRoot)) {
        $SourcePackageRoot = Split-Path $PSScriptRoot -Parent
    }
    $SourcePackageRoot = (Resolve-Path -LiteralPath $SourcePackageRoot).Path
    $sourceSkins = Join-Path $SourcePackageRoot 'Skins'
    if (-not (Test-Path -LiteralPath $sourceSkins)) { throw 'Package Skins folder not found.' }

    $roots = Get-SkinsRoot $Root
    $rainmeterExe = Find-RainmeterExe $roots.RainmeterRoot
    if ($WaitPid -gt 0) {
        try { Wait-Process -Id $WaitPid -Timeout 30 -ErrorAction SilentlyContinue } catch {}
    }

    $targetHostPaths = @(
        (Join-Path $roots.SkinsRoot 'Todo\@Resources\TodoHost.exe'),
        (Join-Path $roots.SkinsRoot 'Calendar\@Resources\CalendarHost.exe')
    )
    foreach ($hostProcess in Get-Process -Name TodoHost,CalendarHost -ErrorAction SilentlyContinue) {
        if ($targetHostPaths -contains $hostProcess.Path) {
            try { $hostProcess.CloseMainWindow() | Out-Null } catch {}
        }
    }
    Start-Sleep -Milliseconds 800
    foreach ($hostProcess in Get-Process -Name TodoHost,CalendarHost -ErrorAction SilentlyContinue) {
        if ($targetHostPaths -contains $hostProcess.Path) {
            try {
                if (-not $hostProcess.HasExited) { $hostProcess.Kill() }
                $hostProcess.WaitForExit(3000)
            } catch {}
        }
    }

    foreach ($skin in @('Todo', 'Calendar')) {
        $source = Join-Path $sourceSkins $skin
        $target = Join-Path $roots.SkinsRoot $skin
        New-Item -ItemType Directory -Path $target -Force | Out-Null

        $preserved = @{}
        foreach ($name in @('tasks.json','Generated.inc','calendar-cache.json','calendar-state.json','caldav.secret','translation.secret','paper-sync.secret')) {
            $path = Join-Path $target ('@Resources\' + $name)
            if (Test-Path -LiteralPath $path) { $preserved[$name] = [IO.File]::ReadAllBytes($path) }
        }

        Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
        Get-ChildItem -LiteralPath $target -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

        foreach ($name in $preserved.Keys) {
            $destination = Join-Path $target ('@Resources\' + $name)
            New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
            [IO.File]::WriteAllBytes($destination, $preserved[$name])
        }
    }

    Install-UpdaterFiles $SourcePackageRoot $roots.SkinsRoot

    if (Test-Path -LiteralPath $rainmeterExe) {
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
        if ($ShouldActivate) {
            Start-Sleep -Milliseconds 800
            & $rainmeterExe '!ActivateConfig' 'Todo' 'Todo.ini'
            & $rainmeterExe '!ActivateConfig' 'Calendar' 'Calendar.ini'
            Start-Sleep -Milliseconds 800
            & $rainmeterExe '!SetWindowPosition' '100%' '0%' '100%' '0%' 'Todo'
        }
    } else {
        Write-Warning 'Rainmeter.exe was not found. Skins were copied, but Rainmeter was not restarted automatically.'
    }
    Write-Host "Installed skins to $($roots.SkinsRoot)"
}

function Get-LatestTag {
    param([string]$Repo)
    $tags = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/tags" -Headers @{ 'User-Agent' = $UserAgent; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 20
    $best = ''
    foreach ($tag in $tags) {
        $name = [string]$tag.name
        if (-not ((Normalize-Version $name) -match '^\d')) { continue }
        if ($best -eq '' -or (Compare-VersionText $name $best) -gt 0) { $best = $name }
    }
    if ($best -eq '') { throw 'No version tag was found on GitHub.' }
    return $best
}

function Invoke-WebRequestCompat {
    param(
        [string]$Uri,
        [string]$Method = 'GET',
        [string]$OutFile = '',
        [hashtable]$Headers = @{},
        [int]$TimeoutSec = 20
    )
    $parameters = @{
        Uri = $Uri
        Method = $Method
        Headers = $Headers
        TimeoutSec = $TimeoutSec
    }
    if (-not [string]::IsNullOrWhiteSpace($OutFile)) { $parameters.OutFile = $OutFile }
    if ((Get-Command Invoke-WebRequest).Parameters.ContainsKey('UseBasicParsing')) {
        $parameters.UseBasicParsing = $true
    }
    Invoke-WebRequest @parameters
}

function Find-PackageRoot {
    param([string]$ExtractRoot)
    $installer = Get-ChildItem -LiteralPath $ExtractRoot -Recurse -Filter 'Install-Skins.ps1' -File | Select-Object -First 1
    if ($null -eq $installer) { throw 'Install-Skins.ps1 not found in update package.' }
    return $installer.Directory.FullName
}

function Show-Message {
    param([string]$Text, [string]$Caption = 'Rainmeter Desktop Widgets')
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Text, $Caption, [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    } catch {
        Write-Host $Text
    }
}

function Confirm-Update {
    param([string]$Text)
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $result = [System.Windows.Forms.MessageBox]::Show($Text, 'Rainmeter Desktop Widgets Update', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
        return $result -eq [System.Windows.Forms.DialogResult]::Yes
    } catch {
        $answer = Read-Host "$Text [y/N]"
        return $answer -match '^(y|yes)$'
    }
}

function Check-And-Install {
    $latestTag = Get-LatestTag $Repository
    $latestVersion = Normalize-Version $latestTag
    $current = Normalize-Version $CurrentVersion
    $comparison = Compare-VersionText $latestVersion $current
    if ($comparison -le 0) {
        if ($comparison -eq 0) { Show-Message "Already on latest version: $latestTag" }
        else { Show-Message "Current version $CurrentVersion is newer than latest tag $latestTag." }
        return
    }

    $displayFlavor = if ([string]::IsNullOrWhiteSpace($FlavorName)) { $Flavor } else { $FlavorName }
    $prompt = "New version $latestTag ($displayFlavor) is available.`r`n`r`nDownload and install now? Rainmeter will restart."
    if (-not $AssumeYes -and -not (Confirm-Update $prompt)) {
        Show-Message "Update canceled: $latestTag"
        return
    }

    $assetName = "rainmeter-desktop-widgets-$Flavor-$latestVersion.zip"
    $assetUrl = "https://raw.githubusercontent.com/$Repository/$([Uri]::EscapeDataString($latestTag))/releases/$([Uri]::EscapeDataString($latestTag))/$([Uri]::EscapeDataString($assetName))"
    Invoke-WebRequestCompat -Uri $assetUrl -Method Head -Headers @{ 'User-Agent' = $UserAgent } -TimeoutSec 20 | Out-Null

    $downloads = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) 'Downloads'
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    $zipPath = Join-Path $downloads $assetName
    $extractRoot = Join-Path $env:TEMP ('RainmeterDesktopWidgetsUpdate-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-WebRequestCompat -Uri $assetUrl -OutFile $zipPath -Headers @{ 'User-Agent' = $UserAgent } -TimeoutSec 120
        New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
        Unblock-File -LiteralPath $zipPath -ErrorAction SilentlyContinue
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
        Get-ChildItem -LiteralPath $extractRoot -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

        $newPackageRoot = Find-PackageRoot $extractRoot
        $roots = Get-SkinsRoot $RainmeterRoot
        $newUpdater = Join-Path $newPackageRoot 'Updater\RainmeterDesktopWidgetsUpdater.ps1'
        if (-not (Test-Path -LiteralPath $newUpdater)) { throw 'Updater script not found in update package.' }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $newUpdater -Mode UpdateUpdater -PackageRoot $newPackageRoot -RainmeterRoot $roots.RainmeterRoot

        $installedUpdater = Join-Path $roots.SkinsRoot 'Todo\@Resources\Updater\RainmeterDesktopWidgetsUpdater.ps1'
        if (-not (Test-Path -LiteralPath $installedUpdater)) { throw 'Installed updater script was not found after self-update.' }
        $installArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$installedUpdater,'-Mode','InstallPackage','-PackageRoot',$newPackageRoot,'-RainmeterRoot',$roots.RainmeterRoot,'-WaitForProcessId',$WaitForProcessId)
        if ($Activate) { $installArgs += '-Activate' }
        & powershell @installArgs
    }
    finally {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
        if ((Split-Path -Leaf $zipPath) -like 'rainmeter-desktop-widgets-*.zip') {
            Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($Mode -eq 'UpdateUpdater') {
    $roots = Get-SkinsRoot $RainmeterRoot
    if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = Split-Path $PSScriptRoot -Parent }
    Install-UpdaterFiles ((Resolve-Path -LiteralPath $PackageRoot).Path) $roots.SkinsRoot
    Write-Host "Updated updater to version $UpdaterVersion"
    return
}

if ($Mode -eq 'InstallPackage') {
    Install-Package -SourcePackageRoot $PackageRoot -Root $RainmeterRoot -ShouldActivate:$Activate -WaitPid $WaitForProcessId
    return
}

Check-And-Install
