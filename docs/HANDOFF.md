# Codex 项目交接

## 用户目标

在 Windows 桌面右上角制作“待办 / 已办”磁贴。任务文字可点击，并跳转到网页、本地文件、文件夹或应用。Wallpaper Engine 继续负责动态壁纸，Rainmeter 负责可交互磁贴。

## 已完成

1. 从 Rainmeter 官方 GitHub 发布页下载 `Rainmeter-4.5.26.exe`。
2. 安装包数字签名验证为有效；SHA-256：`A3A5579B1B54C03FB5301CAD3D68731D3AB4620F6BCB0BA2585AE5823B4187C7`。
3. 使用官方便携模式安装到 `D:\Program Files (x86)\Rainmeter`。
4. 已验证实际运行进程路径为 `D:\Program Files (x86)\Rainmeter\Rainmeter.exe`。
5. 将默认双磁盘皮肤扩展为 C、D、E 三盘版本。
6. 已卸载桌面上的 `illustro\Welcome` 和 `illustro\Clock` 皮肤。
7. 已创建并激活右上角 `Todo\Todo.ini` 皮肤，支持待办增删改、完成/恢复和目标跳转。
8. 已用 Rainmeter + C# 后端接入 `E:\Programmes\skills\daily_arxiv` 数据，无需 Python 或 PowerShell 运行时；按摘要分、标题分降序取前五篇。
9. 已验证 2026-06-21 启动检查；当日暂无已评分 JSON，磁贴正确显示“暂无 2026-06-21 已评分论文数据”。

## 重要路径

- Rainmeter 程序和实时配置：`D:\Program Files (x86)\Rainmeter`
- 已部署三盘皮肤：`D:\Program Files (x86)\Rainmeter\Skins\illustro\Disk\3 Disks.ini`
- 项目源码：`E:\Programmes\Rainmeter`
- 已部署待办皮肤：`D:\Program Files (x86)\Rainmeter\Skins\Todo\Todo.ini`
- 运行中的待办数据：`D:\Program Files (x86)\Rainmeter\Skins\Todo\@Resources\tasks.json`

## 工作约定

- 先在本项目内修改源码，再部署到 D 盘 Rainmeter 运行目录。
- 每次部署后应刷新 Rainmeter，并核对启用的皮肤与实际进程路径。
- 不把 Rainmeter 的运行目录直接当作唯一源码副本。

## 下一步

等待用户从桌面实际使用磁贴，根据体感调整宽度、透明度、行高或右上角位置。当 `daily_arxiv` 产生当日已评分 JSON 后，可点击磁贴右上角 `↻` 做首次真实论文导入验证。
