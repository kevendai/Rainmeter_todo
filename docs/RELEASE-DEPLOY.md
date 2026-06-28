# 发布包部署说明

本项目发布两个 zip：

- `rainmeter-desktop-widgets-full-*.zip`：完整功能版，包含待办、今日日程、CalDAV 同步、日程转待办、论文推送和论文标题翻译入口。发布包不会包含 `translation.secret`、`paper-sync.secret`、`caldav.secret`、`tasks.json` 或任何缓存。
- `rainmeter-desktop-widgets-lite-*.zip`：精简版，包含待办和今日日程，关闭论文推送与论文翻译。CalDAV 日程功能仍可使用。

## 空白机器部署

1. 解压对应 zip。
2. 运行包内 `Rainmeter-4.5.26.exe`。
3. 安装时选择 Portable installation，并自行选择一个 Rainmeter 便携安装目录。记下这个目录，后面部署皮肤时会用到。
4. 安装完成后关闭 Rainmeter。
5. 在解压目录运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Skins.ps1 -Activate
   ```

6. 脚本会询问 Rainmeter 皮肤库目录。便携安装可输入第 3 步选择的目录；标准安装可输入 `Documents\Rainmeter`，也可以直接输入 `Documents\Rainmeter\Skins`。
7. 脚本部署皮肤后会自动重启 Rainmeter；启动后应看到 `Todo` 和 `Calendar` 两个皮肤。

## 凭据与数据

- CalDAV 凭据通过 Calendar 设置窗口保存，密文位于目标 Rainmeter 目录的 `Skins\Todo\@Resources\caldav.secret`。
- 完整功能版的论文同步凭据通过 Todo 设置窗口保存，密文位于 `Skins\Todo\@Resources\paper-sync.secret`。
- 完整功能版的翻译凭据通过 Todo 设置窗口保存，密文位于 `Skins\Todo\@Resources\translation.secret`。
- 这些 secret 使用 Windows DPAPI CurrentUser 加密，只能由创建它们的 Windows 用户解密。
- 发布包不会覆盖已有的 `tasks.json`、`Generated.inc`、`paper-sync.secret`、`calendar-cache.json` 或 `calendar-state.json`。

## 从源码重新打包

在源码根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-ReleasePackages.ps1
```

脚本会生成完整功能版和精简版两个 zip，并自动下载官方 Rainmeter 4.5.26 安装器到 `.release-cache`。
