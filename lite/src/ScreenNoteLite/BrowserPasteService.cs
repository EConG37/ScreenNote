using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenNote;

internal sealed record BrowserPasteTarget(string Name, Uri Url)
{
    internal static BrowserPasteTarget For(string id) => id switch
    {
        "deepseek" => new("DeepSeek", new Uri("https://chat.deepseek.com/")),
        "chatgpt" => new("ChatGPT", new Uri("https://chatgpt.com/")),
        "doubao" => new("豆包", new Uri("https://www.doubao.com/chat/")),
        _ => throw new ArgumentException("不支持这个 AI 网页。", nameof(id))
    };
}

// Uses the same Windows image clipboard and Ctrl+V as a manual paste. No browser
// extension, page script, debugging port, login credential or model API is needed.
internal sealed class BrowserPasteService
{
    internal Task<BridgeDeliveryResult> SendAsync(IReadOnlyList<byte[]> images, string target, string text = "", CancellationToken cancellation = default)
        => SendAsync(images, BrowserPasteTarget.For(target), text, cancellation);

    internal async Task<BridgeDeliveryResult> SendAsync(IReadOnlyList<byte[]> images, BrowserPasteTarget target, string text = "", CancellationToken cancellation = default)
    {
        if (images.Count == 0 && string.IsNullOrWhiteSpace(text)) return new(false, "没有可以复制的内容。");
        if (images.Count > 10) return new(false, "一次最多粘贴 10 张图片。");
        // Decode before switching applications. Invalid images never replace the
        // user's clipboard or open an unrelated browser window.
        var bitmaps = new List<BitmapSource>();
        foreach (var png in images)
        {
            using var stream = new MemoryStream(png, writable: false);
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            bitmap.Freeze(); bitmaps.Add(bitmap);
        }
        var sequence = await CopyAsync(() => { if (bitmaps.Count > 0) CopyImage(bitmaps[0], images[0]); else Clipboard.SetText(text); }, cancellation);
        var browser = await Task.Run(() => BrowserPasteAutomation.FindBrowser(target), cancellation);
        if (browser == null)
        {
            try { using var process = Process.Start(new ProcessStartInfo(target.Url.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { return new(false, "内容已复制，打开 " + target.Name + " 未完成：" + ex.Message + "。可手动打开网页并按 Ctrl+V。"); }
        }
        var deadline = Stopwatch.StartNew();
        BrowserPasteComposer? composer = null;
        while (deadline.Elapsed < TimeSpan.FromSeconds(9))
        {
            cancellation.ThrowIfCancellationRequested();
            browser ??= await Task.Run(() => BrowserPasteAutomation.FindBrowser(target), cancellation);
            if (browser != null)
            {
                var destination = await Task.Run(() => BrowserPasteAutomation.FindComposer(browser, target), cancellation);
                composer = destination.Composer;
                if (composer != null) break;
                if (destination.LoginRequired)
                {
                    await Task.Run(() => BrowserPasteAutomation.Activate(browser, target), cancellation);
                    return new(false, $"内容已复制。请先登录 {target.Name}，点击聊天输入框后按 Ctrl+V 粘贴；不需要安装扩展。");
                }
            }
            await Task.Delay(250, cancellation);
        }
        if (composer == null)
        {
            if (browser != null) await Task.Run(() => BrowserPasteAutomation.Activate(browser, target), cancellation);
            return new(false, $"内容已复制并打开 {target.Name}。请点击聊天输入框后按 Ctrl+V 粘贴；不需要安装扩展。");
        }
        var pasted = 0;
        for (var index = 0; index < bitmaps.Count; index++)
        {
            if (index > 0)
            {
                if (GetClipboardSequenceNumber() != sequence || !await Task.Run(() => BrowserPasteAutomation.CanContinue(composer, target), cancellation)) return Incomplete(target, pasted, bitmaps.Count);
                sequence = await CopyAsync(() => CopyImage(bitmaps[index], images[index]), cancellation);
            }
            if (!await PasteAsync(composer, target, sequence, cancellation, activate: index == 0)) return Incomplete(target, pasted, bitmaps.Count);
            pasted++;
            // Let the browser consume the clipboard before preparing the next image.
            await Task.Delay(850, cancellation);
        }
        if (!string.IsNullOrWhiteSpace(text))
        {
            if (GetClipboardSequenceNumber() != sequence || (bitmaps.Count > 0 && !await Task.Run(() => BrowserPasteAutomation.CanContinue(composer, target), cancellation)))
                return new(false, "已停止后续粘贴，避免覆盖新的剪贴板内容或打断其他操作。请检查网页已有附件和草稿。");
            var append = await Task.Run(() => BrowserPasteAutomation.HasText(composer.Input), cancellation);
            if (bitmaps.Count > 0 || append) sequence = await CopyAsync(() => Clipboard.SetText((append ? "\n\n" : "") + text), cancellation);
            if (!await PasteAsync(composer, target, sequence, cancellation, activate: bitmaps.Count == 0)) return new(false, "内容已复制，文字未继续粘贴。请检查网页草稿，再按 Ctrl+V 补充。");
        }
        return new(true, bitmaps.Count > 0
            ? $"已向 {target.Name} 执行 {pasted} 张图片的粘贴{(text.Length > 0 ? "及提示词粘贴" : "")}。请检查网页附件后自行发送。"
            : $"已向 {target.Name} 粘贴文字，请检查草稿后自行发送。");
    }
    private static BridgeDeliveryResult Incomplete(BrowserPasteTarget target, int pasted, int total)
        => new(false, pasted == 0 ? $"图片已复制。请点击 {target.Name} 聊天输入框后按 Ctrl+V 粘贴。" : $"已执行 {pasted}/{total} 张图片的粘贴，页面焦点发生变化，后续已停止。请检查已有附件，再从历史中补充剩余图片。");
    private static async Task<uint> CopyAsync(Action write, CancellationToken cancellation)
    {
        for (var retry = 0; ; retry++)
        {
            cancellation.ThrowIfCancellationRequested();
            try { write(); return GetClipboardSequenceNumber(); }
            catch (COMException) when (retry < 4) { await Task.Delay(100, cancellation); }
        }
    }
    internal static void CopyImage(BitmapSource image, byte[] png)
    {
        var data = new DataObject();
        data.SetData(DataFormats.Bitmap, image);
        data.SetData("PNG", new MemoryStream(png, writable: false));
        Clipboard.SetDataObject(data, copy: true);
    }
    private static async Task<bool> PasteAsync(BrowserPasteComposer composer, BrowserPasteTarget target, uint sequence, CancellationToken cancellation, bool activate)
    {
        for (var retry = 0; retry < 15 && BrowserPasteAutomation.ModifiersPressed; retry++) await Task.Delay(80, cancellation);
        cancellation.ThrowIfCancellationRequested();
        if (GetClipboardSequenceNumber() != sequence || BrowserPasteAutomation.ModifiersPressed) return false;
        return await Task.Run(() => BrowserPasteAutomation.Paste(composer, target, sequence, activate), cancellation);
    }
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
}
