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
    # ServiceClient.cs（Broker 客户端）必须一起导出：arxiv 2.0.0 靠它调三个 Provider，
    # 漏掉它导出的仓库一编译就断（csproj 里那条 Link 会被替换成同名文件）。
    # TodoUpdateService.cs 自 2.0.0 起**不再**链进 arxiv：它现在只剩本体自己的升级/翻译凭据
    # 辅助函数，而 arxiv 两者都不再用（翻译走 translation_provider@1）。
    @{ Folder='arxiv'; Sources=@('Common.cs','PaperBundle.cs','ServiceClient.cs','TodoPaperService.cs','TodoPaperRssService.cs') },
    @{ Folder='calendar-to-todo' },
    @{ Folder='ssdp-server-ip' },
    @{ Folder='paper-snapshot-sync'; Sources=@('Common.cs','PaperBundle.cs') },
    @{ Folder='ai-deepseek'; Sources=@('Common.cs') },
    @{ Folder='translate-tencent'; Sources=@('Common.cs') }
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
    $slug = $manifest.id -replace '^io\.github\.kevendai\.', ''
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

    if ($definition.Sources) {
        foreach ($name in $definition.Sources) {
            Copy-Item -LiteralPath (Join-Path $projectRoot ('backend\' + $name)) -Destination (Join-Path $target 'src')
        }
        $projectFile = Get-ChildItem -LiteralPath (Join-Path $target 'src') -Filter '*.csproj' | Select-Object -First 1
        $projectText = Get-Content -LiteralPath $projectFile.FullName -Raw -Encoding UTF8
        foreach ($name in $definition.Sources) {
            $projectText = $projectText.Replace(('..\..\..\..\backend\' + $name), $name)
        }
        Set-Content -LiteralPath $projectFile.FullName -Value $projectText -Encoding UTF8
    }

    # 必须写成无 BOM 的 ASCII：Windows PowerShell 5.1 的 -Encoding UTF8 会写入 BOM，
    # 而 .gitignore 首行一旦带上 BOM，`dist/` 就不再被忽略（dist 里正是打包产物）。
    Set-Content -LiteralPath (Join-Path $target 'Build-Package.ps1') -Value $buildScript -Encoding ascii
    Set-Content -LiteralPath (Join-Path $target '.gitignore') -Value $repositoryGitIgnore -Encoding ascii

    if (-not $SkipBuild) {
        & (Join-Path $target 'Build-Package.ps1') -OutputDirectory (Join-Path $target 'dist')
    }
}

Write-Host "Exported the consolidated plugin registry repository to $outputRoot"
