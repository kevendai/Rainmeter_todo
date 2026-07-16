# Rainmeter Desktop Widgets 部署说明

本项目的初次安装只使用 `.rmskin`，不再提供 ZIP 内 PowerShell 安装入口。

- `rainmeter-desktop-widgets-*.rmskin`：给新用户或手动重装使用。双击后由 Rainmeter Skin Installer 安装 Todo 和 Calendar。
- `rainmeter-desktop-widgets-*.zip`：仅供应用内数据保留型自动更新下载，不应手动解压安装。
- `rainmeter-desktop-widgets-full-*.zip` / `lite-*.zip`：仅为旧升级器保留的内部兼容引导包，不包含皮肤，也不是安装包。

## 初次安装

1. 从 [Rainmeter 官网](https://www.rainmeter.net/) 安装 Rainmeter 4.5.26 或更高版本。
2. 从 GitHub Releases 下载 `rainmeter-desktop-widgets-<版本>.rmskin`。
3. 双击该文件，在 Rainmeter Skin Installer 中确认并点击 Install。
4. 安装完成后应能看到 `Todo` 和 `Calendar` 两个皮肤；如未自动加载，可在 Rainmeter 管理器中加载各自的 `.ini` 文件。

`.rmskin` 已内置独立升级器：`Todo\@Resources\Updater\RainmeterDesktopWidgetsUpdater.ps1`。因此首次安装完成后，可在 Todo 设置的“关于”页直接检查更新。

## 从旧版本更新

请在 Todo 设置的“关于”页点击“检查更新”。更新器会下载 ZIP、保留任务、缓存和 DPAPI 凭据，并更新自身后部署 Todo/Calendar。不要手动覆盖 `Skins` 目录；这正是过去可能漏掉独立升级器的路径。

## 凭据与数据

- CalDAV 凭据位于 `Skins\Todo\@Resources\caldav.secret`。
- 论文和翻译设置分别位于 `paper-sync.secret`、`translation.secret`，均使用 Windows DPAPI CurrentUser 加密。
- 自动更新会保留 `tasks.json`、各类 secret、`calendar-cache.json`、`calendar-state.json` 与 `ui-scale.txt`。

## 从源码打包

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Backends.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\Build-ReleasePackages.ps1
```

构建会生成统一 ZIP、统一 `.rmskin` 和仅供旧客户端使用的 full/lite 引导 ZIP。
