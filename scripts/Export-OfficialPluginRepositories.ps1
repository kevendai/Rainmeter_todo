param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if ($outputRoot.TrimEnd('\') -eq $projectRoot.TrimEnd('\')) {
    throw 'OutputDirectory cannot be the main repository root.'
}

$definitions = @(
    @{ Folder='arxiv' },
    @{ Folder='calendar-to-todo' },
    @{ Folder='ssdp-server-ip' }
)

$buildScript = @'
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist'))
$ErrorActionPreference = 'Stop'
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$project = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.csproj' | Select-Object -First 1
if (-not $project) { throw 'Plugin project was not found.' }
$msbuild = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { $msbuild = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\MSBuild.exe' }
if (-not (Test-Path -LiteralPath $msbuild)) { throw '.NET Framework 4 MSBuild was not found.' }
$stage = Join-Path ([IO.Path]::GetTempPath()) ('rwplugin-' + [guid]::NewGuid().ToString('N'))
try {
    $bin = Join-Path $stage 'bin'
    New-Item -ItemType Directory -Path $bin -Force | Out-Null
    & $msbuild $project.FullName /nologo /verbosity:minimal /target:Build /property:Configuration=Release "/property:OutputPath=$bin\" "/property:IntermediateOutputPath=$(Join-Path $stage 'obj')\"
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    foreach ($name in @('plugin.json','settings.schema.json','README.md','THIRD-PARTY-NOTICES.md','icon.png')) {
        $source = Join-Path $PSScriptRoot $name
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $stage }
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $slug = if ($manifest.id -eq 'io.github.kevendai.arxiv') { 'arxiv' } elseif ($manifest.id -eq 'io.github.kevendai.ssdp-server-ip') { 'ssdp-server-ip' } else { 'calendar-to-todo' }
    $baseName = $slug + '-' + $manifest.version
    $zip = Join-Path $OutputDirectory ($baseName + '.zip')
    $package = Join-Path $OutputDirectory ($baseName + '.rwplugin')
    Remove-Item -LiteralPath $zip,$package -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
    Move-Item -LiteralPath $zip -Destination $package
    $sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath ($package + '.sha256') -Value ($sha + '  ' + [IO.Path]::GetFileName($package)) -Encoding ascii
    Write-Host "Created $package"
}
finally {
    Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
}
'@

$repositoryGitIgnore = @'
dist/
bin/
obj/
*.exe
*.dll
*.pdb
*.log
*.tmp
config.json
secret.dat
state.json
PluginData/
PluginJobs/
PluginLogs/
PluginValues.inc
'@

if (Test-Path -LiteralPath $outputRoot) { Remove-Item -LiteralPath $outputRoot -Recurse -Force }
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'plugin-registry-template') -Force) {
    Copy-Item -LiteralPath $item.FullName -Destination $outputRoot -Recurse -Force
}
foreach ($definition in $definitions) {
    $source = Join-Path $projectRoot ('plugins\official\' + $definition.Folder)
    $target = Join-Path $outputRoot ('official-plugins\' + $definition.Folder)
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target -Recurse

    if ($definition.Folder -eq 'arxiv') {
        foreach ($name in @('Common.cs','TodoPaperService.cs','TodoPaperRssService.cs','TodoUpdateService.cs')) {
            Copy-Item -LiteralPath (Join-Path $projectRoot ('backend\' + $name)) -Destination (Join-Path $target 'src')
        }
        $projectPath = Join-Path $target 'src\ArxivPlugin.csproj'
        $projectText = Get-Content -LiteralPath $projectPath -Raw -Encoding UTF8
        foreach ($name in @('Common.cs','TodoPaperService.cs','TodoPaperRssService.cs','TodoUpdateService.cs')) {
            $projectText = $projectText.Replace(('..\..\..\..\backend\' + $name), $name)
        }
        Set-Content -LiteralPath $projectPath -Value $projectText -Encoding UTF8
    }

    Set-Content -LiteralPath (Join-Path $target 'Build-Package.ps1') -Value $buildScript -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $target '.gitignore') -Value $repositoryGitIgnore -Encoding UTF8

    if (-not $SkipBuild) {
        & (Join-Path $target 'Build-Package.ps1') -OutputDirectory (Join-Path $target 'dist')
    }
}

Write-Host "Exported the consolidated plugin registry repository to $outputRoot"
