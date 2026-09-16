# GitHub Release Upload Runbook

Release binaries live only in GitHub Release assets. The repository contains source, build scripts, and tags; the updater refuses a package unless the matching `.sha256` asset is present and valid.

## Build

Run from the repository root:

```powershell
$version = 'X.Y.Z'

# VERSION is the single source of truth for host binaries, skin metadata,
# manifests, and package file names.
[IO.File]::WriteAllText((Resolve-Path .\VERSION).Path, "$version`r`n", [Text.UTF8Encoding]::new($false))

# Add a matching "## X.Y.Z - YYYY-MM-DD" section to docs\RELEASE-NOTES.md
# before building. The GitHub Release step extracts notes from that section.
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Backends.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Build-ReleasePackages.ps1
```

The build emits a `.sha256` sidecar for every ZIP and RMSKIN. Keep these artifacts in the ignored `release-build` directory until they are uploaded.

Legacy updaters select the newest numeric tag but download from `raw.githubusercontent.com/<tag>/releases/<tag>/`. `scripts\Build-LegacyCompatPackages.ps1` (called by `Build-ReleasePackages.ps1`) writes one tiny updater-only archive into `releases/vX.Y.Z/` and publishes it under all three accepted names: `rainmeter-desktop-widgets-vX.Y.Z.zip` (canonical, used by the v1.4.4+ generations), `rainmeter-desktop-widgets-full-vX.Y.Z.zip` and `rainmeter-desktop-widgets-lite-vX.Y.Z.zip` (flavor-aware clients such as v1.3.5). All three are byte-identical and carry **no application payload**: an `Install-Skins.ps1` root marker, a `manifest.json`, two `Skins\<name>\.transition` placeholders, and `Updater\RainmeterDesktopWidgetsUpdater.ps1`. That updater downloads the canonical package from the GitHub Release, verifies its SHA256, releases every file lock, merges the new skin files in while keeping user data byte for byte, and installs the canonical updater. Never commit the full installer or user data here, and never let this channel point at a tag that itself serves a compatibility archive.

Before committing, inspect the unified zip manifest, app version, runtime feature flag, user-data exclusions, and legacy bootstrap packages:

```powershell
$version = 'X.Y.Z'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = Resolve-Path ".\release-build\rainmeter-desktop-widgets-$version.zip"
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $manifestEntry = $archive.Entries | Where-Object FullName -eq 'manifest.json' | Select-Object -First 1
    $reader = [IO.StreamReader]::new($manifestEntry.Open())
    try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }

    $appEntry = $archive.Entries | Where-Object { $_.FullName -eq 'Skins\Todo\@Resources\app-version.txt' } | Select-Object -First 1
    $reader = [IO.StreamReader]::new($appEntry.Open())
    try { $appVersion = $reader.ReadToEnd().Trim() }
    finally { $reader.Dispose() }

    $bad = $archive.Entries |
      Where-Object { $_.FullName -match '(translation\.secret|paper-sync\.secret|caldav\.secret|tasks\.json|calendar-cache\.json|calendar-state\.json|PaperCache)' } |
      Select-Object -ExpandProperty FullName
    $hasUpdater = $null -ne ($archive.Entries | Where-Object FullName -eq 'Updater\RainmeterDesktopWidgetsUpdater.ps1' | Select-Object -First 1)
    $hasInstallEntry = $null -ne ($archive.Entries | Where-Object FullName -eq 'Install-Skins.ps1' | Select-Object -First 1)

    [pscustomobject]@{
      Version = $manifest.version
      Updater = $manifest.updater_version
      PaperFeatures = $manifest.paper_features
      RuntimeSwitch = $manifest.paper_features_runtime_switch
      AppVersion = $appVersion
      BadEntries = ($bad -join ', ')
      HasUpdater = $hasUpdater
      HasInstallEntry = $hasInstallEntry
    }
}
finally { $archive.Dispose() }

foreach ($flavor in 'full','lite') {
  $compat = ".\releases\v$version\rainmeter-desktop-widgets-$flavor-$version.zip"
  $extract = Join-Path $env:TEMP "rainmeter-compat-$flavor-$version"
  Expand-Archive $compat $extract -Force
  try {
    if (-not (Test-Path "$extract\Install-Skins.ps1")) { throw "$flavor compat entry marker missing" }
    if (Test-Path "$extract\Skins\Todo\@Resources") { throw "$flavor compat unexpectedly contains an application payload" }
    # The v1.4.4 - v1.5.3 clients resolve the bootstrap marker through the raw
    # channel, so a marker here would hand them this same archive again and loop.
    if (Test-Path "$extract\unified-bootstrap.json") { throw "$flavor compat advertises a bootstrap and would loop" }
    $compatUpdater = Get-Content "$extract\Updater\RainmeterDesktopWidgetsUpdater.ps1" -Raw
    if ($compatUpdater -notmatch "`$TargetVersion = '$version'") { throw "$flavor compat updater does not target $version" }
  } finally {
    Remove-Item $extract -Recurse -Force
  }
}
```

Expected values:

- Unified package: `Version = X.Y.Z`, `AppVersion = X.Y.Z`, `PaperFeatures = True`, `RuntimeSwitch = True`, `BadEntries` empty.
- Unified zip contains the complete product and its top-level updater, but `HasInstallEntry = False`; it is an automatic-update transport package, not a manual installer.
- full/lite zips are the legacy compatibility archives built from `scripts\legacy-compat\RainmeterDesktopWidgetsUpdater.ps1`: a v1.3.5 client downloads one of them from raw, installs the packaged updater, and that updater fetches the canonical release package and performs the upgrade in one hop.
- The three names under `releases\vX.Y.Z\` are the same archive; only the canonical name is also published as a Release asset, because the v1.4.4+ generations read the Release while the v1.3.x generations read raw.
- Only the unified `.rmskin` is published.
- The `.rmskin` must end with the 16-byte Rainmeter package footer: an 8-byte little-endian archive size, one flags byte, and the ASCII key `RMSKIN\0`. A normal ZIP renamed to `.rmskin` is invalid.

Zip entries use Windows-style `\` separators because `Compress-Archive` preserves the PowerShell source path style; use `Skins\Todo\@Resources\app-version.txt`, not `Skins/Todo/@Resources/app-version.txt`, when reading entries.

Also verify that the `.rmskin` contains the installed updater path, and that
the updater keeps the expected package discovery and wildcard copy flow:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$rmskin = [IO.Compression.ZipFile]::OpenRead((Resolve-Path .\release-build\rainmeter-desktop-widgets-X.Y.Z.rmskin))
try {
  if ($null -eq ($rmskin.Entries | Where-Object FullName -eq 'Skins\Todo\@Resources\Updater\RainmeterDesktopWidgetsUpdater.ps1' | Select-Object -First 1)) {
    throw 'RMSKIN updater missing'
  }
} finally { $rmskin.Dispose() }
Select-String -Path .\release-build\rainmeter-desktop-widgets-X.Y.Z\Updater\RainmeterDesktopWidgetsUpdater.ps1 `
  -Pattern 'manifest.json','Copy-Item -Path \(Join-Path \$source ''\*''\)'
```

## Commit And Tag

```powershell
git status --short
git tag --list vX.Y.Z
git add VERSION docs\RELEASE-NOTES.md
# Add the actual source/script/docs files changed for this release, for example:
# git add backend\CalendarForms.cs scripts\Deploy-Calendar.ps1 docs\GITHUB-RELEASE.md
git diff --cached --name-only
git diff --cached --stat
git commit -m "Release vX.Y.Z"
git tag vX.Y.Z
git push origin master
git push origin vX.Y.Z
```

If the tag already exists, do not move it casually. Prefer a new patch version unless the user explicitly wants to replace the same tag for a test.

In the Codex desktop sandbox, writing `.git/index.lock` may require an escalated `git add` / `commit` / `tag` / `push`. That is expected; do not work around it by copying `.git` files manually.

## GitHub Release Assets

All installers and updaters require GitHub Release assets. If `gh` is available:

```powershell
$version = 'X.Y.Z'
$notesPath = ".\release-build\release-notes-v$version.md"
$lines = Get-Content .\docs\RELEASE-NOTES.md -Encoding UTF8
$start = [Array]::FindIndex($lines, [Predicate[string]]{ param($line) $line -like "## $version -*" })
if ($start -lt 0) { throw "Release notes section not found for $version" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
  if ($lines[$i] -like '## *') { $end = $i; break }
}
[IO.File]::WriteAllText((Resolve-Path .\release-build).Path + "\release-notes-v$version.md", (($lines[($start + 1)..($end - 1)] -join "`r`n").Trim() + "`r`n"), [Text.UTF8Encoding]::new($false))

gh release create "v$version" `
  ".\release-build\rainmeter-desktop-widgets-$version.zip" `
  ".\release-build\rainmeter-desktop-widgets-$version.zip.sha256" `
  ".\release-build\rainmeter-desktop-widgets-$version.rmskin" `
  ".\release-build\rainmeter-desktop-widgets-$version.rmskin.sha256" `
  ".\release-build\rainmeter-desktop-widgets-full-$version.zip" `
  ".\release-build\rainmeter-desktop-widgets-full-$version.zip.sha256" `
  ".\release-build\rainmeter-desktop-widgets-lite-$version.zip" `
  ".\release-build\rainmeter-desktop-widgets-lite-$version.zip.sha256" `
  --repo kevendai/Rainmeter_todo `
  --title "Rainmeter Desktop Widgets $version" `
  --notes-file $notesPath
```

If `gh` is not on `PATH`, check `C:\Program Files\GitHub CLI\gh.exe` before falling back to the REST API.

If `gh` is not installed, use the GitHub REST API with explicit user approval before reading Git Credential Manager credentials. Never print the token. Upload assets to:

```text
https://uploads.github.com/repos/kevendai/Rainmeter_todo/releases/{release_id}/assets?name={asset_name}
```

When replacing assets, delete the existing matching asset and checksum first, then upload both regenerated files.

## Verification

Check the old updater path:

```powershell
Invoke-RestMethod -Uri https://api.github.com/repos/kevendai/Rainmeter_todo/releases/tags/vX.Y.Z -Headers @{ 'User-Agent'='Codex-Rainmeter-Verify' } |
  Select-Object tag_name,assets_url
```

Check the direct release asset redirects:

```powershell
curl.exe -I https://github.com/kevendai/Rainmeter_todo/releases/download/vX.Y.Z/rainmeter-desktop-widgets-full-X.Y.Z.zip
curl.exe -I https://github.com/kevendai/Rainmeter_todo/releases/download/vX.Y.Z/rainmeter-desktop-widgets-lite-X.Y.Z.zip
curl.exe -I https://github.com/kevendai/Rainmeter_todo/releases/download/vX.Y.Z/rainmeter-desktop-widgets-X.Y.Z.zip
```

Check the checksum assets and verify a downloaded package:

```powershell
$zip = '.\release-build\rainmeter-desktop-widgets-X.Y.Z.zip'
$expected = (Get-Content "$zip.sha256" -Raw).Split()[0]
if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $expected) { throw 'SHA256 mismatch' }
```

Verify the published asset inventory through the release API:

```powershell
gh release view vX.Y.Z --repo kevendai/Rainmeter_todo --json tagName,name,url,assets
```

If verifying a live install, inspect the compiled host for the version string:

```powershell
$path = 'D:\Program Files (x86)\Rainmeter\Skins\Todo\@Resources\TodoHost.exe'
$text = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($path))
[regex]::Match($text, '当前版本：.{0,50}').Value
```

## Encoding Rules

- Treat repository Markdown, PowerShell, C#, and INI sources as UTF-8 unless the file is deliberately converted during packaging.
- Use `[Text.UTF8Encoding]::new($false)` when a script must read or write source files.
- Do not rely on PowerShell console rendering of Chinese text; mojibake in terminal output does not always mean the file is corrupt.
- Avoid rewriting whole files when a targeted patch is enough.
- For generated release install scripts, prefer ASCII-only prompt/error strings. They run on unknown PowerShell hosts and must not break here-strings or quotes.
