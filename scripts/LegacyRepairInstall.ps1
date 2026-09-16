param([string]$RainmeterRoot,[switch]$Activate,[int]$WaitForProcessId=0)
$ErrorActionPreference='Stop'
$script=Join-Path $PSScriptRoot 'Updater\RainmeterDesktopWidgetsUpdater.ps1'
if(-not (Test-Path -LiteralPath $script)){throw 'RainmeterDesktopWidgetsUpdater.ps1 not found.'}
$args=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$script,'-Mode','InstallPackage','-RainmeterRoot',$RainmeterRoot)
if($Activate){$args+='-Activate'}
if($WaitForProcessId -gt 0){$args+=@('-WaitForProcessId',$WaitForProcessId)}
$p=Start-Process -FilePath 'powershell.exe' -ArgumentList $args -Wait -PassThru
exit $p.ExitCode
