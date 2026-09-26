using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using RainmeterBackend;


internal static partial class TodoApp
{
    private static void RenderUiScaleSkins()
    {
        string todoExe=Path.Combine(ResourceDir,"TodoHost.exe");
        using(Process todo=Process.Start(new ProcessStartInfo(todoExe,"Render"){UseShellExecute=false,CreateNoWindow=true}))
        {
            if(todo!=null&&!todo.WaitForExit(15000))throw new Exception("待办磁贴刷新超时");
            if(todo!=null&&todo.ExitCode!=0)throw new Exception("待办磁贴刷新失败");
        }
        string calendarExe=Path.GetFullPath(Path.Combine(ResourceDir,"..","..","Calendar","@Resources","CalendarHost.exe"));
        if(File.Exists(calendarExe))using(Process calendar=Process.Start(new ProcessStartInfo(calendarExe,"Render"){UseShellExecute=false,CreateNoWindow=true}))
        {
            if(calendar!=null&&!calendar.WaitForExit(15000))throw new Exception("日程磁贴刷新超时");
            if(calendar!=null&&calendar.ExitCode!=0)throw new Exception("日程磁贴刷新失败");
        }
        RuntimeUtil.RefreshAll();
    }

    private delegate void LockedStateAction(Dictionary<string, object> state, ref bool refresh);
    private static void WithGlobalStateLocks(Action action)
    {
        // Calendar code can acquire its state mutex before touching Todo state.
        // Keep the same order here so backup import cannot deadlock with it.
        using (Mutex calendar = new Mutex(false, @"Global\RainmeterCalendarState"))
        using (Mutex todo = new Mutex(false, @"Global\RainmeterTodoState"))
        {
            bool calendarHeld = false, todoHeld = false;
            try
            {
                try { calendarHeld = calendar.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { calendarHeld = true; }
                if (!calendarHeld) throw new Exception("日历数据正在使用，请稍后重试。");
                try { todoHeld = todo.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { todoHeld = true; }
                if (!todoHeld) throw new Exception("待办数据正在使用，请稍后重试。");
                action();
            }
            finally
            {
                if (todoHeld) todo.ReleaseMutex();
                if (calendarHeld) calendar.ReleaseMutex();
            }
        }
    }

    private static int WithLockedState(LockedStateAction action)
    {
        using (Mutex mutex = new Mutex(false, @"Global\RainmeterTodoState"))
        {
            bool held = false;
            Dictionary<string, object> state = null;
            try
            {
                try { held = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { held = true; }
                if (!held) return 4;
                state = LoadState();
                bool refresh = false;
                action(state, ref refresh);
                if (refresh) Refresh();
                return 0;
            }
            catch (Exception ex)
            {
                if (state != null)
                {
                    Meta(state)["status"] = "操作失败：" + ex.Message;
                    try { Commit(state); Refresh(); } catch { }
                }
                return 1;
            }
            finally { if (held) mutex.ReleaseMutex(); }
        }
    }
}
