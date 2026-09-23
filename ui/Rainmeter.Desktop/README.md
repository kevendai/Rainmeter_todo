# 桌面组件 WinUI 3 界面

这是独立的 WinUI 3 迁移基础，采用 C#、.NET 10 和 Windows App SDK。界面代码为本项目新写，未复制参考项目源码。

使用仓库内安装的 .NET SDK 构建：

```powershell
& .winui-tools/dotnet/dotnet.exe publish ui/Rainmeter.Desktop/Rainmeter.Desktop.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64
```

默认读取仓库 `skins` 目录；部署时可设置 `RAINMETER_SKINS_ROOT` 指向实际 Rainmeter Skins 目录。

迁移状态：

- Rainmeter 桌面待办和日程继续保持磁贴；WinUI 管理页也使用磁贴浏览。
- 待办新增、编辑、删除与日程新增、普通日程编辑、详情已经是 WinUI 窗口。日程保存通过无界面命令复用现有本地日历和 CalDAV 服务。
- 插件市场从本地缓存打开，点击“刷新市场”才下载并更新缓存；安装确认、搜索、已安装状态与启用/禁用在 WinUI 内完成。
- 云母与亚克力两种深色材质可切换，选择保存在当前用户的本地应用数据目录。

仍待迁移：周期日程高级编辑、插件逐项配置、备份与 CalDAV 账号等高级设置。当前版本尚未完全替换旧界面；完成迁移后应删除新分支中不再使用的旧窗体。旧设计在原分支/存档中保留。

隔离验证：`& .winui-tools/dotnet/dotnet.exe run --project ui/Rainmeter.Desktop.Tests/Rainmeter.Desktop.Tests.csproj -c Release`。可设置 `RAINMETER_UI_START_PAGE` 为 `todo`、`calendar`、`plugins`、`settings`，搭配 `RAINMETER_SKINS_ROOT` 指向测试数据启动指定页面。
