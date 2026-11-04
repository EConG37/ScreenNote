using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Threading;
using ScreenNote;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        foreach (var (source, expected) in new[]
        {
            ("alt+control+a", "Ctrl+Alt+A"), (" Shift + Alt + Ctl + F2 ", "Ctrl+Alt+Shift+F2"),
            ("Ctrl+Alt+1", "Ctrl+Alt+1"), ("Ctrl+Esc", "Ctrl+Escape"),
            ("Ctrl+Return", "Ctrl+Enter"), ("Ctrl+PgUp", "Ctrl+PageUp"), ("Ctrl+Backspace", "Ctrl+Backspace")
        })
        {
            Require(HotkeyService.TryNormalize(source, out var normalized) && normalized == expected, "Normalizes modifier order and aliases: " + source);
        }
        foreach (var invalid in new[] { "", "A", "Ctrl++A", "Ctrl+Control+A", "Ctrl+Alt+99", "Ctrl+Alt+LeftCtrl", "Foo+A" })
        {
            Require(!HotkeyService.TryParse(invalid, out _, out _), "Rejects malformed hotkey: " + invalid);
        }

        var owner = new Window { ShowInTaskbar = false, Width = 1, Height = 1 };
        var blockerWindow = new Window { ShowInTaskbar = false, Width = 1, Height = 1 };
        using var service = new HotkeyService(owner);
        using var blocker = new NativeOwner(blockerWindow);
        try
        {
            Require(!service.Probe("Win+A", out var reservedWin) && reservedWin.Contains("Windows"), "Windows-reserved combinations are excluded without native registration.");
            Require(!service.Probe("Ctrl+Alt+Shift+F12", out var reservedF12) && reservedF12.Contains("保留"), "Debugger-reserved F12 is excluded.");

            var original = FindFree(service, "Ctrl+Alt+Shift+F11", "Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F9", "Ctrl+Alt+Shift+F8");
            Require(service.Register(original, out var error), "Initial native hotkey registration succeeds: " + error);
            Require(service.CurrentCombination == original, "Current combination reflects the successful registration.");
            Require(service.Probe(original.ToLowerInvariant(), out _), "Own currently registered combination remains available to this service.");
            Require(service.Register(Reorder(original), out _), "Equivalent alias/order does not attempt a conflicting second registration.");
            Require(service.CurrentCombination == original, "Equivalent hotkey stays canonical.");
            Require(!blocker.TryOwn(original) && blocker.LastError == 1409, "Native RegisterHotKey confirms the old combination is owned by the service.");

            var conflicted = FindFree(service, "Ctrl+Alt+Shift+F1", "Ctrl+Alt+Shift+F5", "Ctrl+Alt+Shift+F6", "Ctrl+Alt+Shift+F7");
            Require(blocker.TryOwn(conflicted), "A separate native window reserves a real conflicting hotkey.");
            Require(!service.Probe(conflicted, out error) && error.Contains("占用") && error.Contains("管理员"), "Probe reports a real conflict and explains that administrator rights do not take ownership.");
            Require(service.CurrentCombination == original, "Conflict probe does not change the old hotkey.");
            for (var attempt = 0; attempt < 3; attempt++)
            {
                Require(!service.Register(conflicted, out error), "Changing to a conflicting combination fails safely.");
                Require(service.CurrentCombination == original && !blocker.TryOwn(original) && blocker.LastError == 1409,
                    "Failed replacement leaves the original native registration owned throughout subsequent checks.");
            }

            var probeOnly = FindFree(service, "Ctrl+Alt+Shift+Z", "Ctrl+Alt+Shift+R", "Ctrl+Alt+Shift+T", "Ctrl+Alt+Shift+V");
            Require(service.Probe(probeOnly, out _), "A new free combination is available.");
            Require(blocker.TryOwn(probeOnly), "Successful probe unregisters its temporary reservation.");
            Require(service.CurrentCombination == original, "Successful probe preserves the current registration.");
            blocker.Release(probeOnly);

            var preferred = new[] { "Ctrl+Alt+Shift+A", "Ctrl+Alt+Shift+F2", "Ctrl+Alt+Shift+S" };
            foreach (var combination in preferred)
            {
                // Another program may already own a preference; never release someone else's hotkey.
                blocker.TryOwn(combination);
                Require(!service.Probe(combination, out _), "Occupied suggestion preference is unavailable: " + combination);
            }
            var suggestion = service.FindAvailableSuggestion();
            Require(suggestion is not null && !preferred.Contains(suggestion) && !suggestion.Contains("Win") && !suggestion.Contains("F12"), "Suggestion skips occupied preferences and Windows/debugger combinations.");
            Require(suggestion is not null && blocker.TryOwn(suggestion), "Suggestion only probes availability and does not retain ownership.");
            blocker.Release(suggestion!);

            Require(service.Register(probeOnly, out _), "A free candidate safely replaces the current hotkey.");
            Require(service.CurrentCombination == probeOnly && !blocker.TryOwn(probeOnly) && blocker.LastError == 1409, "New combination is natively registered.");
            Require(blocker.TryOwn(original), "Old combination is released after successful replacement.");
            blocker.Release(original);
            Require(!service.Register("Ctrl+Alt+Shift+F12", out _) && service.CurrentCombination == probeOnly, "Reserved-key replacement also preserves the current key.");

            service.Dispose();
            Require(service.CurrentCombination is null && blocker.TryOwn(probeOnly), "Disposal releases the native registration.");
            service.Dispose();
            Require(!service.Probe("Ctrl+Alt+Shift+U", out _) && !service.Register("Ctrl+Alt+Shift+U", out _), "Stopped service cannot create a new native registration.");
            VerifyPriority(owner, blocker);
            Console.WriteLine($"PASS: {_checks} hotkey checks, using real RegisterHotKey conflicts, safe replacement, temporary probing and suggestion exclusions.");
        }
        finally { owner.Close(); blockerWindow.Close(); }
    }

    private static string FindFree(HotkeyService service, params string[] combinations) => combinations.FirstOrDefault(combination => service.Probe(combination, out _))
        ?? throw new InvalidOperationException("Every test candidate is already occupied; free a test combination and rerun.");

    private static string Reorder(string canonical)
    {
        var parts = canonical.Split('+');
        return string.Join('+', parts.Take(parts.Length - 1).Reverse().Select(part => part == "Ctrl" ? "Control" : part)) + "+" + parts[^1];
    }
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    private static void DrainInput()
    {
        var frame = new DispatcherFrame(); var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; }; timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static void Press(string combination)
    {
        HotkeyService.TryParse(combination, out var modifiers, out var key);
        var held = new List<byte>(); if ((modifiers & 2) != 0) held.Add(0xA2); if ((modifiers & 1) != 0) held.Add(0xA4); if ((modifiers & 4) != 0) held.Add(0xA0);
        try { foreach (var modifier in held) keybd_event(modifier, 0, 0, 0); keybd_event((byte)key, 0, 0, 0); keybd_event((byte)key, 0, 2, 0); }
        finally { foreach (var modifier in held.AsEnumerable().Reverse()) keybd_event(modifier, 0, 2, 0); }
        DrainInput();
    }
    private static void VerifyPriority(Window owner, NativeOwner blocker)
    {
        using var priority = new HotkeyService(owner);
        Require(HotkeyService.TryNormalize("PrintScreen", out var single) && single == "PrintScreen", "A single PrintScreen key is supported.");
        Require(HotkeyService.TryFromKey(Key.A, ModifierKeys.Alt, out var simple) && simple == "Alt+A", "Recorder normalizes real Alt+A input.");
        Require(!HotkeyService.TryFromKey(Key.A, ModifierKeys.Alt | ModifierKeys.Control | ModifierKeys.Shift, out _), "Recorder prevents four-key chords.");
        var combination = FindFree(priority, "Ctrl+F9", "Alt+F9", "Ctrl+F8");
        Require(blocker.TryOwn(combination), "Another real window owns the simple test shortcut.");
        var previous = blocker.Received; Press(combination);
        Require(blocker.Received == previous + 1, "Baseline injected input reaches the competing native hotkey owner.");
        Require(priority.Register(combination, out var error, true), "Priority mode accepts an occupied simple chord: " + error);
        var pressed = 0; priority.Pressed += (_, _) => pressed++;
        previous = blocker.Received; Press(combination);
        Require(pressed == 1 && blocker.Received == previous, "Priority hook triggers ScreenNote once and suppresses the competing RegisterHotKey action.");
        priority.Suspended = true;
        RecordedShortcut? recorded = null; priority.Recorded += (_, value) => recorded = value;
        Require(priority.BeginRecording(out _), "Physical shortcut recording starts while capture is suspended.");
        Press(combination);
        Require(recorded != null && HotkeyService.TryFromKey(recorded.Key, recorded.Modifiers, out var captured) && captured == combination && pressed == 1 && blocker.Received == previous, "Recorder captures the occupied chord without triggering either screenshot program.");
        priority.EndRecording(); priority.Suspended = false;
        Require(!priority.Register("Ctrl+Alt+Shift+A", out _, true) && priority.CurrentCombination == combination, "Priority mode never expands to a four-key combination.");
        priority.Dispose(); Press(combination);
        Require(blocker.Received == previous + 1 && pressed == 1, "Disposal releases priority interception and restores the competing shortcut.");
        blocker.Release(combination);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _checks++;
        Console.WriteLine("PASS: " + message);
    }

    private sealed class NativeOwner : IDisposable
    {
        private readonly nint _handle;
        private readonly Dictionary<string, int> _owned = new();
        private int _nextId = 0x7100;
        public int LastError { get; private set; }
        private readonly HwndSource _source;
        public int Received { get; private set; }
        public NativeOwner(Window window) { _handle = new WindowInteropHelper(window).EnsureHandle(); _source = HwndSource.FromHwnd(_handle)!; _source.AddHook(Hook); }
        private nint Hook(nint hwnd, int message, nint w, nint l, ref bool handled) { if (message == 0x312 && _owned.Values.Contains(w.ToInt32())) { Received++; handled = true; } return 0; }
        public bool TryOwn(string combination)
        {
            if (_owned.ContainsKey(combination)) { LastError = 0; return true; }
            if (!HotkeyService.TryParse(combination, out var modifiers, out var key)) throw new ArgumentException("Invalid test combination.");
            var id = _nextId++;
            if (!RegisterHotKey(_handle, id, modifiers | 0x4000, key)) { LastError = Marshal.GetLastWin32Error(); return false; }
            _owned.Add(combination, id);
            LastError = 0;
            return true;
        }
        public void Release(string combination)
        {
            if (_owned.Remove(combination, out var id)) UnregisterHotKey(_handle, id);
        }
        public void Dispose()
        {
            foreach (var id in _owned.Values) UnregisterHotKey(_handle, id);
            _owned.Clear();
            _source.RemoveHook(Hook);
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
    }
}
