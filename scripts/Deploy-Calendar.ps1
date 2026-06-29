param([switch]$Activate)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$source = Join-Path $projectRoot 'skins\Calendar'
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION'), [Text.UTF8Encoding]::new($false)).Trim()
$rainmeterRoot = 'D:\Program Files (x86)\Rainmeter'
$target = Join-Path $rainmeterRoot 'Skins\Calendar'
$exe = Join-Path $rainmeterRoot 'Rainmeter.exe'

if (-not (Test-Path -LiteralPath $exe)) { throw "Rainmeter not found: $exe" }
New-Item -ItemType Directory -Path $target -Force | Out-Null

$preserved = @{}
foreach ($name in @('calendar-cache.json','calendar-state.json')) {
    $path = Join-Path $target ('@Resources\' + $name)
    if (Test-Path -LiteralPath $path) { $preserved[$name] = [IO.File]::ReadAllBytes($path) }
}
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
foreach ($name in $preserved.Keys) {
    [IO.File]::WriteAllBytes((Join-Path $target ('@Resources\' + $name)), $preserved[$name])
}
& (Join-Path $PSScriptRoot 'New-RefreshArrow.ps1') -OutputDirectory (Join-Path $target '@Resources\RefreshFrames')

# Rainmeter reads Chinese skin literals reliably from UTF-16 LE with BOM.
$liveIni = Join-Path $target 'Calendar.ini'
$iniText = [IO.File]::ReadAllText($liveIni, [Text.UTF8Encoding]::new($false))
$iniText = $iniText -replace '(?m)^Version=.*$', "Version=$version"
[IO.File]::WriteAllText($liveIni, $iniText, [Text.UnicodeEncoding]::new($false, $true))
[IO.File]::WriteAllText((Join-Path $target '@Resources\app-version.txt'), $version, [Text.UTF8Encoding]::new($false))

$backendRoot = Join-Path $projectRoot 'backend'
$commonSource = Join-Path $backendRoot 'Common.cs'
$hostSources = @(Get-ChildItem -LiteralPath $backendRoot -Filter 'Calendar*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$hostExe = Join-Path $target '@Resources\CalendarHost.exe'
$hostBuildExe = Join-Path $target '@Resources\CalendarHost.build.exe'
foreach ($hostProcess in Get-Process -Name CalendarHost -ErrorAction SilentlyContinue) {
    if ($hostProcess.Path -eq $hostExe) {
        try { $hostProcess.CloseMainWindow() | Out-Null } catch {}
    }
}
Start-Sleep -Milliseconds 500
foreach ($hostProcess in Get-Process -Name CalendarHost -ErrorAction SilentlyContinue) {
    if ($hostProcess.Path -eq $hostExe) {
        try {
            if (-not $hostProcess.HasExited) { $hostProcess.Kill() }
            $hostProcess.WaitForExit(3000)
        } catch {}
    }
}
$csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) { $csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path -LiteralPath $csc)) { throw 'C# compiler not found' }
& $csc /nologo /target:winexe /optimize+ /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll "/out:$hostBuildExe" $commonSource $hostSources
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $hostBuildExe)) { throw 'Failed to build CalendarHost.exe' }
try {
    $moved = $false
    foreach ($attempt in 1..20) {
        try { Move-Item -LiteralPath $hostBuildExe -Destination $hostExe -Force; $moved = $true; break }
        catch { Start-Sleep -Milliseconds 200 }
    }
    if (-not $moved) { throw 'CalendarHost.exe remained in use during deployment' }
} catch {
    throw
}
Remove-Item -LiteralPath (Join-Path $target '@Resources\Calendar.ps1') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $target '@Resources\CalendarHost.cs') -Force -ErrorAction SilentlyContinue

$liveSecret = Join-Path $rainmeterRoot 'Skins\Todo\@Resources\caldav.secret'
$sourceSecret = Join-Path $projectRoot 'skins\Todo\@Resources\caldav.secret'
if (-not (Test-Path -LiteralPath $liveSecret) -and (Test-Path -LiteralPath $sourceSecret)) {
    New-Item -ItemType Directory -Path (Split-Path $liveSecret -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $sourceSecret -Destination $liveSecret -Force
}

& $hostExe Render
if ($LASTEXITCODE -ne 0) { throw 'CalendarHost Render failed' }
& $exe '!RefreshApp'
if ($Activate) {
    Start-Sleep -Milliseconds 800
    & $exe '!ActivateConfig' 'Calendar' 'Calendar.ini'
    Start-Sleep -Milliseconds 800
    Add-Type -AssemblyName PresentationFramework
    $x = [Math]::Max(0, [int][System.Windows.SystemParameters]::WorkArea.Width - 1010)
    & $exe '!SetWindowPosition' ([string]$x) '0' '0' '0' 'Calendar'
}
Write-Host "Deployed Calendar skin to $target"
