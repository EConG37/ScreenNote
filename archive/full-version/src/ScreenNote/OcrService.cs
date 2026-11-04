using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ScreenNote;

public sealed record OcrResult(string Text, string Language, string? Error = null);

/// <summary>Runs bundled Chinese OCR or the Windows backup locally, without a cloud service.</summary>
public sealed class OcrService
{
    private static readonly TimeSpan RecognitionTimeout = TimeSpan.FromSeconds(60);

    public async Task<OcrResult> RecognizeAsync(BitmapSource image, CancellationToken cancellationToken = default, string engine = "rapid")
    {
        ArgumentNullException.ThrowIfNull(image);
        cancellationToken.ThrowIfCancellationRequested();

        var workerPath = Path.Combine(AppContext.BaseDirectory, "ocr-worker.ps1");
        var rapidWorker = Path.Combine(AppContext.BaseDirectory, "rapid-ocr-worker.py");
        var python = Path.Combine(AppContext.BaseDirectory, "ocr-runtime", "python.exe");
        var enhanced = engine != "windows" && File.Exists(python) && File.Exists(rapidWorker);
        if (!enhanced && !File.Exists(workerPath))
            return new OcrResult("", "", "OCR 组件缺失，请重新解压完整的软件文件。");

        var operationDirectory = Path.Combine(Path.GetTempPath(), "ScreenNote", "ocr", Guid.NewGuid().ToString("N"));
        Process? process = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tileCount = Math.Max(1, (image.PixelHeight + 1259) / 1260);
        timeout.CancelAfter(enhanced ? TimeSpan.FromSeconds(Math.Clamp(20 + tileCount * 9, RecognitionTimeout.TotalSeconds, 300)) : RecognitionTimeout);

        try
        {
            Directory.CreateDirectory(operationDirectory);
            var imagePath = Path.Combine(operationDirectory, "capture.png");
            // BitmapSource may belong to the UI dispatcher; encode before the first await.
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var output = File.Create(imagePath))
                encoder.Save(output);

            timeout.Token.ThrowIfCancellationRequested();
            var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var startInfo = new ProcessStartInfo
            {
                FileName = enhanced ? python : Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            // This affects only this worker process; no system execution policy is changed.
            var arguments = enhanced ? new[] { "-I", "-B", rapidWorker, imagePath } : new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", workerPath, "-ImagePath", imagePath };
            foreach (var argument in arguments)
                startInfo.ArgumentList.Add(argument);

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return new OcrResult("", "", "无法启动本地 OCR 组件。");
            using var cancelled = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });

            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            var json = await standardOutput.ConfigureAwait(false);
            var error = await standardError.ConfigureAwait(false);

            OcrResult? result = null;
            if (!string.IsNullOrWhiteSpace(json))
                result = JsonSerializer.Deserialize<OcrResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (result is not null)
                return result;

            return new OcrResult("", "", string.IsNullOrWhiteSpace(error)
                ? enhanced ? "中文增强引擎未返回有效结果，请重新解压完整的软件文件。" : "本地 OCR 未返回有效结果。请确认 Windows 已安装中文或英文 OCR 语言组件。"
                : enhanced ? "中文增强引擎启动失败，请重新解压完整的软件文件。" : "本地 OCR 启动失败。请确认 Windows PowerShell 和 OCR 语言组件可用。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new OcrResult("", "", "识别超时，请缩小截图范围后重试。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            return new OcrResult("", "", "本地 OCR 无法完成识别：" + exception.Message);
        }
        finally
        {
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        // Ensure the worker releases the PNG before removing our temporary directory.
                        process.WaitForExit(2000);
                    }
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }

            try
            {
                if (Directory.Exists(operationDirectory) && Path.GetFullPath(operationDirectory).StartsWith(Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ScreenNote", "ocr")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    Directory.Delete(operationDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
