<#
    Builds the legacy compatibility packages that are committed to
    releases/<version>/ and served through raw.githubusercontent.com.

    Background
    ----------
    Clients from the pre-2.0 generations select the newest numeric tag and then
    download the compatibility archive straight from

        https://raw.githubusercontent.com/<repo>/<tag>/releases/<tag>/<asset>

    with <asset> being "rainmeter-desktop-widgets-<tag>.zip" for the unified
    generations and "rainmeter-desktop-widgets-<flavor>-<tag>.zip" for the
    flavor aware generations (v1.3.x, v1.4.x, v1.5.x).  Those archives must stay
    tiny: they carry no application payload at all.

    Archive layout produced here
    ----------------------------
        Install-Skins.ps1                            package root marker, delegates to the updater
        Updater\RainmeterDesktopWidgetsUpdater.ps1   downloads and installs the canonical release
        manifest.json                                version marker used by the updater
        Skins\Todo\.transition                       layout marker, no skin content
        Skins\Calendar\.transition                   layout marker, no skin content

    Deliberately absent: unified-bootstrap.json.  The generations that read it
    (v1.4.4 - v1.5.3) resolve the bootstrap through the raw path, so pointing it
    at this same tag would hand them this very archive again and loop.

    The same archive is published under all three accepted names, and the build
    fails if it ever gains an .exe/.dll payload or a bootstrap redirection.
#>
param(
    [string]$Version = '',
    [string]$Repository = 'kevendai/Rainmeter_todo',
    [string]$OutputRoot = '',
    [string]$UpdaterSource = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = [IO.File]::ReadAllText((Join-Path $projectRoot 'VERSION'), [Text.UTF8Encoding]::new($false)).Trim()
}
if ($Version -notmatch '^\d+(\.\d+){1,3}$') { throw "Version is invalid: $Version" }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $projectRoot ('releases\v' + $Version) }
if ([string]::IsNullOrWhiteSpace($UpdaterSource)) { $UpdaterSource = Join-Path $PSScriptRoot 'legacy-compat\RainmeterDesktopWidgetsUpdater.ps1' }

$assetNames = @(
    "rainmeter-desktop-widgets-$Version.zip",
    "rainmeter-desktop-widgets-full-$Version.zip",
    "rainmeter-desktop-widgets-lite-$Version.zip"
)
$expectedEntries = @(
    'Install-Skins.ps1',
    'manifest.json',
    'Skins\Todo\.transition',
    'Skins\Calendar\.transition',
    'Updater\RainmeterDesktopWidgetsUpdater.ps1'
)

$installScript = @'
param(
    [string]$RainmeterRoot,
    [switch]$Activate,
    [int]$WaitForProcessId = 0
)

# Compatibility entry point of the legacy package. It carries no payload: the
# real work happens in Updater\RainmeterDesktopWidgetsUpdater.ps1, which
# downloads the canonical release package and installs it.
$ErrorActionPreference = 'Stop'
$updater = Join-Path $PSScriptRoot 'Updater\RainmeterDesktopWidgetsUpdater.ps1'
if (-not (Test-Path -LiteralPath $updater)) { throw 'Updater script not found in the compatibility package.' }
$forward = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $updater, '-Mode', 'InstallPackage', '-PackageRoot', $PSScriptRoot)
if (-not [string]::IsNullOrWhiteSpace($RainmeterRoot)) { $forward += @('-RainmeterRoot', $RainmeterRoot) }
if ($Activate) { $forward += '-Activate' }
if ($WaitForProcessId -gt 0) { $forward += @('-WaitForProcessId', $WaitForProcessId) }
$process = Start-Process -FilePath 'powershell.exe' -ArgumentList $forward -Wait -PassThru
exit $process.ExitCode
'@

$buildRoot = Join-Path ([IO.Path]::GetTempPath()) ('RainmeterDesktopWidgetsCompatBuild-' + [guid]::NewGuid().ToString('N'))
$staging = Join-Path $buildRoot 'staging'
$zip = Join-Path $buildRoot $assetNames[0]
$verifyRoot = Join-Path ([IO.Path]::GetTempPath()) ('RainmeterDesktopWidgetsCompatVerify-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $staging, (Join-Path $staging 'Updater'), (Join-Path $staging 'Skins\Todo'), (Join-Path $staging 'Skins\Calendar') -Force | Out-Null
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null

try {
    # 1. the compatibility updater, with its target version baked in
    $updaterText = [IO.File]::ReadAllText($UpdaterSource, [Text.UTF8Encoding]::new($false))
    if (-not $updaterText.Contains('__TARGET_VERSION__')) { throw "Updater source has no __TARGET_VERSION__ placeholder: $UpdaterSource" }
    $updaterText = $updaterText.Replace('__TARGET_VERSION__', $Version)
    if ($updaterText.Contains('__TARGET_VERSION__')) { throw 'Updater target version placeholder was not replaced.' }
    if (-not $updaterText.Contains("`$TargetVersion = '$Version'")) { throw "Updater source does not declare `$TargetVersion = '$Version'." }
    if ($updaterText.Contains('unified-bootstrap')) { throw 'Updater source must not reference unified-bootstrap.json.' }
    foreach ($char in $updaterText.ToCharArray()) {
        if ([int][char]$char -gt 127) { throw 'Updater source must stay ASCII only.' }
    }
    [IO.File]::WriteAllText((Join-Path $staging 'Updater\RainmeterDesktopWidgetsUpdater.ps1'), $updaterText, [Text.UTF8Encoding]::new($false))

    # 2. package root marker used by Find-PackageRoot in the pre-2.0 updaters
    $installText = $installScript.Replace("`r`n", "`n").Replace("`n", "`r`n").TrimEnd() + "`r`n"
    [IO.File]::WriteAllText((Join-Path $staging 'Install-Skins.ps1'), $installText, [Text.UTF8Encoding]::new($false))

    # 3. manifest used by Install-Skins.ps1 and by the compatibility updater
    $manifest = [ordered]@{
        name = 'Rainmeter Desktop Widgets legacy compatibility package'
        version = $Version
        kind = 'legacy-compat'
        repository = $Repository
        target_release = "v$Version"
        target_asset = "rainmeter-desktop-widgets-$Version.zip"
    } | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText((Join-Path $staging 'manifest.json'), $manifest, [Text.UTF8Encoding]::new($false))

    # 4. layout markers: the v1.4.4 - v1.5.3 updaters insist on a Skins folder
    [IO.File]::WriteAllText((Join-Path $staging 'Skins\Todo\.transition'), '', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $staging 'Skins\Calendar\.transition'), '', [Text.UTF8Encoding]::new($false))

    $payload = Get-ChildItem -LiteralPath $staging -Recurse -File -Force | Where-Object { $_.Extension -in @('.exe', '.dll', '.rmskin') }
    if ($payload) { throw ('Compatibility package must not contain a payload: ' + (($payload | ForEach-Object { $_.Name }) -join ', ')) }
    $payloadLike = Get-ChildItem -LiteralPath $staging -Recurse -File -Force | Where-Object { $_.Length -gt 262144 }
    if ($payloadLike) { throw ('Compatibility package contains an unexpectedly large file: ' + (($payloadLike | ForEach-Object { $_.Name }) -join ', ')) }

    # 5. build once, publish under every accepted alias
    $zip = Join-Path $staging ('..\' + $assetNames[0])
    $zip = [IO.Path]::GetFullPath($zip)
    if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $zip -Force

    if (Test-Path -LiteralPath $verifyRoot) { Remove-Item -LiteralPath $verifyRoot -Recurse -Force }
    Expand-Archive -LiteralPath $zip -DestinationPath $verifyRoot -Force
    $entries = @(Get-ChildItem -LiteralPath $verifyRoot -Recurse -File -Force | ForEach-Object { $_.FullName.Substring($verifyRoot.Length + 1) })
    $missing = @($expectedEntries | Where-Object { $entries -notcontains $_ })
    $unexpected = @($entries | Where-Object { $expectedEntries -notcontains $_ })
    if ($missing.Count -gt 0) { throw ('Compatibility package is missing: ' + ($missing -join ', ')) }
    if ($unexpected.Count -gt 0) { throw ('Compatibility package has unexpected entries: ' + ($unexpected -join ', ')) }

    $installed = [IO.File]::ReadAllText((Join-Path $verifyRoot 'Updater\RainmeterDesktopWidgetsUpdater.ps1'), [Text.UTF8Encoding]::new($false))
    if (-not $installed.Contains("`$TargetVersion = '$Version'")) { throw 'Packaged updater does not carry the expected target version.' }
    if (-not $installed.Contains('SHA256')) { throw 'Packaged updater does not verify the SHA256 checksum.' }
    if ($installed.Contains('unified-bootstrap')) { throw 'Packaged updater must not reference unified-bootstrap.json.' }

    $published = @()
    foreach ($name in $assetNames) {
        $target = Join-Path $OutputRoot $name
        Copy-Item -LiteralPath $zip -Destination $target -Force
        $hash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText($target + '.sha256', "$hash  $name`n", [Text.UTF8Encoding]::new($false))
        $published += [pscustomobject]@{ Name = $name; Size = (Get-Item -LiteralPath $target).Length; Sha256 = $hash }
    }
    $hashes = @($published | ForEach-Object { $_.Sha256 } | Select-Object -Unique)
    if ($hashes.Count -ne 1) { throw 'Published compatibility aliases are not identical.' }

    $gitignorePath = Join-Path $projectRoot '.gitignore'
    if (Test-Path -LiteralPath $gitignorePath) {
        $gitignore = Get-Content -LiteralPath $gitignorePath -Raw
        $generic = [regex]::IsMatch($gitignore, '(?m)^!releases/\*/\*\.zip\s*$')
        $explicit = [regex]::IsMatch($gitignore, ('(?m)^!releases/' + [regex]::Escape("v$Version/" + $assetNames[0]) + '\s*$'))
        if (-not ($generic -or $explicit)) {
            Write-Warning "releases/v$Version is not whitelisted in .gitignore; the raw bootstrap channel would stay untracked."
        }
    }

    foreach ($item in $published) {
        Write-Host ("Created releases\v$Version\{0} ({1} bytes, sha256 {2})" -f $item.Name, $item.Size, $item.Sha256)
    }
    Write-Host ("Repositories/tags served by this build: {0} v{1}" -f $Repository, $Version)
}
finally {
    Remove-Item -LiteralPath $buildRoot -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $verifyRoot -Recurse -Force -ErrorAction SilentlyContinue
}
