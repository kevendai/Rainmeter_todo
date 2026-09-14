param(
    [ValidateSet('CheckAndInstall','InstallPackage','UpdateUpdater')]
    [string]$Mode = 'InstallPackage',
    [string]$Repository = 'kevendai/Rainmeter_todo',
    [string]$CurrentVersion = '',
    [string]$Flavor = 'full',
    [string]$FlavorName = '',
    [string]$PackageRoot,
    [string]$RainmeterRoot,
    [switch]$Activate,
    [switch]$AssumeYes,
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = 'Stop'
$UpdaterVersionPath = Join-Path $PSScriptRoot 'updater-version.txt'
if (-not (Test-Path -LiteralPath $UpdaterVersionPath -PathType Leaf)) { throw 'Updater version file is missing.' }
$UpdaterVersion = [IO.File]::ReadAllText($UpdaterVersionPath, [Text.UTF8Encoding]::new($false)).Trim()
if ($UpdaterVersion -notmatch '^\d+\.\d+$') { throw 'Updater version must use major.minor format.' }
$UserAgent = "RainmeterDesktopWidgetsUpdater/$UpdaterVersion"

function Normalize-Version {
    param([string]$Value)
    $text = if ($null -eq $Value) { '' } else { $Value.Trim() }
    if ($text.StartsWith('v', [StringComparison]::OrdinalIgnoreCase)) { $text = $text.Substring(1) }
    $match = [regex]::Match($text, '\d+(?:\.\d+){0,3}')
    if ($match.Success) { return $match.Value }
    return $text
}

function Get-VersionParts {
    param([string]$Value)
    $normalized = Normalize-Version $Value
    if ([string]::IsNullOrWhiteSpace($normalized)) { return @(0) }
    return @($normalized.Split('.') | ForEach-Object {
        $part = 0
        if ([int]::TryParse($_, [ref]$part)) { $part } else { 0 }
    })
}

function Compare-VersionText {
    param([string]$Left, [string]$Right)
    $a = Get-VersionParts $Left
    $b = Get-VersionParts $Right
    $count = [Math]::Max($a.Count, $b.Count)
    for ($i = 0; $i -lt $count; $i++) {
        $av = if ($i -lt $a.Count) { $a[$i] } else { 0 }
        $bv = if ($i -lt $b.Count) { $b[$i] } else { 0 }
        if ($av -ne $bv) { return ($av.CompareTo($bv)) }
    }
    return 0
}

function Get-SkinsRoot {
    param([string]$Root)
    $value = if ($null -eq $Root) { '' } else { $Root.Trim().Trim('"') }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = Read-Host 'Enter Rainmeter skin library directory, for example Documents\Rainmeter'
    }
    $value = $value.Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'RainmeterRoot cannot be empty.' }
    $skins = Join-Path $value 'Skins'
    $rainmeter = $value
    if ((Split-Path -Leaf $value) -eq 'Skins') {
        $skins = $value
        $rainmeter = Split-Path $value -Parent
    } elseif (Test-Path -LiteralPath (Join-Path $value 'Rainmeter.exe') -PathType Leaf) {
        $rainmeterIni = Join-Path $env:APPDATA 'Rainmeter\Rainmeter.ini'
        if (Test-Path -LiteralPath $rainmeterIni) {
            $match = [regex]::Match([IO.File]::ReadAllText($rainmeterIni), '(?m)^SkinPath=(.+)$')
            if ($match.Success) {
                $configured = [Environment]::ExpandEnvironmentVariables($match.Groups[1].Value.Trim().Trim('"'))
                if (-not [string]::IsNullOrWhiteSpace($configured)) { $skins = [IO.Path]::GetFullPath($configured) }
            }
        }
    }
    New-Item -ItemType Directory -Path $skins -Force | Out-Null
    return [pscustomobject]@{ RainmeterRoot = $rainmeter; SkinsRoot = $skins }
}

function Find-RainmeterExe {
    param([string]$Root)
    $rainmeterExe = Join-Path $Root 'Rainmeter.exe'
    if (Test-Path -LiteralPath $rainmeterExe) { return $rainmeterExe }
    $runningRainmeter = Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path } | Select-Object -First 1
    if ($null -ne $runningRainmeter) { return $runningRainmeter.Path }
    foreach ($key in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\Rainmeter.exe',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\Rainmeter.exe'
    )) {
        $appPath = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($null -ne $appPath -and -not [string]::IsNullOrWhiteSpace($appPath.'(default)')) { return $appPath.'(default)' }
    }
    return $rainmeterExe
}

function Install-UpdaterFiles {
    param([string]$SourcePackageRoot, [string]$TargetSkinsRoot)
    $sourceUpdater = Join-Path $SourcePackageRoot 'Updater'
    if (-not (Test-Path -LiteralPath $sourceUpdater)) { return }
    $targetUpdater = Join-Path $TargetSkinsRoot 'Todo\@Resources\Updater'
    New-Item -ItemType Directory -Path $targetUpdater -Force | Out-Null
    Copy-Item -Path (Join-Path $sourceUpdater '*') -Destination $targetUpdater -Recurse -Force
    Get-ChildItem -LiteralPath $targetUpdater -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
}

function Assert-PackageAndBundledPlugins {
    param([string]$SourcePackageRoot)
    $manifestPath = Join-Path $SourcePackageRoot 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'Package manifest.json is missing.' }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $packageVersion = Normalize-Version ([string]$manifest.version)
    if ($packageVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Package version must use x.y.z format.' }
    if ((Compare-VersionText ([string]$manifest.updater_version) $UpdaterVersion) -gt 0) { throw "Package requires updater $($manifest.updater_version), but this updater is $UpdaterVersion." }

    $resources = Join-Path $SourcePackageRoot 'Skins\Todo\@Resources'
    foreach ($versionPath in @((Join-Path $resources 'app-version.txt'), (Join-Path $SourcePackageRoot 'Skins\Calendar\@Resources\app-version.txt'))) {
        if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw "Package host version file is missing: $versionPath" }
        $hostVersion = [IO.File]::ReadAllText($versionPath, [Text.UTF8Encoding]::new($false)).Trim()
        if ($hostVersion -ne $packageVersion) { throw "Package host version $hostVersion does not match manifest $packageVersion." }
    }

    $lockPath = Join-Path $resources 'bundled-plugins.lock.json'
    $packageDirectory = Join-Path $resources 'BundledPluginPackages'
    $expandedDirectory = Join-Path $resources 'BundledPlugins'
    foreach ($required in @($lockPath, $packageDirectory, $expandedDirectory)) { if (-not (Test-Path -LiteralPath $required)) { throw "Bundled plugin resource is missing: $required" } }
    $lock = Get-Content -LiteralPath $lockPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([int]$lock.version -ne 1) { throw 'Unsupported bundled plugin lock version.' }
    $entries = @($lock.plugins)
    if ($entries.Count -eq 0) { throw 'Bundled plugin lock is empty.' }
    $expandedById = @{}
    foreach ($directory in Get-ChildItem -LiteralPath $expandedDirectory -Directory) {
        $pluginManifestPath = Join-Path $directory.FullName 'plugin.json'
        if (-not (Test-Path -LiteralPath $pluginManifestPath -PathType Leaf)) { throw "Bundled plugin manifest is missing: $($directory.Name)" }
        $pluginManifest = Get-Content -LiteralPath $pluginManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $pluginId = [string]$pluginManifest.id
        if ([string]::IsNullOrWhiteSpace($pluginId) -or $expandedById.ContainsKey($pluginId)) { throw "Invalid or duplicate bundled plugin id: $pluginId" }
        $expandedById[$pluginId] = [pscustomobject]@{ Directory = $directory.FullName; Manifest = $pluginManifest }
    }
    if ($expandedById.Count -ne $entries.Count) { throw 'Bundled plugin lock and expanded plugin count differ.' }
    $seen = @{}
    foreach ($entry in $entries) {
        $id = [string]$entry.id; $version = [string]$entry.version; $file = [string]$entry.file
        $expectedHash = ([string]$entry.sha256).ToLowerInvariant()
        if ($id -notmatch '^([A-Za-z0-9-]+\.){2,}[A-Za-z0-9-]+$' -or $seen.ContainsKey($id)) { throw "Invalid or duplicate plugin id in lock: $id" }
        $seen[$id] = $true
        if ($version -notmatch '^\d+\.\d+\.\d+$' -or [IO.Path]::GetFileName($file) -ne $file -or $file -notlike '*.rwplugin' -or $expectedHash -notmatch '^[0-9a-f]{64}$') { throw "Invalid bundled plugin lock entry: $id" }
        $pluginPackage = Join-Path $packageDirectory $file
        if (-not (Test-Path -LiteralPath $pluginPackage -PathType Leaf)) { throw "Bundled plugin package is missing: $file" }
        if ((Get-FileHash -LiteralPath $pluginPackage -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedHash) { throw "Bundled plugin SHA256 mismatch: $id" }
        if (-not $expandedById.ContainsKey($id)) { throw "Expanded bundled plugin is missing: $id" }
        $expanded = $expandedById[$id]; $pluginManifest = $expanded.Manifest
        if ([string]$pluginManifest.version -ne $version -or [int]$pluginManifest.api_version -ne 1) { throw "Bundled plugin manifest mismatch: $id" }
        if ((Compare-VersionText $packageVersion ([string]$pluginManifest.min_host_version)) -lt 0) { throw "Bundled plugin $id requires host $($pluginManifest.min_host_version)." }
        $entryRelative = ([string]$pluginManifest.entry).Replace('/', '\')
        if ([string]::IsNullOrWhiteSpace($entryRelative) -or [IO.Path]::IsPathRooted($entryRelative) -or $entryRelative.Split('\') -contains '..') { throw "Invalid bundled plugin entry: $id" }
        $entryPath = [IO.Path]::GetFullPath((Join-Path $expanded.Directory $entryRelative))
        $pluginRoot = [IO.Path]::GetFullPath($expanded.Directory).TrimEnd('\') + '\'
        if (-not $entryPath.StartsWith($pluginRoot, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $entryPath -PathType Leaf)) { throw "Bundled plugin entry is missing or escapes its directory: $id" }
    }
    return $entries
}

function Stop-ManagedPluginProcesses {
    $pluginRoot = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'RainmeterDesktopWidgets\Plugins')).TrimEnd('\') + '\'
    $jobsRoot = Join-Path $env:LOCALAPPDATA 'RainmeterDesktopWidgets\PluginJobs'
    if (-not (Test-Path -LiteralPath $jobsRoot -PathType Container)) { return }
    $managed = New-Object System.Collections.Generic.List[object]
    foreach ($jobFile in Get-ChildItem -LiteralPath $jobsRoot -Filter '*.json' -File -ErrorAction SilentlyContinue) {
        try {
            $job = Get-Content -LiteralPath $jobFile.FullName -Raw -Encoding UTF8 | ConvertFrom-Json
            $pidValue = [int]$job.pid
            $entry = [IO.Path]::GetFullPath([string]$job.entry)
            if ($pidValue -le 0 -or -not $entry.StartsWith($pluginRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $process = Get-Process -Id $pidValue -ErrorAction Stop
            if (-not [IO.Path]::GetFullPath($process.Path).Equals($entry, [StringComparison]::OrdinalIgnoreCase)) { $process.Dispose(); continue }
            try { $process.CloseMainWindow() | Out-Null } catch {}
            $managed.Add([pscustomobject]@{ Process = $process; JobFile = $jobFile.FullName; Job = $job })
        } catch {}
    }
    if ($managed.Count -gt 0) { Start-Sleep -Seconds 3 }
    foreach ($item in $managed) {
        try {
            if (-not $item.Process.HasExited) { $item.Process.Kill(); $item.Process.WaitForExit(3000) }
            $item.Job.state = 'cancelled'
            $item.Job.message = 'Cancelled for host update'
            $item.Job.PSObject.Properties.Remove('pid')
            $item.Job.PSObject.Properties.Remove('entry')
            $item.Job.PSObject.Properties.Remove('process_started_at')
            [IO.File]::WriteAllText($item.JobFile, ($item.Job | ConvertTo-Json -Depth 12 -Compress), [Text.UTF8Encoding]::new($false))
        } catch {}
        finally { try { $item.Process.Dispose() } catch {} }
    }
}

function New-PluginStateSnapshot {
    param([object[]]$BundledEntries, [string]$TransactionRoot)
    $snapshotRoot = Join-Path $TransactionRoot 'plugin-state'
    New-Item -ItemType Directory -Path $snapshotRoot -Force | Out-Null
    $localRoot = Join-Path $env:LOCALAPPDATA 'RainmeterDesktopWidgets'
    $paths = New-Object System.Collections.Generic.List[string]
    $paths.Add((Join-Path $localRoot 'migration-v2.json'))
    foreach ($entry in $BundledEntries) {
        $id = [string]$entry.id
        $paths.Add((Join-Path $localRoot ("Plugins\$id\current.json")))
        foreach ($name in @('config.json','secret.dat','state.json')) { $paths.Add((Join-Path $localRoot ("PluginData\$id\$name"))) }
    }
    $records = New-Object System.Collections.Generic.List[object]
    $index = 0
    foreach ($path in $paths) {
        $exists = Test-Path -LiteralPath $path -PathType Leaf
        $backup = Join-Path $snapshotRoot ("file-$index")
        if ($exists) { Copy-Item -LiteralPath $path -Destination $backup -Force }
        $records.Add([pscustomobject]@{ Path = $path; Existed = $exists; Backup = $backup })
        $index++
    }
    return $records.ToArray()
}

function Restore-PluginStateSnapshot {
    param([object[]]$Snapshot)
    if ($null -eq $Snapshot) { return }
    foreach ($record in $Snapshot) {
        if ([bool]$record.Existed) {
            New-Item -ItemType Directory -Path (Split-Path ([string]$record.Path) -Parent) -Force | Out-Null
            Copy-Item -LiteralPath ([string]$record.Backup) -Destination ([string]$record.Path) -Force
        } elseif (Test-Path -LiteralPath ([string]$record.Path) -PathType Leaf) {
            Remove-Item -LiteralPath ([string]$record.Path) -Force
        }
    }
}

function Assert-RainmeterConfigsActive {
    param([string[]]$Names)
    $iniPath = Join-Path $env:APPDATA 'Rainmeter\Rainmeter.ini'
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 250
        if (-not (Test-Path -LiteralPath $iniPath -PathType Leaf)) { continue }
        $ini = [IO.File]::ReadAllText($iniPath)
        $allActive = $true
        foreach ($name in $Names) {
            $section = [regex]::Match($ini, "(?ms)^\[$([regex]::Escape($name))\]\s*(.*?)(?=^\[|\z)")
            if (-not $section.Success -or -not [regex]::IsMatch($section.Value, '(?m)^Active=1\s*$')) { $allActive = $false; break }
        }
        if ($allActive) { return }
    } while ((Get-Date) -lt $deadline)
    throw "Rainmeter configs did not become active: $($Names -join ', ')"
}

function Install-Package {
    param([string]$SourcePackageRoot, [string]$Root, [switch]$ShouldActivate, [int]$WaitPid)
    if ([string]::IsNullOrWhiteSpace($SourcePackageRoot)) {
        $SourcePackageRoot = Split-Path $PSScriptRoot -Parent
    }
    $SourcePackageRoot = (Resolve-Path -LiteralPath $SourcePackageRoot).Path
    $bootstrapPath = Join-Path $SourcePackageRoot 'unified-bootstrap.json'
    if (Test-Path -LiteralPath $bootstrapPath) {
        $bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $bootstrapRepository = [string]$bootstrap.repository
        $bootstrapTag = [string]$bootstrap.tag
        $bootstrapAsset = [string]$bootstrap.asset
        if ([string]::IsNullOrWhiteSpace($bootstrapRepository) -or [string]::IsNullOrWhiteSpace($bootstrapTag) -or [string]::IsNullOrWhiteSpace($bootstrapAsset)) {
            throw 'Unified bootstrap metadata is incomplete.'
        }
        $bootstrapReleaseAsset = Get-VerifiedReleaseAsset -Repo $bootstrapRepository -Tag $bootstrapTag -AssetName $bootstrapAsset
        $bootstrapDownload = Join-Path ([IO.Path]::GetTempPath()) $bootstrapAsset
        $bootstrapExtract = Join-Path ([IO.Path]::GetTempPath()) ('RainmeterUnifiedUpdate-' + [guid]::NewGuid().ToString('N'))
        try {
            Invoke-WebRequestCompat -Uri $bootstrapReleaseAsset.Url -OutFile $bootstrapDownload -Headers @{ 'User-Agent' = $UserAgent } -TimeoutSec 120
            Assert-FileSha256 -Path $bootstrapDownload -Expected $bootstrapReleaseAsset.Sha256
            New-Item -ItemType Directory -Path $bootstrapExtract -Force | Out-Null
            Unblock-File -LiteralPath $bootstrapDownload -ErrorAction SilentlyContinue
            Expand-Archive -LiteralPath $bootstrapDownload -DestinationPath $bootstrapExtract -Force
            Get-ChildItem -LiteralPath $bootstrapExtract -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
            $unifiedRoot = Find-PackageRoot $bootstrapExtract
            Install-Package -SourcePackageRoot $unifiedRoot -Root $Root -ShouldActivate:$ShouldActivate -WaitPid $WaitPid
            return
        }
        finally {
            Remove-Item -LiteralPath $bootstrapExtract -Recurse -Force -ErrorAction SilentlyContinue
            if ((Split-Path -Leaf $bootstrapDownload) -like 'rainmeter-desktop-widgets-*.zip') {
                Remove-Item -LiteralPath $bootstrapDownload -Force -ErrorAction SilentlyContinue
            }
        }
    }
    $sourceSkins = Join-Path $SourcePackageRoot 'Skins'
    if (-not (Test-Path -LiteralPath $sourceSkins)) { throw 'Package Skins folder not found.' }
    $bundledEntries = @(Assert-PackageAndBundledPlugins $SourcePackageRoot)

    $roots = Get-SkinsRoot $Root
    $rainmeterExe = Find-RainmeterExe $roots.RainmeterRoot
    if ($WaitPid -gt 0) {
        try { Wait-Process -Id $WaitPid -Timeout 30 -ErrorAction SilentlyContinue } catch {}
    }

    $targetHostPaths = @(
        (Join-Path $roots.SkinsRoot 'Todo\@Resources\TodoHost.exe'),
        (Join-Path $roots.SkinsRoot 'Calendar\@Resources\CalendarHost.exe'),
        (Join-Path $roots.SkinsRoot 'Todo\@Resources\PluginHost.exe')
    )
    foreach ($hostProcess in Get-Process -Name TodoHost,CalendarHost,PluginHost -ErrorAction SilentlyContinue) {
        if ($targetHostPaths -contains $hostProcess.Path) {
            try { $hostProcess.CloseMainWindow() | Out-Null } catch {}
        }
    }
    Start-Sleep -Milliseconds 800
    foreach ($hostProcess in Get-Process -Name TodoHost,CalendarHost,PluginHost -ErrorAction SilentlyContinue) {
        if ($targetHostPaths -contains $hostProcess.Path) {
            try {
                if (-not $hostProcess.HasExited) { $hostProcess.Kill() }
                $hostProcess.WaitForExit(3000)
            } catch {}
        }
    }
    Stop-ManagedPluginProcesses

    foreach ($skin in @('Todo', 'Calendar')) {
        $target = Join-Path $roots.SkinsRoot $skin
        if (-not (Test-Path -LiteralPath $target)) {
            $recovery = Get-ChildItem -LiteralPath $roots.SkinsRoot -Directory -Filter '.rainmeter-update-*' -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTimeUtc -Descending | ForEach-Object { Join-Path $_.FullName ("backup\$skin") } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
            if ($recovery) { Move-Item -LiteralPath $recovery -Destination $target; Write-Warning "Recovered interrupted $skin update from $recovery" }
        }
    }

    $transaction = Join-Path $roots.SkinsRoot ('.rainmeter-update-' + [guid]::NewGuid().ToString('N'))
    $swapped = New-Object System.Collections.Generic.List[string]
    $installSucceeded = $false
    $preservedHashesBySkin = @{}
    $pluginSnapshot = $null
    try {
        $pluginSnapshot = @(New-PluginStateSnapshot $bundledEntries $transaction)
        foreach ($skin in @('Todo', 'Calendar')) {
            $source = Join-Path $sourceSkins $skin
            $target = Join-Path $roots.SkinsRoot $skin
            $stage = Join-Path $transaction ("stage\$skin")
            New-Item -ItemType Directory -Path $stage -Force | Out-Null
            Copy-Item -Path (Join-Path $source '*') -Destination $stage -Recurse -Force
            $skinHashes = @{}
            foreach ($name in @('tasks.json','ui-scale.txt','ui-window-scale.txt','calendar-cache.json','calendar-state.json','caldav.secret','translation.secret','paper-sync.secret')) {
                $current = Join-Path $target ('@Resources\' + $name)
                if (Test-Path -LiteralPath $current) { $skinHashes[$name] = (Get-FileHash -LiteralPath $current -Algorithm SHA256).Hash; $destination = Join-Path $stage ('@Resources\' + $name); New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null; Copy-Item -LiteralPath $current -Destination $destination -Force }
            }
            $preservedHashesBySkin[$skin] = $skinHashes
            Remove-Item -LiteralPath (Join-Path $stage '@Resources\UiScale.inc') -Force -ErrorAction SilentlyContinue
            Get-ChildItem -LiteralPath $stage -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
        }
        $sourceUpdater = Join-Path $SourcePackageRoot 'Updater'
        if (Test-Path -LiteralPath $sourceUpdater) {
            $stagedUpdater = Join-Path $transaction 'stage\Todo\@Resources\Updater'
            New-Item -ItemType Directory -Path $stagedUpdater -Force | Out-Null
            Copy-Item -Path (Join-Path $sourceUpdater '*') -Destination $stagedUpdater -Recurse -Force
        }
        foreach ($skin in @('Todo', 'Calendar')) { foreach ($name in $preservedHashesBySkin[$skin].Keys) { $stagedData = Join-Path $transaction ("stage\$skin\@Resources\$name"); if (-not (Test-Path -LiteralPath $stagedData) -or (Get-FileHash -LiteralPath $stagedData -Algorithm SHA256).Hash -ne $preservedHashesBySkin[$skin][$name]) { throw "Staged user data verification failed: $skin/$name" } } }
        $rainmeterWasRunning = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $rainmeterExe }).Count -gt 0
        if ($rainmeterWasRunning) { & $rainmeterExe '!Quit'; $deadline = (Get-Date).AddSeconds(10); do { Start-Sleep -Milliseconds 250; $remainingRainmeter = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $rainmeterExe }) } while ($remainingRainmeter.Count -gt 0 -and (Get-Date) -lt $deadline); if ($remainingRainmeter.Count -gt 0) { throw 'Rainmeter did not exit within 10 seconds.' } }
        foreach ($skin in @('Todo', 'Calendar')) {
            $target = Join-Path $roots.SkinsRoot $skin
            $stage = Join-Path $transaction ("stage\$skin")
            $backup = Join-Path $transaction ("backup\$skin")
            if (Test-Path -LiteralPath $target) { New-Item -ItemType Directory -Path (Split-Path $backup -Parent) -Force | Out-Null; Move-Item -LiteralPath $target -Destination $backup }
            Move-Item -LiteralPath $stage -Destination $target
            $swapped.Add($skin)
            foreach ($name in $preservedHashesBySkin[$skin].Keys) { $liveData = Join-Path $target ("@Resources\$name"); if (-not (Test-Path -LiteralPath $liveData) -or (Get-FileHash -LiteralPath $liveData -Algorithm SHA256).Hash -ne $preservedHashesBySkin[$skin][$name]) { throw "Installed user data verification failed: $skin/$name" } }
        }
        $installedTodoHost=Join-Path $roots.SkinsRoot 'Todo\@Resources\TodoHost.exe';$installedCalendarHost=Join-Path $roots.SkinsRoot 'Calendar\@Resources\CalendarHost.exe';$installedPluginHost=Join-Path $roots.SkinsRoot 'Todo\@Resources\PluginHost.exe'
        foreach($requiredHost in @($installedTodoHost,$installedCalendarHost,$installedPluginHost)){if(-not (Test-Path -LiteralPath $requiredHost -PathType Leaf)){throw "Installed host is missing: $requiredHost"}}
        foreach($probe in @(@{File=$installedPluginHost;Args='SelfTest';Name='PluginHost'},@{File=$installedTodoHost;Args='Render';Name='TodoHost'},@{File=$installedCalendarHost;Args='Render';Name='CalendarHost'})){$process=Start-Process -FilePath $probe.File -ArgumentList $probe.Args -WindowStyle Hidden -PassThru;if(-not $process.WaitForExit(30000)){try{$process.Kill()}catch{};throw "$($probe.Name) validation timed out"};if($process.ExitCode -ne 0){throw "$($probe.Name) validation failed with exit code $($process.ExitCode)"}}
    }
    catch {
        Restore-PluginStateSnapshot $pluginSnapshot
        [array]$rollbackSkins = @($swapped)
        [array]::Reverse($rollbackSkins)
        foreach ($skin in $rollbackSkins) {
            $target = Join-Path $roots.SkinsRoot $skin
            $backup = Join-Path $transaction ("backup\$skin")
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $target }
        }
        throw
    }

    try {
        if (Test-Path -LiteralPath $rainmeterExe) {
            $running = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $rainmeterExe })
            if ($running.Count -gt 0) {
                & $rainmeterExe '!Quit'
                Start-Sleep -Milliseconds 1200
                $remaining = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $rainmeterExe })
                foreach ($process in $remaining) {
                    try { $process.Kill(); $process.WaitForExit(3000) } catch {}
                }
            }
            Start-Process -FilePath $rainmeterExe | Out-Null
            Start-Sleep -Milliseconds 1200
            & $rainmeterExe '!RefreshApp'
            if ($ShouldActivate) {
                Start-Sleep -Milliseconds 800
                & $rainmeterExe '!ActivateConfig' 'Todo' 'Todo.ini'
                & $rainmeterExe '!ActivateConfig' 'Calendar' 'Calendar.ini'
                Start-Sleep -Milliseconds 800
                & $rainmeterExe '!SetWindowPosition' '100%' '0%' '100%' '0%' 'Todo'
                Assert-RainmeterConfigsActive @('Todo','Calendar')
            }
        } else {
            Write-Warning 'Rainmeter.exe was not found. Skins were copied, but Rainmeter was not restarted automatically.'
        }
        Remove-Item -LiteralPath $transaction -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "Installed skins transactionally to $($roots.SkinsRoot)"
    }
    catch {
        Restore-PluginStateSnapshot $pluginSnapshot
        [array]$rollbackSkins = @($swapped)
        [array]::Reverse($rollbackSkins)
        foreach ($skin in $rollbackSkins) {
            $target = Join-Path $roots.SkinsRoot $skin
            $backup = Join-Path $transaction ("backup\$skin")
            if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
            if (Test-Path -LiteralPath $backup) { Move-Item -LiteralPath $backup -Destination $target }
        }
        if (Test-Path -LiteralPath $rainmeterExe) { try { Start-Process -FilePath $rainmeterExe | Out-Null } catch {} }
        throw
    }
}

function Get-LatestTag {
    param([string]$Repo)
    $tags = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/tags" -Headers @{ 'User-Agent' = $UserAgent; 'Accept' = 'application/vnd.github+json' } -TimeoutSec 20
    $best = ''
    foreach ($tag in $tags) {
        $name = [string]$tag.name
        if (-not ((Normalize-Version $name) -match '^\d')) { continue }
        if ($best -eq '' -or (Compare-VersionText $name $best) -gt 0) { $best = $name }
    }
    if ($best -eq '') { throw 'No version tag was found on GitHub.' }
    return $best
}

function Get-VerifiedReleaseAsset {
    param([string]$Repo, [string]$Tag, [string]$AssetName)
    $headers = @{ 'User-Agent' = $UserAgent; 'Accept' = 'application/vnd.github+json' }
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/tags/$([Uri]::EscapeDataString($Tag))" -Headers $headers -TimeoutSec 20
    $asset = @($release.assets | Where-Object { [string]$_.name -eq $AssetName } | Select-Object -First 1)
    $checksumName = $AssetName + '.sha256'
    $checksumAsset = @($release.assets | Where-Object { [string]$_.name -eq $checksumName } | Select-Object -First 1)
    if ($asset.Count -eq 0) { throw "GitHub Release asset not found: $AssetName" }
    if ($checksumAsset.Count -eq 0) { throw "GitHub Release checksum not found: $checksumName" }
    $checksumResponse = Invoke-WebRequestCompat -Uri ([string]$checksumAsset[0].browser_download_url) -Headers @{ 'User-Agent' = $UserAgent } -TimeoutSec 20
    $checksumContent = $checksumResponse.Content
    $checksumText = if ($checksumContent -is [byte[]]) {
        [Text.Encoding]::UTF8.GetString($checksumContent)
    } else {
        [string]$checksumContent
    }
    $match = [regex]::Match($checksumText.Trim(), '(?i)^([0-9a-f]{64})(?:\s+\*?.+)?$')
    if (-not $match.Success) { throw "Invalid SHA256 file: $checksumName" }
    return [pscustomobject]@{ Url = [string]$asset[0].browser_download_url; Sha256 = $match.Groups[1].Value.ToUpperInvariant() }
}

function Assert-FileSha256 {
    param([string]$Path, [string]$Expected)
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $Expected.ToUpperInvariant()) { throw "SHA256 verification failed for $(Split-Path -Leaf $Path)." }
}

function Invoke-WebRequestCompat {
    param(
        [string]$Uri,
        [string]$Method = 'GET',
        [string]$OutFile = '',
        [hashtable]$Headers = @{},
        [int]$TimeoutSec = 20
    )
    $parameters = @{
        Uri = $Uri
        Method = $Method
        Headers = $Headers
        TimeoutSec = $TimeoutSec
    }
    if (-not [string]::IsNullOrWhiteSpace($OutFile)) { $parameters.OutFile = $OutFile }
    if ((Get-Command Invoke-WebRequest).Parameters.ContainsKey('UseBasicParsing')) {
        $parameters.UseBasicParsing = $true
    }
    Invoke-WebRequest @parameters
}

function Find-PackageRoot {
    param([string]$ExtractRoot)
    $manifest = Get-ChildItem -LiteralPath $ExtractRoot -Recurse -Filter 'manifest.json' -File |
        Where-Object {
            $root = $_.Directory.FullName
            (Test-Path -LiteralPath (Join-Path $root 'Skins')) -and
            (Test-Path -LiteralPath (Join-Path $root 'Updater\RainmeterDesktopWidgetsUpdater.ps1'))
        } |
        Select-Object -First 1
    if ($null -eq $manifest) { throw 'Complete update package (manifest, Skins, and updater) not found.' }
    return $manifest.Directory.FullName
}

function Show-Message {
    param([string]$Text, [string]$Caption = 'Rainmeter Desktop Widgets')
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Text, $Caption, [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    } catch {
        Write-Host $Text
    }
}

function Confirm-Update {
    param([string]$Text)
    try {
        Add-Type -AssemblyName System.Windows.Forms
        $result = [System.Windows.Forms.MessageBox]::Show($Text, 'Rainmeter Desktop Widgets Update', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question)
        return $result -eq [System.Windows.Forms.DialogResult]::Yes
    } catch {
        $answer = Read-Host "$Text [y/N]"
        return $answer -match '^(y|yes)$'
    }
}

function Check-And-Install {
    $latestTag = Get-LatestTag $Repository
    $latestVersion = Normalize-Version $latestTag
    $current = Normalize-Version $CurrentVersion
    $comparison = Compare-VersionText $latestVersion $current
    if ($comparison -le 0) {
        if ($comparison -eq 0) { Show-Message "Already on latest version: $latestTag" }
        else { Show-Message "Current version $CurrentVersion is newer than latest tag $latestTag." }
        return
    }

    $prompt = "New version $latestTag (Unified) is available.`r`n`r`nDownload and install now? Rainmeter will restart."
    if (-not $AssumeYes -and -not (Confirm-Update $prompt)) {
        Show-Message "Update canceled: $latestTag"
        return
    }

    # Flavor is retained only as a command-line compatibility parameter for
    # older TodoHost builds. Every installed edition now downloads the same
    # canonical package.
    $assetName = "rainmeter-desktop-widgets-$latestVersion.zip"
    $releaseAsset = Get-VerifiedReleaseAsset -Repo $Repository -Tag $latestTag -AssetName $assetName

    $downloads = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) 'Downloads'
    New-Item -ItemType Directory -Path $downloads -Force | Out-Null
    $zipPath = Join-Path $downloads $assetName
    $extractRoot = Join-Path $env:TEMP ('RainmeterDesktopWidgetsUpdate-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-WebRequestCompat -Uri $releaseAsset.Url -OutFile $zipPath -Headers @{ 'User-Agent' = $UserAgent } -TimeoutSec 120
        Assert-FileSha256 -Path $zipPath -Expected $releaseAsset.Sha256
        New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
        Unblock-File -LiteralPath $zipPath -ErrorAction SilentlyContinue
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractRoot -Force
        Get-ChildItem -LiteralPath $extractRoot -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

        $newPackageRoot = Find-PackageRoot $extractRoot
        $roots = Get-SkinsRoot $RainmeterRoot
        $newUpdater = Join-Path $newPackageRoot 'Updater\RainmeterDesktopWidgetsUpdater.ps1'
        if (-not (Test-Path -LiteralPath $newUpdater)) { throw 'Updater script not found in update package.' }
        & powershell -NoProfile -ExecutionPolicy Bypass -File $newUpdater -Mode UpdateUpdater -PackageRoot $newPackageRoot -RainmeterRoot $roots.RainmeterRoot

        $installedUpdater = Join-Path $roots.SkinsRoot 'Todo\@Resources\Updater\RainmeterDesktopWidgetsUpdater.ps1'
        if (-not (Test-Path -LiteralPath $installedUpdater)) { throw 'Installed updater script was not found after self-update.' }
        $installArgs = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$installedUpdater,'-Mode','InstallPackage','-PackageRoot',$newPackageRoot,'-RainmeterRoot',$roots.RainmeterRoot,'-WaitForProcessId',$WaitForProcessId)
        if ($Activate) { $installArgs += '-Activate' }
        & powershell @installArgs
    }
    finally {
        Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue
        if ((Split-Path -Leaf $zipPath) -like 'rainmeter-desktop-widgets-*.zip') {
            Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($Mode -eq 'UpdateUpdater') {
    $roots = Get-SkinsRoot $RainmeterRoot
    if ([string]::IsNullOrWhiteSpace($PackageRoot)) { $PackageRoot = Split-Path $PSScriptRoot -Parent }
    Install-UpdaterFiles ((Resolve-Path -LiteralPath $PackageRoot).Path) $roots.SkinsRoot
    Write-Host "Updated updater to version $UpdaterVersion"
    return
}

if ($Mode -eq 'InstallPackage') {
    Install-Package -SourcePackageRoot $PackageRoot -Root $RainmeterRoot -ShouldActivate:$Activate -WaitPid $WaitForProcessId
    return
}

Check-And-Install
