param(
    [ValidateSet('CheckAndInstall','InstallPackage','UpdateUpdater','SelfTest')]
    [string]$Mode = 'InstallPackage',
    [string]$Repository = 'kevendai/Rainmeter_todo',
    [string]$CurrentVersion = '',
    [string]$Flavor = 'full',
    [string]$FlavorName = '',
    [string]$PackageRoot = '',
    [string]$RainmeterRoot = '',
    [switch]$Activate,
    [switch]$AssumeYes,
    [int]$WaitForProcessId = 0
)

# Compatibility launcher only. Released v1.x hosts invoke this filename with
# Windows PowerShell. All update work is performed by UpdaterHost.exe.
$ErrorActionPreference = 'Stop'
$updater = Join-Path $PSScriptRoot 'UpdaterHost.exe'
$packageUpdaterSelected = $false
if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) {
    $packageUpdater = Join-Path $PackageRoot 'Updater\UpdaterHost.exe'
    if (Test-Path -LiteralPath $packageUpdater -PathType Leaf) { $updater = $packageUpdater; $packageUpdaterSelected = $true }
}
if (-not (Test-Path -LiteralPath $updater -PathType Leaf)) { throw 'UpdaterHost.exe was not found in the package or next to the compatibility launcher.' }
# The old updater removes its downloaded extraction directory as soon as this
# compatibility script returns.  At the same time, this script itself may be
# loaded from Todo\@Resources, which prevents the child from atomically
# replacing that whole skin directory.  Stage the selected package outside both
# locations, then detach the EXE from the old PowerShell process.
if ($Mode -eq 'InstallPackage' -and $packageUpdaterSelected) {
    $sourcePackage = (Resolve-Path -LiteralPath $PackageRoot).Path
    $stagedPackage = Join-Path $env:TEMP ('RainmeterDesktopWidgetsUpdater-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stagedPackage -Force | Out-Null
    Copy-Item -Path (Join-Path $sourcePackage '*') -Destination $stagedPackage -Recurse -Force
    $PackageRoot = $stagedPackage
    $updater = Join-Path $PackageRoot 'Updater\UpdaterHost.exe'
    if (-not (Test-Path -LiteralPath $updater -PathType Leaf)) { throw 'Staged updater package is incomplete.' }
}
$forward = @('-Mode', $Mode, '-Repository', $Repository)
if (-not [string]::IsNullOrWhiteSpace($CurrentVersion)) { $forward += @('-CurrentVersion', $CurrentVersion) }
if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) { $forward += @('-PackageRoot', $PackageRoot) }
if (-not [string]::IsNullOrWhiteSpace($RainmeterRoot)) { $forward += @('-RainmeterRoot', $RainmeterRoot) }
if ($Activate) { $forward += '-Activate' }
if ($AssumeYes) { $forward += '-AssumeYes' }
$escaped = @($forward | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } })
if ($Mode -eq 'InstallPackage' -and $packageUpdaterSelected) {
    Start-Process -FilePath $updater -ArgumentList $escaped | Out-Null
    exit 0
}
$process = Start-Process -FilePath $updater -ArgumentList $escaped -Wait -PassThru
exit $process.ExitCode
