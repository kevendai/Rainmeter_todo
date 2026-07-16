# GitHub Release Upload Runbook

This project has two release surfaces that must stay aligned:

- The repository commit/tag, used by the newer updater to read `releases/vX.Y.Z/*.zip` from the tagged tree.
- The GitHub Release assets, used by older updaters such as `1.0.2` through `GET /repos/kevendai/Rainmeter_todo/releases/tags/vX.Y.Z`.

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

Copy the generated release files into the versioned release folder:

```powershell
New-Item -ItemType Directory -Path .\releases\vX.Y.Z -Force | Out-Null
Copy-Item .\release-build\rainmeter-desktop-widgets-X.Y.Z.zip .\releases\vX.Y.Z\ -Force
Copy-Item .\release-build\rainmeter-desktop-widgets-X.Y.Z.rmskin .\releases\vX.Y.Z\ -Force
# Small updater bootstraps for old full/lite clients:
Copy-Item .\release-build\rainmeter-desktop-widgets-full-X.Y.Z.zip .\releases\vX.Y.Z\ -Force
Copy-Item .\release-build\rainmeter-desktop-widgets-lite-X.Y.Z.zip .\releases\vX.Y.Z\ -Force
```

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

    [pscustomobject]@{
      Version = $manifest.version
      Updater = $manifest.updater_version
      PaperFeatures = $manifest.paper_features
      RuntimeSwitch = $manifest.paper_features_runtime_switch
      AppVersion = $appVersion
      BadEntries = ($bad -join ', ')
    }
}
finally { $archive.Dispose() }

foreach ($flavor in 'full','lite') {
  $bootstrap = ".\release-build\rainmeter-desktop-widgets-$flavor-$version.zip"
  $extract = Join-Path $env:TEMP "rainmeter-bootstrap-$flavor-$version"
  Expand-Archive $bootstrap $extract -Force
  try {
    if (-not (Test-Path "$extract\unified-bootstrap.json")) { throw "$flavor bootstrap marker missing" }
    if (Test-Path "$extract\Skins") { throw "$flavor bootstrap unexpectedly contains Skins" }
  } finally {
    Remove-Item $extract -Recurse -Force
  }
}
```

Expected values:

- Unified package: `Version = X.Y.Z`, `AppVersion = X.Y.Z`, `PaperFeatures = True`, `RuntimeSwitch = True`, `BadEntries` empty.
- Unified zip contains the complete product. full/lite zips contain only `Install-Skins.ps1`, the updater, and `unified-bootstrap.json`.
- Only the unified `.rmskin` is published.
- The `.rmskin` must end with the 16-byte Rainmeter package footer: an 8-byte little-endian archive size, one flags byte, and the ASCII key `RMSKIN\0`. A normal ZIP renamed to `.rmskin` is invalid.

Zip entries use Windows-style `\` separators because `Compress-Archive` preserves the PowerShell source path style; use `Skins\Todo\@Resources\app-version.txt`, not `Skins/Todo/@Resources/app-version.txt`, when reading entries.

Also verify that `Install-Skins.ps1` uses the package directory as its root and
delegates installation to the packaged updater, while the updater keeps the
expected wildcard copy flow:

```powershell
Select-String -Path .\release-build\rainmeter-desktop-widgets-X.Y.Z\Install-Skins.ps1 `
  -Pattern '\$packageRoot = \$PSScriptRoot','InstallPackage','-PackageRoot'
Select-String -Path .\release-build\rainmeter-desktop-widgets-X.Y.Z\Updater\RainmeterDesktopWidgetsUpdater.ps1 `
  -Pattern 'Copy-Item -Path \(Join-Path \$source ''\*''\)'
```

## Commit And Tag

```powershell
git status --short
git tag --list vX.Y.Z
git add VERSION docs\RELEASE-NOTES.md releases\vX.Y.Z
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

Older installed versions may require an actual GitHub Release with zip assets. If `gh` is available:

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
  ".\releases\v$version\rainmeter-desktop-widgets-$version.zip" `
  ".\releases\v$version\rainmeter-desktop-widgets-$version.rmskin" `
  ".\releases\v$version\rainmeter-desktop-widgets-full-$version.zip" `
  ".\releases\v$version\rainmeter-desktop-widgets-lite-$version.zip" `
  --repo kevendai/Rainmeter_todo `
  --title "Rainmeter Desktop Widgets $version" `
  --notes-file $notesPath
```

If `gh` is not on `PATH`, check `C:\Program Files\GitHub CLI\gh.exe` before falling back to the REST API.

If `gh` is not installed, use the GitHub REST API with explicit user approval before reading Git Credential Manager credentials. Never print the token. Upload assets to:

```text
https://uploads.github.com/repos/kevendai/Rainmeter_todo/releases/{release_id}/assets?name={asset_name}
```

When replacing assets, delete the existing matching asset first, then upload the regenerated zip.

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

Check the newer raw updater path:

```powershell
curl.exe -I https://raw.githubusercontent.com/kevendai/Rainmeter_todo/vX.Y.Z/releases/vX.Y.Z/rainmeter-desktop-widgets-full-X.Y.Z.zip
curl.exe -I https://raw.githubusercontent.com/kevendai/Rainmeter_todo/vX.Y.Z/releases/vX.Y.Z/rainmeter-desktop-widgets-lite-X.Y.Z.zip
```

The raw domain can occasionally return `429 Too Many Requests` during repeated checks. If that happens, verify the same asset through GitHub Release assets and the tag tree before treating it as missing:

```powershell
gh release view vX.Y.Z --repo kevendai/Rainmeter_todo --json tagName,name,url,assets
git ls-tree -r --name-only vX.Y.Z releases/vX.Y.Z
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
