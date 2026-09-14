# Rainmeter Desktop Widgets

一套面向 Windows 的 Rainmeter 桌面组件，把待办、日程和可安装的数据插件放在桌面右上角。日常操作通过原生 C# 窗口完成；`PluginHost.exe` 和插件均按需启动，不需要常驻终端或 Windows 服务。

## 功能

### 待办与已办

- 快速新增、编辑、完成、恢复和删除任务。
- 支持开始时间、截止时间、逾期标红、备注、标签和批量管理。
- 点击任务可打开网页、文件、文件夹或应用。
- 外部任务通过通用 policy 控制每日整理、完成标签和恢复行为；普通“论文”标签不会触发隐藏规则。

### 今日日程

- 可新建、编辑和删除本地日程与 CalDAV 日程，并同步展示当天内容；支持跨天事件、重复日程和时间冲突提示。
- 显示地点、备注、会议链接和提醒时间。
- 可将单次日程或整个重复系列单向转换为本地待办，不修改服务器上的原日程。
- 支持网页链接和腾讯会议链接。

### arXiv 论文推荐

- 直接在本机使用 C# 抓取 arXiv RSS，不需要安装 Python。
- 使用 DeepSeek 对标题和摘要进行两阶段并发评分，并把推荐论文导入待办。
- 可配置分类、排除分类、评分提示词、阈值、批大小、并发、导入数量和缓存时间。
- 支持断点续跑、本地缓存、进度显示和可选的文件服务器同步。
- 可选启用仅绑定 `127.0.0.1:8891` 的本地 RSS，让同机 AI 或阅读器全天订阅今日未完成推荐；08:00–20:00 只限制后台自动同步，不限制 RSS 读取，该服务默认关闭。
- 只有用户主动确认后才会调用评分 API；论文推荐也可以在设置中完全关闭。

### 插件与动态变量

- 内置 arXiv、Calendar-to-Todo 和默认关闭的“SSDP 服务器 IP”三个官方独立进程插件。
- Todo 设置中可安装、启用、禁用、配置、更新、卸载插件，并可从本地安装 `.rwplugin`。
- 桌面磁贴缩放与管理/编辑窗口缩放可以分别调整；窗口比例在下次打开窗口时生效。
- 官方市场索引通过 GitHub Pages 提供，插件二进制来自各自 GitHub Release 并校验 SHA256；v2.0 市场只展示官方插件。
- 动态值写入 `PluginValues.inc`，失败时保留最后成功值并提供 `_Stale` 和 `_UpdatedAt` 变量。
- 第三方插件是普通 Windows 程序；permissions 用于声明和提示，并不是系统级沙箱。仅安装你信任的插件。

### 用户配置备份

- Todo 设置的“关于”页可以导出、导入跨电脑使用的 `.rwbackup` 加密备份。
- 默认仅导出 CalDAV、论文、文件服务器、翻译、界面缩放和日历自动转入规则；也可以选择完整备份待办与本地日程。
- 备份使用独立密码加密；导入到新电脑后，敏感配置会使用新电脑当前 Windows 用户的 DPAPI 重新加密。
- 当前用户配置结构版本为 `2.0`，继续支持导入 `1.0`；插件 secret 会在恢复时用当前 Windows 用户的 DPAPI 重新加密。

## 安装

从 [GitHub Releases](https://github.com/kevendai/Rainmeter_todo/releases/latest) 下载最新版本。

### 已安装 Rainmeter

下载 `rainmeter-desktop-widgets-<版本>.rmskin`，双击后通过 Rainmeter Skin Installer 安装。

`.rmskin` 适合首次安装，不用于迁移已有任务和凭据。

### 尚未安装 Rainmeter

先从 [Rainmeter 官网](https://www.rainmeter.net/) 安装 Rainmeter 4.5.26 或更高版本，再下载 `rainmeter-desktop-widgets-<版本>.rmskin` 并双击安装。完整 ZIP 仅供应用内自动更新下载，不能作为手动安装入口。

### 从旧版本更新

优先在 Todo 设置的“外观、备份与更新”页面点击“检查更新”。插件程序和数据位于 `%LOCALAPPDATA%\RainmeterDesktopWidgets`，不参与皮肤目录替换。更新器会保留：

- 待办任务和日程转换状态
- CalDAV、DeepSeek、文件服务器和腾讯翻译凭据
- 论文设置与本地缓存

为兼容 v1.3.5 的旧版 full/lite 升级器，v2.0.0 的 full 和 lite 引导 zip 会先安装 v1.4.4。第一次更新结束后请再次点击“检查更新”，再由 v1.4.4 的统一更新器校验 SHA256 并安装 v2.0.0；full 和 lite 不再代表不同功能。

## 初次使用

1. 加载 `Todo\Todo.ini` 和 `Calendar\Calendar.ini`。
2. 点击 Todo 顶部的 `+` 新增任务，点击 `☰` 管理全部任务。
3. 如需日程同步，在 Calendar 设置中填写 CalDAV 配置。
4. 如需论文推荐，在 Todo 设置的“插件”页配置 arXiv 插件并填写 DeepSeek API Key。
5. 文件服务器同步和腾讯云标题翻译均为可选功能。

更完整的行为说明：

- [待办与论文推荐](docs/TODO-TILE.md)
- [日程与 CalDAV](docs/CALENDAR-TILE.md)
- [用户配置备份与迁移](docs/USER-BACKUP.md)

## 数据与隐私

- 任务和日程状态保存在本机皮肤资源目录；插件程序、设置、缓存、日志和 job 保存在 `%LOCALAPPDATA%\RainmeterDesktopWidgets`。
- API Key、服务器密码和 CalDAV 等凭据使用 Windows DPAPI CurrentUser 加密。
- 用户主动导出的 `.rwbackup` 使用备份密码派生的独立密钥加密，可以跨 Windows 电脑迁移；密码无法找回。
- 发布包不包含任何凭据、任务、论文缓存或用户配置。
- 启动检查不会自动消费 DeepSeek API；本地评分需要用户主动确认。

## 系统要求

- Windows 10 或更高版本
- Rainmeter 4.5.26 或更高版本
- 使用论文评分、CalDAV 或文件同步时需要网络连接

## 开发

源码目录：

- `skins/`：Todo 与 Calendar 皮肤
- `backend/`：C# 后端
- `scripts/`：部署、测试、升级和打包脚本
- `docs/`：功能与发布文档
- `plugins/official/`：三个官方插件源码与 manifest
- `schemas/`：Plugin API v1 的公开 JSON Schema

运行后端和 UI 冒烟测试：

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-Backends.ps1
```

构建统一包、full/lite 兼容引导包和 `.rmskin`：

```powershell
pwsh.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-ReleasePackages.ps1
```

插件开发接口见 [Plugin API v1](docs/PLUGIN-API.md)，主程序发布流程见 [GitHub Release 指南](docs/GITHUB-RELEASE.md)。
