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
$forward = @('-Mode', $Mode, '-Repository', $Repository)
if (-not [string]::IsNullOrWhiteSpace($CurrentVersion)) { $forward += @('-CurrentVersion', $CurrentVersion) }
if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) { $forward += @('-PackageRoot', $PackageRoot) }
if (-not [string]::IsNullOrWhiteSpace($RainmeterRoot)) { $forward += @('-RainmeterRoot', $RainmeterRoot) }
if ($Activate) { $forward += '-Activate' }
if ($AssumeYes) { $forward += '-AssumeYes' }
$escaped = @($forward | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } })
# v1.4.4 calls this script from a directory about to be replaced.  For its
# InstallPackage handoff, the updater chosen above lives in the extracted
# package, so detach before the legacy script file is swapped out.
if ($Mode -eq 'InstallPackage' -and $packageUpdaterSelected) {
    Start-Process -FilePath $updater -ArgumentList $escaped | Out-Null
    exit 0
}
$process = Start-Process -FilePath $updater -ArgumentList $escaped -Wait -PassThru
exit $process.ExitCode
