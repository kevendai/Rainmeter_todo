# 桌面组件 WinUI 3 界面

这是独立的 WinUI 3 迁移基础，采用 C#、.NET 10 和 Windows App SDK。界面代码为本项目新写，未复制参考项目源码。

使用仓库内安装的 .NET SDK 构建：

```powershell
& .winui-tools/dotnet/dotnet.exe publish ui/Rainmeter.Desktop/Rainmeter.Desktop.csproj -c Release -r win-x64 --self-contained true -p:Platform=x64
```

默认读取仓库 `skins` 目录；部署时可设置 `RAINMETER_SKINS_ROOT` 指向实际 Rainmeter Skins 目录。

迁移状态：总览、待办、日历、插件、设置的 WinUI 导航与只读展示已实现。新增、编辑、同步和插件市场暂时转交现有宿主。它们还不是最终的 WinUI 原生实现，不能把当前版本当作完整替换。旧设计在原分支/存档中保留；完成迁移后应删除新分支中不再使用的旧窗体。
