# GitHub Release Upload Runbook

This project has two release surfaces that must stay aligned:

- The repository commit/tag, used by the newer updater to read `releases/vX.Y.Z/*.zip` from the tagged tree.
- The GitHub Release assets, used by older updaters such as `1.0.2` through `GET /repos/kevendai/Rainmeter_todo/releases/tags/vX.Y.Z`.

## Build

Run from the repository root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Backends.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Build-ReleasePackages.ps1 -Version X.Y.Z
```

Copy the two generated zip files into the versioned release folder:

```powershell
New-Item -ItemType Directory -Path .\releases\vX.Y.Z -Force | Out-Null
Copy-Item .\release-build\rainmeter-desktop-widgets-full-X.Y.Z.zip .\releases\vX.Y.Z\ -Force
Copy-Item .\release-build\rainmeter-desktop-widgets-lite-X.Y.Z.zip .\releases\vX.Y.Z\ -Force
```

Before committing, inspect the zip contents:

```powershell
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Resolve-Path .\release-build\rainmeter-desktop-widgets-full-X.Y.Z.zip
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    $manifest = $archive.GetEntry('manifest.json')
    $reader = [IO.StreamReader]::new($manifest.Open())
    try { $reader.ReadToEnd() | ConvertFrom-Json | Select-Object version,updater_version,paper_features }
    finally { $reader.Dispose() }
}
finally { $archive.Dispose() }
```

Also verify that `Install-Skins.ps1` uses the package directory as its root:

```powershell
Select-String -Path .\release-build\rainmeter-desktop-widgets-full-X.Y.Z\Install-Skins.ps1 -Pattern '\$packageRoot = \$PSScriptRoot','Copy-Item -Path'
```

## Commit And Tag

```powershell
git add README.md docs\RELEASE-NOTES.md scripts\Build-ReleasePackages.ps1 releases\vX.Y.Z
git commit -m "Release vX.Y.Z"
git tag vX.Y.Z
git push origin master
git push origin vX.Y.Z
```

If the tag already exists, do not move it casually. Prefer a new patch version unless the user explicitly wants to replace the same tag for a test.

## GitHub Release Assets

Older installed versions may require an actual GitHub Release with zip assets. If `gh` is available:

```powershell
gh release create vX.Y.Z `
  .\releases\vX.Y.Z\rainmeter-desktop-widgets-full-X.Y.Z.zip `
  .\releases\vX.Y.Z\rainmeter-desktop-widgets-lite-X.Y.Z.zip `
  --repo kevendai/Rainmeter_todo `
  --title "Rainmeter Desktop Widgets X.Y.Z" `
  --notes-file .\docs\RELEASE-NOTES.md
```

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
```

Check the newer raw updater path:

```powershell
curl.exe -I https://raw.githubusercontent.com/kevendai/Rainmeter_todo/vX.Y.Z/releases/vX.Y.Z/rainmeter-desktop-widgets-full-X.Y.Z.zip
curl.exe -I https://raw.githubusercontent.com/kevendai/Rainmeter_todo/vX.Y.Z/releases/vX.Y.Z/rainmeter-desktop-widgets-lite-X.Y.Z.zip
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
