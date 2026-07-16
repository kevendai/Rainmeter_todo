# Rainmeter Desktop Widgets

一套面向 Windows 的 Rainmeter 桌面组件，把待办、日程和论文推荐放在桌面右上角。日常操作通过原生 C# 窗口完成，不需要常驻终端，也不依赖外部 Python 服务。

## 功能

### 待办与已办

- 快速新增、编辑、完成、恢复和删除任务。
- 支持开始时间、截止时间、逾期标红、备注、标签和批量管理。
- 点击任务可打开网页、文件、文件夹或应用。
- 每天 06:00 自动整理上一周期未完成的论文任务，并区分“已读”和“自动归档”。

### 今日日程

- 从 CalDAV 只读同步当天日程，支持跨天事件、重复日程和时间冲突提示。
- 显示地点、备注、会议链接和提醒时间。
- 可将单次日程或整个重复系列单向转换为本地待办，不修改服务器上的原日程。
- 支持网页链接和腾讯会议链接。

### arXiv 论文推荐

- 直接在本机使用 C# 抓取 arXiv RSS，不需要安装 Python。
- 使用 DeepSeek 对标题和摘要进行两阶段并发评分，并把推荐论文导入待办。
- 可配置分类、排除分类、评分提示词、阈值、批大小、并发、导入数量和缓存时间。
- 支持断点续跑、本地缓存、进度显示和可选的文件服务器同步。
- 只有用户主动确认后才会调用评分 API；论文推荐也可以在设置中完全关闭。

## 安装

从 [GitHub Releases](https://github.com/kevendai/Rainmeter_todo/releases/latest) 下载最新版本。

### 已安装 Rainmeter

下载 `rainmeter-desktop-widgets-<版本>.rmskin`，双击后通过 Rainmeter Skin Installer 安装。

`.rmskin` 适合首次安装，不用于迁移已有任务和凭据。

### 尚未安装 Rainmeter

先从 [Rainmeter 官网](https://www.rainmeter.net/) 安装 Rainmeter 4.5.26 或更高版本，再下载 `rainmeter-desktop-widgets-<版本>.rmskin` 并双击安装。完整 ZIP 仅供应用内自动更新下载，不能作为手动安装入口。

### 从旧版本更新

优先在 Todo 设置的“关于”页面点击“检查更新”。更新器会保留：

- 待办任务和日程转换状态
- CalDAV、DeepSeek、文件服务器和腾讯翻译凭据
- 论文设置与本地缓存

为兼容旧版 full/lite 升级器，每个版本仍发布 full 和 lite 引导 zip。它们会先更新旧升级器，再下载同一份统一完整包；full 和 lite 不再代表不同功能。

## 初次使用

1. 加载 `Todo\Todo.ini` 和 `Calendar\Calendar.ini`。
2. 点击 Todo 顶部的 `+` 新增任务，点击 `☰` 管理全部任务。
3. 如需日程同步，在 Calendar 设置中填写 CalDAV 配置。
4. 如需论文推荐，在 Todo 设置中开启该功能并填写 DeepSeek API Key。
5. 文件服务器同步和腾讯云标题翻译均为可选功能。

更完整的行为说明：

- [待办与论文推荐](docs/TODO-TILE.md)
- [日程与 CalDAV](docs/CALENDAR-TILE.md)

## 数据与隐私

- 任务、日程缓存和论文缓存只保存在本机 Rainmeter 皮肤目录。
- API Key、服务器密码和 CalDAV 等凭据使用 Windows DPAPI CurrentUser 加密。
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

运行后端和 UI 冒烟测试：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Test-Backends.ps1
```

构建统一包、full/lite 兼容引导包和 `.rmskin`：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-ReleasePackages.ps1
```

发布流程见 [GitHub Release 指南](docs/GITHUB-RELEASE.md)。
