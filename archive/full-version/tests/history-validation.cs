using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;

internal static class HistoryValidation
{
    [STAThread]
    private static int Main()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ScreenNote-history-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var pixels = new byte[64 * 48 * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = 31; pixels[offset + 1] = 83; pixels[offset + 2] = 127; pixels[offset + 3] = 255;
            }
            var source = BitmapSource.Create(64, 48, 96, 96, PixelFormats.Bgra32, null, pixels, 256);
            source.Freeze();
            var service = new HistoryService(directory);
            var entry = service.Create(source);
            var second = service.Create(source, "打开图片");
            var surface = new AnnotationSurface();
            var tools = Enum.GetValues<AnnotationTool>().Where(tool => tool != AnnotationTool.Select);
            var json = JsonSerializer.Serialize(new
            {
                Version = 1, Width = 64, Height = 48, Color = "#FFFF0000", StrokeWidth = 3, TextSize = 22,
                Tool = AnnotationTool.Select,
                Marks = tools.Select(tool => new
                {
                    Id = Guid.NewGuid(), Tool = tool, Start = new { X = 12d, Y = 12d }, End = new { X = 40d, Y = 36d },
                    Points = new[] { new { X = 12d, Y = 12d }, new { X = 26d, Y = 20d }, new { X = 40d, Y = 36d } },
                    Color = "#FF1166CC", Width = 3, TextSize = 10, TextWidth = 40, Text = "测试\n修改文字", Number = 2
                }).ToArray()
            });
            surface.RestoreDocument(source, json);
            var rendered = surface.Export();
            var exportedJson = surface.ExportDocument();
            var thumbnailChanges = 0;
            entry.PropertyChanged += (_, args) => { if (args.PropertyName == "Thumbnail") thumbnailChanges++; };
            service.Update(entry, rendered, exportedJson, "编辑后的中文\n第二行", "原始 OCR", "上次识别结果");
            Assert(thumbnailChanges == 1, "Thumbnail update was not observable.");

            var reopened = new HistoryService(directory);
            var entries = reopened.LoadEntries();
            Assert(entries.Count == 2 && entries[0].Id == second.Id, "Persisted captures did not reopen newest first.");
            var loaded = reopened.Load(entries.Single(item => item.Id == entry.Id));
            Assert(loaded.Text == "编辑后的中文\n第二行" && loaded.OriginalOcr == "原始 OCR"
                && loaded.LastRecognized == "上次识别结果", "OCR edits did not survive reopen.");
            Assert(ReadPixels(loaded.Source).SequenceEqual(pixels), "Original image changed after annotation.");
            var restored = new AnnotationSurface();
            restored.RestoreDocument(loaded.Source, loaded.DocumentJson);
            Assert(ReadPixels(restored.Export()).SequenceEqual(ReadPixels(rendered)), "Layer restoration changed rendering.");
            Assert(ReadPixels(loaded.Rendered).SequenceEqual(ReadPixels(rendered)), "Persisted rendered image changed.");
            var savedPng = reopened.ReadRenderedPng(entries.Single(item => item.Id == entry.Id));
            using (var pngStream = new MemoryStream(savedPng))
            {
                var pngBitmap = BitmapFrame.Create(pngStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                Assert(ReadPixels(pngBitmap).SequenceEqual(ReadPixels(rendered)), "Fast PNG path did not preserve the latest annotated revision.");
            }
            ExpectThrows<IOException>(() => reopened.ReadRenderedPng(entry, savedPng.Length - 1));
            Assert(!restored.CanUndo && !restored.CanRedo, "Switching history inherited another document's undo stack.");
            var marksField = typeof(AnnotationSurface).GetField("_marks", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var marks = (System.Collections.IList)marksField.GetValue(restored)!;
            var expectedMarks = Enum.GetValues<AnnotationTool>().Length - 1;
            Assert(marks.Count == expectedMarks, "Editable layers were flattened or omitted.");
            var setSelected = typeof(AnnotationSurface).GetMethod("SetSelected", BindingFlags.NonPublic | BindingFlags.Instance)!;
            setSelected.Invoke(restored, new[] { marks[0], false });
            restored.InkColor = Colors.Green;
            Assert(restored.CanUndo, "Restored layer cannot be edited.");
            restored.Undo();
            restored.DeleteSelected();
            Assert(marks.Count == expectedMarks - 1, "Restored layer cannot be deleted.");
            restored.Undo();
            Assert(marks.Count == expectedMarks, "Restored deletion cannot be undone.");

            // Parsing must finish before replacing the user's currently visible document.
            var intact = restored.ExportDocument();
            ExpectThrows<FormatException>(() => restored.RestoreDocument(source, json.Replace("\"Width\":64", "\"Width\":999")));
            Assert(restored.ExportDocument() == intact, "Invalid history cleared the active document.");
            var invalid = JsonNode.Parse(json)!;
            invalid["Marks"]![0]!["Points"] = new JsonArray();
            ExpectThrows<FormatException>(() => restored.RestoreDocument(source, invalid.ToJsonString()));
            invalid = JsonNode.Parse(json)!;
            invalid["Marks"]![0]!["Color"] = "not a color";
            ExpectThrows<FormatException>(() => restored.RestoreDocument(source, invalid.ToJsonString()));

            // Simulate interrupted commit by preventing replacement of the pointer.
            var pointer = Path.Combine(directory, entry.Id, "current.json");
            var pointerBefore = File.ReadAllText(pointer);
            using (var locked = new FileStream(pointer, FileMode.Open, FileAccess.Read, FileShare.Read))
                ExpectBlockedCommit(() => service.Update(entry, source, exportedJson, "uncommitted", "", ""));
            Assert(File.ReadAllText(pointer) == pointerBefore, "Failed commit replaced the current revision.");
            Assert(service.Load(entry).Text == "编辑后的中文\n第二行", "Interrupted update lost the previous document.");
            // Stray incomplete writes must not appear as additional captures.
            File.WriteAllText(Path.Combine(directory, entry.Id, "orphan.tmp"), "incomplete");
            Directory.CreateDirectory(Path.Combine(directory, ".creating-" + Guid.NewGuid().ToString("N")));
            var uncommittedDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(uncommittedDirectory);
            File.Copy(Path.Combine(directory, entry.Id, "source.png"), Path.Combine(uncommittedDirectory, "source.png"));
            File.WriteAllText(Path.Combine(uncommittedDirectory, Guid.NewGuid().ToString("N") + ".json"), "{}");
            Assert(reopened.LoadEntries().Count == 2 && reopened.LoadWarnings.Count == 0,
                "An uncommitted GUID directory appeared as a capture or corruption warning.");
            Assert(File.Exists(Path.Combine(directory, entry.Id, "current.json"))
                && !Directory.GetDirectories(directory).Any(path => Path.GetFileName(path) == ".creating-" + entry.Id),
                "New captures still depend on staging-directory renames.");

            // A corrupt record is skipped, while healthy captures remain available.
            var secondPointer = Path.Combine(directory, second.Id, "current.json");
            var broken = JsonNode.Parse(File.ReadAllText(secondPointer))!;
            broken["RevisionId"] = "../../outside";
            File.WriteAllText(secondPointer, broken.ToJsonString());
            Assert(reopened.LoadEntries().Count == 1 && reopened.LoadWarnings.Count == 1,
                "Path traversal or one corrupt capture prevented loading healthy history.");
            var outside = new HistoryService(Path.Combine(directory, "other-library"));
            ExpectThrows<ArgumentException>(() => outside.Load(entry));
            var corrupt = service.Create(source);
            File.WriteAllText(Path.Combine(directory, corrupt.Id, "source.png"), "broken png");
            Assert(reopened.LoadEntries().Count == 1 && reopened.LoadWarnings.Count == 2,
                "Corrupt original image was not isolated.");
            var largePixels = new byte[1248 * 605 * 4];
            for (var offset = 0; offset < largePixels.Length; offset += 4)
            {
                largePixels[offset] = (byte)(offset % 251);
                largePixels[offset + 1] = 93;
                largePixels[offset + 2] = 175;
                largePixels[offset + 3] = 255;
            }
            var largeSource = BitmapSource.Create(1248, 605, 96, 96, PixelFormats.Bgra32, null, largePixels, 1248 * 4);
            largeSource.Freeze();
            var largeDirectory = Path.Combine(directory, "large-captures");
            var largeService = new HistoryService(largeDirectory);
            var largeEntry = largeService.Create(largeSource);
            largeService.Update(largeEntry, largeSource, "", "实际尺寸测试", "", "");
            var reopenedLarge = new HistoryService(largeDirectory);
            var reopenedLargeEntries = reopenedLarge.LoadEntries();
            var largeLoaded = reopenedLarge.Load(reopenedLargeEntries.Single());
            Assert(largeLoaded.Source.PixelWidth == 1248 && largeLoaded.Source.PixelHeight == 605
                && ReadPixels(largeLoaded.Source).SequenceEqual(largePixels)
                && largeLoaded.Text == "实际尺寸测试" && reopenedLargeEntries[0].Thumbnail.IsFrozen,
                "1248x605 capture did not survive creation, update and reopening.");
            VerifyDeletion(directory, source, rendered, exportedJson);
            Console.WriteLine("PASS: persistent screenshots, editable layers/OCR, atomic commit recovery, path validation, 1248x605 reopen, scoped deletion, restart disappearance, lock/read-only failure retention and reparse rejection.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static byte[] ReadPixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var bytes = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(bytes, converted.PixelWidth * 4, 0);
        return bytes;
    }

    private static void VerifyDeletion(string fixtureRoot, BitmapSource source, BitmapSource rendered, string layers)
    {
        var directory = Path.Combine(fixtureRoot, "deletion-library");
        var service = new HistoryService(directory);
        var preserved = service.Create(source, "需要保留的截图");
        service.Update(preserved, rendered, layers, "保留的 OCR", "保留原文", "保留识别");
        var preservedPath = Path.Combine(directory, preserved.Id);
        var preservedBefore = SnapshotFiles(preservedPath);
        var deleted = service.Create(source, "需要删除的截图");
        service.Update(deleted, rendered, layers, "删除的 OCR", "删除原文", "删除识别");
        var deletedPath = Path.Combine(directory, deleted.Id);
        var nested = Path.Combine(deletedPath, "owned-temporary-files");
        Directory.CreateDirectory(nested);
        var lockedPath = Path.Combine(nested, "locked.tmp");
        File.WriteAllText(lockedPath, "owned interrupted save");
        var deletedBefore = SnapshotFiles(deletedPath);
        using (var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            ExpectThrows<IOException>(() => service.Delete(deleted));
        CheckFilesUnchanged(deletedPath, deletedBefore, "A blocked deletion partially removed screenshot contents.");
        Assert(service.Load(deleted).Text == "删除的 OCR", "A blocked deletion damaged the editable history document.");
        CheckFilesUnchanged(preservedPath, preservedBefore, "Deletion affected another screenshot.");
        service.Delete(deleted);
        Assert(!Directory.Exists(deletedPath), "Successful deletion left the original/OCR/layers on disk.");
        var reopened = new HistoryService(directory);
        Assert(reopened.LoadEntries().Single().Id == preserved.Id && reopened.LoadWarnings.Count == 0,
            "Deleted screenshot reappeared after reopening history.");
        service.Delete(deleted);
        CheckFilesUnchanged(preservedPath, preservedBefore, "Repeated deletion affected another screenshot.");
        var foreign = new HistoryService(Path.Combine(fixtureRoot, "foreign-deletion-library"));
        ExpectThrows<ArgumentException>(() => foreign.Delete(preserved));
        var invalidEntry = new HistoryEntry(directory, "../../outside", DateTimeOffset.Now, "不合法记录", source);
        ExpectThrows<FormatException>(() => service.Delete(invalidEntry));
        CheckFilesUnchanged(preservedPath, preservedBefore, "Cross-library or invalid-ID deletion affected history.");

        var readOnly = service.Create(source, "只读截图");
        var readOnlyPath = Path.Combine(directory, readOnly.Id);
        var original = Path.Combine(readOnlyPath, "source.png");
        var originalAttributes = File.GetAttributes(original);
        var readOnlyBefore = SnapshotFiles(readOnlyPath);
        try
        {
            File.SetAttributes(original, originalAttributes | FileAttributes.ReadOnly);
            ExpectThrows<UnauthorizedAccessException>(() => service.Delete(readOnly));
            CheckFilesUnchanged(readOnlyPath, readOnlyBefore, "Read-only failure partially removed screenshot contents.");
        }
        finally { File.SetAttributes(original, originalAttributes); }

        var external = Path.Combine(fixtureRoot, "external-delete-marker");
        Directory.CreateDirectory(external);
        var marker = Path.Combine(external, "keep.txt");
        File.WriteAllText(marker, "external file must remain");
        var childLink = Path.Combine(readOnlyPath, "external-link");
        CreateJunction(childLink, external);
        try
        {
            ExpectThrows<IOException>(() => service.Delete(readOnly));
            Assert(File.ReadAllText(marker) == "external file must remain", "Deletion followed a child junction outside its record.");
        }
        finally { Directory.Delete(childLink); }
        CheckFilesUnchanged(readOnlyPath, readOnlyBefore, "Reparse failure partially removed screenshot contents.");
        var libraryAlias = Path.Combine(fixtureRoot, "deletion-library-alias");
        CreateJunction(libraryAlias, directory);
        try
        {
            var aliased = new HistoryService(libraryAlias);
            var aliasedEntry = aliased.LoadEntries().Single(entry => entry.Id == preserved.Id);
            ExpectThrows<IOException>(() => aliased.Delete(aliasedEntry));
            CheckFilesUnchanged(preservedPath, preservedBefore, "Deletion followed an ancestor junction.");
        }
        finally { Directory.Delete(libraryAlias); }
        service.Delete(readOnly);
        CheckFilesUnchanged(preservedPath, preservedBefore, "Successful later deletion affected a preserved screenshot.");
    }

    private static Dictionary<string, byte[]> SnapshotFiles(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void CheckFilesUnchanged(string directory, Dictionary<string, byte[]> expected, string message)
    {
        var actual = SnapshotFiles(directory);
        Assert(actual.Count == expected.Count && expected.All(file => actual.TryGetValue(file.Key, out var bytes)
            && file.Value.SequenceEqual(bytes)), message);
    }

    private static void CreateJunction(string link, string target)
    {
        var command = new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        command.ArgumentList.Add("/c"); command.ArgumentList.Add("mklink"); command.ArgumentList.Add("/J");
        command.ArgumentList.Add(link); command.ArgumentList.Add(target);
        using var process = Process.Start(command) ?? throw new InvalidOperationException("Could not create owned junction fixture.");
        if (!process.WaitForExit(5000)) throw new InvalidOperationException("Owned junction fixture timed out.");
        Assert(process.ExitCode == 0, "Could not create owned junction fixture: " + process.StandardError.ReadToEnd());
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void ExpectThrows<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
    }

    private static void ExpectBlockedCommit(Action action)
    {
        try { action(); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected a locked commit pointer to reject replacement.");
    }
}
