$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$index = Get-Content -LiteralPath (Join-Path $root 'index-v1.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($index.version -ne 1) { throw 'Registry version must be 1.' }
if (-not $index.plugins -or $index.plugins.Count -eq 0) { throw 'Registry must contain plugins.' }
$seen = @{}
foreach ($entry in $index.plugins) {
    if ($seen.ContainsKey($entry.id)) { throw "Duplicate plugin id: $($entry.id)" }
    $seen[$entry.id] = $true
    if ($entry.official -ne $true) { throw "Non-official entry is not allowed in v1: $($entry.id)" }
    if ($entry.api_version -ne 1) { throw "Unsupported API version: $($entry.id)" }
    if ($entry.version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid version: $($entry.id)" }
    if ($entry.sha256 -notmatch '^[0-9a-fA-F]{64}$') { throw "Invalid SHA256: $($entry.id)" }
    $download = [Uri]$entry.download
    if ($download.Scheme -ne 'https' -or $download.Host -ne 'github.com' -or $download.AbsolutePath -notmatch '/releases/download/') {
        throw "Download must be a GitHub HTTPS release asset: $($entry.id)"
    }
    $detailPath = Join-Path $root ('plugins\' + $entry.id + '.json')
    if (-not (Test-Path -LiteralPath $detailPath)) { throw "Missing detail file: $($entry.id)" }
    $detail = Get-Content -LiteralPath $detailPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($field in @('id','name','channel','official','version','api_version','min_host_version','download','sha256','release_notes','homepage')) {
        if ([string]$detail.$field -ne [string]$entry.$field) { throw "Detail mismatch for $($entry.id): $field" }
    }
}
Write-Host "Registry validation passed for $($index.plugins.Count) plugins."
