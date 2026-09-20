param(
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'plugin-build'),
    [string]$PackageDirectory = '',
    [string]$LockPath = '',
    [switch]$IncludePrivate
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$msbuild = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) { $msbuild = Join-Path ([Environment]::GetFolderPath('Windows')) 'Microsoft.NET\Framework\v4.0.30319\MSBuild.exe' }
if (-not (Test-Path -LiteralPath $msbuild)) { throw 'MSBuild was not found.' }

$definitions = @(
    @{ Folder = 'calendar-to-todo'; Project = 'CalendarToTodoPlugin.csproj'; Exe = 'CalendarToTodoPlugin.exe' },
    @{ Folder = 'arxiv'; Project = 'ArxivPlugin.csproj'; Exe = 'ArxivPlugin.exe' },
    @{ Folder = 'paper-snapshot-sync'; Project = 'PaperSnapshotSyncPlugin.csproj'; Exe = 'PaperSnapshotSyncPlugin.exe' },
    @{ Folder = 'ai-deepseek'; Project = 'AiDeepSeekPlugin.csproj'; Exe = 'AiDeepSeekPlugin.exe' }
)
if ($IncludePrivate) {
    $definitions = @(@{ Folder = 'ssdp-server-ip'; Project = 'SsdpServerIpPlugin.csproj'; Exe = 'SsdpServerIpPlugin.exe' }) + $definitions
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$lockEntries = @()
foreach ($definition in $definitions) {
    $source = Join-Path $projectRoot ('plugins\official\' + $definition.Folder)
    $target = Join-Path $OutputDirectory $definition.Folder
    $bin = Join-Path $target 'bin'
    $obj = Join-Path ([IO.Path]::GetTempPath()) ('RainmeterPlugin-' + $definition.Folder + '-' + [guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Path $bin -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $source 'plugin.json') -Destination $target
    foreach ($optional in @('settings.schema.json','README.md','THIRD-PARTY-NOTICES.md','icon.png')) {
        $path = Join-Path $source $optional
        if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $target }
    }
    try {
        & $msbuild (Join-Path $source ('src\' + $definition.Project)) /nologo /verbosity:minimal /target:Build /property:Configuration=Release "/property:OutputPath=$bin\" "/property:IntermediateOutputPath=$obj\"
        if ($LASTEXITCODE -ne 0) { throw ($definition.Folder + ' plugin build failed.') }
        if (-not (Test-Path -LiteralPath (Join-Path $bin $definition.Exe))) { throw ($definition.Exe + ' was not created.') }
    }
    finally { Remove-Item -LiteralPath $obj -Recurse -Force -ErrorAction SilentlyContinue }
    if ($PackageDirectory) {
        $PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
        New-Item -ItemType Directory -Path $PackageDirectory -Force | Out-Null
        $manifest = Get-Content -LiteralPath (Join-Path $target 'plugin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $zip = Join-Path $PackageDirectory ($definition.Folder + '-' + $manifest.version + '.zip')
        $package = [IO.Path]::ChangeExtension($zip, '.rwplugin')
        if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
        if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
        Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip -CompressionLevel Optimal
        Move-Item -LiteralPath $zip -Destination $package
        $sha = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant()
        Set-Content -LiteralPath ($package + '.sha256') -Value ($sha + '  ' + [IO.Path]::GetFileName($package)) -Encoding ascii
        $lockEntries += [ordered]@{ id=$manifest.id; version=$manifest.version; file=[IO.Path]::GetFileName($package); sha256=$sha }
    }
}

if ($LockPath) {
    $lockFull=[IO.Path]::GetFullPath($LockPath);New-Item -ItemType Directory -Path (Split-Path $lockFull -Parent) -Force | Out-Null
    [ordered]@{version=1;plugins=$lockEntries}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $lockFull -Encoding UTF8
}

Write-Host "Built official plugins to $OutputDirectory"
