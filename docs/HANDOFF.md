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
8. Todo C# 后端已内置 arXiv 抓取、DeepSeek 两阶段并发评分、断点缓存、File Browser 同步和 Todo 导入，不依赖外部 Python 评分工具。
9. 论文推荐由统一版本的运行时开关控制；启动只读取已有文件，手动刷新确认后才会调用评分 API。

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

等待用户在 Todo 设置页填写 DeepSeek API 后，主动点击“测试 DeepSeek”做小额真实请求，再点击磁贴右上角 `↻` 验证首次后台抓取、评分、上传和 Todo 导入。
