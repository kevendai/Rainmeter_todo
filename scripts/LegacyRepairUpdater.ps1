param(
  [ValidateSet('InstallPackage','UpdateUpdater','CheckAndInstall')][string]$Mode='InstallPackage',
  [string]$Repository='kevendai/Rainmeter_todo', [string]$PackageRoot='', [string]$RainmeterRoot='',
  [switch]$Activate, [int]$WaitForProcessId=0
)
$ErrorActionPreference='Stop'
$release='v2.0.1'; $asset='rainmeter-desktop-widgets-2.0.1.zip'
if($Mode -eq 'UpdateUpdater'){
  if([string]::IsNullOrWhiteSpace($RainmeterRoot)){ throw 'RainmeterRoot is required.' }
  $target=Join-Path $RainmeterRoot 'Skins\Todo\@Resources\Updater'
  New-Item -ItemType Directory -Path $target -Force | Out-Null
  Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $target 'RainmeterDesktopWidgetsUpdater.ps1') -Force
  exit 0
}
$temp=Join-Path $env:TEMP ('RainmeterDesktopWidgetsRepair-'+[guid]::NewGuid().ToString('N'))
$download=Join-Path $temp $asset; $extract=Join-Path $temp 'package'; $stage=Join-Path $env:TEMP ('RainmeterDesktopWidgetsRepairStage-'+[guid]::NewGuid().ToString('N'))
try {
  New-Item -ItemType Directory -Path $temp,$stage -Force | Out-Null
  $url="https://github.com/$Repository/releases/download/$release/$asset"
  $checksum=Join-Path $temp ($asset+'.sha256')
  Invoke-WebRequest -Uri $url -OutFile $download -UseBasicParsing -TimeoutSec 180
  Invoke-WebRequest -Uri ($url+'.sha256') -OutFile $checksum -UseBasicParsing -TimeoutSec 30
  $expected=([regex]::Match((Get-Content -LiteralPath $checksum -Raw), '(?i)\b[0-9a-f]{64}\b')).Value.ToLowerInvariant()
  if($expected.Length -ne 64){throw 'Update package checksum file is invalid.'}
  $actual=(Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
  if($actual -ne $expected){throw 'Update package SHA256 verification failed.'}
  Expand-Archive -LiteralPath $download -DestinationPath $extract -Force
  $package=Get-ChildItem -LiteralPath $extract -Recurse -Filter 'UpdaterHost.exe' -File | Select-Object -First 1
  if($null -eq $package){throw 'UpdaterHost.exe is missing from the update package.'}
  $packageRootFound=$package.Directory.Parent.FullName
  Copy-Item -Path (Join-Path $packageRootFound '*') -Destination $stage -Recurse -Force
  $updater=Join-Path $stage 'Updater\UpdaterHost.exe'
  if(-not (Test-Path -LiteralPath $updater)){throw 'Staged update package is incomplete.'}
  $forward=@('-Mode','InstallPackage','-PackageRoot',$stage,'-RainmeterRoot',$RainmeterRoot,'-DelayMilliseconds','1500')
  if($Activate){$forward+='-Activate'}
  Start-Process -FilePath $updater -ArgumentList $forward | Out-Null
  exit 0
} finally { Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue }
