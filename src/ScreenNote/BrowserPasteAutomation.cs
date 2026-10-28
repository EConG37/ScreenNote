using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ScreenNote;

internal sealed record BrowserPasteWindow(AutomationElement Root, nint Handle);
internal sealed record BrowserPasteComposer(BrowserPasteWindow Browser, AutomationElement Input);
internal readonly record struct BrowserPasteDestination(BrowserPasteComposer? Composer, bool LoginRequired);

internal static class BrowserPasteAutomation
{
    internal static BrowserPasteWindow? FindBrowser(BrowserPasteTarget target)
    {
        var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition);
        foreach (var window in windows.Cast<AutomationElement>().OrderByDescending(window => window.Current.NativeWindowHandle == GetForegroundWindow()))
        {
            try
            {
                var className = window.Current.ClassName;
                if (!className.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal) && className != "MozillaWindowClass") continue;
                if (window.Current.IsOffscreen || !HasTargetAddress(window, target)) continue;
                return new(window, window.Current.NativeWindowHandle);
            }
            catch (Exception ex) when (IsAutomationError(ex)) { }
        }
        return null;
    }
    internal static bool HasTargetAddress(AutomationElement root, BrowserPasteTarget target)
    {
        var top = root.Current.BoundingRectangle.Top;
        foreach (AutomationElement edit in root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            var info = edit.Current;
            if (info.IsOffscreen || info.BoundingRectangle.Top > top + 190 || info.BoundingRectangle.Width < 150) continue;
            var name = info.Name.ToLowerInvariant();
            if (!(name.Contains("地址") || name.Contains("address") || name.Contains("搜索或输入") || name.Contains("search or enter"))) continue;
            if (edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) && MatchesAddress(((ValuePattern)pattern).Current.Value, target)) return true;
        }
        return false;
    }
    internal static bool MatchesAddress(string value, BrowserPasteTarget target)
    {
        value = value.Trim();
        if (!value.Contains("://", StringComparison.Ordinal)) value = target.Url.Scheme + "://" + value;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == target.Url.Scheme
            && uri.Authority.Equals(target.Url.Authority, StringComparison.OrdinalIgnoreCase)
            && uri.UserInfo.Length == 0
            && (target.Url.AbsolutePath == "/" || uri.AbsolutePath.StartsWith(target.Url.AbsolutePath, StringComparison.Ordinal));
    }
    internal static BrowserPasteDestination FindComposer(BrowserPasteWindow browser, BrowserPasteTarget target)
    {
        try
        {
            if (!HasTargetAddress(browser.Root, target)) return default;
            var documents = browser.Root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            var page = documents.Cast<AutomationElement>().Where(x => !x.Current.IsOffscreen && x.Current.BoundingRectangle.Width > 250 && x.Current.BoundingRectangle.Height > 120)
                .OrderByDescending(x => x.Current.BoundingRectangle.Width * x.Current.BoundingRectangle.Height).FirstOrDefault();
            if (page == null) return default;
            var login = page.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>()
                .Any(x => !x.Current.IsOffscreen && new[] { "登录", "登录/注册", "登录 / 注册", "注册/登录", "Log in", "Sign in", "发送验证码", "重新登录" }.Contains(x.Current.Name.Trim(), StringComparer.OrdinalIgnoreCase));
            if (login) return new(null, true);
            var inputs = page.FindAll(TreeScope.Descendants, new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit), new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document)));
            var candidates = inputs.Cast<AutomationElement>().Where(IsChatInput).OrderByDescending(x => Score(x)).ToArray();
            if (candidates.Length > 0) return new(new(browser, candidates[0]), false);
            return default;
        }
        catch (Exception ex) when (IsAutomationError(ex)) { return default; }
    }
    private static bool IsChatInput(AutomationElement element)
    {
        var info = element.Current; var rect = info.BoundingRectangle;
        if (info.IsOffscreen || !info.IsEnabled || !info.IsKeyboardFocusable || info.IsPassword || rect.Width < 200 || rect.Height < 35) return false;
        var name = info.Name.ToLowerInvariant();
        if (new[] { "搜索", "search", "手机号", "验证码", "密码", "password", "email", "邮箱", "phone", "验证", "地址", "address" }.Any(name.Contains)) return false;
        var hint = new[] { "发送", "消息", "问", "message", "ask", "chat", "输入", "deepseek", "豆包" }.Any(name.Contains);
        if (info.ControlType == ControlType.Document && !hint) return false;
        if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value) && ((ValuePattern)value).Current.IsReadOnly) return false;
        return hint;
    }
    private static double Score(AutomationElement input)
    {
        var name = input.Current.Name.ToLowerInvariant();
        return input.Current.BoundingRectangle.Bottom + (new[] { "消息", "message", "ask", "deepseek", "豆包" }.Any(name.Contains) ? 10000 : 0);
    }
    internal static bool HasText(AutomationElement input)
    {
        try
        {
            if (input.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.Value.Length > 0;
            return input.TryGetCurrentPattern(TextPattern.Pattern, out var text) && ((TextPattern)text).DocumentRange.GetText(1).Length > 0;
        }
        catch (Exception ex) when (IsAutomationError(ex)) { return false; }
    }
    internal static bool CanContinue(BrowserPasteComposer composer, BrowserPasteTarget target)
    {
        try { return GetForegroundWindow() == composer.Browser.Handle && HasTargetAddress(composer.Browser.Root, target)
            && IsChatInput(composer.Input) && Automation.Compare(AutomationElement.FocusedElement, composer.Input); }
        catch (Exception ex) when (IsAutomationError(ex)) { return false; }
    }
    internal static bool Paste(BrowserPasteComposer composer, BrowserPasteTarget target, uint sequence, bool activate = true)
    {
        try
        {
            if (BrowserPasteService.GetClipboardSequenceNumber() != sequence) return Stop("Clipboard changed");
            if (!HasTargetAddress(composer.Browser.Root, target) || !IsChatInput(composer.Input)) return Stop("Destination changed");
            if (activate) Activate(composer.Browser, target);
            if (GetForegroundWindow() != composer.Browser.Handle) return Stop("Browser is not foreground (target=" + composer.Browser.Handle + ", actual=" + GetForegroundWindow() + ")");
            composer.Input.SetFocus();
            // Chromium applies accessibility focus asynchronously.
            var focusWait = Stopwatch.StartNew();
            while (!Automation.Compare(AutomationElement.FocusedElement, composer.Input) && focusWait.ElapsedMilliseconds < 500)
            {
                if (GetForegroundWindow() != composer.Browser.Handle) return Stop("Focus moved to another application");
                System.Threading.Thread.Sleep(25);
            }
            if (!Automation.Compare(AutomationElement.FocusedElement, composer.Input) || GetForegroundWindow() != composer.Browser.Handle || ModifiersPressed) return Stop("Composer focus not ready or modifiers held");
            if (BrowserPasteService.GetClipboardSequenceNumber() != sequence) return Stop("Clipboard changed before paste");
            // Move to the end without selecting/replacing an existing text draft.
            var inputs = Chord(0x23).Concat(Chord(0x56)).ToArray();
            return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) == inputs.Length || Stop("Windows rejected input");
        }
        catch (Exception ex) when (IsAutomationError(ex)) { return Stop(ex.GetType().Name); }
    }
    private static bool Stop(string reason) { Trace.WriteLine("Browser paste: " + reason); return false; }
    internal static bool Activate(BrowserPasteWindow browser, BrowserPasteTarget target)
    {
        try
        {
            if (!HasTargetAddress(browser.Root, target)) return false;
            if (IsIconic(browser.Handle)) ShowWindow(browser.Handle, 9);
            SetForegroundWindow(browser.Handle);
            if (GetForegroundWindow() != browser.Handle) browser.Root.SetFocus();
            var activationWait = Stopwatch.StartNew();
            while (GetForegroundWindow() != browser.Handle && activationWait.ElapsedMilliseconds < 300) System.Threading.Thread.Sleep(25);
            return GetForegroundWindow() == browser.Handle;
        }
        catch (Exception ex) when (IsAutomationError(ex)) { return Stop("Activation: " + ex.GetType().Name); }
    }
    internal static bool ModifiersPressed => new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C }.Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);
    private static NativeInput[] Chord(ushort key) => [Key(0x11), Key(key), Key(key, 2), Key(0x11, 2)];
    private static NativeInput Key(ushort key, uint flags = 0) => new() { Type = 1, Data = new() { Keyboard = new() { VirtualKey = key, Flags = flags | (key == 0x23 ? 1u : 0u) } } };
    private static bool IsAutomationError(Exception ex) => ex is ElementNotAvailableException or InvalidOperationException or COMException or UnauthorizedAccessException;
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort VirtualKey, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, NativeInput[] inputs, int size);
}
