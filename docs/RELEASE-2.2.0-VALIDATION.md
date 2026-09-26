# 2.2.0 升级验证

## 发布修复

- 发布构建必须先 `dotnet publish --self-contained true`，再打包完整 DesktopUI；不复用工作区的旧 UI 二进制。
- 更新器 2.1 关闭安装目录内的 WinUI 进程、检查必需运行时、保留 PaperCache，并修正目录交换中途失败的回滚登记顺序。
- 官方捆绑插件递增补丁版本，避免已有安装继续使用同版本旧程序；保留启用状态和数据目录。
- 移除旧 WinForms 窗体及项目引用，修正过时迁移说明。

## 可重复验证

1. 运行 `pwsh -File scripts/Test-Backends.ps1`。
2. 运行 `.winui-tools/dotnet/dotnet.exe run --project ui/Rainmeter.Desktop.Tests/Rainmeter.Desktop.Tests.csproj -c Release`。
3. 运行 `pwsh -File scripts/Build-ReleasePackages.ps1`。
4. 下载并校验 GitHub v2.1.0 的正式 ZIP，使用 .NET Framework C# 编译器编译 `tests/UpgradePackageProbe.cs`，引用 `System.IO.Compression.FileSystem.dll` 和 `System.Web.Extensions.dll`。
5. 探针参数依次为旧 ZIP、新 ZIP、尚不存在的隔离目录。它直接加载已发布旧更新器的 ZIP 读取器，模拟等待父进程的交接，并实际安装新包；不启动真实 Rainmeter，插件数据也隔离。

探针检查任务和主题不变、论文缓存保留、旧文件清除、完整运行时落盘、事务目录清理，以及已安装插件升级且禁用状态保持。隔离安装的 WinUI 已验证能创建窗口且无 startup-error.txt；不等同于全部页面的人工视觉验收。

2.1.0 无需先单独更新更新器：旧版下载、校验、解压后启动新包内的更新器完成安装。新包解压大小低于旧版 512 MiB 限制，单文件及条目数也在旧限制内。
