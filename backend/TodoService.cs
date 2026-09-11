using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using RainmeterBackend;


internal static partial class TodoApp
{
    private static int AddInteractive()
    {
        EditorResult e = ShowEditor(null);
        if (e == null) return 0;
        return WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
            int rolled = Normalize(state);
            if (rolled > 0) Meta(state)["status"] = "已自动归档昨日论文" + rolled + " 篇";
            Tasks(state).Add(NewTask(e, "manual"));
            Meta(state)["status"] = "已新增待办";
            Commit(state);
            refresh = true;
        });
    }

    private static int EditInteractive(string id)
    {
        Dictionary<string, object> snapshot = null;
        int loaded = WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
            Dictionary<string, object> task = Find(state, id);
            if (task != null) snapshot = new Dictionary<string, object>(task);
        });
        if (loaded != 0 || snapshot == null) return loaded;
        EditorResult e = ShowEditor(snapshot);
        if (e == null) return 0;
        return WithLockedState(delegate(Dictionary<string, object> state, ref bool refresh) {
            Dictionary<string, object> task = Find(state, id);
            if (task == null) return;
            task["title"] = e.Title; task["target"] = e.Target; task["note"] = e.Note; task["labels"] = e.Labels.Cast<object>().ToList(); task["available_from"] = e.Available == "" ? null : (object)e.Available; task["due_at"] = e.Due == "" ? null : (object)e.Due;
            Meta(state)["status"] = "已修改待办";
            Commit(state);
            refresh = true;
        });
    }

    private static int ManageInteractive()
    {
        Dictionary<string, object> state = null;
        int loaded = WithLockedState(delegate(Dictionary<string, object> current, ref bool refresh) { state = current; });
        if (loaded != 0 || state == null) return loaded;
        bool refreshAfter = false;
        try { Manage(state, ref refreshAfter); if (refreshAfter) Refresh(); return 0; }
        catch (Exception ex)
        {
            return WithLockedState(delegate(Dictionary<string, object> current, ref bool refresh) {
                Meta(current)["status"] = "操作失败：" + ex.Message;
                Commit(current);
                refresh = true;
            });
        }
    }

    private static int SettingsInteractive()
    {
        try { ShowSettings(); return 0; }
        catch (Exception ex) { LightUi.Error("设置失败：" + ex.Message); return 1; }
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
