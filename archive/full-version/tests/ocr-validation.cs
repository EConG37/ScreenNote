using System.IO;
using System.Windows.Media.Imaging;
using ScreenNote;

internal static class OcrValidation
{
    [STAThread]
    private static int Main(string[] args)
    {
        var fixtures = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(Environment.CurrentDirectory, ".build", "ocr-fixtures"));
        Check(TextCleanup.NormalizeLineEndings("a\r\nb\rc\n"), "a\nb\nc\n", "line ending normalization");
        Check(TextCleanup.MergeLines("截图之后\n直接修改文字\n\nSecond\nparagraph."), "截图之后直接修改文字\n\nSecond paragraph.", "mixed language paragraphs");
        Check(TextCleanup.MergeLines("- first\n- second\n    code();\n```cs\nvar x = 1;\nvar y = 2;\n```"), "- first\n- second\n    code();\n```cs\nvar x = 1;\nvar y = 2;\n```", "list, indentation, and fenced code preservation");
        Check(TextCleanup.MergeLines("var first = 1;\nvar second = 2;\n{\nRun();\n}"), "var first = 1;\nvar second = 2;\n{\nRun();\n}", "code without OCR indentation");
        Check(TextCleanup.RemoveEmptyLines("a\r\n \r\nb\n\nc"), "a\nb\nc", "empty line removal");
        Check(TextCleanup.CleanSpaces("    code   here\n中 文  ， 文 字\nEnglish   words\t stay"), "    code here\n中文，文字\nEnglish words\t stay", "Chinese spacing, Latin spacing, and indentation");

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(Path.Combine(fixtures, "chinese.png"));
        bitmap.EndInit();
        bitmap.Freeze();
        var service = new OcrService();
        var result = service.RecognizeAsync(bitmap).GetAwaiter().GetResult();
        if (result.Error is not null || !result.Text.Contains("截图") || !result.Text.Contains("API"))
            throw new Exception("C# wrapper recognition failed: " + result.Error + " " + result.Text);
        Console.WriteLine($"PASS: C# wrapper recognized Chinese/English via {result.Language}.");

        var workerPath = Path.Combine(AppContext.BaseDirectory, "ocr-worker.ps1");
        var savedWorker = File.ReadAllText(workerPath);
        var tempRoot = Path.Combine(Path.GetTempPath(), "ScreenNote", "ocr");
        var directoriesBefore = Directory.Exists(tempRoot) ? Directory.GetDirectories(tempRoot).ToHashSet() : [];
        try
        {
            File.WriteAllText(workerPath, "param([string] $ImagePath)\nStart-Sleep -Seconds 10\n");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try
            {
                service.RecognizeAsync(bitmap, cancel.Token).GetAwaiter().GetResult();
                throw new Exception("Cancellation was not observed.");
            }
            catch (OperationCanceledException) { }
            var directoriesAfter = Directory.Exists(tempRoot) ? Directory.GetDirectories(tempRoot).ToHashSet() : [];
            if (directoriesAfter.Except(directoriesBefore).Any())
                throw new Exception("Cancellation left an OCR temporary directory.");
            Console.WriteLine("PASS: cancellation terminates worker and removes its temporary image directory.");
        }
        finally
        {
            File.WriteAllText(workerPath, savedWorker);
        }

        Console.WriteLine("PASS: explicit text cleanup transforms.");
        return 0;
    }

    private static void Check(string actual, string expected, string name)
    {
        if (actual != expected)
            throw new Exception($"{name}: expected '{expected}', got '{actual}'");
    }
}
