# 今日日程磁贴

## 数据边界

- CalDAV 是日程标题、时间、周期和手机提醒的来源；Rainmeter 只读访问 CalDAV。
- 日程可单向转为本地带时间待办，不支持待办反向写入日历。
- 转换时可选择是否从桌面日程磁贴隐藏；无论如何，CalDAV 和手机日历中的原事件保持不变。
- `calendar-cache.json` 是可重建缓存；`calendar-state.json` 是持久转换账本，部署时必须保留。

## 当天与排序

- 仅显示与本地当天 `[00:00, 次日 00:00)` 有交集的事件，跨天事件在涉及的每一天显示。
- 每天零点换日；若零点时电脑休眠，唤醒后的第一次分钟检查补刷新。
- 启动、手动点击 `↻`、换日及每 15 分钟执行一次只读同步。
- 按真实开始时间、结束时间、标题依次升序；时间重叠的事件使用冲突色。
- 磁贴直接显示地点；点击日程打开只读详情窗，查看完整时间、最早提醒、地点、备注和链接。

## 转为待办

- `仅本次`：记录 `UID + RECURRENCE-ID`，本期以后不会因重新同步而回到日程磁贴。
- `本次及今后`：记录 UID 级系列规则，未来每一期自动生成独立待办。
- 标题以 `[待办]` 或 `[代办]` 开头时，等价于系列自动转入；前缀不会出现在待办标题中。
- 最早的 CalDAV 提醒映射为 `available_from`；没有提醒时才使用日程开始时间。日程结束映射为 `due_at`，全天事件的截止时间取最后一天 23:59。
- 转换按钮位于日程详情窗；生成的待办备注会写入日程实际起止时间、最早提醒、地点和原备注。跳转目标依次读取日程的标准 URL、地点和备注，并提取第一个 `http://`、`https://` 或 `wemeet://` 链接；`wemeet://` 点击后由 Windows 注册协议直接启动腾讯会议。
- 顶部 `☰` 可停止系列的未来自动转入；已经生成的待办不受影响。

## 文件

- 日程源码：`skins/Calendar/`
- CalDAV 凭据：复用 `Todo/@Resources/caldav.secret`，由 Windows DPAPI CurrentUser 加密。
- 待办数据：`Todo/@Resources/tasks.json`
- 部署：`powershell -ExecutionPolicy Bypass -File scripts/Deploy-Calendar.ps1 -Activate`

运行时由 `CalendarHost.exe` 直接完成 CalDAV 发现、REPORT 查询、ICS 解析、转换账本和详情窗口；它由 `backend/Common.cs` 与 `backend/CalendarApp.cs` 编译，不再调用 PowerShell。Calendar 转 Todo 时通过全局互斥锁直接更新原有 `tasks.json`，然后调用 `TodoHost.exe Render`。
