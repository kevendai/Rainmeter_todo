param([switch]$Activate)
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'skins\Todo'
$rainmeterRoot = 'D:\Program Files (x86)\Rainmeter'
$target = Join-Path $rainmeterRoot 'Skins\Todo'
$exe = Join-Path $rainmeterRoot 'Rainmeter.exe'

if (-not (Test-Path -LiteralPath $exe)) { throw "Rainmeter not found: $exe" }
New-Item -ItemType Directory -Path $target -Force | Out-Null
$liveData = Join-Path $target '@Resources\tasks.json'
$liveGenerated = Join-Path $target '@Resources\Generated.inc'
$savedData = $null
$savedGenerated = $null
if (Test-Path -LiteralPath $liveData) { $savedData = [IO.File]::ReadAllBytes($liveData) }
if (Test-Path -LiteralPath $liveGenerated) { $savedGenerated = [IO.File]::ReadAllBytes($liveGenerated) }
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
if ($null -ne $savedData) { [IO.File]::WriteAllBytes($liveData, $savedData) }
if ($null -ne $savedGenerated) { [IO.File]::WriteAllBytes($liveGenerated, $savedGenerated) }
& (Join-Path $PSScriptRoot 'New-RefreshArrow.ps1') -OutputDirectory (Join-Path $target '@Resources\RefreshFrames')

# Rainmeter reliably reads Chinese skin literals from UTF-16 LE with BOM.
# Keep the repository source UTF-8 for editing, normalize only the live INI.
$liveIni = Join-Path $target 'Todo.ini'
$iniBytes = [IO.File]::ReadAllBytes($liveIni)
$iniText = if ($iniBytes.Length -ge 2 -and $iniBytes[0] -eq 0xFF -and $iniBytes[1] -eq 0xFE) {
    [IO.File]::ReadAllText($liveIni, [Text.Encoding]::Unicode)
} else {
    [IO.File]::ReadAllText($liveIni, [Text.UTF8Encoding]::new($false))
}
[IO.File]::WriteAllText($liveIni, $iniText, [Text.UnicodeEncoding]::new($false, $true))

$backendRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'backend'
$commonSource = Join-Path $backendRoot 'Common.cs'
$hostSource = Join-Path $backendRoot 'TodoApp.cs'
$hostExe = Join-Path $target '@Resources\TodoHost.exe'
$hostBuildExe = Join-Path $target '@Resources\TodoHost.build.exe'
$csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    $csc = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $csc)) { throw "C# compiler not found; cannot build TodoHost.exe" }
& $csc /nologo /target:winexe /optimize+ /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll "/out:$hostBuildExe" $commonSource $hostSource
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $hostBuildExe)) { throw 'Failed to build TodoHost.exe' }
$moved = $false
foreach ($attempt in 1..20) {
    try { Move-Item -LiteralPath $hostBuildExe -Destination $hostExe -Force; $moved = $true; break }
    catch { Start-Sleep -Milliseconds 200 }
}
if (-not $moved) { throw 'TodoHost.exe remained in use during deployment' }
Remove-Item -LiteralPath (Join-Path $target '@Resources\Todo.ps1') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $target '@Resources\TodoHost.cs') -Force -ErrorAction SilentlyContinue

& $hostExe Render
if ($LASTEXITCODE -ne 0) { throw 'TodoHost Render failed' }
& $exe '!RefreshApp'
if ($Activate) {
    Start-Sleep -Milliseconds 800
    & $exe '!ActivateConfig' 'Todo' 'Todo.ini'
    Start-Sleep -Milliseconds 800
    & $exe '!SetWindowPosition' '100%' '0%' '100%' '0%' 'Todo'
}
Write-Host "Deployed Todo skin to $target"
