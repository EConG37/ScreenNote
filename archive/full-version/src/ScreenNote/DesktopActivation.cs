using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ScreenNote;

/// <summary>Routes a second desktop launch to this user's existing ScreenNote window.</summary>
public static class DesktopActivation
{
    private const uint QueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private static readonly uint ShowHistoryMessage = RegisterWindowMessage("ScreenNote.ShowHistory");

    public static IDisposable Attach(Window window, Action showHistory)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(showHistory);
        if (ShowHistoryMessage == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册桌面窗口唤醒消息。");
        return new WindowSubscription(window, showHistory);
    }

    public static bool TryShowExisting()
    {
        if (ShowHistoryMessage == 0 || string.IsNullOrWhiteSpace(Environment.ProcessPath)) return false;
        string? currentUser;
        using (var identity = WindowsIdentity.GetCurrent()) currentUser = identity.User?.Value;
        if (currentUser is null) return false;
        var executable = Path.GetFullPath(Environment.ProcessPath);
        var posted = false;
        // EnumWindows includes hidden top-level windows; a tray resident still has its WPF HWND.
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == 0 || processId == (uint)Environment.ProcessId) return true;
            var title = new StringBuilder(512);
            if (GetWindowText(window, title, title.Capacity) == 0
                || !title.ToString().StartsWith("拾屏 ·", StringComparison.Ordinal)) return true;
            if (!MatchesInstance(processId, executable, currentUser)) return true;
            // A user-launched shortcut can transfer its foreground permission to its own resident.
            AllowSetForegroundWindow(processId);
            if (!PostMessage(window, ShowHistoryMessage, nint.Zero, nint.Zero)) return true;
            posted = true;
            return false;
        }, nint.Zero);
        return posted;
    }

    private static bool MatchesInstance(uint processId, string executable, string userSid)
    {
        var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process == nint.Zero) return false;
        try
        {
            var path = new StringBuilder(32768);
            var length = (uint)path.Capacity;
            if (!QueryFullProcessImageName(process, 0, path, ref length)
                || !string.Equals(Path.GetFullPath(path.ToString()), executable, StringComparison.OrdinalIgnoreCase)) return false;
            if (!OpenProcessToken(process, TokenQuery, out var token)) return false;
            try
            {
                using var identity = new WindowsIdentity(token);
                return string.Equals(identity.User?.Value, userSid, StringComparison.Ordinal);
            }
            finally { CloseHandle(token); }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or IOException)
        {
            return false;
        }
        finally { CloseHandle(process); }
    }

    private sealed class WindowSubscription : IDisposable
    {
        private readonly Window _window;
        private readonly HwndSource _source;
        private readonly Action _showHistory;
        private bool _disposed;

        public WindowSubscription(Window window, Action showHistory)
        {
            _window = window;
            _showHistory = showHistory;
            var handle = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("桌面窗口句柄尚未初始化。");
            _source.AddHook(Hook);
        }

        private nint Hook(nint window, int message, nint wParam, nint lParam, ref bool handled)
        {
            if (_disposed || unchecked((uint)message) != ShowHistoryMessage) return nint.Zero;
            handled = true;
            if (!_window.Dispatcher.HasShutdownStarted)
                _window.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) _showHistory(); }));
            return nint.Zero;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!_source.IsDisposed) _source.RemoveHook(Hook);
        }
    }

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder path, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint desiredAccess, out nint token);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
