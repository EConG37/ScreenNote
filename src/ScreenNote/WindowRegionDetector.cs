using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Drawing = System.Drawing;

namespace ScreenNote;

internal static class WindowRegionDetector
{
    // EnumWindows supplies front-to-back order; bounds are physical desktop pixels.
    internal static IReadOnlyList<Drawing.Rectangle> Snapshot(Drawing.Rectangle desktop)
    {
        var regions = new List<Drawing.Rectangle>();
        EnumWindows((window, _) =>
        {
            if (!IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out var process);
            if (process == Environment.ProcessId) return true;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var name = new System.Text.StringBuilder(128); GetClassName(window, name, name.Capacity);
            if (name.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return true;
            if (DwmGetWindowAttribute(window, 9, out NativeRect rect, Marshal.SizeOf<NativeRect>()) != 0 && !GetWindowRect(window, out rect)) return true;
            var region = Drawing.Rectangle.Intersect(desktop, Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom));
            if (region.Width > 32 && region.Height > 32) regions.Add(region);
            return true;
        }, 0);
        return regions;
    }
    private delegate bool EnumCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint window, System.Text.StringBuilder text, int capacity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rect);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out NativeRect value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, uint attribute, out int value, int size);
}
