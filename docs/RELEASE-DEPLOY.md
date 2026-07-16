# 发布包部署说明

本项目正式发布一个统一 `.rmskin` 和一个统一 zip：

- `rainmeter-desktop-widgets-*.rmskin`：已有 Rainmeter 的统一初始安装包。
- `rainmeter-desktop-widgets-*.zip`：统一部署/更新包，包含待办、日程、CalDAV 和可选的论文推荐功能。
- `rainmeter-desktop-widgets-full-*.zip` / `lite-*.zip`：仅供旧升级器使用的轻量引导包。旧升级器先从其中更新自身，随后新版升级器下载上面的统一 zip；引导包不包含 Skins、Rainmeter 安装器或重复编译的后端。
- full/lite `.rmskin` 不再生成。

## 已有 Rainmeter 的初始安装

1. 下载对应的 `.rmskin`。
2. 双击 `.rmskin`，确认 Rainmeter Skin Installer 窗口中的安装内容。
3. 点击 Install。安装完成后应看到 `Todo` 和 `Calendar` 两个皮肤。
4. 初始安装不会迁移旧数据；已有数据或从旧版本更新时，请使用应用内“检查更新”或 zip 部署方式。

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
- 论文开关、DeepSeek API、文件服务器及评分规则通过 Todo 设置窗口保存，密文位于 `Skins\Todo\@Resources\paper-sync.secret`。
- 翻译凭据通过 Todo 设置窗口保存，密文位于 `Skins\Todo\@Resources\translation.secret`。
- 这些 secret 使用 Windows DPAPI CurrentUser 加密，只能由创建它们的 Windows 用户解密。
- 发布包不会覆盖已有的 `tasks.json`、`Generated.inc`、`paper-sync.secret`、`calendar-cache.json` 或 `calendar-state.json`。

## 从源码重新打包

在源码根目录运行：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\Build-ReleasePackages.ps1
```

脚本会生成统一 zip / `.rmskin` 和两个轻量 full/lite 升级引导 zip，并自动下载官方 Rainmeter 4.5.26 安装器到 `.release-cache`。
