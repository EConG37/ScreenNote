using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using Drawing = System.Drawing;

namespace ScreenNote;

// Queue fast wheel gestures and replay small steps only after the preceding frame
// has been sampled. The hook itself never captures or waits on the UI thread.
internal sealed class ScrollWheelGate : IDisposable
{
    private readonly Drawing.Rectangle _region;
    private readonly HookProc _callback;
    private nint _hook;
    private int _pending;
    private const nuint Marker = 0x534E5748;
    internal int Pending => Volatile.Read(ref _pending);
    internal ScrollWheelGate(Drawing.Rectangle region)
    {
        _region = region; _callback = OnMouse;
        _hook = SetWindowsHookEx(14, _callback, GetModuleHandle(null), 0);
        if (_hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用滚动保护，请重试。");
    }
    private nint OnMouse(int code, nuint message, nint data)
    {
        if (code >= 0 && message == 0x020A)
        {
            var value = Marshal.PtrToStructure<MouseHook>(data);
            if (value.Extra != Marker && _region.Contains(value.X, value.Y))
            {
                var delta = (short)(value.MouseData >> 16);
                Interlocked.Add(ref _pending, delta);
                return 1;
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }
    internal bool ReplayStep()
    {
        if (!GetCursorPos(out var point) || !_region.Contains(point.X, point.Y)) { Clear(); return false; }
        var queued = Pending;
        if (queued == 0) return false;
        var stepSize = _region.Height >= 320 ? 120 : 30;
        var step = Math.Clamp(queued, -stepSize, stepSize);
        var input = new Input { Type = 0, Mouse = new MouseInput { Data = unchecked((uint)step), Flags = 0x0800, Extra = Marker } };
        if (SendInput(1, [input], Marshal.SizeOf<Input>()) != 1) throw new Win32Exception(Marshal.GetLastWin32Error(), "页面未接收滚动，请使用相同权限打开页面和拾屏。");
        Interlocked.Add(ref _pending, -step);
        return true;
    }
    internal void Clear() => Interlocked.Exchange(ref _pending, 0);
    public void Dispose() { if (_hook != 0) { UnhookWindowsHookEx(_hook); _hook = 0; } Clear(); }
    private delegate nint HookProc(int code, nuint message, nint data);
    [StructLayout(LayoutKind.Sequential)] private struct MouseHook { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nuint message, nint data);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
