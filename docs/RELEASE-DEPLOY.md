# Rainmeter Desktop Widgets 部署说明

本项目的初次安装只使用 `.rmskin`，不再提供 ZIP 内 PowerShell 安装入口。

- `rainmeter-desktop-widgets-*.rmskin`：给新用户或手动重装使用。双击后由 Rainmeter Skin Installer 安装 Todo 和 Calendar。
- `rainmeter-desktop-widgets-*.zip`：仅供应用内数据保留型自动更新下载，不应手动解压安装。
- `rainmeter-desktop-widgets-full-*.zip` / `lite-*.zip`：仅为旧版升级器保留的内部兼容包。它不含皮肤本体，只有兼容入口和 `Updater\RainmeterDesktopWidgetsUpdater.ps1`；旧版客户端下载后会运行该脚本，由脚本自动去 GitHub Release 取正式包、校验 SHA256、释放占用并完成升级，同时保留用户数据。不要手动解压安装。

## 初次安装

2.2.0 起管理界面使用 WinUI 3，支持 Windows 10 2004（19041）或更高版本的 x64 系统。安装包自带 .NET 和 Windows App SDK 运行时，无需另装 .NET。

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
- 自动更新会保留 `tasks.json`、各类 secret、`calendar-cache.json`、`calendar-state.json`、磁贴缩放 `ui-scale.txt` 与窗口缩放 `ui-window-scale.txt`。

## 2.1.0 起的变化（插件 Provider 化）

- 主程序不再自带 AI 评分 / 标题翻译 / 文件服务器客户端：arXiv 插件（2.0.0）改为调用三个独立的 Provider 插件 ——「DeepSeek AI 评分」（AI 评分）、「腾讯云翻译」（标题翻译）、「论文快照同步」（远端快照）。三者随 2.1.0 一起捆绑安装，其中两个可能产生费用的默认**不启用**。
- 首次启动 2.1.0 时会把旧的 DeepSeek / 腾讯云 / 文件服务器配置**复制**到对应 Provider；`Skins\Todo\@Resources` 下的旧 `paper-sync.secret`、`translation.secret` **保留不删除**，所以随时可以退回旧版本继续用。
- 之后这些凭据位于插件自己的数据目录：`%LOCALAPPDATA%\RainmeterDesktopWidgets\PluginData\<插件 id>\secret.dat`（同样是 Windows DPAPI CurrentUser 加密）。文件服务器地址改由「论文快照同步」插件声明，可与 SSDP 地址插件配合自动替换主机。
- AI 调用一律要你在主程序里确认，不会自动执行；arXiv 插件单独安装时只会提示「今天没有可用的论文推荐结果」，不会静默生成待办。

## 从源码打包

```powershell
pwsh -File .\scripts\Test-Backends.ps1
pwsh -File .\scripts\Build-ReleasePackages.ps1
```

构建会生成统一 ZIP、统一 `.rmskin` 和仅供旧客户端使用的 full/lite 兼容 ZIP（三份内容相同，同时写入 `releases\v<版本>\` 并作为 Release 资产发布）。
