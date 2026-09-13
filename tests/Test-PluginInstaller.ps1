param([Parameter(Mandatory=$true)][string]$Installer,[Parameter(Mandatory=$true)][string]$PluginSource)
$ErrorActionPreference='Stop';$root=Join-Path ([IO.Path]::GetTempPath()) ('rwplugin-installer-test-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory -Path $root|Out-Null
try {
    $zip=Join-Path $root 'valid.zip';Compress-Archive -Path (Join-Path $PluginSource '*') -DestinationPath $zip;$package=Join-Path $root 'valid.rwplugin';Move-Item $zip $package;$sha=(Get-FileHash $package -Algorithm SHA256).Hash
    & $Installer -Package $package -PluginRoot (Join-Path $root 'plugins') -ExpectedSha256 $sha|Out-Null;if($LASTEXITCODE -ne 0){throw 'Valid package was rejected'}
    try{& $Installer -Package $package -PluginRoot (Join-Path $root 'plugins') -ExpectedSha256 ('0'*64)|Out-Null;throw 'Bad SHA256 was accepted'}catch{if($_.Exception.Message -eq 'Bad SHA256 was accepted'){throw}}
    Add-Type -AssemblyName System.IO.Compression;Add-Type -AssemblyName System.IO.Compression.FileSystem;$bad=Join-Path $root 'traversal.rwplugin';$archive=[IO.Compression.ZipFile]::Open($bad,[IO.Compression.ZipArchiveMode]::Create);try{$entry=$archive.CreateEntry('../escape.txt');$writer=[IO.StreamWriter]::new($entry.Open());$writer.Write('escape');$writer.Dispose()}finally{$archive.Dispose()}
    try{& $Installer -Package $bad -PluginRoot (Join-Path $root 'plugins')|Out-Null;throw 'ZIP traversal was accepted'}catch{if($_.Exception.Message -eq 'ZIP traversal was accepted'){throw}}
    if(Test-Path (Join-Path $root 'escape.txt')){throw 'ZIP traversal wrote outside staging'}
    Write-Host 'rwplugin SHA256, manifest staging and ZIP traversal rejection passed'
} finally {if(Test-Path $root){Remove-Item -LiteralPath $root -Recurse -Force}}
