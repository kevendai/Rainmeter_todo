# Rainmeter Desktop Widgets

用于维护本机 Rainmeter 桌面磁贴、待办面板和部署脚本。

## 本机环境

- Rainmeter：4.5.26.3894（64 位，便携安装）
- 运行目录：`D:\Program Files (x86)\Rainmeter`
- 项目目录：`E:\Programmes\Rainmeter`
- 当前磁盘皮肤：`illustro\Disk\3 Disks.ini`

## 目录

- `skins/`：可编辑的皮肤源码
- `scripts/`：安装、部署和备份脚本
- `backend/`：Todo / Calendar 的 C# 运行时后端；Rainmeter 运行时不再启动 PowerShell
- `docs/`：项目说明与设计记录
- `backups/`：手动备份（不提交大型或敏感文件）

## 当前状态

- 磁盘磁贴显示 C、D、E 三个盘。
- 已关闭 Rainmeter 默认 Welcome 面板。
- 已关闭 Rainmeter 默认 Clock 面板。
- 已部署桌面右上角“待办 / 已办”磁贴，支持增删改、完成/恢复、点击跳转、定时出现和逾期标红。
- 已内置 C# arXiv 抓取与 DeepSeek 两阶段并发评分；08:00–20:00 启动时只读取已有本地/远端文件，手动刷新确认后才会产生评分 API 调用。
- Todo 与 Calendar 的事件、窗口、JSON、网络同步均由编译后的单文件 C# 后端执行；PowerShell 仅用于部署和凭据维护。
- 后端编译与数据兼容冒烟测试：`powershell -ExecutionPolicy Bypass -File scripts/Test-Backends.ps1`

## 待办磁贴

- 源码：`skins/Todo/`
- 部署脚本：`scripts/Deploy-Todo.ps1`
- 使用和 arXiv 自动同步规则：`docs/TODO-TILE.md`

## 今日日程磁贴

- 源码：`skins/Calendar/`
- 部署：`powershell -ExecutionPolicy Bypass -File scripts/Deploy-Calendar.ps1 -Activate`
- 从 Davis CalDAV 只读同步当天 VEVENT，支持跨天、冲突提示和单向转为带时间待办。
- 详细规则：`docs/CALENDAR-TILE.md`

## 发布包

生成发布包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-ReleasePackages.ps1
```

发布到 GitHub 的正式包如下：

- `rainmeter-desktop-widgets-*.rmskin`：已有 Rainmeter 的统一初始安装包，论文功能由 Todo 设置页运行时开关控制。
- `rainmeter-desktop-widgets-*.zip`：统一 zip，包含 Rainmeter 官方安装器、部署脚本和自动更新器。
- 过渡版本仍生成内容完全相同的 `full` / `lite` 文件名别名，供旧升级器继续下载；它们不再代表不同功能。

`.rmskin` 面向初始安装，不迁移旧数据；已有数据或从旧版本更新时继续使用应用内“检查更新”或 zip 里的 `Install-Skins.ps1`，以保留 `tasks.json`、缓存和 DPAPI 密钥。zip 部署详情见 `docs/RELEASE-DEPLOY.md`。

## 维护提醒

- 每次改完源码、脚本、文档或部署配置后，先运行相关检查，再用 Git 提交并推送到 GitHub 做备份。
- 不要提交 `translation.secret`、`paper-sync.secret`、`caldav.secret`、`tasks.json`、`PaperCache`、备份 JSON 或编译出来的 exe。
