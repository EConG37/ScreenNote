using System;
using System.Collections.Generic;
using System.Threading;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace ScreenNote;

public sealed record RecordedShortcut(Key Key, ModifierKeys Modifiers);

public sealed class HotkeyService : IDisposable
{
    private const int FirstId = 0x5C17;
    private const int SecondId = 0x5C18;
    private const int ProbeId = 0x5C19;
    private const uint NoRepeat = 0x4000;
    private static readonly string[] Suggestions =
    [
        "Alt+A", "Alt+S", "Ctrl+F8", "Shift+F8", "Ctrl+F9", "Shift+F9", "Ctrl+Alt+A", "Ctrl+Alt+S"
    ];
    private readonly nint _handle;
    private readonly HwndSource _source;
    private int? _activeId;
    private uint _currentModifiers, _currentKey;
    private volatile bool _disposed;
    private volatile bool _suspended;
    private volatile bool _recording;
    private long _priorityBinding;
    private nint _keyboardHook;
    private Thread? _keyboardThread;
    private uint _keyboardThreadId;
    private readonly KeyboardHookProc _keyboardCallback;
    private readonly HashSet<uint> _modifierKeys = [];
    private uint _suppressedKey;
    public bool UsesPriorityCapture { get; private set; }
    public bool Suspended
    {
        get => _suspended;
        set
        {
            _source.Dispatcher.VerifyAccess();
            if (_suspended == value || _disposed) return;
            _suspended = value;
            if (value && _activeId is int active && UnregisterHotKey(_handle, active)) _activeId = null;
            else if (!value && !UsesPriorityCapture && _activeId == null && CurrentCombination is string combination)
            { if (!Register(combination, out _)) Register(combination, out _, true); }
        }
    }
    public string? CurrentCombination { get; private set; }
    public event EventHandler? Pressed;
    public event EventHandler<RecordedShortcut>? Recorded;
    public bool BeginRecording(out string error) { if (!StartKeyboardHook(out error)) return false; _recording = true; return true; }
    public void EndRecording() => _recording = false;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(nint window, int id);
    private delegate nint KeyboardHookProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int id, KeyboardHookProc callback, nint module, uint threadId);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint threadId, uint message, nint w, nint l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? module);

    public HotkeyService(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("无法创建快捷键消息窗口。");
        _source.AddHook(Hook);
        _keyboardCallback = KeyboardHook;
    }

    /// <summary>Claims a new combination before releasing the old one; a conflict leaves the old registration intact.</summary>
    public bool Register(string combination, out string error, bool preferScreenshot = false)
    {
        _source.Dispatcher.VerifyAccess();
        if (!Validate(combination, out var modifiers, out var key, out error)) return false;
        if (preferScreenshot && ModifierCount(modifiers) > 2) { error = "最多使用两个辅助键加一个主键，例如 Alt+A 或 Ctrl+Alt+A。"; return false; }
        if ((UsesPriorityCapture || _activeId != null) && CurrentCombination != null && modifiers == _currentModifiers && key == _currentKey && preferScreenshot == UsesPriorityCapture) return true;

        if (preferScreenshot)
        {
            if (!StartKeyboardHook(out error)) return false;
            if (_activeId is int registered && !UnregisterHotKey(_handle, registered)) { error = "原快捷键未能释放，设置保持原样。"; return false; }
            _activeId = null; _currentModifiers = modifiers; _currentKey = key;
            CurrentCombination = CanonicalCombination(modifiers, key); UsesPriorityCapture = true;
            Volatile.Write(ref _priorityBinding, ((long)modifiers << 32) | key);
            return true;
        }

        var candidateId = _activeId == FirstId ? SecondId : FirstId;
        if (!RegisterHotKey(_handle, candidateId, modifiers | NoRepeat, key))
        {
            error = RegistrationError(Marshal.GetLastWin32Error());
            return false;
        }

        // The current key stays reserved throughout the candidate registration.
        if (_activeId is int oldId && !UnregisterHotKey(_handle, oldId))
        {
            UnregisterHotKey(_handle, candidateId);
            error = "未能切换快捷键，原快捷键已保留。请稍后重试。";
            return false;
        }
        _activeId = candidateId;
        if (_suspended) { UnregisterHotKey(_handle, candidateId); _activeId = null; }
        Volatile.Write(ref _priorityBinding, 0); UsesPriorityCapture = false;
        _currentModifiers = modifiers;
        _currentKey = key;
        CurrentCombination = CanonicalCombination(modifiers, key);
        error = "";
        return true;
    }

    /// <summary>Tests availability by briefly registering a separate ID, without changing the current hotkey.</summary>
    public bool Probe(string combination, out string error)
    {
        _source.Dispatcher.VerifyAccess();
        if (!Validate(combination, out var modifiers, out var key, out error)) return false;
        if (CurrentCombination != null && modifiers == _currentModifiers && key == _currentKey) return true;
        if (!RegisterHotKey(_handle, ProbeId, modifiers | NoRepeat, key))
        {
            error = RegistrationError(Marshal.GetLastWin32Error());
            return false;
        }
        if (!UnregisterHotKey(_handle, ProbeId))
        {
            error = "快捷键检测未完成，请稍后重试。";
            return false;
        }
        error = "";
        return true;
    }

    /// <summary>Availability is checked now; callers should still handle a conflict when registering later.</summary>
    public string? FindAvailableSuggestion()
    {
        foreach (var suggestion in Suggestions)
            if (Probe(suggestion, out _)) return suggestion;
        return null;
    }

    public static bool TryNormalize(string text, out string normalized)
    {
        normalized = "";
        if (!TryParse(text, out var modifiers, out var key)) return false;
        normalized = CanonicalCombination(modifiers, key);
        return true;
    }

    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (Array.Exists(parts, string.IsNullOrWhiteSpace)) return false;
        uint parsedModifiers = 0;
        for (var index = 0; index < parts.Length - 1; index++)
        {
            uint flag = parts[index].ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" or "CTL" => 2,
                "ALT" or "MENU" => 1,
                "SHIFT" => 4,
                "WIN" or "WINDOWS" or "META" => 8,
                _ => 0
            };
            if (flag == 0 || (parsedModifiers & flag) != 0) return false;
            parsedModifiers |= flag;
        }
        var keyText = parts[^1].ToUpperInvariant() switch
        {
            "ESC" => "Escape", "RETURN" => "Enter", "SPACEBAR" => "Space",
            "BACKSPACE" => "Back", "PGUP" => "PageUp", "PGDN" => "PageDown",
            "DEL" => "Delete", "INS" => "Insert", "PRTSC" or "PRTSCN" => "PrintScreen",
            var value => value
        };
        if (keyText.Length == 1 && keyText[0] is >= '0' and <= '9') keyText = "D" + keyText;
        // Enum.TryParse accepts integer enum values; only named keys (and normalized digit names) are valid here.
        if (char.IsDigit(keyText[0]) || !Enum.TryParse<Key>(keyText, true, out var parsed) || !Enum.IsDefined(parsed) || parsed == Key.None ||
            parsed is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return false;
        var parsedKey = (uint)KeyInterop.VirtualKeyFromKey(parsed);
        if (parsedKey == 0) return false;
        if (parsedModifiers == 0 && parsed != Key.Snapshot && parsed is not (>= Key.F1 and <= Key.F11)) return false;
        modifiers = parsedModifiers;
        key = parsedKey;
        return true;
    }

    private bool Validate(string combination, out uint modifiers, out uint key, out string error)
    {
        error = "";
        if (!TryParse(combination, out modifiers, out key)) { error = "请按下快捷键，例如 Alt+A、Ctrl+Alt+A 或 PrintScreen。"; return false; }
        if (_disposed) { error = "快捷键服务已停止。"; return false; }
        if ((modifiers & 8) != 0) { error = "Win 组合由 Windows 保留，请选择 Ctrl、Alt 或 Shift 组合。"; return false; }
        if (key == 0x7B) { error = "F12 由系统调试器保留，请换一个按键。"; return false; }
        return true;
    }

    private static string CanonicalCombination(uint modifiers, uint key)
    {
        var parts = new List<string>(5);
        if ((modifiers & 2) != 0) parts.Add("Ctrl");
        if ((modifiers & 1) != 0) parts.Add("Alt");
        if ((modifiers & 4) != 0) parts.Add("Shift");
        if ((modifiers & 8) != 0) parts.Add("Win");
        var parsed = KeyInterop.KeyFromVirtualKey((int)key);
        var name = parsed switch
        {
            Key.Return => "Enter", Key.Back => "Backspace", Key.Prior => "PageUp", Key.Next => "PageDown",
            Key.Snapshot => "PrintScreen", >= Key.D0 and <= Key.D9 => ((int)parsed - (int)Key.D0).ToString(),
            _ => parsed.ToString()
        };
        parts.Add(name);
        return string.Join('+', parts);
    }

    public static bool TryFromKey(Key key, ModifierKeys modifiers, out string combination)
    {
        combination = "";
        uint flags = ((modifiers & ModifierKeys.Control) != 0 ? 2u : 0) | ((modifiers & ModifierKeys.Alt) != 0 ? 1u : 0) | ((modifiers & ModifierKeys.Shift) != 0 ? 4u : 0) | ((modifiers & ModifierKeys.Windows) != 0 ? 8u : 0);
        if (ModifierCount(flags) > 2 || (flags & 8) != 0 || key == Key.F12) return false;
        var text = CanonicalCombination(flags, (uint)KeyInterop.VirtualKeyFromKey(key));
        return TryNormalize(text, out combination);
    }
    private static int ModifierCount(uint flags) => System.Numerics.BitOperations.PopCount(flags);

    private bool StartKeyboardHook(out string error)
    {
        error = "";
        if (_keyboardHook != 0) return true;
        using var ready = new ManualResetEventSlim();
        var nativeError = 0;
        _keyboardThread = new Thread(() =>
        {
            _keyboardThreadId = GetCurrentThreadId();
            foreach (var key in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C }) if (GetAsyncKeyState(key) < 0) _modifierKeys.Add((uint)key);
            _keyboardHook = SetWindowsHookEx(13, _keyboardCallback, GetModuleHandle(null), 0);
            nativeError = Marshal.GetLastWin32Error(); ready.Set();
            if (_keyboardHook != 0) System.Windows.Forms.Application.Run();
        }) { IsBackground = true, Name = "ScreenNote shortcut" };
        _keyboardThread.SetApartmentState(ApartmentState.STA); _keyboardThread.Start(); ready.Wait();
        if (_keyboardHook != 0) return true;
        error = $"优先截图未能启用（{nativeError}），原快捷键已保留。"; return false;
    }
    private static uint ModifierFlag(uint key) => key switch { 0x10 or 0xA0 or 0xA1 => 4, 0x11 or 0xA2 or 0xA3 => 2, 0x12 or 0xA4 or 0xA5 => 1, 0x5B or 0x5C => 8, _ => 0 };
    private nint KeyboardHook(int code, nint message, nint data)
    {
        if (code < 0 || _disposed) return CallNextHookEx(_keyboardHook, code, message, data);
        var key = (uint)Marshal.ReadInt32(data);
        var down = message.ToInt32() is 0x100 or 0x104;
        var up = message.ToInt32() is 0x101 or 0x105;
        if (ModifierFlag(key) != 0) { if (down) _modifierKeys.Add(key); else if (up) _modifierKeys.Remove(key); }
        if (_suppressedKey == key) { if (up) _suppressedKey = 0; return 1; }
        var binding = Volatile.Read(ref _priorityBinding);
        if (_recording && down && ModifierFlag(key) == 0)
        {
            uint flags = 0; foreach (var held in _modifierKeys) flags |= ModifierFlag(held);
            var modifiers = ((flags & 2) != 0 ? ModifierKeys.Control : ModifierKeys.None) | ((flags & 1) != 0 ? ModifierKeys.Alt : ModifierKeys.None) | ((flags & 4) != 0 ? ModifierKeys.Shift : ModifierKeys.None) | ((flags & 8) != 0 ? ModifierKeys.Windows : ModifierKeys.None);
            _suppressedKey = key;
            if (!_source.Dispatcher.HasShutdownStarted) _source.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed && _recording) Recorded?.Invoke(this, new RecordedShortcut(KeyInterop.KeyFromVirtualKey((int)key), modifiers)); }));
            return 1;
        }
        if (!_suspended && down && binding != 0 && key == (uint)binding)
        {
            uint modifiers = 0; foreach (var held in _modifierKeys) modifiers |= ModifierFlag(held);
            if (modifiers == (uint)(binding >> 32))
            {
                _suppressedKey = key;
                if (!_source.Dispatcher.HasShutdownStarted) _source.Dispatcher.BeginInvoke(new Action(() => { if (!_disposed && !_suspended) Pressed?.Invoke(this, EventArgs.Empty); }));
                return 1;
            }
        }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private static string RegistrationError(int nativeError) => nativeError == 1409
        ? "这个快捷键已被其他程序占用。管理员权限也不能抢占，请换一个组合或在占用软件中释放。"
        : $"系统未能注册这个快捷键（错误 {nativeError}），请换一个组合。";

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (!_suspended && message == 0x0312 && _activeId is int activeId && wParam.ToInt32() == activeId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _source.Dispatcher.VerifyAccess();
        _disposed = true;
        Volatile.Write(ref _priorityBinding, 0);
        if (_keyboardHook != 0) { UnhookWindowsHookEx(_keyboardHook); _keyboardHook = 0; }
        if (_keyboardThreadId != 0) PostThreadMessage(_keyboardThreadId, 0x12, 0, 0);
        _keyboardThread?.Join(1000);
        UnregisterHotKey(_handle, FirstId);
        UnregisterHotKey(_handle, SecondId);
        UnregisterHotKey(_handle, ProbeId);
        _activeId = null;
        CurrentCombination = null;
        if (!_source.IsDisposed) _source.RemoveHook(Hook);
    }
}
