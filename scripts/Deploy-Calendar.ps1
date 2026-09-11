param([switch]$Activate, [string]$RainmeterRoot = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = Join-Path $projectRoot 'skins\Calendar'
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION'), [Text.UTF8Encoding]::new($false)).Trim()
if (-not [string]::IsNullOrWhiteSpace($env:RAINMETER_DEPLOY_VERSION_OVERRIDE)) { $version = $env:RAINMETER_DEPLOY_VERSION_OVERRIDE.Trim() }
. (Join-Path $PSScriptRoot 'Rainmeter-Paths.ps1')
$environment = Resolve-RainmeterEnvironment -RainmeterRoot $RainmeterRoot
$target = Join-Path $environment.SkinsRoot 'Calendar'
$exe = $environment.RainmeterExe

if (-not (Test-Path -LiteralPath $target)) {
    $recovery = Get-ChildItem -LiteralPath $environment.SkinsRoot -Directory -Filter '.rainmeter-calendar-deploy-*' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | ForEach-Object { Join-Path $_.FullName 'backup\Calendar' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($recovery) { Move-Item -LiteralPath $recovery -Destination $target; Write-Warning "Recovered interrupted Calendar deployment from $recovery" }
}

$transaction = Join-Path $environment.SkinsRoot ('.rainmeter-calendar-deploy-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $transaction 'stage\Calendar'
$backup = Join-Path $transaction 'backup\Calendar'
$swapped = $false
$success = $false
$preservedHashes = @{}
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse -Force
    foreach ($name in @('calendar-cache.json','calendar-state.json')) {
        $current = Join-Path $target ('@Resources\' + $name)
        if (Test-Path -LiteralPath $current) { $preservedHashes[$name] = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash; $destination = Join-Path $stage ('@Resources\' + $name); New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null; Copy-Item -LiteralPath $current -Destination $destination -Force }
    }
    Remove-Item -LiteralPath (Join-Path $stage '@Resources\UiScale.inc') -Force -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'New-RefreshArrow.ps1') -OutputDirectory (Join-Path $stage '@Resources\RefreshFrames')
    $liveIni = Join-Path $stage 'Calendar.ini'
    $iniText = [IO.File]::ReadAllText($liveIni, [Text.UTF8Encoding]::new($false))
    $iniText = $iniText -replace '(?m)^Version=.*$', "Version=$version"
    [IO.File]::WriteAllText($liveIni, $iniText, [Text.UnicodeEncoding]::new($false, $true))
    [IO.File]::WriteAllText((Join-Path $stage '@Resources\app-version.txt'), $version, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Calendar -OutputDirectory (Join-Path $stage '@Resources') | Out-Null
    foreach ($name in $preservedHashes.Keys) { $stagedData = Join-Path $stage ('@Resources\' + $name); if (-not (Test-Path -LiteralPath $stagedData) -or (Get-FileHash -LiteralPath $stagedData -Algorithm SHA256).Hash -ne $preservedHashes[$name]) { throw "Staged user data verification failed: $name" } }
    Remove-Item -LiteralPath (Join-Path $stage '@Resources\Calendar.ps1') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $stage '@Resources\CalendarHost.cs') -Force -ErrorAction SilentlyContinue

    $oldHost = Join-Path $target '@Resources\CalendarHost.exe'
    foreach ($process in Get-Process -Name CalendarHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $oldHost }) { try { $process.CloseMainWindow() | Out-Null } catch {} }
    Start-Sleep -Milliseconds 500
    foreach ($process in Get-Process -Name CalendarHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $oldHost }) { try { if (-not $process.HasExited) { $process.Kill() }; $process.WaitForExit(3000) } catch {} }
    if (Test-Path -LiteralPath $target) { New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null; Move-Item -LiteralPath $target -Destination $backup }
    Move-Item -LiteralPath $stage -Destination $target
    $swapped = $true
    foreach ($name in $preservedHashes.Keys) { $liveData = Join-Path $target ('@Resources\' + $name); if (-not (Test-Path -LiteralPath $liveData) -or (Get-FileHash -LiteralPath $liveData -Algorithm SHA256).Hash -ne $preservedHashes[$name]) { throw "Installed user data verification failed: $name" } }
    $hostExe = Join-Path $target '@Resources\CalendarHost.exe'
    $render = Start-Process -FilePath $hostExe -ArgumentList 'Render' -WindowStyle Hidden -PassThru
    if (-not $render.WaitForExit(20000)) { try { $render.Kill() } catch {}; throw 'CalendarHost Render timed out' }
    if ($render.ExitCode -ne 0) { throw 'CalendarHost Render failed' }
    & $exe '!RefreshApp'
    if ($Activate) { Start-Sleep -Milliseconds 800; & $exe '!ActivateConfig' 'Calendar' 'Calendar.ini'; Start-Sleep -Milliseconds 800; Add-Type -AssemblyName PresentationFramework; $x = [Math]::Max(0, [int][System.Windows.SystemParameters]::WorkArea.Width - 1010); & $exe '!SetWindowPosition' ([string]$x) '0' '0' '0' 'Calendar' }
    $success = $true
    Write-Host "Deployed Calendar skin transactionally to $target"
}
catch {
    if ($swapped) {
        $newHost = Join-Path $target '@Resources\CalendarHost.exe'
        foreach ($process in Get-Process -Name CalendarHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $newHost }) { try { $process.Kill(); $process.WaitForExit(3000) } catch {} }
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $target }
        try { & $exe '!RefreshApp' } catch {}
    }
    throw
}
finally {
    if ($success -or -not $swapped) { Remove-Item -LiteralPath $transaction -Recurse -Force -ErrorAction SilentlyContinue }
}
