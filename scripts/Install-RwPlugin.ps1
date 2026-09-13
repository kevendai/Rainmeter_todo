param(
    [Parameter(Mandatory=$true)][string]$Package,
    [Parameter(Mandatory=$true)][string]$PluginRoot,
    [string]$ExpectedSha256 = ''
)
$ErrorActionPreference='Stop'
$packagePath=[IO.Path]::GetFullPath($Package)
$root=[IO.Path]::GetFullPath($PluginRoot)
if(-not (Test-Path -LiteralPath $packagePath -PathType Leaf)){throw '插件包不存在。'}
if((Get-Item -LiteralPath $packagePath).Length -gt 64MB){throw '插件包超过 64 MB 限制。'}
$actual=(Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
if($ExpectedSha256 -and $actual -ne $ExpectedSha256.Trim().ToLowerInvariant()){throw '插件包 SHA256 不匹配。'}
[void][Reflection.Assembly]::LoadWithPartialName('System.IO.Compression')
[void][Reflection.Assembly]::LoadWithPartialName('System.IO.Compression.FileSystem')
$zip=[IO.Compression.ZipFile]::OpenRead($packagePath)
$stage=Join-Path ([IO.Path]::GetTempPath()) ('rwplugin-'+[guid]::NewGuid().ToString('N'))
try {
    if($zip.Entries.Count -gt 4096){throw '插件包文件数量超过限制。'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    [long]$expanded=0
    foreach($entry in $zip.Entries){
        $name=$entry.FullName.Replace('/','\')
        if([IO.Path]::IsPathRooted($name) -or $name -match '(^|\\)\.\.(\\|$)'){throw "插件包含不安全路径：$name"}
        if(-not $seen.Add($name)){throw "插件包含重复路径：$name"}
        if($entry.Length -gt 32MB){throw "插件文件超过 32 MB 限制：$name"}
        $expanded += $entry.Length
        if($expanded -gt 256MB){throw '插件解压后大小超过 256 MB 限制。'}
    }
    New-Item -ItemType Directory -Path $stage | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToDirectory($zip,$stage)
} finally {$zip.Dispose()}
try {
    $manifestPath=Join-Path $stage 'plugin.json'
    if(-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw '插件缺少 plugin.json。'}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if($manifest.id -notmatch '^[a-z0-9]+(?:[.-][a-z0-9]+)+$'){throw '插件 ID 格式无效。'}
    if($manifest.version -notmatch '^\d+\.\d+\.\d+$'){throw '插件版本必须为 x.y.z。'}
    if([int]$manifest.api_version -ne 1){throw '不支持的 Plugin API 版本。'}
    try {if([version]([string]$manifest.min_host_version) -gt [version]'2.0.0'){throw '插件要求更高版本的宿主。'}} catch [System.Management.Automation.RuntimeException] {throw}
    $allowed=@('todo_source','todo_transform','value_provider')
    if(-not $manifest.capabilities -or @($manifest.capabilities|Where-Object{$_ -notin $allowed}).Count){throw '插件 capability 无效。'}
    if(-not $manifest.entry -or [IO.Path]::IsPathRooted([string]$manifest.entry) -or [string]$manifest.entry -match '(^|[\\/])\.\.([\\/]|$)'){throw '插件入口路径无效。'}
    $entryPath=[IO.Path]::GetFullPath((Join-Path $stage ([string]$manifest.entry)))
    if(-not $entryPath.StartsWith(([IO.Path]::GetFullPath($stage).TrimEnd('\')+'\'),[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $entryPath -PathType Leaf)){throw '插件入口不存在或超出插件目录。'}
    $pluginDir=Join-Path $root ([string]$manifest.id);$versions=Join-Path $pluginDir 'versions';$destination=Join-Path $versions ([string]$manifest.version)
    New-Item -ItemType Directory -Path $versions -Force | Out-Null
    if(Test-Path -LiteralPath $destination){Remove-Item -LiteralPath $stage -Recurse -Force;$stage=$null}else{Move-Item -LiteralPath $stage -Destination $destination;$stage=$null}
    $currentPath=Join-Path $pluginDir 'current.json';$temporary=$currentPath+'.tmp';$wasEnabled=$false
    if(Test-Path -LiteralPath $currentPath){try{$wasEnabled=[bool]((Get-Content -LiteralPath $currentPath -Raw -Encoding UTF8|ConvertFrom-Json).enabled)}catch{$wasEnabled=$false}}
    @{version=[string]$manifest.version;enabled=$wasEnabled}|ConvertTo-Json|Set-Content -LiteralPath $temporary -Encoding UTF8
    Move-Item -LiteralPath $temporary -Destination $currentPath -Force
    [pscustomobject]@{id=$manifest.id;version=$manifest.version;sha256=$actual}|ConvertTo-Json -Compress
} finally {if($stage -and (Test-Path -LiteralPath $stage)){Remove-Item -LiteralPath $stage -Recurse -Force}}
