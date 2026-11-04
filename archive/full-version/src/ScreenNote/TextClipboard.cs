using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenNote;

internal static class TextClipboard
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);

    // Eager CF_UNICODETEXT avoids delayed OLE rendering/flush contention with clipboard monitors.
    internal static bool TrySetText(nint owner, string text)
    {
        if (owner == 0) return false;
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        var memory = GlobalAlloc(0x0042, (nuint)bytes.Length);
        if (memory == 0) return false;
        var opened = false;
        try
        {
            var address = GlobalLock(memory);
            if (address == 0) return false;
            try { Marshal.Copy(bytes, 0, address, bytes.Length); } finally { GlobalUnlock(memory); }
            if (!OpenClipboard(owner)) return false;
            opened = true;
            if (!EmptyClipboard() || SetClipboardData(13, memory) == 0) return false;
            memory = 0; // The OS owns the allocated block after successful SetClipboardData.
            return true;
        }
        finally { if (opened) CloseClipboard(); if (memory != 0) GlobalFree(memory); }
    }
}
