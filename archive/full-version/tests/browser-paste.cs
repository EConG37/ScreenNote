using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ScreenNote;
using Condition = System.Windows.Automation.Condition;

// Only the transport result contract is needed when compiling the production
// clipboard/automation source independently of the legacy extension transport.
namespace ScreenNote { public record BridgeDeliveryResult(bool Success, string Message); }

internal static class BrowserPasteValidation
{
    private static readonly List<string> Checks = [];
    private static string _folder = "";
    private static BrowserPasteWindow? _browser;
    [STAThread]
    private static int Main(string[] args)
    {
        _folder = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--")) ?? ".build/native-paste");
        Directory.CreateDirectory(_folder);
        Trace.Listeners.Add(new TextWriterTraceListener(Path.Combine(_folder, "native-paste-trace.txt"))); Trace.AutoFlush = true;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var frame = new DispatcherFrame();
        var code = 1;
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(async () =>
        {
            try
            {
                if (args.Contains("--deepseek")) await VerifyDeepSeekFallback();
                else await VerifyNativeBrowser(args.Contains("--clipboard"));
                File.WriteAllText(Path.Combine(_folder, "report.json"), JsonSerializer.Serialize(new { status = "passed", checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("PASS: " + Checks.Count + " native browser paste checks."); code = 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(_folder, "failure.txt"), ex.ToString()); Console.Error.WriteLine(ex); }
            finally
            {
                // Close only the independently launched local test page.
                if (!args.Contains("--deepseek") && _browser != null) PostMessage(_browser.Handle, 0x10, 0, 0);
                frame.Continue = false;
            }
        }));
        Dispatcher.PushFrame(frame); app.Shutdown(); return code;
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Checks.Add(message); Console.WriteLine("PASS: " + message);
    }
    private static async Task Until(Func<bool> condition, string message, int milliseconds = 8000)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds) { if (condition()) return; await Task.Delay(120); }
        throw new TimeoutException(message);
    }
    private static byte[] Png(byte red, byte green, byte blue)
    {
        var pixels = Enumerable.Range(0, 64 * 48).SelectMany(_ => new byte[] { blue, green, red, 255 }).ToArray();
        var bitmap = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 64 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static async Task VerifyNativeBrowser(bool clipboardOnly)
    {
        var target = BrowserPasteTarget.For("deepseek");
        Check(BrowserPasteAutomation.MatchesAddress("chat.deepseek.com/", target), "Matches the exact DeepSeek host when the browser hides the scheme");
        Check(!BrowserPasteAutomation.MatchesAddress("https://chat.deepseek.com.attacker.test/", target), "Rejects a different host");
        Check(!BrowserPasteAutomation.MatchesAddress("http://chat.deepseek.com/", target), "Rejects an HTTP downgrade");
        Check(!BrowserPasteAutomation.MatchesAddress("https://other@chat.deepseek.com/", target), "Rejects credentials embedded in an address");
        Check(!BrowserPasteAutomation.MatchesAddress("https://www.doubao.com/legal/", BrowserPasteTarget.For("doubao")), "Rejects a non-chat path on the target site");
        var service = new BrowserPasteService();
        var sequence = BrowserPasteService.GetClipboardSequenceNumber();
        Check(!(await service.SendAsync([], target)).Success && sequence == BrowserPasteService.GetClipboardSequenceNumber(), "An empty request leaves the clipboard and browser alone");
        Check(!(await service.SendAsync(Enumerable.Repeat(Png(255, 0, 0), 11).ToArray(), target)).Success && sequence == BrowserPasteService.GetClipboardSequenceNumber(), "Oversized image batches are rejected before changing the clipboard");
        var invalid = false;
        try { await service.SendAsync([new byte[] { 1, 2, 3 }], target); } catch { invalid = true; }
        Check(invalid && sequence == BrowserPasteService.GetClipboardSequenceNumber(), "A corrupt image is rejected before changing the clipboard");
        using var server = new FixtureServer();
        target = new("本地粘贴验证", new Uri(server.Url));
        var debugListener = new TcpListener(IPAddress.Loopback, 0); debugListener.Start();
        var debugPort = ((IPEndPoint)debugListener.LocalEndpoint).Port; debugListener.Stop();
        var chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
        var start = new ProcessStartInfo(chrome) { UseShellExecute = false };
        foreach (var argument in new[] { "--user-data-dir=" + Path.Combine(_folder, "chrome-profile"), "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--disable-features=Translate", "--force-renderer-accessibility", "--remote-debugging-port=" + debugPort, "--new-window", "--window-size=1000,760", server.Url }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var deadline = Stopwatch.StartNew();
        while (_browser == null && deadline.ElapsedMilliseconds < 20000)
        { _browser = await Task.Run(() => BrowserPasteAutomation.FindBrowser(target)); await Task.Delay(200); }
        if (_browser == null)
        {
            await Task.Run(DumpWindows);
            throw new InvalidOperationException("Local Chromium address was not recognized; see uia.txt.");
        }
        BrowserPasteDestination destination = default;
        while (destination.Composer == null && deadline.ElapsedMilliseconds < 25000)
        { destination = await Task.Run(() => BrowserPasteAutomation.FindComposer(_browser, target)); await Task.Delay(200); }
        if (destination.Composer == null) { await Task.Run(DumpWindows); throw new InvalidOperationException("Native chat composer was not recognized."); }
        Check(destination.Composer != null && !destination.LoginRequired, "Finds the chat input through native Windows accessibility, without an extension");
        using var cdp = clipboardOnly ? await FixtureCdp.Connect(debugPort, server.Url) : null;
        // Match a real user clicking Send in the foreground desktop app. A
        // background command runner has no Windows foreground activation rights.
        var sender = new Window { Title = "ScreenNote native paste validation", Topmost = true, Left = 30, Top = 220, Width = 300, Height = 160, Content = new System.Windows.Controls.Button { Content = "Send synthetic test images" } };
        if (!clipboardOnly)
        {
            sender.Show(); await Task.Delay(200);
            SetCursorPos(150, 300); mouse_event(2, 0, 0, 0, 0); mouse_event(4, 0, 0, 0, 0);
            await Task.Delay(200); sender.Topmost = false;
        }
        var result = clipboardOnly ? new BridgeDeliveryResult(true, "Clipboard/browser compatibility test; foreground activation is tested separately.")
            : await service.SendAsync([Png(255, 0, 0), Png(0, 255, 0)], target, "Screenshot summary");
        sender.Close();
        if (cdp != null)
        {
            foreach (var png in new[] { Png(255, 0, 0), Png(0, 255, 0) })
            {
                using var imageStream = new MemoryStream(png);
                var bitmap = BitmapFrame.Create(imageStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad); bitmap.Freeze();
                BrowserPasteService.CopyImage(bitmap, png); await cdp.Paste(); await Task.Delay(300);
            }
            Clipboard.SetText("\n\nScreenshot summary"); await cdp.Paste();
        }
        File.WriteAllText(Path.Combine(_folder, "delivery.txt"), result.Message);
        if (!result.Success) { await Task.Run(DumpWindows); throw new InvalidOperationException(result.Message); }
        File.WriteAllText(Path.Combine(_folder, "before-wait.json"), JsonSerializer.Serialize(server.State));
        await Until(() => server.State?.Images.Length == 2 && server.State.Text.Contains("Screenshot summary"), "The browser did not receive the native image and text paste.");
        var state = server.State!;
        Check(state.Images.Length == 2, clipboardOnly ? "Real Chromium clipboard reads received two image files" : "Real Chromium paste events received two image files");
        Check(state.Images.All(x => x.Type == "image/png" && x.Signature == "137,80,78,71,13,10,26,10"), "Both native clipboard images reached the browser as valid PNG files");
        Check(state.Images[0].Color == "255,0,0,255" && state.Images[1].Color == "0,255,0,255", "Image order and decoded pixels are preserved");
        Check(state.Text == "Existing draft\n\nScreenshot summary", clipboardOnly ? "The browser can read clipboard text and append to the existing draft" : "Pasting the prompt preserves and appends to the existing draft");
        Check(state.Sent == 0 && state.Enter == 0, "The validation never presses Enter or clicks the chat Send button");
        if (cdp == null)
        {
            var textResult = await service.SendAsync([], target, "Follow-up");
            Check(textResult.Success, "The same flow accepts text-only OCR content");
        }
        else { Clipboard.SetText("\n\nFollow-up"); await cdp.Paste(); }
        await Until(() => server.State!.Text.EndsWith("\n\nFollow-up"), "Text-only paste did not reach the browser.");
        Check(server.State!.Text.StartsWith("Existing draft\n\nScreenshot summary"), clipboardOnly ? "The browser can read a second text clipboard without losing the existing draft" : "Text-only delivery also preserves the existing draft");
        var composer = (await Task.Run(() => BrowserPasteAutomation.FindComposer(_browser, target))).Composer!;
        if (clipboardOnly && GetForegroundWindow() != _browser.Handle)
            Check(!await Task.Run(() => BrowserPasteAutomation.Paste(composer, target, BrowserPasteService.GetClipboardSequenceNumber(), activate: false)), "Automatic paste stops when the target browser is not the foreground application");
        Check(!await Task.Run(() => BrowserPasteAutomation.Paste(composer, BrowserPasteTarget.For("chatgpt"), BrowserPasteService.GetClipboardSequenceNumber())), "A destination change cannot paste into an unrelated page");
        var stale = BrowserPasteService.GetClipboardSequenceNumber(); Clipboard.SetText("Clipboard replaced by user");
        Check(!await Task.Run(() => BrowserPasteAutomation.Paste(composer, target, stale)), "An external clipboard change stops automatic paste");
        Clipboard.SetText("Clipboard cancelled");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); var cancelled = false; sequence = BrowserPasteService.GetClipboardSequenceNumber();
            try { await service.SendAsync([Png(0, 0, 255)], target, cancellation: cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && sequence == BrowserPasteService.GetClipboardSequenceNumber(), "Cancellation before delivery keeps the clipboard unchanged");
        }
        if (cdp == null) await Task.Run(() => Navigate(_browser, server.Url + "login"));
        else await cdp.Call("Page.navigate", new { url = server.Url + "login" });
        await Until(() => server.LoginLoaded, "Login fixture did not load.");
        await Task.Delay(300);
        var login = await service.SendAsync([Png(0, 0, 255)], target);
        Check(!login.Success && login.Message.Contains("登录") && login.Message.Contains("Ctrl+V"), "A login form produces a clear manual paste message");
        Check(Clipboard.ContainsImage() && Clipboard.ContainsData("PNG"), "The login fallback retains image and PNG clipboard formats");
        Check(server.LoginPastes == 0, "The image is never pasted into the login phone field");
        File.WriteAllText(Path.Combine(_folder, "browser-state.json"), JsonSerializer.Serialize(server.State, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task VerifyDeepSeekFallback()
    {
        var result = await new BrowserPasteService().SendAsync([Png(75, 125, 238)], "deepseek");
        File.WriteAllText(Path.Combine(_folder, "delivery.txt"), result.Message);
        Check(!result.Success && result.Message.Contains("Ctrl+V"), "The actual DeepSeek page leaves a manual paste fallback when automatic delivery cannot proceed");
        Check(Clipboard.ContainsImage() && Clipboard.ContainsData("PNG"), "A synthetic test image remains available for manual paste");
    }
    private static void Navigate(BrowserPasteWindow browser, string url)
    {
        BrowserPasteAutomation.Activate(browser, new("test", new Uri(url)));
        foreach (AutomationElement edit in browser.Root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)))
        {
            if (!(edit.Current.Name.Contains("地址") || edit.Current.Name.Contains("Address") || edit.Current.Name.Contains("address"))) continue;
            if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) continue;
            edit.SetFocus(); ((ValuePattern)value).SetValue(url);
            keybd_event(13, 0, 0, 0); keybd_event(13, 0, 2, 0); return;
        }
        throw new InvalidOperationException("Fixture navigation address not found.");
    }
    private static void DumpWindows()
    {
        var text = new StringBuilder();
        foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
        {
            if (!window.Current.ClassName.StartsWith("Chrome_WidgetWin_")) continue;
            text.AppendLine(window.Current.Name + " " + window.Current.ControlType.ProgrammaticName);
            if (!window.Current.Name.Contains("ScreenNote paste fixture")) continue;
            foreach (AutomationElement item in window.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                text.AppendLine(item.Current.ControlType.ProgrammaticName + " | " + item.Current.Name + " | " + item.Current.BoundingRectangle + " | focus=" + item.Current.IsKeyboardFocusable);
        }
        File.WriteAllText(Path.Combine(_folder, "uia.txt"), text.ToString());
    }
    private sealed record ImageReport(string Type, string Signature, string Color);
    private sealed record PageReport(ImageReport[] Images, string Text, int Sent, int Enter);
    // A fallback for validating real OS clipboard PNG interoperability when a
    // separate foreground application prevents desktop activation. Only this
    // isolated test browser exposes a debugging port; the app never uses CDP.
    private sealed class FixtureCdp : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private int _id;
        internal static async Task<FixtureCdp> Connect(int port, string url)
        {
            using var http = new HttpClient();
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var pages = JsonDocument.Parse(await http.GetStringAsync("http://127.0.0.1:" + port + "/json/list"));
                    var page = pages.RootElement.EnumerateArray().First(x => x.GetProperty("url").GetString() == url);
                    var cdp = new FixtureCdp(); await cdp._socket.ConnectAsync(new Uri(page.GetProperty("webSocketDebuggerUrl").GetString()!), CancellationToken.None);
                    await cdp.Call("Emulation.setFocusEmulationEnabled", new { enabled = true });
                    await cdp.Call("Browser.grantPermissions", new { origin = new Uri(url).GetLeftPart(UriPartial.Authority), permissions = new[] { "clipboardReadWrite", "clipboardSanitizedWrite" } }); return cdp;
                }
                catch when (attempt < 25) { await Task.Delay(200); }
            }
        }
        internal async Task<JsonElement> Call(string method, object parameters)
        {
            var id = ++_id; var json = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
            await _socket.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
            while (true)
            {
                using var content = new MemoryStream(); var buffer = new byte[65536]; WebSocketReceiveResult message;
                do { message = await _socket.ReceiveAsync(buffer, CancellationToken.None); content.Write(buffer, 0, message.Count); } while (!message.EndOfMessage);
                using var reply = JsonDocument.Parse(content.ToArray());
                if (!reply.RootElement.TryGetProperty("id", out var replyId) || replyId.GetInt32() != id) continue;
                if (reply.RootElement.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString()); return reply.RootElement.Clone();
            }
        }
        internal async Task Paste()
        {
            var read = await Call("Runtime.evaluate", new { expression = "readClipboardForVerification()", awaitPromise = true, userGesture = true });
            if (read.GetProperty("result").TryGetProperty("exceptionDetails", out var error)) throw new InvalidOperationException(error.ToString());
        }
        public void Dispose() => _socket.Dispose();
    }
    private sealed class FixtureServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        internal string Url { get; }
        internal PageReport? State;
        internal bool LoginLoaded;
        internal int LoginPastes;
        internal FixtureServer()
        {
            _listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/";
            _ = Accept();
        }
        private async Task Accept()
        {
            try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _ = Handle(client); } }
            catch (OperationCanceledException) { }
        }
        private async Task Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream(); var header = new List<byte>(); var one = new byte[1];
                    while (header.Count < 16000 && await stream.ReadAsync(one, _stop.Token) == 1)
                    { header.Add(one[0]); if (header.Count >= 4 && header.TakeLast(4).SequenceEqual(new byte[] { 13, 10, 13, 10 })) break; }
                    var lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                    var route = lines[0].Split(' ')[1]; var lengthLine = lines.FirstOrDefault(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    var body = new byte[lengthLine == null ? 0 : int.Parse(lengthLine.Split(':')[1])];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var content = "ok";
                    if (route == "/report") State = JsonSerializer.Deserialize<PageReport>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    else if (route == "/login-paste") Interlocked.Increment(ref LoginPastes);
                    else if (route == "/login-ready") LoginLoaded = true;
                    else content = route == "/login" ? LoginHtml : Html;
                    var bytes = Encoding.UTF8.GetBytes(content);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n"), _stop.Token);
                    await stream.WriteAsync(bytes, _stop.Token);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
        }
        public void Dispose() { _stop.Cancel(); _listener.Stop(); _stop.Dispose(); }
        private const string Html = """
<!doctype html><html lang="en"><meta charset="utf-8"><title>ScreenNote paste fixture</title>
<style>body{font:18px Segoe UI;padding:40px}textarea{width:650px;height:160px;font:18px Segoe UI}button{padding:16px}</style>
<h1>Native clipboard test</h1><p>This isolated page receives only synthetic red and green test images.</p>
<textarea id="composer" aria-label="Message DeepSeek">Existing draft</textarea><p><button id="send">Send</button></p><pre id="output"></pre>
<script>
const input=document.querySelector('#composer'),state={images:[],text:input.value,sent:0,enter:0};
const report=()=>{state.text=input.value;document.querySelector('#output').textContent=JSON.stringify(state,null,2);return fetch('/report',{method:'POST',body:JSON.stringify(state)})};
async function inspectFiles(files){for(const file of files){const signature=[...new Uint8Array(await file.slice(0,8).arrayBuffer())].join(',');const bitmap=await createImageBitmap(file);const canvas=document.createElement('canvas');canvas.width=bitmap.width;canvas.height=bitmap.height;const ctx=canvas.getContext('2d');ctx.drawImage(bitmap,0,0);state.images.push({type:file.type,signature,color:[...ctx.getImageData(0,0,1,1).data].join(',')});}await report()}
input.addEventListener('paste',async e=>{const files=[...e.clipboardData.files];if(!files.length)return;e.preventDefault();await inspectFiles(files)});
async function readClipboardForVerification(){const items=await navigator.clipboard.read();for(const item of items){if(item.types.includes('image/png'))await inspectFiles([await item.getType('image/png')]);else if(item.types.includes('text/plain')){input.value+=await (await item.getType('text/plain')).text();await report()}}return true}
input.addEventListener('input',report);input.addEventListener('keydown',e=>{if(e.key==='Enter'){state.enter++;report()}});document.querySelector('#send').onclick=()=>{state.sent++;report()};report();
</script></html>
""";
        private const string LoginHtml = """
<!doctype html><html lang="en"><meta charset="utf-8"><title>ScreenNote paste fixture - Login</title>
<style>body{font:18px Segoe UI;padding:50px}input{width:400px;height:60px}button{padding:20px}</style><h1>Log in</h1>
<input type="tel"><button>Log in</button><script>document.querySelector('input').onpaste=()=>fetch('/login-paste',{method:'POST'});fetch('/login-ready',{method:'POST'});</script></html>
""";
    }
    [DllImport("user32.dll")] private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern void mouse_event(uint flags, uint x, uint y, uint data, nuint extra);
}
