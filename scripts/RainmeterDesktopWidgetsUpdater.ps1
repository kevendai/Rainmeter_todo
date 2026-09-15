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
# A legacy raw-transition package contains unified-bootstrap.json.  Its EXE
# must finish before the v1.x caller removes the extraction directory: it uses
# its own adjacent updater files as the fallback while it installs the 1.4.4
# hop.  Regular full packages can still detach before replacing the installed
# updater script.
$isBootstrapPackage = -not [string]::IsNullOrWhiteSpace($PackageRoot) -and (Test-Path -LiteralPath (Join-Path $PackageRoot 'unified-bootstrap.json'))
if ($Mode -eq 'InstallPackage' -and $packageUpdaterSelected -and -not $isBootstrapPackage) {
    Start-Process -FilePath $updater -ArgumentList $escaped | Out-Null
    exit 0
}
$process = Start-Process -FilePath $updater -ArgumentList $escaped -Wait -PassThru
exit $process.ExitCode
