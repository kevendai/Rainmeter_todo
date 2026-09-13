param([switch]$Activate, [string]$RainmeterRoot = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = Join-Path $projectRoot 'skins\Todo'
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION'), [Text.UTF8Encoding]::new($false)).Trim()
if (-not [string]::IsNullOrWhiteSpace($env:RAINMETER_DEPLOY_VERSION_OVERRIDE)) { $version = $env:RAINMETER_DEPLOY_VERSION_OVERRIDE.Trim() }
. (Join-Path $PSScriptRoot 'Rainmeter-Paths.ps1')
$environment = Resolve-RainmeterEnvironment -RainmeterRoot $RainmeterRoot
$target = Join-Path $environment.SkinsRoot 'Todo'
$exe = $environment.RainmeterExe

# Recover the only unsafe interruption point: old target renamed, staged target not yet installed.
if (-not (Test-Path -LiteralPath $target)) {
    $recovery = Get-ChildItem -LiteralPath $environment.SkinsRoot -Directory -Filter '.rainmeter-todo-deploy-*' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending | ForEach-Object { Join-Path $_.FullName 'backup\Todo' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if ($recovery) { Move-Item -LiteralPath $recovery -Destination $target; Write-Warning "Recovered interrupted Todo deployment from $recovery" }
}

$transaction = Join-Path $environment.SkinsRoot ('.rainmeter-todo-deploy-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $transaction 'stage\Todo'
$backup = Join-Path $transaction 'backup\Todo'
$swapped = $false
$success = $false
$preservedHashes = @{}
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse -Force
    foreach ($name in @('tasks.json','ui-scale.txt','caldav.secret','translation.secret','paper-sync.secret')) {
        $current = Join-Path $target ('@Resources\' + $name)
        if (Test-Path -LiteralPath $current) { $preservedHashes[$name] = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash; $destination = Join-Path $stage ('@Resources\' + $name); New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null; Copy-Item -LiteralPath $current -Destination $destination -Force }
    }
    $updaterTarget = Join-Path $stage '@Resources\Updater'
    New-Item -ItemType Directory -Path $updaterTarget -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RainmeterDesktopWidgetsUpdater.ps1') -Destination (Join-Path $updaterTarget 'RainmeterDesktopWidgetsUpdater.ps1') -Force
    Remove-Item -LiteralPath (Join-Path $stage '@Resources\UiScale.inc') -Force -ErrorAction SilentlyContinue
    & (Join-Path $PSScriptRoot 'New-RefreshArrow.ps1') -OutputDirectory (Join-Path $stage '@Resources\RefreshFrames')

    $liveIni = Join-Path $stage 'Todo.ini'
    $iniBytes = [IO.File]::ReadAllBytes($liveIni)
    $iniText = if ($iniBytes.Length -ge 2 -and $iniBytes[0] -eq 0xFF -and $iniBytes[1] -eq 0xFE) { [IO.File]::ReadAllText($liveIni, [Text.Encoding]::Unicode) } else { [IO.File]::ReadAllText($liveIni, [Text.UTF8Encoding]::new($false)) }
    $iniText = $iniText -replace '(?m)^Version=.*$', "Version=$version"
    [IO.File]::WriteAllText($liveIni, $iniText, [Text.UnicodeEncoding]::new($false, $true))
    [IO.File]::WriteAllText((Join-Path $stage '@Resources\app-version.txt'), $version, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Todo -OutputDirectory (Join-Path $stage '@Resources') | Out-Null
    & (Join-Path $PSScriptRoot 'Build-Backend.ps1') -Backend Plugin -OutputDirectory (Join-Path $stage '@Resources') | Out-Null
    & (Join-Path $PSScriptRoot 'Build-OfficialPlugins.ps1') -OutputDirectory (Join-Path $stage '@Resources\BundledPlugins') | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-RwPlugin.ps1') -Destination (Join-Path $stage '@Resources\PluginInstaller.ps1') -Force
    foreach ($name in $preservedHashes.Keys) { $stagedData = Join-Path $stage ('@Resources\' + $name); if (-not (Test-Path -LiteralPath $stagedData) -or (Get-FileHash -LiteralPath $stagedData -Algorithm SHA256).Hash -ne $preservedHashes[$name]) { throw "Staged user data verification failed: $name" } }
    Remove-Item -LiteralPath (Join-Path $stage '@Resources\Todo.ps1') -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $stage '@Resources\TodoHost.cs') -Force -ErrorAction SilentlyContinue

    $oldHost = Join-Path $target '@Resources\TodoHost.exe'
    $oldPluginHost = Join-Path $target '@Resources\PluginHost.exe'
    foreach ($process in Get-Process -Name TodoHost,PluginHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $oldHost -or $_.Path -eq $oldPluginHost }) { try { $process.CloseMainWindow() | Out-Null } catch {} }
    Start-Sleep -Milliseconds 500
    foreach ($process in Get-Process -Name TodoHost,PluginHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $oldHost -or $_.Path -eq $oldPluginHost }) { try { if (-not $process.HasExited) { $process.Kill() }; $process.WaitForExit(3000) } catch {} }

    $rainmeterWasRunning = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }).Count -gt 0
    if ($rainmeterWasRunning) { & $exe '!Quit'; $deadline = (Get-Date).AddSeconds(10); do { Start-Sleep -Milliseconds 250; $remainingRainmeter = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) } while ($remainingRainmeter.Count -gt 0 -and (Get-Date) -lt $deadline); if ($remainingRainmeter.Count -gt 0) { throw 'Rainmeter did not exit within 10 seconds.' } }

    if (Test-Path -LiteralPath $target) { New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null; Move-Item -LiteralPath $target -Destination $backup }
    Move-Item -LiteralPath $stage -Destination $target
    $swapped = $true
    foreach ($name in $preservedHashes.Keys) { $liveData = Join-Path $target ('@Resources\' + $name); if (-not (Test-Path -LiteralPath $liveData) -or (Get-FileHash -LiteralPath $liveData -Algorithm SHA256).Hash -ne $preservedHashes[$name]) { throw "Installed user data verification failed: $name" } }
    $hostExe = Join-Path $target '@Resources\TodoHost.exe'
    $pluginHostExe = Join-Path $target '@Resources\PluginHost.exe'
    $pluginProbe = Start-Process -FilePath $pluginHostExe -ArgumentList 'SelfTest' -WindowStyle Hidden -PassThru
    if (-not $pluginProbe.WaitForExit(20000)) { try { $pluginProbe.Kill() } catch {}; throw 'PluginHost SelfTest timed out' }
    if ($pluginProbe.ExitCode -ne 0) { throw 'PluginHost SelfTest failed' }
    $render = Start-Process -FilePath $hostExe -ArgumentList 'Render' -WindowStyle Hidden -PassThru
    if (-not $render.WaitForExit(20000)) { try { $render.Kill() } catch {}; throw 'TodoHost Render timed out' }
    if ($render.ExitCode -ne 0) { throw 'TodoHost Render failed' }
    if ($rainmeterWasRunning -and -not (Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })) { Start-Process -FilePath $exe | Out-Null; Start-Sleep -Milliseconds 1200 }
    & $exe '!RefreshApp'
    if ($Activate) { Start-Sleep -Milliseconds 800; & $exe '!ActivateConfig' 'Todo' 'Todo.ini'; Start-Sleep -Milliseconds 800; & $exe '!SetWindowPosition' '100%' '0%' '100%' '0%' 'Todo' }
    $success = $true
    Write-Host "Deployed Todo skin transactionally to $target"
}
catch {
    if ($swapped) {
        $newHost = Join-Path $target '@Resources\TodoHost.exe'
        $newPluginHost = Join-Path $target '@Resources\PluginHost.exe'
        foreach ($process in Get-Process -Name TodoHost,PluginHost -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $newHost -or $_.Path -eq $newPluginHost }) { try { $process.Kill(); $process.WaitForExit(3000) } catch {} }
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $target }
        if ($rainmeterWasRunning -and -not (Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe })) { try { Start-Process -FilePath $exe | Out-Null; Start-Sleep -Milliseconds 1200 } catch {} }
        try { & $exe '!RefreshApp' } catch {}
    }
    throw
}
finally {
    if ($success -or -not $swapped) { Remove-Item -LiteralPath $transaction -Recurse -Force -ErrorAction SilentlyContinue }
}
