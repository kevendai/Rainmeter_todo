<#
    Legacy compatibility updater for Rainmeter Desktop Widgets.

    This script is the only real payload of the tiny "full"/"lite"
    compatibility archives that are served from releases/<tag>/ of the
    repository.  Clients from the pre-2.0 generations (for example the released
    v1.3.5 build) download one of those archives from raw.githubusercontent.com
    and then run two steps:

        1. <package>\Updater\RainmeterDesktopWidgetsUpdater.ps1 -Mode UpdateUpdater
           installs this very file as the resident updater, and
        2. the installed copy is invoked with
           -Mode InstallPackage -PackageRoot <package> -RainmeterRoot <root>
           -WaitForProcessId <host pid> [-Activate]

    The compatibility archive carries no application payload.  This script
    fetches the canonical package (rainmeter-desktop-widgets-<TargetVersion>.zip)
    from the GitHub Release, verifies its SHA256 checksum, releases every file
    lock inside the target skin library, merges the new skin files in while
    keeping the user's own data byte for byte, installs the canonical updater
    (RainmeterDesktopWidgetsUpdater.ps1 + UpdaterHost.exe), and restarts
    Rainmeter.

    Every string is ASCII on purpose: this file is executed by unknown Windows
    PowerShell hosts whose active code page is unknown.

    IMPORTANT: the build script rewrites __TARGET_VERSION__ with the release
    that shipped this archive.  Never point that version at a tag whose
    releases/<tag>/rainmeter-desktop-widgets-<tag>.zip is another compatibility
    archive: the pre-2.0 updaters follow the bootstrap file through the raw
    channel, so such a target would hand them this very archive again and loop.
#>
param(
    [ValidateSet('CheckAndInstall','InstallPackage','UpdateUpdater')]
    [string]$Mode = 'InstallPackage',
    [string]$Repository = 'kevendai/Rainmeter_todo',
    [string]$CurrentVersion = '',
    [string]$Flavor = 'full',
    [string]$FlavorName = '',
    [string]$TargetVersion = '__TARGET_VERSION__',
    [string]$PackageRoot = '',
    [string]$RainmeterRoot = '',
    [switch]$Activate,
    [switch]$AssumeYes,
    [switch]$VerifyOnly,
    [int]$WaitForProcessId = 0
)

$ErrorActionPreference = 'Stop'
$UserAgent = 'RainmeterDesktopWidgetsLegacyCompat/1'
$LogPath = Join-Path ([IO.Path]::GetTempPath()) 'RainmeterDesktopWidgets-legacy-compat.log'

# Windows cannot rename a directory that is the current directory of a live
# process.  This script is started from inside the skin folder and then waits
# for the installer, so it has to leave the skin tree before doing any work.
if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) { try { $PackageRoot = (Resolve-Path -LiteralPath $PackageRoot).Path } catch { } }
try { Set-Location -LiteralPath ([IO.Path]::GetTempPath()) } catch { }

# Names inside Skins\<skin>\@Resources that belong to the user.  They are read
# before the package is copied over the target and written back afterwards, so
# a release that ships a placeholder copy of them can never clear user data.
$PreservedResourceNames = @(
    'tasks.json',
    'Generated.inc',
    'PluginValues.inc',
    'UiScale.inc',
    'ui-scale.txt',
    'ui-window-scale.txt',
    'ui-theme.txt',
    'calendar-cache.json',
    'calendar-state.json',
    'caldav.secret',
    'translation.secret',
    'paper-sync.secret',
    '.refresh-guard'
)

$SkinNames = @('Todo', 'Calendar')

function Write-Step {
    param([string]$Text)
    $line = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $Text
    Write-Host $line
    try { [IO.File]::AppendAllText($LogPath, $line + [Environment]::NewLine, [Text.UTF8Encoding]::new($false)) } catch { }
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

function Get-BytesSha256 {
    param([byte[]]$Bytes)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-SkinsRoot {
    param([string]$Root)
    $value = if ($null -eq $Root) { '' } else { $Root.Trim().Trim('"') }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)) 'Rainmeter'
        Write-Step "RainmeterRoot was not supplied; falling back to $value"
    }
    if ([string]::IsNullOrWhiteSpace($value)) { throw 'RainmeterRoot cannot be empty.' }
    $skins = Join-Path $value 'Skins'
    $rainmeter = $value
    if ((Split-Path -Leaf $value) -eq 'Skins') {
        $skins = $value
        $rainmeter = Split-Path $value -Parent
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

function Get-ConfiguredProxy {
    # Windows PowerShell already resolves the machine's system proxy on its own,
    # so this only covers hosts that export an explicit proxy instead - for
    # example a machine where GitHub is reachable through a local proxy only.
    # When one is exported it is tried before the system route.
    foreach ($name in @('HTTPS_PROXY', 'https_proxy', 'HTTP_PROXY', 'http_proxy')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if (-not [string]::IsNullOrWhiteSpace($value)) { return $value.Trim() }
    }
    return ''
}

function Invoke-WebRequestCompat {
    param(
        [string]$Uri,
        [string]$Method = 'GET',
        [string]$OutFile = '',
        [hashtable]$Headers = @{},
        [int]$TimeoutSec = 60,
        [string]$Proxy = ''
    )
    $parameters = @{
        Uri = $Uri
        Method = $Method
        Headers = $Headers
        TimeoutSec = $TimeoutSec
    }
    if (-not [string]::IsNullOrWhiteSpace($OutFile)) { $parameters.OutFile = $OutFile }
    if (-not [string]::IsNullOrWhiteSpace($Proxy)) { $parameters.Proxy = $Proxy }
    if ((Get-Command Invoke-WebRequest).Parameters.ContainsKey('UseBasicParsing')) {
        $parameters.UseBasicParsing = $true
    }
    Invoke-WebRequest @parameters
}

function Get-CanonicalPackage {
    param([string]$Repo, [string]$Version, [string]$WorkRoot)
    if ($Version -notmatch '^\d+(\.\d+){1,3}$') {
        throw "Compatibility updater target version is invalid: $Version"
    }
    $asset = "rainmeter-desktop-widgets-$Version.zip"
    $base = "https://github.com/$Repo/releases/download/v$Version/$([Uri]::EscapeDataString($asset))"
    $zip = Join-Path $WorkRoot $asset
    $checksum = Join-Path $WorkRoot ($asset + '.sha256')

    # A host reaches GitHub either through the Windows system proxy or through
    # an explicitly exported one.  The tiny checksum sidecar is fetched first:
    # it reveals which route works, and a dead route fails in seconds instead of
    # stalling the multi-megabyte download behind one long timeout.
    $proxies = @()
    $environmentProxy = Get-ConfiguredProxy
    if ($environmentProxy -ne '') { $proxies += $environmentProxy }
    $proxies += ''

    $checksumText = ''
    $usedProxy = ''
    $lastError = ''
    foreach ($proxy in $proxies) {
        $label = if ($proxy -eq '') { 'system proxy' } else { $proxy }
        try {
            Write-Step "Contacting the $Version release via $label"
            Invoke-WebRequestCompat -Uri ($base + '.sha256') -OutFile $checksum -Headers @{ 'User-Agent' = $UserAgent } -TimeoutSec 20 -Proxy $proxy
            $checksumText = [string](Get-Content -LiteralPath $checksum -Raw)
            $usedProxy = $proxy
            $lastError = ''
            break
        } catch {
            $lastError = $_.Exception.Message
            Write-Step ('Connection failed via ' + $label + ': ' + $lastError)
        }
    }
    if ([string]::IsNullOrWhiteSpace($checksumText)) {
        throw "Unable to reach the $Version release of $Repo ($lastError). Check the network and the proxy settings."
    }
    $expected = ([regex]::Match($checksumText, '(?i)\b[0-9a-f]{64}\b')).Value.ToLowerInvariant()
    if ($expected.Length -ne 64) { throw 'Update package checksum file is invalid.' }

    $label = if ($usedProxy -eq '') { 'system proxy' } else { $usedProxy }
    Write-Step "Downloading $asset via $label"
    try {
        Invoke-WebRequestCompat -Uri $base -OutFile $zip -Headers @{ 'User-Agent' = $UserAgent } -TimeoutSec 300 -Proxy $usedProxy
    } catch {
        throw ('Downloading ' + $asset + ' failed: ' + $_.Exception.Message)
    }
    if (-not (Test-Path -LiteralPath $zip -PathType Leaf)) { throw "Downloaded package is missing: $zip" }
    $actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw 'Update package SHA256 verification failed.' }
    Write-Step "Verified SHA256 $actual"
    Unblock-File -LiteralPath $zip -ErrorAction SilentlyContinue
    return $zip
}

function Expand-CanonicalPackage {
    param([string]$Zip, [string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Expand-Archive -LiteralPath $Zip -DestinationPath $Destination -Force
    Get-ChildItem -LiteralPath $Destination -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue
    foreach ($required in @('Skins\Todo', 'Skins\Calendar', 'Updater\UpdaterHost.exe', 'Updater\RainmeterDesktopWidgetsUpdater.ps1')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Destination $required))) {
            throw "Downloaded package is incomplete: $required is missing."
        }
    }
}

function Wait-ForProcessExit {
    param([int]$TargetProcessId, [int]$TimeoutSec = 20)
    if ($TargetProcessId -le 0) { return }
    try { Wait-Process -Id $TargetProcessId -Timeout $TimeoutSec -ErrorAction SilentlyContinue } catch { }
}

function Stop-SkinProcesses {
    param([string]$SkinsRoot)
    $prefix = $SkinsRoot.TrimEnd('\') + '\'
    $names = @('TodoHost', 'CalendarHost', 'PluginHost')
    for ($pass = 0; $pass -lt 4; $pass++) {
        $matched = @()
        foreach ($name in $names) {
            foreach ($process in @(Get-Process -Name $name -ErrorAction SilentlyContinue)) {
                $path = ''
                try { $path = [string]$process.Path } catch { $path = '' }
                if ($path.Length -gt 0 -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    $matched += ,$process
                }
            }
        }
        if ($matched.Count -eq 0) { return }
        if ($pass -eq 0) {
            Write-Step ('Closing ' + $matched.Count + ' widget host process(es)')
            foreach ($process in $matched) { try { $process.CloseMainWindow() | Out-Null } catch { } }
            Start-Sleep -Milliseconds 800
            continue
        }
        foreach ($process in $matched) {
            try {
                if (-not $process.HasExited) { $process.Kill() }
                $process.WaitForExit(4000)
            } catch { }
        }
        Start-Sleep -Milliseconds 600
    }
    Write-Warning 'Some widget host processes did not exit; file replacement may fail.'
}

function Stop-Rainmeter {
    param([string]$RainmeterExe)
    if ([string]::IsNullOrWhiteSpace($RainmeterExe)) { return }
    if (-not (Test-Path -LiteralPath $RainmeterExe)) { return }
    $running = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $RainmeterExe })
    if ($running.Count -eq 0) { return }
    Write-Step 'Asking Rainmeter to quit'
    try { & $RainmeterExe '!Quit' } catch { }
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 300
        $running = @(Get-Process -Name Rainmeter -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $RainmeterExe })
    } while ($running.Count -gt 0 -and (Get-Date) -lt $deadline)
    foreach ($process in $running) {
        Write-Step 'Rainmeter did not quit in time; terminating it'
        try {
            if (-not $process.HasExited) { $process.Kill() }
            $process.WaitForExit(5000)
        } catch { }
    }
    Start-Sleep -Milliseconds 500
}

function Wait-FileUnlocked {
    param([string]$Path, [int]$TimeoutSec = 30)
    if (-not (Test-Path -LiteralPath $Path)) { return $true }
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ($true) {
        try {
            $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $stream.Close()
            return $true
        } catch { }
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Milliseconds 500
    }
}

function Install-Skin {
    param([string]$SourceSkin, [string]$TargetSkin)
    if (-not (Test-Path -LiteralPath $SourceSkin)) { return }
    New-Item -ItemType Directory -Path $TargetSkin -Force | Out-Null
    $sourceResources = Join-Path $SourceSkin '@Resources'
    $targetResources = Join-Path $TargetSkin '@Resources'
    New-Item -ItemType Directory -Path $targetResources -Force | Out-Null

    $preservedBytes = @{}
    $preservedHashes = @{}
    foreach ($name in $PreservedResourceNames) {
        $path = Join-Path $targetResources $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $bytes = [IO.File]::ReadAllBytes($path)
            $preservedBytes[$name] = $bytes
            $preservedHashes[$name] = Get-BytesSha256 -Bytes $bytes
        }
    }
    if ($preservedBytes.Count -gt 0) {
        Write-Step ('Keeping user data: ' + (($preservedBytes.Keys | Sort-Object) -join ', '))
    }

    Copy-Item -Path (Join-Path $SourceSkin '*') -Destination $TargetSkin -Recurse -Force
    Get-ChildItem -LiteralPath $TargetSkin -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue

    foreach ($name in $preservedBytes.Keys) {
        $destination = Join-Path $targetResources $name
        [IO.File]::WriteAllBytes($destination, $preservedBytes[$name])
        $restored = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($restored -ne $preservedHashes[$name]) { throw "Restoring user data failed: $name" }
    }
}

function Remove-PreviousBackup {
    param([string]$BackupRoot)
    if (Test-Path -LiteralPath $BackupRoot) {
        Write-Step "Removing the previous rollback copy $BackupRoot"
        Remove-Item -LiteralPath $BackupRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Backup-Skins {
    param([string]$SkinsRoot, [string]$BackupRoot)
    $existing = @{}
    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    foreach ($skin in $SkinNames) {
        $target = Join-Path $SkinsRoot $skin
        if (Test-Path -LiteralPath $target) {
            Copy-Item -LiteralPath $target -Destination (Join-Path $BackupRoot $skin) -Recurse -Force
            $existing[$skin] = $true
        }
    }
    return $existing
}

function Restore-Skins {
    param([string]$SkinsRoot, [string]$BackupRoot, [hashtable]$BackedUp)
    foreach ($skin in $SkinNames) {
        if (-not $BackedUp.ContainsKey($skin)) { continue }
        $backup = Join-Path $BackupRoot $skin
        if (-not (Test-Path -LiteralPath $backup)) { continue }
        $target = Join-Path $SkinsRoot $skin
        if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue }
        Move-Item -LiteralPath $backup -Destination $target
        Write-Step "Rolled $skin back from $backup"
    }
}

function Install-CompatPackage {
    param([string]$Root, [switch]$ShouldActivate, [int]$WaitPid, [switch]$NoProcessControl)
    $roots = Get-SkinsRoot $Root
    $rainmeterExe = Find-RainmeterExe $roots.RainmeterRoot
    Write-Step ("Target: " + $roots.SkinsRoot + " -> " + $TargetVersion + " from " + $Repository)

    if ($WaitPid -gt 0) {
        Write-Step "Waiting up to 20s for the running host (pid $WaitPid) to exit"
        Wait-ForProcessExit -TargetProcessId $WaitPid -TimeoutSec 20
    }

    $work = Join-Path ([IO.Path]::GetTempPath()) ('RainmeterDesktopWidgetsLegacyCompat-' + [guid]::NewGuid().ToString('N'))
    $stage = Join-Path $work 'package'
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    try {
        $zip = Get-CanonicalPackage -Repo $Repository -Version $TargetVersion -WorkRoot $work
        Expand-CanonicalPackage -Zip $zip -Destination $stage

        Stop-SkinProcesses -SkinsRoot $roots.SkinsRoot
        if (-not $NoProcessControl) { Stop-Rainmeter -RainmeterExe $rainmeterExe }
        $probe = Join-Path $roots.SkinsRoot 'Todo\@Resources\TodoHost.exe'
        if (-not (Wait-FileUnlocked -Path $probe -TimeoutSec 30)) {
            throw 'TodoHost.exe is still locked by a running process.'
        }

        $backupRoot = Join-Path $roots.RainmeterRoot '.widgets-update-backup'
        Remove-PreviousBackup -BackupRoot $backupRoot
        $backedUp = Backup-Skins -SkinsRoot $roots.SkinsRoot -BackupRoot $backupRoot
        try {
            foreach ($skin in $SkinNames) {
                Write-Step "Installing skin $skin"
                Install-Skin -SourceSkin (Join-Path $stage ('Skins\' + $skin)) -TargetSkin (Join-Path $roots.SkinsRoot $skin)
            }
            $sourceUpdater = Join-Path $stage 'Updater'
            $targetUpdater = Join-Path $roots.SkinsRoot 'Todo\@Resources\Updater'
            if (Test-Path -LiteralPath $sourceUpdater) {
                New-Item -ItemType Directory -Path $targetUpdater -Force | Out-Null
                Copy-Item -Path (Join-Path $sourceUpdater '*') -Destination $targetUpdater -Recurse -Force
                Get-ChildItem -LiteralPath $targetUpdater -Recurse -File -Force | Unblock-File -ErrorAction SilentlyContinue
                Write-Step 'Canonical updater installed'
            } else {
                Write-Warning 'The downloaded package did not contain an Updater folder.'
            }
        } catch {
            Write-Warning ('Update failed, rolling back: ' + $_.Exception.Message)
            Restore-Skins -SkinsRoot $roots.SkinsRoot -BackupRoot $backupRoot -BackedUp $backedUp
            throw
        }

        if (-not $NoProcessControl) {
            if (Test-Path -LiteralPath $rainmeterExe) {
                Write-Step 'Restarting Rainmeter'
                # Keep Rainmeter's own working directory out of the skin tree,
                # otherwise the next update would inherit it and the swap would
                # fail with "access denied" again.
                Start-Process -FilePath $rainmeterExe -WorkingDirectory ([IO.Path]::GetTempPath()) | Out-Null
                Start-Sleep -Milliseconds 1500
                & $rainmeterExe '!RefreshApp'
                if ($ShouldActivate) {
                    Start-Sleep -Milliseconds 1000
                    & $rainmeterExe '!ActivateConfig' 'Todo' 'Todo.ini'
                    & $rainmeterExe '!ActivateConfig' 'Calendar' 'Calendar.ini'
                    Start-Sleep -Milliseconds 800
                    & $rainmeterExe '!SetWindowPosition' '100%' '0%' '100%' '0%' 'Todo'
                }
            } else {
                Write-Warning 'Rainmeter.exe was not found. Skins were installed, but Rainmeter was not restarted.'
            }
        }
        Write-Step ("Installed " + $TargetVersion + " into " + $roots.SkinsRoot)
        Write-Step "Previous skins are kept in $backupRoot in case a rollback is needed."
    } finally {
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-PackageTargetVersion {
    # The compatibility archive also carries manifest.json.  It is authoritative
    # when it is readable, so a rebuilt archive can never drift from this script.
    $version = $TargetVersion
    if ([string]::IsNullOrWhiteSpace($PackageRoot)) { return $version }
    $manifestPath = Join-Path $PackageRoot 'manifest.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { return $version }
    try {
        $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $candidateVersion = Normalize-Version ([string]$manifest.version)
        if ($candidateVersion -match '^\d+(\.\d+){1,3}$') { $version = $candidateVersion }
    } catch { }
    return $version
}

if ($Mode -eq 'UpdateUpdater') {
    $roots = Get-SkinsRoot $RainmeterRoot
    $target = Join-Path $roots.SkinsRoot 'Todo\@Resources\Updater'
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $target 'RainmeterDesktopWidgetsUpdater.ps1') -Force
    Write-Host 'Compatibility updater installed.'
    return
}

if ($Mode -eq 'CheckAndInstall') {
    $sibling = Join-Path $PSScriptRoot 'UpdaterHost.exe'
    if (Test-Path -LiteralPath $sibling -PathType Leaf) {
        $forward = @('-Mode', 'CheckAndInstall', '-Repository', $Repository)
        if (-not [string]::IsNullOrWhiteSpace($CurrentVersion)) { $forward += @('-CurrentVersion', $CurrentVersion) }
        if (-not [string]::IsNullOrWhiteSpace($PackageRoot)) { $forward += @('-PackageRoot', $PackageRoot) }
        if (-not [string]::IsNullOrWhiteSpace($RainmeterRoot)) { $forward += @('-RainmeterRoot', $RainmeterRoot) }
        if ($Activate) { $forward += '-Activate' }
        if ($AssumeYes) { $forward += '-AssumeYes' }
        $escaped = @($forward | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } })
        $process = Start-Process -FilePath $sibling -ArgumentList $escaped -Wait -PassThru -WorkingDirectory ([IO.Path]::GetTempPath())
        exit $process.ExitCode
    }
    $TargetVersion = Get-PackageTargetVersion
    if (-not [string]::IsNullOrWhiteSpace($CurrentVersion)) {
        if ((Compare-VersionText $TargetVersion $CurrentVersion) -le 0) {
            Show-Message "Already on $CurrentVersion (compatibility target $TargetVersion)."
            return
        }
    }
    try {
        Install-CompatPackage -Root $RainmeterRoot -ShouldActivate:$Activate -WaitPid $WaitForProcessId -NoProcessControl:$VerifyOnly
    } catch {
        Write-Host $_.Exception.Message
        Show-Message ("Update failed: " + $_.Exception.Message + "`r`n`r`nLog: " + $LogPath)
        exit 1
    }
    return
}

$TargetVersion = Get-PackageTargetVersion
try {
    Install-CompatPackage -Root $RainmeterRoot -ShouldActivate:$Activate -WaitPid $WaitForProcessId -NoProcessControl:$VerifyOnly
} catch {
    Write-Host $_.Exception.Message
    Show-Message ("Update to $TargetVersion failed: " + $_.Exception.Message + "`r`n`r`nLog: " + $LogPath)
    exit 1
}
