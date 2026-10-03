using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;

internal static class StorageLocationValidation
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "ScreenNote-storage-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var sourceRoot = Path.Combine(root, "旧资料 中文");
            var targetRoot = Path.Combine(root, "新资料 中文");
            StorageLocationService.EnsureWritableDataDirectory(sourceRoot);
            StorageLocationService.EnsureWritableDataDirectory(targetRoot);
            Assert(StorageLocationService.IsSameDirectory(sourceRoot, Path.Combine(sourceRoot, ".") + Path.DirectorySeparatorChar),
                "Directory normalization does not handle trailing separators or dot segments.");
            Assert(Path.IsPathFullyQualified(StorageLocationService.NormalizeDataDirectory("tests")), "Normalization did not return an absolute path.");
            Assert(!Directory.EnumerateFiles(sourceRoot).Any(), "Writable probe left a file behind.");
            File.WriteAllText(Path.Combine(sourceRoot, "settings.json"), "source settings stay here");
            File.WriteAllText(Path.Combine(targetRoot, "settings.json"), "target settings stay here");
            var source = new HistoryService(Path.Combine(sourceRoot, "history"));
            var destination = new HistoryService(Path.Combine(targetRoot, "history"));
            var image = Image(180);
            var entry = source.Create(image);
            var existingTargetEntry = destination.Create(Image(80), "目标原有截图");
            var surface = new AnnotationSurface { Source = image };
            var layer = JsonSerializer.Serialize(new
            {
                Version = 1, Width = 64, Height = 48, Color = "#FF2563EB", StrokeWidth = 3, TextSize = 22,
                Tool = AnnotationTool.Select,
                Marks = new[] { new { Id = Guid.NewGuid(), Tool = AnnotationTool.Text,
                    Start = new { X = 4d, Y = 4d }, End = new { X = 4d, Y = 4d }, Points = Array.Empty<object>(),
                    Color = "#FF2563EB", Width = 3, TextSize = 10, TextWidth = 50, Text = "可编辑标注", Number = 0 } }
            });
            surface.RestoreDocument(image, layer);
            source.Update(entry, surface.Export(), surface.ExportDocument(), "OCR 修改后\n第二行", "OCR 原始内容", "上次识别");
            var sourceDirectory = Path.Combine(sourceRoot, "history", entry.Id);
            var targetDirectory = Path.Combine(targetRoot, "history", entry.Id);
            var sourcePointer = File.ReadAllBytes(Path.Combine(sourceDirectory, "current.json"));
            var originalBytes = File.ReadAllBytes(Path.Combine(sourceDirectory, "source.png"));
            File.WriteAllText(Path.Combine(sourceDirectory, "unused-orphan.png"), "do not migrate unrelated files");
            var uncommitted = Path.Combine(sourceRoot, "history", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(uncommitted);
            File.WriteAllText(Path.Combine(uncommitted, "source.png"), "incomplete");

            Assert(Task.Run(() => StorageLocationService.CopyHistory(sourceRoot, targetRoot)).GetAwaiter().GetResult() == 1,
                "Background migration did not count the committed source capture.");
            Assert(File.ReadAllBytes(Path.Combine(sourceDirectory, "current.json")).SequenceEqual(sourcePointer)
                && File.ReadAllBytes(Path.Combine(sourceDirectory, "source.png")).SequenceEqual(originalBytes), "Migration altered source history.");
            Assert(Directory.GetFiles(targetDirectory).Length == 4 && !File.Exists(Path.Combine(targetDirectory, "unused-orphan.png")),
                "Migration copied orphan files instead of exactly three referenced files and the pointer.");
            Assert(File.ReadAllText(Path.Combine(sourceRoot, "settings.json")) == "source settings stay here"
                && File.ReadAllText(Path.Combine(targetRoot, "settings.json")) == "target settings stay here", "Migration changed application settings.");
            var reopened = new HistoryService(Path.Combine(targetRoot, "history"));
            var entries = reopened.LoadEntries();
            Assert(entries.Count == 2 && reopened.LoadWarnings.Count == 0 && entries.Any(item => item.Id == existingTargetEntry.Id),
                "Migration did not merge existing destination captures.");
            var migrated = reopened.Load(entries.Single(item => item.Id == entry.Id));
            Assert(migrated.Text == "OCR 修改后\n第二行" && migrated.OriginalOcr == "OCR 原始内容" && migrated.LastRecognized == "上次识别",
                "OCR state was lost during migration.");
            var restored = new AnnotationSurface(); restored.RestoreDocument(migrated.Source, migrated.DocumentJson);
            var marks = (System.Collections.IList)typeof(AnnotationSurface).GetField("_marks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(restored)!;
            Assert(marks.Count == 1, "Migrated annotations were flattened.");
            typeof(AnnotationSurface).GetMethod("SetSelected", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(restored, new[] { marks[0], false });
            restored.InkColor = Colors.Red; restored.Undo(); restored.DeleteSelected(); restored.Undo();
            Assert(marks.Count == 1, "Migrated annotations cannot be edited and undone.");
            var pointerTime = File.GetLastWriteTimeUtc(Path.Combine(targetDirectory, "current.json"));
            Assert(StorageLocationService.CopyHistory(sourceRoot, targetRoot) == 1
                && File.GetLastWriteTimeUtc(Path.Combine(targetDirectory, "current.json")) == pointerTime, "Identical migration was not idempotent.");
            Assert(StorageLocationService.CopyHistory(sourceRoot, sourceRoot) == 0, "Same-location migration was not a no-op.");

            var oldTargetRevision = RevisionId(targetDirectory);
            source.Update(entry, surface.Export(), surface.ExportDocument(), "新的当前版本", "原文新版", "新版识别");
            var newerPointer = File.ReadAllBytes(Path.Combine(sourceDirectory, "current.json"));
            using (var locked = new FileStream(Path.Combine(targetDirectory, "current.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
                ExpectFailure(() => StorageLocationService.CopyHistory(sourceRoot, targetRoot));
            Assert(RevisionId(targetDirectory) == oldTargetRevision
                && reopened.Load(entries.Single(item => item.Id == entry.Id)).Text == "OCR 修改后\n第二行", "Failed commit damaged previous destination history.");
            Assert(StorageLocationService.CopyHistory(sourceRoot, targetRoot) == 1, "Interrupted copy did not resume.");
            Assert(File.ReadAllBytes(Path.Combine(targetDirectory, "current.json")).SequenceEqual(newerPointer)
                && reopened.Load(entries.Single(item => item.Id == entry.Id)).Text == "新的当前版本", "Active source revision did not replace stale destination pointer.");
            Assert(File.Exists(Path.Combine(targetDirectory, oldTargetRevision + ".png"))
                && File.Exists(Path.Combine(targetDirectory, oldTargetRevision + ".json")), "Migration deleted an older destination revision.");

            var stablePointer = File.ReadAllBytes(Path.Combine(targetDirectory, "current.json"));
            var currentImage = Path.Combine(sourceDirectory, RevisionId(sourceDirectory) + ".png");
            var validPng = File.ReadAllBytes(currentImage);
            File.WriteAllText(currentImage, "broken png");
            ExpectFailure(() => StorageLocationService.CopyHistory(sourceRoot, targetRoot));
            Assert(File.ReadAllBytes(Path.Combine(targetDirectory, "current.json")).SequenceEqual(stablePointer), "Corrupt source changed destination pointer.");
            File.WriteAllBytes(currentImage, validPng);
            var currentJson = Path.Combine(sourceDirectory, RevisionId(sourceDirectory) + ".json");
            var validJson = File.ReadAllBytes(currentJson);
            File.WriteAllText(currentJson, "{broken");
            ExpectFailure(() => StorageLocationService.CopyHistory(sourceRoot, targetRoot));
            File.WriteAllBytes(currentJson, validJson);
            var badPointer = JsonNode.Parse(File.ReadAllText(Path.Combine(sourceDirectory, "current.json")))!;
            badPointer["RevisionId"] = "../../outside";
            File.WriteAllText(Path.Combine(sourceDirectory, "current.json"), badPointer.ToJsonString());
            ExpectFailure(() => StorageLocationService.CopyHistory(sourceRoot, targetRoot));
            File.WriteAllBytes(Path.Combine(sourceDirectory, "current.json"), newerPointer);
            ExpectFailure(() => StorageLocationService.CopyHistory(sourceRoot, Path.Combine(sourceRoot, "nested")));
            ExpectFailure(() => StorageLocationService.CopyHistory(sourceRoot, root));
            Assert(!Directory.Exists(Path.Combine(sourceRoot, "nested")), "Nested migration created a destination before validation.");

            var conflictingRoot = Path.Combine(root, "conflicting");
            Assert(StorageLocationService.CopyHistory(sourceRoot, conflictingRoot) == 1, "Conflict fixture was not initialized.");
            var conflictDirectory = Path.Combine(conflictingRoot, "history", entry.Id);
            var conflictOriginal = Path.Combine(conflictDirectory, "source.png");
            WritePng(conflictOriginal, Image(40));
            var conflictBefore = File.ReadAllBytes(conflictOriginal);
            var conflictPointer = File.ReadAllBytes(Path.Combine(conflictDirectory, "current.json"));
            ExpectFailure(() => StorageLocationService.CopyHistory(sourceRoot, conflictingRoot));
            Assert(File.ReadAllBytes(conflictOriginal).SequenceEqual(conflictBefore)
                && File.ReadAllBytes(Path.Combine(conflictDirectory, "current.json")).SequenceEqual(conflictPointer), "Conflicting immutable original was overwritten.");
            var junctionTarget = Path.Combine(root, "junction-target");
            var junction = Path.Combine(root, "junction-link");
            Directory.CreateDirectory(junctionTarget);
            var junctionCommand = new ProcessStartInfo("cmd.exe")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            junctionCommand.ArgumentList.Add("/c"); junctionCommand.ArgumentList.Add("mklink");
            junctionCommand.ArgumentList.Add("/J"); junctionCommand.ArgumentList.Add(junction); junctionCommand.ArgumentList.Add(junctionTarget);
            using (var process = Process.Start(junctionCommand) ?? throw new InvalidOperationException("Could not start owned junction fixture."))
            {
                if (!process.WaitForExit(5000)) throw new InvalidOperationException("Owned junction fixture timed out.");
                Assert(process.ExitCode == 0, "Could not create owned junction fixture: " + process.StandardError.ReadToEnd());
            }
            try
            {
                ExpectFailure(() => StorageLocationService.EnsureWritableDataDirectory(junction));
                ExpectFailure(() => StorageLocationService.CopyHistory(sourceRoot, junction));
                ExpectFailure(() => StorageLocationService.CopyHistory(junction, Path.Combine(root, "junction-copy")));
                Assert(!Directory.EnumerateFileSystemEntries(junctionTarget).Any(), "Reparse rejection wrote into the link target.");
            }
            finally { Directory.Delete(junction); }
            Console.WriteLine("PASS: writable probe, absolute paths, background migration, editable layers/OCR reopen, source retention, orphan exclusion, merge, idempotency, locked-commit resume, active-version replacement, old-version retention, corrupt PNG/JSON, traversal/nesting, reparse rejection and immutable-original conflicts.");
            return 0;
        }
        catch (Exception exception) { Console.Error.WriteLine(exception); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static BitmapSource Image(byte red)
    {
        var pixels = new byte[64 * 48 * 4];
        for (var index = 0; index < pixels.Length; index += 4)
        { pixels[index] = 90; pixels[index + 1] = 120; pixels[index + 2] = red; pixels[index + 3] = 255; }
        var source = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 256); source.Freeze(); return source;
    }

    private static void WritePng(string path, BitmapSource image)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static string RevisionId(string directory) => JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "current.json")))!["RevisionId"]!.GetValue<string>();
    private static void ExpectFailure(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or ArgumentException)
        { return; }
        throw new InvalidOperationException("Expected invalid migration to fail explicitly.");
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
