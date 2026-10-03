using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenNote;

public sealed record TranslationResult(string Text, string Direction, string? Error = null);
public interface ITranslationService
{
    Task<TranslationResult> TranslateAsync(string text, string direction = "auto", CancellationToken cancellationToken = default);
}

public sealed class TranslationService : ITranslationService
{
    public async Task<TranslationResult> TranslateAsync(string text, string direction = "auto", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return new("", "", "请先输入或识别需要翻译的文字。");
        if (text.Length > 20_000) return new("", "", "每次最多翻译 20,000 个字符，请分段处理。");
        if (direction is not ("auto" or "en_zh" or "zh_en")) return new("", "", "请选择中文或英文翻译方向。");
        var python = Path.Combine(AppContext.BaseDirectory, "ocr-runtime", "python.exe");
        var worker = Path.Combine(AppContext.BaseDirectory, "translation-worker.py");
        var models = Path.Combine(AppContext.BaseDirectory, "translation-models");
        if (!File.Exists(python) || !File.Exists(worker) || !File.Exists(Path.Combine(models, "en_zh", "model", "model.bin")) || !File.Exists(Path.Combine(models, "zh_en", "model", "model.bin")))
            return new("", "", "离线翻译组件缺失，请重新解压完整的软件文件。");
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "ScreenNote", "translation");
        var operation = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        Process? process = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(30 + text.Length / 20, 60, 300)));
        try
        {
            Directory.CreateDirectory(operation);
            var request = Path.Combine(operation, "request.json");
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(new { Text = text, Direction = direction }), new UTF8Encoding(false), timeout.Token).ConfigureAwait(false);
            var start = new ProcessStartInfo
            {
                FileName = python, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (var argument in new[] { "-I", "-B", worker, request }) start.ArgumentList.Add(argument);
            process = new Process { StartInfo = start };
            if (!process.Start()) return new("", "", "无法启动本地翻译组件。");
            using var cancelled = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            });
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            var result = JsonSerializer.Deserialize<TranslationResult>(await output.ConfigureAwait(false));
            await errors.ConfigureAwait(false);
            return result ?? new("", "", "本地翻译未返回结果，可以校对原文后重试。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new("", "", "翻译超时，原文仍保留，请分段重试。"); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        { return new("", "", "本地翻译未完成：" + ex.Message); }
        finally
        {
            if (process != null)
            {
                try { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                process.Dispose();
            }
            try
            {
                var resolved = Path.GetFullPath(operation);
                if (resolved.StartsWith(Path.GetFullPath(temporaryRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
