using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;

internal static class OcrComparison
{
    [STAThread]
    private static int Main()
    {
        var directory = Path.GetFullPath(".build/ocr-comparison-070"); Directory.CreateDirectory(directory);
        var scenarios = new[]
        {
            ("mixed", 940, 420, 19d, false, new[] { "截图之后，直接修改识别文字。", "快捷键支持按键录入，保存后生效。", "订单编号 SN-20261003-042，数量 18。", "ScreenNote: Capture. Annotate. Edit. Ask." }),
            ("small", 780, 260, 12d, false, new[] { "中文识别与英文数字混排测试", "原图保留，可撤销、可编辑、可导出。", "毛利率 18.6%，金额 2,560.00 元。", "Ctrl+Alt+A / PrintScreen / OCR 2026" }),
            ("dark", 940, 380, 16d, true, new[] { "深色界面的文字识别", "截图与标注分别保存，重要资料请备份。", "发送给 DeepSeek、豆包或者 ChatGPT。", "The total is 1,234.56 USD, including tax." }),
            ("long", 940, 6000, 16d, false, Enumerable.Range(1, 64).Select(i => $"第 {i:00} 条工作记录：截图保留原图，文字可以继续编辑。金额 {i * 17}.50 元。").ToArray())
        };
        var results = new List<object>(); var totalRapid = 0; var totalWindows = 0; var totalChars = 0;
        var service = new OcrService();
        foreach (var (name, width, height, size, dark, lines) in scenarios)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(dark ? new SolidColorBrush(Color.FromRgb(32, 35, 42)) : Brushes.White, null, new Rect(0, 0, width, height));
                for (var i = 0; i < lines.Length; i++) dc.DrawText(new FormattedText(lines[i], CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(62, 66, 72)), 1), new Point(30, 30 + i * (name == "long" ? 88 : size * 2.7)));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(Path.Combine(directory, name + ".png"))) encoder.Save(file);
            var expected = Normalize(string.Join('\n', lines)); totalChars += expected.Length;
            var times = Stopwatch.StartNew(); var enhanced = service.RecognizeAsync(bitmap, engine: "rapid").GetAwaiter().GetResult(); var rapidMilliseconds = times.ElapsedMilliseconds;
            if (enhanced.Error != null || !enhanced.Language.Contains("中文增强")) throw new Exception("Bundled OCR did not run: " + enhanced.Error + " " + enhanced.Language);
            times.Restart(); var system = service.RecognizeAsync(bitmap, engine: "windows").GetAwaiter().GetResult(); var windowsMilliseconds = times.ElapsedMilliseconds;
            if (system.Error != null) throw new Exception(system.Error);
            var rapidErrors = Distance(expected, Normalize(enhanced.Text)); var windowsErrors = Distance(expected, Normalize(system.Text)); totalRapid += rapidErrors; totalWindows += windowsErrors;
            results.Add(new { name, expectedChars = expected.Length, rapidErrors, windowsErrors, rapidMilliseconds, windowsMilliseconds, enhancedText = enhanced.Text, windowsText = system.Text });
            Console.WriteLine($"{name}: enhanced {rapidErrors}/{expected.Length} errors ({rapidMilliseconds}ms), Windows {windowsErrors}/{expected.Length} ({windowsMilliseconds}ms)");
            if (name == "long" && !enhanced.Text.Contains("64")) throw new Exception("The enhanced engine lost the last long-image segment.");
        }
        var report = new { syntheticSamples = true, totalChars, rapidErrors = totalRapid, windowsErrors = totalWindows, scenarios = results };
        File.WriteAllText(Path.Combine(directory, "comparison.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Total: enhanced {totalRapid}/{totalChars}, Windows {totalWindows}/{totalChars}.");
        return 0;
    }
    private static string Normalize(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
    private static int Distance(string first, string second)
    {
        var previous = Enumerable.Range(0, second.Length + 1).ToArray(); var current = new int[previous.Length];
        for (var i = 1; i <= first.Length; i++) { current[0] = i; for (var j = 1; j <= second.Length; j++) current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (first[i - 1] == second[j - 1] ? 0 : 1)); (previous, current) = (current, previous); }
        return previous[second.Length];
    }
}
