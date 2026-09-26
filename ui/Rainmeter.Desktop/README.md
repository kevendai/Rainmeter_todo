# 桌面组件 WinUI 3 界面

这是独立的 WinUI 3 迁移基础，采用 C#、.NET 10 和 Windows App SDK。界面代码为本项目新写，未复制参考项目源码。

应用图标使用用户提供的原创图稿 `Assets/brand-mark.png`。运行 `scripts/New-DesktopIcon.ps1` 可重新生成 16–256 像素的 Windows `.ico`；构建时将图标嵌入可执行文件，运行时也设置窗口图标。

使用仓库内安装的 .NET SDK 构建：

```powershell
& .winui-tools/dotnet/dotnet.exe publish ui/Rainmeter.Desktop/Rainmeter.Desktop.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64
```

默认读取仓库 `skins` 目录；部署时可设置 `RAINMETER_SKINS_ROOT` 指向实际 Rainmeter Skins 目录。

迁移状态：

- Rainmeter 桌面待办和日程继续保持磁贴；WinUI 待办管理使用列表，日程管理使用日历。
- 活动磁贴的管理、新增、编辑和详情命令通过 `TodoHost.exe` / `CalendarHost.exe` 转入 WinUI。发布包必须包含 `Todo/@Resources/DesktopUI` 的完整自包含运行时，不再回退旧窗口。
- 待办新增、编辑、删除与日程新增、普通日程编辑、详情已经是 WinUI 窗口。日程保存通过无界面命令复用现有本地日历和 CalDAV 服务。
- 插件市场从本地缓存打开，点击“刷新市场”才下载并更新缓存；安装确认、搜索、已安装状态与启用/禁用在 WinUI 内完成。
- 云母与亚克力两种窗口材质可切换；配色可选日间、夜间和跟随 Windows。选择保存在当前用户的本地应用数据目录。
- 管理、新增和设置入口复用同一 WinUI 进程；重复点击会切换现有窗口到对应页面。

周期日程编辑、提醒、插件配置、日程转待办管理、备份与 CalDAV 设置均由 WinUI 提供。旧窗体源码及 WinForms 运行依赖已移除；历史设计仅保留在 Git 历史中。

隔离验证：`& .winui-tools/dotnet/dotnet.exe run --project ui/Rainmeter.Desktop.Tests/Rainmeter.Desktop.Tests.csproj -c Release`。可设置 `RAINMETER_UI_START_PAGE` 为 `todo`、`calendar`、`plugins`、`settings`，搭配 `RAINMETER_SKINS_ROOT` 指向测试数据启动指定页面。
