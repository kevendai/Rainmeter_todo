# Official plugin registry template

This single repository contains the market index and all official plugin source under `official-plugins/`. Publish its root as GitHub Pages. The stable client reads `index-v1.json` from the Pages root and ignores entries whose `official` value is not `true`.

Before publishing:

1. Build each plugin and publish its `.rwplugin` and `.sha256` in this repository using plugin-specific tags such as `arxiv-v1.0.0`.
2. Download the three release assets into one directory and run `pwsh ./scripts/Set-RegistryHashes.ps1 -PackageDirectory <path>`.
3. Run `pwsh ./scripts/Test-Registry.ps1` (the hash script also runs it automatically).
4. Enable GitHub Pages for the repository root.

The validation workflow rejects placeholders, duplicate IDs, non-official entries, malformed versions, non-GitHub release URLs, and mismatches between the index and per-plugin detail files.
