# Rainmeter Desktop Widgets Plugin API v1

The single official plugin registry repository can be generated from this monorepo with:

```powershell
pwsh ./scripts/Export-OfficialPluginRepositories.ps1 -OutputDirectory <path>
```

The export places all plugin source under `official-plugins/`, copies every required source file, rewrites the arXiv project's monorepo links, and performs independent verification builds.

Plugins are ordinary Windows executables launched on demand by `PluginHost.exe`. They receive exactly one UTF-8 JSON request line on stdin, write protocol messages as JSON Lines to stdout, and write diagnostic logs only to stderr. A plugin must emit zero or more `progress` messages followed by exactly one `result` message.

## Package and identity

A `.rwplugin` is a ZIP containing `plugin.json` at its root. Plugin IDs use reverse-domain form, versions are numeric `x.y.z`, and every manifest path must remain inside the package. API v1 accepts `todo_source`, `todo_transform`, and `value_provider`. The host validates size limits, duplicate and unsafe ZIP paths, entry existence, `api_version`, `min_host_version`, and—when installed from the registry—the published SHA256.

Permissions are declarations shown to users. API v1 does not claim to sandbox an ordinary EXE. Plugins receive only their own `config`, their own decrypted `secret`, the requested input, and a private data directory through `RW_PLUGIN_DATA_DIR`. They must never edit `tasks.json`.

## Request

```json
{"api_version":1,"request_id":"uuid","plugin_id":"io.github.example.demo","action":"sync","context":{"host_version":"2.0.0","locale":"zh-CN","now":"2026-09-13T09:00:00+08:00","trigger":"manual"},"config":{},"secret":{},"input":{}}
```

The response repeats the same `request_id`:

```json
{"type":"progress","request_id":"uuid","current":1,"total":10,"message":"Working"}
{"type":"result","request_id":"uuid","ok":true,"payload":{}}
```

`todo_source.sync` returns `payload.tasks` with at most 500 task drafts. `todo_transform.transform` receives one normalized `CalendarEvent` in `input` and returns `payload.task` or no task. `value_provider.get_values` returns scalar values under `payload.values` plus a TTL. External task identity is `(plugin_id, external_id)`; the host creates only missing tasks and never overwrites a user's edits.

## Settings

`settings.schema.json` is an object schema. The v1 UI supports `string`, `multiline`, `password`, `integer`, `boolean`, and `enum`, plus `minimum`, `maximum`, `default`, `required`, help text, `x-secret`, `x-order`, and `x-advanced`. Sensitive values are stored with Windows CurrentUser DPAPI.

See `plugins/official` for complete .NET Framework 4.0 examples. A plugin may use any language that can produce a Windows executable and follow this protocol.
