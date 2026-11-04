using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ScreenNote;

/// <summary>
/// Windows 权限隔离（UIPI）会拦截低权限进程的全局热键和低层键盘钩子，使其在管理员权限的
/// 前台程序（常见于游戏）内失效。这里检测前台程序权限，并支持以管理员身份重启本程序。
/// </summary>
public static class ElevationService
{
    private const uint QueryInformation = 0x0400;
    private const uint QueryLimitedInformation = 0x1000;

    public static bool IsCurrentProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>前台窗口属于提权（或受保护）进程时返回 true，并给出进程名。</summary>
    public static bool IsForegroundProcessElevated(out string processName)
    {
        processName = "";
        var window = GetForegroundWindow();
        if (window == nint.Zero) return false;
        GetWindowThreadProcessId(window, out var processId);
        if (processId == 0 || processId == (uint)Environment.ProcessId) return false;
        return IsProcessElevated(processId, out processName);
    }

    /// <summary>
    /// 普通权限进程打开提权进程时，完整查询权限会被拒绝（访问被拒），而受限查询仍然成功；
    /// 利用这个差异判断目标是否提权，不需要读取目标令牌。
    /// </summary>
    public static bool IsProcessElevated(uint processId, out string processName)
    {
        processName = "";
        var limited = OpenProcess(QueryLimitedInformation, false, processId);
        if (limited == nint.Zero) return false;
        try
        {
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            if (QueryFullProcessImageName(limited, 0, path, ref length))
                processName = Path.GetFileNameWithoutExtension(path.ToString());
        }
        finally { CloseHandle(limited); }
        var full = OpenProcess(QueryInformation, false, processId);
        if (full != nint.Zero) { CloseHandle(full); return false; }
        return Marshal.GetLastWin32Error() == 5;
    }

    /// <summary>弹出 UAC 以管理员身份启动一份新实例；用户取消或失败时返回 false 并给出原因。</summary>
    public static bool RestartElevated(out string error)
    {
        error = "";
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable)) { error = "无法确定当前程序位置。"; return false; }
        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            error = "已取消管理员授权，快捷键在游戏内仍然不可用。"; return false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            error = "管理员启动未完成：" + ex.Message; return false;
        }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}

/// <summary>
/// 监听前台窗口切换：发现提权进程进入前台时通知一次（同一进程名每次会话只提醒一次）。
/// 使用 WINEVENT_OUTOFCONTEXT，不注入其他进程；回调在创建线程的消息循环上执行。
/// </summary>
public sealed class ForegroundElevationWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WinEventOutOfContext = 0x0000;
    private readonly HashSet<string> _notified = new(StringComparer.OrdinalIgnoreCase);
    private readonly Action<string> _notify;
    private readonly WinEventDelegate _callback;
    private nint _hook;
    private bool _disposed;

    public ForegroundElevationWatcher(Action<string> notify)
    {
        ArgumentNullException.ThrowIfNull(notify);
        _notify = notify;
        _callback = OnForeground;
        _hook = SetWinEventHook(EventSystemForeground, EventSystemForeground, nint.Zero, _callback, 0, 0, WinEventOutOfContext);
        if (_hook == nint.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听前台窗口变化。");
    }

    /// <summary>立即检查一次当前前台；用于启动时提权程序已在运行的情况。</summary>
    public void CheckNow()
    {
        if (_disposed) return;
        if (ElevationService.IsForegroundProcessElevated(out var name)) Notify(name);
    }

    private void OnForeground(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time)
    {
        if (_disposed) return;
        try { if (ElevationService.IsForegroundProcessElevated(out var name)) Notify(name); }
        catch { /* 前台进程可能已退出，忽略本次事件 */ }
    }

    private void Notify(string processName)
    {
        if (processName.Length == 0 || !_notified.Add(processName)) return;
        _notify(processName);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hook != nint.Zero) { UnhookWinEvent(_hook); _hook = nint.Zero; }
    }

    private delegate void WinEventDelegate(nint hook, uint eventType, nint window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll")] private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(nint hook);
}
