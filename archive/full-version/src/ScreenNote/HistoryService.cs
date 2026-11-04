using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace ScreenNote;

public sealed class HistoryEntry : INotifyPropertyChanged
{
    internal HistoryEntry(string storageRoot, string id, DateTimeOffset createdAt, string origin, BitmapSource thumbnail)
    {
        StorageRoot = storageRoot;
        Id = id;
        CreatedAt = createdAt;
        Origin = origin;
        _thumbnail = thumbnail;
    }

    private BitmapSource _thumbnail;
    internal string StorageRoot { get; }
    public string Id { get; }
    public DateTimeOffset CreatedAt { get; }
    public string Origin { get; }
    public string Title => Origin + " · " + CreatedAt.ToLocalTime().ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public BitmapSource Thumbnail => _thumbnail;
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void RefreshThumbnail(BitmapSource thumbnail)
    {
        _thumbnail = thumbnail;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
    }
}

public sealed record HistoryDocument(BitmapSource Source, BitmapSource Rendered, string DocumentJson,
    string Text, string OriginalOcr, string LastRecognized);

/// <summary>
/// One directory per capture. The source is immutable; an atomic current.json pointer commits
/// each rendered image and editable document together so interruption cannot mix two revisions.
/// </summary>
public sealed class HistoryService
{
    private readonly string _root;
    private readonly object _gate = new();
    private readonly List<string> _loadWarnings = [];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };
    private const long MaxJsonBytes = 32_000_000;

    public HistoryService(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        Directory.CreateDirectory(_root);
    }

    public IReadOnlyList<string> LoadWarnings => _loadWarnings.AsReadOnly();

    public IReadOnlyList<HistoryEntry> LoadEntries()
    {
        lock (_gate)
        {
            _loadWarnings.Clear();
            var entries = new List<HistoryEntry>();
            foreach (var folder in Directory.EnumerateDirectories(_root))
            {
                var id = Path.GetFileName(folder);
                if (!Guid.TryParseExact(id, "N", out _)) continue;
                try
                {
                    var directory = EntryDirectory(id);
                    // current.json is the commit point. Interrupted first saves are invisible.
                    if (!File.Exists(Path.Combine(directory, "current.json"))) continue;
                    var metadata = ReadMetadata(directory, id);
                    var revision = ReadRevision(directory, metadata.RevisionId);
                    ValidateOriginal(directory);
                    var thumbnail = ReadBitmap(RevisionImagePath(directory, metadata.RevisionId), 320);
                    if (revision.DocumentJson.Length != 0)
                    {
                        using var document = JsonDocument.Parse(revision.DocumentJson);
                    }
                    entries.Add(new HistoryEntry(_root, id, metadata.CreatedAt, metadata.Origin, thumbnail));
                }
                catch (Exception exception) when (IsRecoverableReadError(exception))
                {
                    _loadWarnings.Add(id + ": " + exception.Message);
                }
            }
            return entries.OrderByDescending(entry => entry.CreatedAt).ThenByDescending(entry => entry.Id).ToList();
        }
    }

    public HistoryEntry Create(BitmapSource source, string origin = "截图")
    {
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N");
            var revisionId = Guid.NewGuid().ToString("N");
            var directory = EntryDirectory(id);
            var created = DateTimeOffset.Now;
            origin = string.IsNullOrWhiteSpace(origin) ? "截图" : origin;
            if (origin.Length > 100) throw new ArgumentException("历史记录名称过长。", nameof(origin));
            var committed = false;
            try
            {
                // Directory renames can be denied even when individual files are writable.
                // Build the record in its final private GUID directory and commit its pointer last.
                Directory.CreateDirectory(directory);
                WritePng(Path.Combine(directory, "source.png"), source);
                WritePng(RevisionImagePath(directory, revisionId), source);
                WriteJson(RevisionDocumentPath(directory, revisionId), new SavedRevision());
                var thumbnail = ReadBitmap(RevisionImagePath(directory, revisionId), 320);
                var entry = new HistoryEntry(_root, id, created, origin, thumbnail);
                WriteJson(Path.Combine(directory, "current.json"), new SavedMetadata
                {
                    Version = 1, Id = id, CreatedAt = created, Origin = origin, RevisionId = revisionId
                });
                committed = true;
                return entry;
            }
            catch
            {
                // Cleanup is limited to this newly generated ID and must not mask the save error.
                if (!committed)
                {
                    try
                    {
                        var cleanupDirectory = EntryDirectory(id);
                        if (Directory.Exists(cleanupDirectory)) Directory.Delete(cleanupDirectory, true);
                    }
                    catch { /* Preserve the original save exception if cleanup also fails. */ }
                }
                throw;
            }
        }
    }

    public void Update(HistoryEntry entry, BitmapSource rendered, string documentJson,
        string text, string originalOcr, string lastRecognized)
    {
        ArgumentNullException.ThrowIfNull(rendered);
        lock (_gate)
        {
            var directory = CheckedEntryDirectory(entry);
            var metadata = ReadMetadata(directory, entry.Id);
            var previousRevision = metadata.RevisionId;
            var revision = new SavedRevision
            {
                DocumentJson = documentJson ?? "", Text = text ?? "", OriginalOcr = originalOcr ?? "",
                LastRecognized = lastRecognized ?? ""
            };
            ValidateRevision(revision);
            if (revision.DocumentJson.Length != 0)
            {
                using var document = JsonDocument.Parse(revision.DocumentJson);
            }
            var revisionId = Guid.NewGuid().ToString("N");
            WritePng(RevisionImagePath(directory, revisionId), rendered);
            WriteJson(RevisionDocumentPath(directory, revisionId), revision);
            var thumbnail = ReadBitmap(RevisionImagePath(directory, revisionId), 320);
            metadata.RevisionId = revisionId;
            WriteJson(Path.Combine(directory, "current.json"), metadata);
            entry.RefreshThumbnail(thumbnail);
            // Older revisions are internal save files, not separate history captures.
            // Remove them only after the new pointer has been committed successfully.
            TryDelete(RevisionImagePath(directory, previousRevision));
            TryDelete(RevisionDocumentPath(directory, previousRevision));
        }
    }

    public HistoryDocument Load(HistoryEntry entry)
    {
        lock (_gate)
        {
            var directory = CheckedEntryDirectory(entry);
            var metadata = ReadMetadata(directory, entry.Id);
            var revision = ReadRevision(directory, metadata.RevisionId);
            var source = ReadBitmap(Path.Combine(directory, "source.png"));
            var rendered = ReadBitmap(RevisionImagePath(directory, metadata.RevisionId));
            if (source.PixelWidth != rendered.PixelWidth || source.PixelHeight != rendered.PixelHeight)
                throw new FormatException("历史截图原图与编辑结果尺寸不一致。");
            return new HistoryDocument(source, rendered, revision.DocumentJson, revision.Text,
                revision.OriginalOcr, revision.LastRecognized);
        }
    }

    public byte[] ReadRenderedPng(HistoryEntry entry, long maximumBytes = int.MaxValue)
    {
        lock (_gate)
        {
            var directory = CheckedEntryDirectory(entry);
            var metadata = ReadMetadata(directory, entry.Id);
            var path = RevisionImagePath(directory, metadata.RevisionId);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("历史图片不能是外部链接。");
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > maximumBytes || file.Length > int.MaxValue)
                throw new IOException("所选截图合计超过 12 MB，请减少选择或分批提交。");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
            if (bytes.Length < 8 || !bytes.AsSpan(0, 8).SequenceEqual(signature))
                throw new FormatException("历史截图不是有效的 PNG 图片。");
            return bytes;
        }
    }

    public void Delete(HistoryEntry entry)
    {
        lock (_gate)
        {
            var directory = CheckedEntryDirectory(entry);
            ValidateDeletionAncestors(directory);
            if (!TryGetDeletionAttributes(directory, out _)) return;
            var files = new List<string>();
            var directories = new List<string>();
            CollectDeletionPaths(directory, directory, files, directories);
            var handles = new Dictionary<string, SafeFileHandle>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Request DELETE before touching any contents. A reader which did not share
                // deletion blocks this preflight, so it cannot leave half a screenshot behind.
                // Keeping these handles open also prevents new incompatible readers until removal.
                foreach (var path in files.Concat(directories)) handles.Add(path, OpenDeletionHandle(path));
                foreach (var path in files)
                {
                    ValidateDeletionPath(directory, path);
                    File.Delete(path);
                    handles[path].Dispose();
                }
                for (var index = directories.Count - 1; index >= 0; index--)
                {
                    var path = directories[index];
                    ValidateDeletionPath(directory, path);
                    // Never recurse at this stage: a newly appeared, unvalidated child causes
                    // failure instead of broadening the set of files the user asked to remove.
                    Directory.Delete(path, recursive: false);
                    handles[path].Dispose();
                }
            }
            finally { foreach (var handle in handles.Values) handle.Dispose(); }
            if (TryGetDeletionAttributes(directory, out _))
                throw new IOException("截图文件夹仍存在，删除未完成，请重试。");
        }
    }

    private static bool TryGetDeletionAttributes(string path, out FileAttributes attributes)
    {
        try { attributes = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { attributes = default; return false; }
        catch (DirectoryNotFoundException) { attributes = default; return false; }
    }

    private static void ValidateDeletionAncestors(string directory)
    {
        var path = directory;
        while (!string.IsNullOrEmpty(path))
        {
            if (TryGetDeletionAttributes(path, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("截图删除路径包含目录或文件链接，未删除任何内容。");
            path = Path.GetDirectoryName(path);
        }
    }

    private static void ValidateDeletionPath(string directory, string path)
    {
        var normalized = Path.GetFullPath(path);
        if (!string.Equals(normalized, directory, StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("删除路径超出这张截图的文件夹。");
        if (TryGetDeletionAttributes(normalized, out var attributes) && (attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("截图文件夹包含目录或文件链接，未删除链接指向的内容。");
    }

    private static void CollectDeletionPaths(string directory, string path, List<string> files, List<string> directories)
    {
        ValidateDeletionPath(directory, path);
        if (!TryGetDeletionAttributes(path, out var attributes)) throw new IOException("截图文件在删除前发生变化，请重试。");
        if ((attributes & FileAttributes.Directory) == 0)
        {
            if ((attributes & FileAttributes.ReadOnly) != 0) throw new UnauthorizedAccessException("截图包含只读文件，删除未完成。");
            files.Add(path);
            return;
        }
        directories.Add(path);
        foreach (var child in Directory.EnumerateFileSystemEntries(path)) CollectDeletionPaths(directory, child, files, directories);
    }

    private static SafeFileHandle OpenDeletionHandle(string path)
    {
        var handle = CreateDeletionHandle(path, 0x00010000, 0x00000007, nint.Zero, 3,
            0x00200000 | 0x02000000, nint.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new IOException("截图文件正被占用或没有删除权限，未删除任何内容。", new Win32Exception(error));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateDeletionHandle(string path, uint access, uint sharing,
        nint security, uint creation, uint flags, nint template);

    private string CheckedEntryDirectory(HistoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.Equals(entry.StorageRoot, _root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("历史记录不属于当前截图资料库。", nameof(entry));
        return EntryDirectory(entry.Id);
    }

    private string EntryDirectory(string id)
    {
        ValidateGuidName(id);
        var directory = SafeChild(id);
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("历史截图目录不能是外部链接。");
        return directory;
    }

    private string SafeChild(string name)
    {
        var path = Path.GetFullPath(Path.Combine(_root, name));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("历史截图路径超出资料库。");
        return path;
    }

    private static void ValidateGuidName(string? value)
    {
        if (value is null || !Guid.TryParseExact(value, "N", out _))
            throw new FormatException("历史截图记录标识不正确。");
    }

    private static string RevisionImagePath(string directory, string revisionId)
    {
        ValidateGuidName(revisionId);
        return Path.Combine(directory, revisionId + ".png");
    }

    private static string RevisionDocumentPath(string directory, string revisionId)
    {
        ValidateGuidName(revisionId);
        return Path.Combine(directory, revisionId + ".json");
    }

    private static SavedMetadata ReadMetadata(string directory, string expectedId)
    {
        var metadata = ReadJson<SavedMetadata>(Path.Combine(directory, "current.json"));
        if (metadata.Version != 1 || metadata.Id != expectedId || metadata.CreatedAt == default
            || string.IsNullOrWhiteSpace(metadata.Origin) || metadata.Origin.Length > 100)
            throw new FormatException("历史截图索引内容不正确。");
        ValidateGuidName(metadata.RevisionId);
        return metadata;
    }

    private static SavedRevision ReadRevision(string directory, string revisionId)
    {
        var revision = ReadJson<SavedRevision>(RevisionDocumentPath(directory, revisionId));
        ValidateRevision(revision);
        return revision;
    }

    private static void ValidateRevision(SavedRevision revision)
    {
        if (revision.Version != 1 || revision.DocumentJson is null || revision.Text is null
            || revision.OriginalOcr is null || revision.LastRecognized is null
            || revision.DocumentJson.Length > 16_000_000 || revision.Text.Length > 4_000_000
            || revision.OriginalOcr.Length > 4_000_000 || revision.LastRecognized.Length > 4_000_000)
            throw new FormatException("历史截图编辑文档不正确或过大。");
    }

    private static void ValidateOriginal(string directory)
    {
        var path = Path.Combine(directory, "source.png");
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
            throw new IOException("历史截图缺少原始图片。");
        _ = ReadBitmap(path, 1);
    }

    private static T ReadJson<T>(string path)
    {
        var length = new FileInfo(path).Length;
        if (length <= 0 || length > MaxJsonBytes) throw new FormatException("历史截图数据文件大小不正确。");
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
            ?? throw new FormatException("历史截图数据文件为空。");
    }

    private static BitmapSource ReadBitmap(string path, int decodeWidth = 0)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void WritePng(string path, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        AtomicWrite(path, stream => encoder.Save(stream));
    }

    private static void WriteJson<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (bytes.Length > MaxJsonBytes) throw new FormatException("历史截图数据文件过大。");
        AtomicWrite(path, stream => stream.Write(bytes));
    }

    private static void AtomicWrite(string path, Action<FileStream> write)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(true);
            }
            // Both paths are siblings on the same volume. One overwrite rename commits the file
            // without ReplaceFile's extra WRITE_DAC/metadata-merging requirements. The temporary
            // file already inherits this private history directory's permissions.
            File.Move(temporary, path, overwrite: true);
        }
        finally { TryDelete(temporary); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsRecoverableReadError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException or FormatException
            or ArgumentException or NotSupportedException or OverflowException;

    private sealed class SavedMetadata
    {
        public int Version { get; set; }
        public string Id { get; set; } = "";
        public DateTimeOffset CreatedAt { get; set; }
        public string Origin { get; set; } = "截图";
        public string RevisionId { get; set; } = "";
    }

    private sealed class SavedRevision
    {
        public int Version { get; set; } = 1;
        public string DocumentJson { get; set; } = "";
        public string Text { get; set; } = "";
        public string OriginalOcr { get; set; } = "";
        public string LastRecognized { get; set; } = "";
    }
}
