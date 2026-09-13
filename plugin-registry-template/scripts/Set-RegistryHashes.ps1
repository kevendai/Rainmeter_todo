param([Parameter(Mandatory=$true)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$indexPath = Join-Path $root 'index-v1.json'
$index = Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8 | ConvertFrom-Json
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory)
foreach ($entry in $index.plugins) {
    $fileName = [IO.Path]::GetFileName(([Uri]$entry.download).AbsolutePath)
    $packagePath = Join-Path $packageRoot $fileName
    if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) { throw "Package not found: $fileName" }
    $sha = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $entry.sha256 = $sha
    $detailPath = Join-Path $root ('plugins\' + $entry.id + '.json')
    $detail = Get-Content -LiteralPath $detailPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $detail.sha256 = $sha
    $detail | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $detailPath -Encoding UTF8
}
$index.generated_at = [DateTimeOffset]::Now.ToString('o')
$index | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $indexPath -Encoding UTF8
& (Join-Path $PSScriptRoot 'Test-Registry.ps1')
