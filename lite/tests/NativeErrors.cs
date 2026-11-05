using System.Runtime.InteropServices;

internal static class NativeErrors
{
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);
    internal static void SuppressDialogs() => SetErrorMode(0x8003);
}
