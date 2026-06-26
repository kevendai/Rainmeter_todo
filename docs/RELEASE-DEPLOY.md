# 发布包部署说明

本项目发布两个 zip：

- `rainmeter-desktop-widgets-full-*.zip`：完整功能版，包含待办、今日日程、CalDAV 同步、日程转待办、论文推送和论文标题翻译入口。发布包不会包含 `translation.secret`、`caldav.secret`、`tasks.json` 或任何缓存。
- `rainmeter-desktop-widgets-lite-*.zip`：精简版，包含待办和今日日程，关闭论文推送与论文翻译。CalDAV 日程功能仍可使用。

## 空白机器部署

1. 解压对应 zip。
2. 运行包内 `Rainmeter-4.5.26.exe`。
3. 安装时选择 Portable installation，目标目录建议使用 `D:\Program Files (x86)\Rainmeter`。
4. 安装完成后关闭 Rainmeter。
5. 在解压目录运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Install-Skins.ps1 -RainmeterRoot 'D:\Program Files (x86)\Rainmeter' -Activate
   ```

6. 启动或刷新 Rainmeter 后，应看到 `Todo` 和 `Calendar` 两个皮肤。

## 凭据与数据

- CalDAV 凭据通过 Calendar 设置窗口保存，密文位于目标 Rainmeter 目录的 `Skins\Todo\@Resources\caldav.secret`。
- 完整功能版的翻译凭据通过 Todo 设置窗口保存，密文位于 `Skins\Todo\@Resources\translation.secret`。
- 这些 secret 使用 Windows DPAPI CurrentUser 加密，只能由创建它们的 Windows 用户解密。
- 发布包不会覆盖已有的 `tasks.json`、`Generated.inc`、`calendar-cache.json` 或 `calendar-state.json`。

## 从源码重新打包

在源码根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-ReleasePackages.ps1
```

脚本会生成完整功能版和精简版两个 zip，并自动下载官方 Rainmeter 4.5.26 安装器到 `.release-cache`。
