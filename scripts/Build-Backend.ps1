param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Todo', 'Calendar')]
    [string]$Backend,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $projectRoot ("backend\{0}Host.csproj" -f $Backend)
$msbuild = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    $msbuild = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\MSBuild.exe'
}
if (-not (Test-Path -LiteralPath $msbuild)) { throw 'MSBuild was not found.' }

$output = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\') + '\'
$intermediate = Join-Path ([IO.Path]::GetTempPath()) ("Rainmeter-{0}-obj-{1}" -f $Backend, [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
try {
    & $msbuild $project /nologo /verbosity:minimal /target:Build /property:Configuration=Release "/property:OutputPath=$output" "/property:IntermediateOutputPath=$intermediate\"
    if ($LASTEXITCODE -ne 0) { throw "$Backend backend build failed." }
    $exe = Join-Path $output ("{0}Host.exe" -f $Backend)
    if (-not (Test-Path -LiteralPath $exe)) { throw "$Backend backend output was not created: $exe" }
    Write-Output $exe
}
finally {
    Remove-Item -LiteralPath $intermediate -Recurse -Force -ErrorAction SilentlyContinue
}
