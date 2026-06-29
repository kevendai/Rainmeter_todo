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
- 已接入论文网页同步服务：08:00–20:00 启动检查，待办无论文时取摘要分前五篇。
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

当前发布版本：`1.2.4`。

生成发布包：

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Build-ReleasePackages.ps1 -Version 1.2.4
```

发布到 GitHub 的两个 zip 分工如下：

- [`rainmeter-desktop-widgets-full-1.2.4.zip`](releases/v1.2.4/rainmeter-desktop-widgets-full-1.2.4.zip)：完整功能版，包含待办、今日日程、CalDAV 同步、日程转待办、论文推送和论文标题翻译入口。不要把 `translation.secret`、`paper-sync.secret`、`caldav.secret`、`tasks.json` 或缓存放进包里。
- [`rainmeter-desktop-widgets-lite-1.2.4.zip`](releases/v1.2.4/rainmeter-desktop-widgets-lite-1.2.4.zip)：精简版，包含待办和今日日程，但编译时关闭论文推送与论文标题翻译，并隐藏对应界面入口。CalDAV 功能保留。

两个 zip 内都包含 Rainmeter 4.5.26 官方安装器、已编译后端、皮肤文件、`Install-Skins.ps1` 和 `DEPLOY.md`。空白机器部署时可以选择 Rainmeter 便携目录，也可以使用标准安装的 `Documents\Rainmeter` 皮肤库目录；详细说明见 `docs/RELEASE-DEPLOY.md`。

## 维护提醒

- 每次改完源码、脚本、文档或部署配置后，先运行相关检查，再用 Git 提交并推送到 GitHub 做备份。
- 不要提交 `translation.secret`、`paper-sync.secret`、`caldav.secret`、`tasks.json`、缓存、备份 JSON 或编译出来的 exe。
