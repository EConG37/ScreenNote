using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace ScreenNote;

/// <summary>Copies committed history without deleting its source or changing application settings.</summary>
public static class StorageLocationService
{
    private const long MaximumJsonBytes = 32_000_000;

    public static string NormalizeDataDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory.Trim()));
    }

    public static bool IsSameDirectory(string first, string second) =>
        string.Equals(NormalizeDataDirectory(first), NormalizeDataDirectory(second), StringComparison.OrdinalIgnoreCase);

    public static void EnsureWritableDataDirectory(string directory)
    {
        directory = NormalizeDataDirectory(directory);
        RejectReparseChain(directory);
        Directory.CreateDirectory(directory);
        RejectReparseChain(directory);
        var probe = Path.Combine(directory, ".screennote-write-" + Guid.NewGuid().ToString("N") + ".tmp");
        using var file = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            4096, FileOptions.DeleteOnClose);
        file.WriteByte(1);
        file.Flush(true);
    }

    public static int CopyHistory(string sourceDataDirectory, string destinationDataDirectory)
    {
        var source = NormalizeDataDirectory(sourceDataDirectory);
        var destination = NormalizeDataDirectory(destinationDataDirectory);
        if (IsSameDirectory(source, destination)) return 0;
        if (ContainsDirectory(source, destination) || ContainsDirectory(destination, source))
            throw new IOException("新旧存放位置不能互相包含，请选择独立的文件夹。");
        RejectReparseChain(source); RejectReparseChain(destination);
        var sourceHistory = Path.Combine(source, "history");
        var destinationHistory = Path.Combine(destination, "history");
        RejectReparseChain(sourceHistory); RejectReparseChain(destinationHistory);

        // Validate the complete source and all destination conflicts before committing any record.
        // Partial copies from an interrupted migration remain resumable but have no current pointer.
        var records = new List<CopyRecord>();
        if (Directory.Exists(sourceHistory))
        {
            foreach (var directory in Directory.EnumerateDirectories(sourceHistory))
            {
                var id = Path.GetFileName(directory);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                RejectReparseChain(directory);
                RejectReparseChain(Path.Combine(directory, "current.json"));
                if (!File.Exists(Path.Combine(directory, "current.json"))) continue;
                var record = ReadRecord(directory, id);
                ValidateDestination(record, Path.Combine(destinationHistory, id));
                records.Add(record);
            }
        }
        EnsureWritableDataDirectory(destination);
        Directory.CreateDirectory(destinationHistory);
        RejectReparseChain(destinationHistory);
        foreach (var record in records)
        {
            var directory = Path.Combine(destinationHistory, record.Id);
            Directory.CreateDirectory(directory);
            RejectReparseChain(directory);
            CopyImmutable(record.SourcePath, Path.Combine(directory, "source.png"), record.SourceHash);
            CopyImmutable(record.ImagePath, Path.Combine(directory, record.RevisionId + ".png"), record.ImageHash);
            CopyImmutable(record.DocumentPath, Path.Combine(directory, record.RevisionId + ".json"), record.DocumentHash);
            var pointer = Path.Combine(directory, "current.json");
            if (!File.Exists(pointer) || !HashFile(pointer).SequenceEqual(record.PointerHash))
                AtomicWrite(pointer, stream => stream.Write(record.PointerBytes), overwrite: true);
        }
        return records.Count;
    }

    private static bool ContainsDirectory(string parent, string child)
    {
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return child.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectReparseChain(string path)
    {
        var current = NormalizeDataDirectory(path);
        while (true)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("存放位置或历史文件不能是目录链接或外部文件链接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || IsSameDirectory(parent, current)) return;
            current = parent;
        }
    }

    private static CopyRecord ReadRecord(string directory, string expectedId)
    {
        var pointerPath = Path.Combine(directory, "current.json");
        var bytes = ReadJsonBytes(pointerPath);
        using var pointer = JsonDocument.Parse(bytes);
        var root = pointer.RootElement;
        if (RequiredInt(root, "Version") != 1 || RequiredString(root, "Id") != expectedId
            || !root.TryGetProperty("CreatedAt", out var created) || !created.TryGetDateTimeOffset(out var timestamp)
            || timestamp == default)
            throw new FormatException("历史截图索引损坏：" + expectedId);
        var origin = RequiredString(root, "Origin");
        if (string.IsNullOrWhiteSpace(origin) || origin.Length > 100) throw new FormatException("历史截图名称损坏。");
        var revisionId = RequiredString(root, "RevisionId");
        if (!Guid.TryParseExact(revisionId, "N", out _)) throw new FormatException("历史截图版本标识损坏。");
        var originalPath = Path.Combine(directory, "source.png");
        var imagePath = Path.Combine(directory, revisionId + ".png");
        var documentPath = Path.Combine(directory, revisionId + ".json");
        var original = ReadPngSize(originalPath);
        var rendered = ReadPngSize(imagePath);
        if (original != rendered) throw new FormatException("历史原图与编辑图片尺寸不一致。");
        var documentBytes = ReadJsonBytes(documentPath);
        using var document = JsonDocument.Parse(documentBytes);
        var revision = document.RootElement;
        if (RequiredInt(revision, "Version") != 1) throw new FormatException("历史编辑文档版本不正确。");
        var annotations = RequiredString(revision, "DocumentJson");
        if (annotations.Length > 16_000_000 || RequiredString(revision, "Text").Length > 4_000_000
            || RequiredString(revision, "OriginalOcr").Length > 4_000_000
            || RequiredString(revision, "LastRecognized").Length > 4_000_000)
            throw new FormatException("历史编辑文字或标注文档过大。");
        if (annotations.Length != 0)
        {
            using var layers = JsonDocument.Parse(annotations);
            var content = layers.RootElement;
            if (RequiredInt(content, "Version") != 1 || RequiredInt(content, "Width") != original.Width
                || RequiredInt(content, "Height") != original.Height
                || !content.TryGetProperty("Marks", out var marks) || marks.ValueKind != JsonValueKind.Array
                || marks.GetArrayLength() > 10_000)
                throw new FormatException("历史可编辑标注文档损坏。");
            RequireColor(content);
            RequireNumber(content, "StrokeWidth", 1, 100);
            RequireNumber(content, "TextSize", 8, 150);
            if (RequiredInt(content, "Tool") is < 0 or > 10) throw new FormatException("历史标注工具不正确。");
            var ids = new HashSet<Guid>();
            var totalPoints = 0;
            foreach (var mark in marks.EnumerateArray())
            {
                if (!Guid.TryParseExact(RequiredString(mark, "Id"), "D", out var id) || id == Guid.Empty
                    || !ids.Add(id) || RequiredInt(mark, "Tool") is < 1 or > 10
                    || RequiredInt(mark, "Number") < 0 || RequiredString(mark, "Text").Length > 1_000_000)
                    throw new FormatException("历史标注内容损坏。");
                RequireColor(mark);
                RequireNumber(mark, "Width", 1, 100);
                RequireNumber(mark, "TextSize", 8, 150);
                RequireNumber(mark, "TextWidth", 1, 1_000_000);
                if (!mark.TryGetProperty("Start", out var start) || !mark.TryGetProperty("End", out var end)
                    || !mark.TryGetProperty("Points", out var points)) throw new FormatException("历史标注缺少坐标。");
                RequirePoint(start); RequirePoint(end);
                if (points.ValueKind != JsonValueKind.Array
                    || ((RequiredInt(mark, "Tool") is 1 or 6) && points.GetArrayLength() == 0))
                    throw new FormatException("历史标注笔画损坏。");
                totalPoints = checked(totalPoints + points.GetArrayLength());
                if (totalPoints > 500_000) throw new FormatException("历史标注笔画过多。");
                foreach (var point in points.EnumerateArray()) RequirePoint(point);
            }
        }
        return new CopyRecord(expectedId, revisionId, originalPath, imagePath, documentPath,
            HashFile(originalPath), HashFile(imagePath), SHA256.HashData(documentBytes), bytes, SHA256.HashData(bytes));
    }

    private static void ValidateDestination(CopyRecord source, string directory)
    {
        RejectReparseChain(directory);
        if (!Directory.Exists(directory)) return;
        var original = Path.Combine(directory, "source.png");
        RejectReparseChain(original);
        if (File.Exists(original) && !HashFile(original).SequenceEqual(source.SourceHash))
            throw new IOException("目标位置已有同编号但原图不同的截图，未覆盖：" + source.Id);
        var pointer = Path.Combine(directory, "current.json");
        RejectReparseChain(pointer);
        if (File.Exists(pointer))
        {
            var existing = ReadRecord(directory, source.Id);
            if (!existing.SourceHash.SequenceEqual(source.SourceHash))
                throw new IOException("目标位置已有同编号但原图不同的截图，未覆盖：" + source.Id);
        }
        ValidateImmutableFile(Path.Combine(directory, source.RevisionId + ".png"), source.ImageHash);
        ValidateImmutableFile(Path.Combine(directory, source.RevisionId + ".json"), source.DocumentHash);
    }

    private static void ValidateImmutableFile(string path, byte[] hash)
    {
        RejectReparseChain(path);
        if (File.Exists(path) && !HashFile(path).SequenceEqual(hash))
            throw new IOException("目标位置已有同编号但内容不同的历史版本，未覆盖：" + Path.GetFileName(path));
    }

    private static void CopyImmutable(string source, string destination, byte[] expectedHash)
    {
        ValidateImmutableFile(destination, expectedHash);
        if (File.Exists(destination)) return;
        AtomicWrite(destination, output =>
        {
            RejectReparseChain(source);
            using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int count;
            while ((count = input.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, count);
                hash.AppendData(buffer, 0, count);
            }
            if (!hash.GetHashAndReset().SequenceEqual(expectedHash))
                throw new IOException("迁移过程中源历史发生变化，请保存当前编辑后重试。");
        }, overwrite: false);
    }

    private static void AtomicWrite(string destination, Action<FileStream> write, bool overwrite)
    {
        RejectReparseChain(destination);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(output);
                output.Flush(true);
            }
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static byte[] HashFile(string path)
    {
        RejectReparseChain(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    private static byte[] ReadJsonBytes(string path)
    {
        RejectReparseChain(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > MaximumJsonBytes) throw new FormatException("历史 JSON 文件大小不正确。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private static (int Width, int Height) ReadPngSize(string path)
    {
        RejectReparseChain(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var signature = new byte[8]; stream.ReadExactly(signature);
        if (!signature.AsSpan().SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new FormatException("历史截图不是有效的 PNG 文件。");
        stream.Position = 0;
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1) throw new FormatException("历史截图包含不支持的图片帧。");
        var frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) throw new FormatException("历史截图尺寸不正确。");
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.String) throw new FormatException("历史文档缺少有效字段：" + property);
        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new FormatException("历史文档缺少有效字段：" + property);
        return result;
    }

    private static void RequireNumber(JsonElement element, string property, double minimum, double maximum)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)
            || !double.IsFinite(number) || number < minimum || number > maximum)
            throw new FormatException("历史标注坐标或尺寸损坏：" + property);
    }

    private static void RequirePoint(JsonElement point)
    {
        RequireNumber(point, "X", -1_000_000, 1_000_000);
        RequireNumber(point, "Y", -1_000_000, 1_000_000);
    }

    private static void RequireColor(JsonElement element)
    {
        var color = RequiredString(element, "Color");
        if (color.Length != 9 || color[0] != '#'
            || !uint.TryParse(color.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new FormatException("历史标注颜色损坏。");
    }

    private sealed record CopyRecord(string Id, string RevisionId, string SourcePath, string ImagePath,
        string DocumentPath, byte[] SourceHash, byte[] ImageHash, byte[] DocumentHash,
        byte[] PointerBytes, byte[] PointerHash);
}
