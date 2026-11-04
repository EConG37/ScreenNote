using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using ScreenNote;

internal static class UiHistoryFlow
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly List<string> Passed = [];

    [STAThread]
    private static int Main(string[] args)
    {
        var dataDirectory = Path.GetFullPath(args.FirstOrDefault() ?? Path.Combine(".build", "ui-history-test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(dataDirectory);
        try
        {
            typeof(App).GetProperty(nameof(App.IsTestMode))!.SetValue(null, true);
            typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, dataDirectory);
            var application = new HarnessApplication { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var appSource = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "test-resources.xaml"));
            XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resourceSource = new XElement(presentation + "ResourceDictionary",
                appSource.Root!.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
                appSource.Root.Element(presentation + "Application.Resources")!.Elements());
            application.Resources = (ResourceDictionary)XamlReader.Parse(resourceSource.ToString());
            var window = new MainWindow([]);

            Verify(window);

            File.WriteAllText(Path.Combine(dataDirectory, "ui-history-test.json"), JsonSerializer.Serialize(
                new { status = "passed", checks = Passed }, new JsonSerializerOptions { WriteIndented = true }));
            application.Shutdown();
            Console.WriteLine("PASS: " + string.Join("; ", Passed));
            Console.WriteLine("Isolated history data: " + dataDirectory);
            return 0;
        }
        catch (Exception exception)
        {
            var cause = exception is TargetInvocationException { InnerException: not null } reflected ? reflected.InnerException : exception;
            File.WriteAllText(Path.Combine(dataDirectory, "failure.txt"), cause!.ToString());
            Console.Error.WriteLine(cause);
            return 1;
        }
    }

    private static void Verify(MainWindow window)
    {
        Check(Entries(window).Count == 0, "Fresh history starts empty");
        Check(Control<FrameworkElement>(window, "HistoryPanel").Visibility == Visibility.Visible
            && Control<FrameworkElement>(window, "EditorPanel").Visibility == Visibility.Collapsed,
            "Startup opens screenshot history instead of an empty editing workspace");
        Call(window, "LoadImage", CreateImage(80, 60, Colors.Red), "截图");
        var first = Field(window, "_currentHistory")!;
        Check(Entries(window).Count == 1, "A screenshot is retained immediately");
        Check(Control<FrameworkElement>(window, "EditorPanel").Visibility == Visibility.Visible
            && Control<FrameworkElement>(window, "HistoryPanel").Visibility == Visibility.Collapsed
            && Control<FrameworkElement>(window, "OcrPanel").Visibility == Visibility.Collapsed,
            "A screenshot opens a compact editor immediately with OCR hidden until requested");

        var surface = Control<AnnotationSurface>(window, "Surface");
        AddCover(surface, Colors.Blue);
        Control<TextBox>(window, "OcrText").Text = "已经修改的文字\n第二行";

        // This is the same document replacement path used by capture, paste and import.
        // It must retain edits even when the debounce timer has not fired yet.
        Call(window, "LoadImage", CreateImage(90, 70, Colors.Green), "截图");
        var second = Field(window, "_currentHistory")!;
        Check(Entries(window).Count == 2, "Replacement creates a second screenshot without losing the first");
        Check(Control<TextBox>(window, "OcrText").Text.Length == 0, "A new screenshot starts with its own OCR text");

        Call(window, "OpenHistory", first);
        Check(Control<TextBox>(window, "OcrText").Text == "已经修改的文字\n第二行", "Reopening history restores edited OCR text");
        Check(Marks(surface).Count == 1, "Reopening history restores editable annotation objects");
        Check(Pixel(surface.Export(), 8, 8) == Colors.Blue, "Reopening history preserves the annotated image");
        Check(Pixel(surface.Source!, 8, 8) == Colors.Red, "History retains the original image beneath annotations");
        Call(surface, "SetSelected", Marks(surface)[0], true);
        surface.InkColor = Colors.Yellow;
        Check(Pixel(surface.Export(), 8, 8) == Colors.Yellow, "A restored annotation can be selected and edited");
        surface.Undo();
        Check(Pixel(surface.Export(), 8, 8) == Colors.Blue, "Editing a restored annotation remains undoable");

        VerifyCanceledCapture(window, first);

        var list = Control<ListBox>(window, "HistoryList");
        list.SelectedItems.Clear();
        list.SelectedItems.Add(first);
        list.SelectedItems.Add(second);
        Control<TextBox>(window, "OcrText").Text += "\n批量选择后继续编辑";
        Check((bool)Call(window, "SaveCurrentHistory")!, "Current history saves successfully");
        Check(list.SelectedItems.Count == 2 && list.SelectedItems.Contains(first) && list.SelectedItems.Contains(second),
            "Autosave preserves multiple selected screenshots");
        var images = (byte[][])Call(window, "SelectedHistoryImages")!;
        var firstImage = DecodeImage(images[0]);
        var secondImage = DecodeImage(images[1]);
        Check(images.Length == 2 && firstImage.PixelWidth == 80 && firstImage.PixelHeight == 60
            && secondImage.PixelWidth == 90 && secondImage.PixelHeight == 70,
            "Batch delivery prepares independent screenshot files in capture order");
        Check(Pixel(firstImage, 8, 8) == Colors.Blue && Pixel(secondImage, 8, 8) == Colors.Green,
            "Batch delivery uses the latest annotated revisions of every selected screenshot");
        Call(window, "SaveCurrentHistory");
        Check(Entries(window).Count == 2, "Repeated saves update the entry instead of adding duplicates");

        Call(window, "ShowHistory");
        Check(Control<FrameworkElement>(window, "HistoryPanel").Visibility == Visibility.Visible
            && Control<FrameworkElement>(window, "EditorPanel").Visibility == Visibility.Collapsed,
            "Returning to history switches out of editing mode");
        Check(list.SelectedItems.Count == 2, "Returning to history preserves batch selection");
        Call(window, "OpenHistory", first);
        Check(Marks(surface).Count == 1, "Repeated history visits do not duplicate annotations");

        // Persist pending changes through window close, then construct a new window
        // against the same isolated directory to exercise a full restart round trip.
        Control<TextBox>(window, "OcrText").Text = "关闭前的最后修改";
        window.Close();
        DrainDispatcher();
        var restarted = new MainWindow([]);
        var savedEntries = Entries(restarted);
        Check(savedEntries.Count == 2, "Screenshot history survives application restart");
        var firstId = first.GetType().GetProperty("Id")!.GetValue(first);
        var restoredFirst = savedEntries.Single(entry => Equals(entry.GetType().GetProperty("Id")!.GetValue(entry), firstId));
        Call(restarted, "OpenHistory", restoredFirst);
        Check(Control<TextBox>(restarted, "OcrText").Text == "关闭前的最后修改", "Closing flushes pending OCR edits before restart");
        Check(Marks(Control<AnnotationSurface>(restarted, "Surface")).Count == 1, "Editable annotations survive application restart");
        Check(Pixel(Control<AnnotationSurface>(restarted, "Surface").Export(), 8, 8) == Colors.Blue,
            "Restart restores the last annotated screenshot");
        restarted.Close();
        VerifyUpgradeBackup();
        VerifyCaptureActions();
        VerifyStorageLocation();
        VerifyHistoryDeletion();
    }

    private static void VerifyHistoryDeletion()
    {
        var previousDirectory = App.DataDirectory;
        var previousIds = new HistoryService(Path.Combine(previousDirectory, "history")).LoadEntries()
            .Select(entry => entry.Id).ToHashSet();
        // Every destructive operation below targets newly generated records in this
        // workspace fixture, never the earlier migration fixtures or a user's library.
        var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".build")) + Path.DirectorySeparatorChar;
        var fixture = Path.GetFullPath(Path.Combine(allowed, "ui-delete-fixture-" + Guid.NewGuid().ToString("N")));
        if (!fixture.StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deletion fixtures must remain within workspace .build.");
        Directory.CreateDirectory(fixture);
        typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, fixture);
        MainWindow? window = null;
        try
        {
            window = new MainWindow([]);
            Check(DeleteHistory(window) == 0 && Entries(window).Count == 0
                && !Control<Button>(window, "HistoryDeleteButton").IsEnabled,
                "Deleting with no selection is a harmless zero-result operation");

            var first = CreateDeletionCapture(window, Colors.Red, "删除测试：单张");
            var single = CreateDeletionCapture(window, Colors.Green, "删除测试：非当前单张");
            var current = CreateDeletionCapture(window, Colors.Blue, "删除测试：保留编辑");
            var surface = Control<AnnotationSurface>(window, "Surface");
            var list = Control<ListBox>(window, "HistoryList");
            var store = (HistoryService)Field(window, "_history")!;
            var source = surface.Source;
            AddCover(surface, Colors.Navy);
            Control<TextBox>(window, "OcrText").Text = "删除其他图片时保留的待保存编辑";
            Check(DeleteHistory(window) == 0 && Entries(window).Count == 3 && CurrentHistory(window)?.Id == current.Id
                && ReferenceEquals(surface.Source, source) && Control<TextBox>(window, "OcrText").Text == "删除其他图片时保留的待保存编辑",
                "Deleting with no selection also leaves an existing library and its active edits unchanged");
            list.SelectedItems.Add(first); list.SelectedItems.Add(current);
            Check(Control<Button>(window, "HistoryDeleteButton").IsEnabled,
                "Selecting screenshots enables the batch-delete action");
            var row = (FrameworkElement)list.ItemTemplate.LoadContent();
            row.DataContext = first;
            row.Measure(new Size(900, 140)); row.Arrange(new Rect(0, 0, 900, 140)); row.UpdateLayout();
            Check(Descendants(row).OfType<Button>().Any(button => button.Name == "DeleteHistoryEntryButton"
                && ReferenceEquals(button.Tag, first)),
                "Each history row exposes a delete action bound to its own screenshot");

            foreach (var busyField in new[] { "_sending", "_changingStorage", "_capturing" })
            {
                SetDeletionField(window, busyField, true);
                try
                {
                    Control<TextBlock>(window, "StatusText").Text = "";
                    Check(DeleteHistory(window, first) == 0 && Entries(window).Count == 3
                        && CurrentHistory(window)?.Id == current.Id
                        && File.Exists(Path.Combine(fixture, "history", first.Id, "source.png"))
                        && Control<TextBlock>(window, "StatusText").Text.Contains("尚未结束", StringComparison.Ordinal),
                        "History deletion refuses active " + busyField + " operations without removing a screenshot");
                }
                finally { SetDeletionField(window, busyField, false); }
            }

            Check(DeleteHistory(window, single) == 1 && HistoryIds(window).SetEquals(new[] { first.Id, current.Id })
                && !Directory.Exists(Path.Combine(fixture, "history", single.Id)),
                "Single-image deletion removes exactly that screenshot and its stored files");
            Check(CurrentHistory(window)?.Id == current.Id && ReferenceEquals(surface.Source, source)
                && Marks(surface).Count == 1 && Pixel(surface.Export(), 8, 8) == Colors.Navy
                && Control<TextBox>(window, "OcrText").Text == "删除其他图片时保留的待保存编辑"
                && list.SelectedItems.Cast<HistoryEntry>().Select(entry => entry.Id).ToHashSet()
                    .SetEquals(new[] { first.Id, current.Id }),
                "Deleting another screenshot preserves the active image, annotations, OCR edits and remaining selection");

            var lockedEntry = CreateDeletionCapture(window, Colors.Gold, "删除测试：锁定失败");
            var retained = CreateDeletionCapture(window, Colors.Orange, "删除测试：批量保留");
            AddCover(surface, Colors.Purple);
            Control<TextBox>(window, "OcrText").Text = "批量删除后保留的编辑";
            list.SelectedItems.Clear();
            foreach (var entry in new[] { first, lockedEntry, retained }) list.SelectedItems.Add(entry);
            var retainedSource = surface.Source;
            using (var locked = new FileStream(Path.Combine(fixture, "history", lockedEntry.Id, "source.png"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(DeleteHistory(window, first, lockedEntry) == 1
                    && HistoryIds(window).SetEquals(new[] { current.Id, lockedEntry.Id, retained.Id })
                    && list.SelectedItems.Cast<HistoryEntry>().Select(entry => entry.Id).ToHashSet()
                        .SetEquals(new[] { lockedEntry.Id, retained.Id }),
                    "A batch deletes successful records while keeping failed records and other selected screenshots");
                var result = Control<TextBlock>(window, "HistoryResult").Text;
                Check(result.Contains("已删除 1 张", StringComparison.Ordinal)
                    && result.Contains("1 张删除失败", StringComparison.Ordinal),
                    "Partial deletion reports both the one successful deletion and the one failed deletion");
                var readable = store.Load(lockedEntry);
                Check(readable.Source.PixelWidth == 80 && Pixel(readable.Source, 8, 8) == Colors.Gold
                    && CurrentHistory(window)?.Id == retained.Id && ReferenceEquals(surface.Source, retainedSource)
                    && Pixel(surface.Export(), 8, 8) == Colors.Purple
                    && Control<TextBox>(window, "OcrText").Text == "批量删除后保留的编辑",
                    "A locked deletion failure leaves its record readable and preserves a different active editor");
            }

            var foreignStore = new HistoryService(Path.Combine(fixture, "foreign-library", "history"));
            var foreign = foreignStore.Create(CreateImage(80, 60, Colors.White), "删除测试：外部资料库");
            var beforeForeign = HistoryIds(window);
            Check(DeleteHistory(window, foreign) == 0 && HistoryIds(window).SetEquals(beforeForeign)
                && foreignStore.LoadEntries().Single().Id == foreign.Id
                && Pixel(foreignStore.Load(foreign).Source, 8, 8) == Colors.White
                && Control<TextBlock>(window, "HistoryResult").Text.Contains("失败", StringComparison.Ordinal),
                "An entry from another library is reported as a failure without deleting any foreign or current data");

            Call(window, "OpenHistory", lockedEntry);
            AddCover(surface, Colors.Crimson);
            Control<TextBox>(window, "OcrText").Text = "当前图片删除失败时保留的编辑";
            source = surface.Source;
            using (var locked = new FileStream(Path.Combine(fixture, "history", lockedEntry.Id, "source.png"),
                FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Check(DeleteHistory(window, lockedEntry) == 0 && CurrentHistory(window)?.Id == lockedEntry.Id
                    && ReferenceEquals(surface.Source, source) && Marks(surface).Count == 1
                    && Control<TextBox>(window, "OcrText").Text == "当前图片删除失败时保留的编辑"
                    && store.Load(lockedEntry).Source.PixelWidth == 80,
                    "Failed deletion of the active screenshot keeps the editor and committed record intact");
            }

            using var recognition = new CancellationTokenSource();
            var recognitionToken = recognition.Token;
            var generation = (int)Field(window, "_documentGeneration")!;
            SetDeletionField(window, "_recognition", recognition);
            SetDeletionField(window, "_recognizing", true);
            SetDeletionField(window, "_originalText", "原始识别文字");
            SetDeletionField(window, "_lastRecognizedText", "最后识别文字");
            Control<TextBox>(window, "OcrText").IsReadOnly = true;
            Check(DeleteHistory(window, lockedEntry) == 1 && recognitionToken.IsCancellationRequested
                && (int)Field(window, "_documentGeneration")! > generation
                && !(bool)Field(window, "_recognizing")!,
                "Deleting the active screenshot cancels its pending OCR and invalidates stale recognition results");
            Check(CurrentHistory(window) == null && surface.Source == null && Marks(surface).Count == 0
                && Control<TextBox>(window, "OcrText").Text.Length == 0
                && !Control<TextBox>(window, "OcrText").IsReadOnly
                && ((string)Field(window, "_originalText")!).Length == 0
                && ((string)Field(window, "_lastRecognizedText")!).Length == 0
                && !(bool)Field(window, "_historyDirty")! && !((DispatcherTimer)Field(window, "_autoSaveTimer")!).IsEnabled
                && Control<FrameworkElement>(window, "HistoryPanel").Visibility == Visibility.Visible
                && Control<FrameworkElement>(window, "EditorPanel").Visibility == Visibility.Collapsed,
                "Deleting the active screenshot clears editable layers and OCR, stops autosave and returns to history");
            Check((bool)Call(window, "SaveCurrentHistory")! && HistoryIds(window).SetEquals(new[] { current.Id, retained.Id })
                && store.LoadEntries().Select(entry => entry.Id).ToHashSet().SetEquals(new[] { current.Id, retained.Id })
                && list.SelectedItems.Cast<HistoryEntry>().Select(entry => entry.Id).SequenceEqual(new[] { retained.Id }),
                "An autosave callback cannot recreate the deleted document and preserves surviving selections");

            window.Close(); window = null; DrainDispatcher();
            window = new MainWindow([]);
            Check(HistoryIds(window).SetEquals(new[] { current.Id, retained.Id })
                && !Directory.Exists(Path.Combine(fixture, "history", first.Id))
                && !Directory.Exists(Path.Combine(fixture, "history", single.Id))
                && !Directory.Exists(Path.Combine(fixture, "history", lockedEntry.Id)),
                "Closing and reopening history never resurrects successfully deleted screenshots");
            var reopenedRetained = Entries(window).Cast<HistoryEntry>().Single(entry => entry.Id == retained.Id);
            Call(window, "OpenHistory", reopenedRetained);
            Check(Control<TextBox>(window, "OcrText").Text == "批量删除后保留的编辑"
                && Pixel(Control<AnnotationSurface>(window, "Surface").Export(), 8, 8) == Colors.Purple,
                "A screenshot retained through partial deletion keeps its edits after restart");
            list = Control<ListBox>(window, "HistoryList"); list.SelectAll();
            Check(DeleteHistory(window, list.SelectedItems.Cast<HistoryEntry>().ToArray()) == 2
                && Entries(window).Count == 0 && list.SelectedItems.Count == 0
                && Control<FrameworkElement>(window, "EmptyHistoryPanel").Visibility == Visibility.Visible
                && Control<FrameworkElement>(window, "HistoryPanel").Visibility == Visibility.Visible
                && !Control<Button>(window, "HistoryDeleteButton").IsEnabled
                && !Control<Button>(window, "HistorySendButton").IsEnabled
                && !Control<Button>(window, "HistoryExportButton").IsEnabled,
                "Deleting every selected screenshot displays the empty state and disables all selection actions");
            Check((bool)Call(window, "SaveCurrentHistory")! && ((HistoryService)Field(window, "_history")!).LoadEntries().Count == 0,
                "Saving after deleting the entire library cannot re-create the former active image");
            window.Close(); window = null; DrainDispatcher();
            window = new MainWindow([]);
            Check(Entries(window).Count == 0 && CurrentHistory(window) == null
                && Control<FrameworkElement>(window, "EmptyHistoryPanel").Visibility == Visibility.Visible,
                "An entirely deleted history remains empty after application restart");
            Check(new HistoryService(Path.Combine(previousDirectory, "history")).LoadEntries()
                    .Select(entry => entry.Id).ToHashSet().SetEquals(previousIds),
                "Deletion tests leave the earlier history and migration fixtures untouched");
        }
        finally
        {
            try { window?.Close(); }
            finally { typeof(App).GetProperty(nameof(App.DataDirectory))!.SetValue(null, previousDirectory); }
        }
    }

    private static HistoryEntry CreateDeletionCapture(MainWindow window, Color color, string origin)
    {
        Call(window, "LoadImage", CreateImage(80, 60, color), origin);
        return CurrentHistory(window) ?? throw new InvalidOperationException("Synthetic deletion screenshot was not retained.");
    }

    private static int DeleteHistory(MainWindow window, params HistoryEntry[] entries) =>
        (int)Call(window, "DeleteHistoryEntries", (object)entries)!;

    private static HistoryEntry? CurrentHistory(MainWindow window) =>
        typeof(MainWindow).GetField("_currentHistory", InstanceMembers)!.GetValue(window) as HistoryEntry;

    private static HashSet<string> HistoryIds(MainWindow window) => Entries(window).Cast<HistoryEntry>()
        .Select(entry => entry.Id).ToHashSet();

    private static void SetDeletionField(MainWindow window, string field, object? value) =>
        typeof(MainWindow).GetField(field, InstanceMembers)!.SetValue(window, value);

    private static void VerifyStorageLocation()
    {
        var source = App.DataDirectory;
        var configuration = SettingsService.ConfigurationDirectory;
        var destination = source + "-moved";
        var window = new MainWindow([]);
        var originalEntries = Entries(window).Cast<HistoryEntry>().ToArray();
        var first = originalEntries[0];
        Call(window, "OpenHistory", first);
        var list = Control<ListBox>(window, "HistoryList"); list.SelectedItems.Clear();
        foreach (var entry in originalEntries.Take(2)) list.SelectedItems.Add(entry);
        var selected = list.SelectedItems.Cast<HistoryEntry>().Select(entry => entry.Id).ToHashSet();
        var surface = Control<AnnotationSurface>(window, "Surface");
        var expectedPixel = Pixel(surface.Export(), 8, 8); var expectedMarks = Marks(surface).Count;
        Control<TextBox>(window, "OcrText").Text = "迁移前尚未自动保存的修改";
        var migration = (Task<int>)Call(window, "ChangeStorageLocationAsync", destination, null)!;
        WaitTask(migration);
        Check(migration.Result == originalEntries.Length && App.DataDirectory == destination,
            "Folder switch migrates every screenshot and activates the selected directory");
        Check(SettingsService.ConfigurationDirectory == configuration && SettingsService.Load().StorageDirectory == destination,
            "Folder preference commits independently from screenshot storage");
        var rebound = (HistoryEntry)Field(window, "_currentHistory")!;
        Check(rebound.Id == first.Id && !ReferenceEquals(rebound, first)
            && list.SelectedItems.Cast<HistoryEntry>().Select(entry => entry.Id).ToHashSet().SetEquals(selected),
            "Folder switch rebinds the current document and preserves batch selection");
        Check(Marks(surface).Count == expectedMarks && Pixel(surface.Export(), 8, 8) == expectedPixel
            && Control<TextBox>(window, "OcrText").Text == "迁移前尚未自动保存的修改",
            "Folder switch preserves the active image, editable annotations and pending OCR changes");
        var oldStore = new HistoryService(Path.Combine(source, "history"));
        var oldRecord = oldStore.LoadEntries().Single(entry => entry.Id == first.Id);
        Check(oldStore.Load(oldRecord).Text == "迁移前尚未自动保存的修改"
            && oldStore.LoadEntries().Count == originalEntries.Length,
            "Migration flushes pending edits and retains the entire source library");
        Control<TextBox>(window, "OcrText").Text = "迁移后的最新修改";
        Check((bool)Call(window, "SaveCurrentHistory")!
            && ((HistoryService)Field(window, "_history")!).Load(rebound).Text == "迁移后的最新修改"
            && oldStore.Load(oldRecord).Text == "迁移前尚未自动保存的修改",
            "Subsequent edits save to the new folder while the original backup remains unchanged");

        var beforeSettings = File.ReadAllBytes(Path.Combine(configuration, "settings.json"));
        var nested = (Task<int>)Call(window, "ChangeStorageLocationAsync", Path.Combine(destination, "nested"), null)!;
        ExpectFailure(nested);
        Check(App.DataDirectory == destination && File.ReadAllBytes(Path.Combine(configuration, "settings.json")).SequenceEqual(beforeSettings)
            && Entries(window).Count == originalEntries.Length,
            "A rejected destination preserves the active library and saved preference");
        using (var locked = new FileStream(Path.Combine(configuration, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var uncommitted = (Task<int>)Call(window, "ChangeStorageLocationAsync", source + "-settings-failure", null)!;
            ExpectFailure(uncommitted);
        }
        Check(App.DataDirectory == destination && ReferenceEquals(Field(window, "_currentHistory"), rebound)
            && File.ReadAllBytes(Path.Combine(configuration, "settings.json")).SequenceEqual(beforeSettings),
            "A settings commit failure leaves the original active folder and document intact");

        window.Close(); DrainDispatcher();
        typeof(App).GetMethod("InitializeStorageLocation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        var restarted = new MainWindow([]);
        Check(App.DataDirectory == destination && Entries(restarted).Count == originalEntries.Length,
            "Startup restores the chosen directory without reimporting a stale backup");
        Call(restarted, "OpenHistory", Entries(restarted).Cast<HistoryEntry>().Single(entry => entry.Id == first.Id));
        Check(Control<TextBox>(restarted, "OcrText").Text == "迁移后的最新修改",
            "Latest edits in the chosen folder survive restart");
        Control<TextBox>(restarted, "OcrText").Text = "原位置无法保存时仍可迁移内存草稿";
        var restoredActive = (HistoryEntry)Field(restarted, "_currentHistory")!;
        using (var locked = new FileStream(Path.Combine(destination, "history", restoredActive.Id, "current.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var rescued = (Task<int>)Call(restarted, "ChangeStorageLocationAsync", source + "-rescued", null)!;
            WaitTask(rescued);
        }
        var rescuedStore = (HistoryService)Field(restarted, "_history")!;
        Check(App.DataDirectory == source + "-rescued"
            && rescuedStore.Load((HistoryEntry)Field(restarted, "_currentHistory")!).Text == "原位置无法保存时仍可迁移内存草稿",
            "When the original folder cannot commit, migration saves pending edits directly to the new folder");
        var previousActual = App.DataDirectory;
        var temporarilyBlocked = source + "-temporarily-unavailable";
        File.WriteAllText(temporarilyBlocked, "Blocking file for isolated startup test");
        var preference = SettingsService.Load(); preference.StorageDirectory = temporarilyBlocked;
        SettingsService.Save(preference);
        typeof(App).GetMethod("InitializeStorageLocation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        Check(App.DataDirectory == previousActual && SettingsService.Load().LastDataDirectory == previousActual,
            "Unavailable preferred folder retains the readable library and tracks the actual fallback location");
        Call(restarted, "LoadImage", CreateImage(40, 30, Colors.Turquoise), "恢复期间截图");
        var fallbackCaptureId = ((HistoryEntry)Field(restarted, "_currentHistory")!).Id;
        restarted.Close(); File.Delete(temporarilyBlocked);
        typeof(App).GetMethod("InitializeStorageLocation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
        restarted = new MainWindow([]);
        Check(App.DataDirectory == temporarilyBlocked
            && Entries(restarted).Cast<HistoryEntry>().Any(entry => entry.Id == fallbackCaptureId)
            && Entries(restarted).Count == originalEntries.Length + 1,
            "When the preferred folder recovers, captures made in the fallback library migrate with it");
        Call(restarted, "OpenHistory", Entries(restarted).Cast<HistoryEntry>().Single(entry => entry.Id == first.Id));
        Check(Control<TextBox>(restarted, "OcrText").Text == "原位置无法保存时仍可迁移内存草稿",
            "Recovery migration preserves the latest edited document");
        restarted = VerifyRepeatedOfflineRecovery(restarted, source, first.Id);
        VerifySettingsLayout(restarted, source);
        restarted.Close();
    }

    private static MainWindow VerifyRepeatedOfflineRecovery(MainWindow window, string fixtureRoot, string originalId)
    {
        var drive = fixtureRoot + "-offline-drive";
        var library = Path.Combine(drive, "library");
        WaitTask((Task<int>)Call(window, "ChangeStorageLocationAsync", library, null)!);
        var expectedText = Control<TextBox>(window, "OcrText").Text;
        var originalCount = Entries(window).Count;
        window.Close(); var firstRecovery = "";
        for (var round = 0; round < 2; round++)
        {
            var parked = drive + "-parked-" + round;
            var allowed = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".build")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(drive).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFullPath(parked).StartsWith(allowed, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Offline fixture moves must remain within workspace .build.");
            Directory.Move(drive, parked); File.WriteAllText(drive, "Simulated unavailable drive");
            try
            {
                typeof(App).GetMethod("InitializeStorageLocation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
                Check(App.DataDirectory.StartsWith(SettingsService.ConfigurationDirectory + "-recovery" + Path.DirectorySeparatorChar)
                    && App.DataDirectory != firstRecovery && SettingsService.Load().LastDataDirectory == App.DataDirectory,
                    "Offline round " + (round + 1) + " creates and remembers an independent recovery library");
                if (round == 0) firstRecovery = App.DataDirectory;
                var recovery = new MainWindow([]);
                Check(Entries(recovery).Count == 0, "New offline recovery excludes stale backup revisions");
                Call(recovery, "LoadImage", CreateImage(42, 32, Colors.Gold), "离线期间新截图");
                recovery.Close();
            }
            finally { File.Delete(drive); Directory.Move(parked, drive); }
            typeof(App).GetMethod("InitializeStorageLocation", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null);
            window = new MainWindow([]);
            Check(Entries(window).Count == originalCount + round + 1,
                "Recovered library retains all captures from offline round " + (round + 1));
            Call(window, "OpenHistory", Entries(window).Cast<HistoryEntry>().Single(entry => entry.Id == originalId));
            Check(Control<TextBox>(window, "OcrText").Text == expectedText,
                "Offline recovery never replaces a newer saved revision with a stale copy");
            if (round == 0)
            {
                expectedText = "第一轮恢复后在正式库更新的最新版本";
                Control<TextBox>(window, "OcrText").Text = expectedText;
                Check((bool)Call(window, "SaveCurrentHistory")!, "Official library saves the newer revision before the second offline round");
                window.Close();
            }
        }
        return window;
    }

    private static void VerifySettingsLayout(MainWindow window, string reportDirectory)
    {
        Exception? failure = null; var inspected = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += async (_, _) =>
        {
            var dialog = System.Windows.Application.Current.Windows.OfType<Window>().FirstOrDefault(item => item.Title == "拾屏设置");
            if (dialog == null) return;
            timer.Stop();
            try
            {
                dialog.UpdateLayout();
                var elements = Descendants((DependencyObject)dialog.Content).OfType<FrameworkElement>().ToArray();
                Check(elements.OfType<TextBox>().Any(item => item.Name == "StorageDirectoryInput" && item.Text == App.DataDirectory)
                    && elements.OfType<Button>().Any(item => item.Name == "ChooseStorageButton")
                    && elements.OfType<TextBlock>().Any(item => item.Text.Contains("卸载软件会同步删除")),
                    "Settings show the selected folder, folder picker and uninstall data warning");
                var save = elements.OfType<Button>().Single(item => item.Name == "SaveSettingsButton");
                var saveBounds = save.TransformToAncestor((Visual)dialog.Content).TransformBounds(new Rect(save.RenderSize));
                Check(save.ActualHeight > 0 && saveBounds.Bottom <= ((FrameworkElement)dialog.Content).ActualHeight
                    && elements.OfType<ScrollViewer>().Any(item => item.VerticalScrollBarVisibility == ScrollBarVisibility.Auto),
                    "Settings keep Save visible and scroll remaining options on smaller screens");
                var visual = (FrameworkElement)dialog.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(reportDirectory, "settings-preview.png")); encoder.Save(output);
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var savedDeadline = DateTime.UtcNow.AddSeconds(5);
                while (!save.IsEnabled && DateTime.UtcNow < savedDeadline) await Task.Delay(20);
                var feedback = elements.OfType<TextBlock>().Single(item => item.Name == "SettingsFeedback");
                Check(dialog.IsVisible && feedback.Text.Contains("设置已保存") && save.IsEnabled,
                    "Saving settings keeps the dialog open and shows successful feedback with Save ready again");
                var combination = elements.OfType<TextBox>().Single(item => item.Name == "ShortcutInput");
                Check(combination.IsReadOnly, "The shortcut control records key events rather than accepting typed labels");
                var keyEvent = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                    System.Windows.PresentationSource.FromVisual(combination), 0, System.Windows.Input.Key.F8)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                combination.RaiseEvent(keyEvent);
                Check(keyEvent.Handled && combination.Text == "F8", "A real keyboard event maps its virtual key into the recorded shortcut");
                var engineChoice = elements.OfType<ComboBox>().Single(item => item.Name == "OcrEngineChoice");
                engineChoice.SelectedIndex = 1;
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!save.IsEnabled && DateTime.UtcNow < savedDeadline) await Task.Delay(20);
                Check(dialog.IsVisible && SettingsService.Load().Hotkey == "F8"
                    && SettingsService.Load().OcrEngine == "windows" && feedback.Text.Contains("设置已保存"),
                    "The open settings dialog saves a recorded key and the selected OCR engine together");
                var storage = elements.OfType<TextBox>().Single(item => item.Name == "StorageDirectoryInput");
                var activeDirectory = App.DataDirectory; storage.Text = Path.Combine(activeDirectory, "invalid-nested");
                save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (!save.IsEnabled && DateTime.UtcNow < savedDeadline) await Task.Delay(20);
                Check(dialog.IsVisible && App.DataDirectory == activeDirectory && feedback.Text.Contains("设置未保存"),
                    "A failed settings save remains open and retains the last committed folder");
                Check(elements.OfType<Button>().Any(item => item.Name == "CloseSettingsButton" && Equals(item.Content, "关闭")),
                    "Settings provide an explicit Close action after successful or failed saves");
                inspected = true;
            }
            catch (Exception ex) { failure = ex; }
            finally { dialog.Close(); }
        };
        timer.Start();
        try { Call(window, "ShowSettings"); }
        finally { timer.Stop(); }
        if (failure != null) throw failure;
        if (!inspected) throw new InvalidOperationException("Settings dialog was not inspected.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void WaitTask(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { DrainDispatcher(); Thread.Sleep(10); }
        if (!task.IsCompleted) throw new TimeoutException("Folder migration did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static void ExpectFailure(Task task)
    {
        try { WaitTask(task); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Expected folder migration failure.");
    }

    private static void VerifyCaptureActions()
    {
        // Materialize the clipboard before replacing it. Keeping only its lazy
        // IDataObject could lose the original provider when SetImage clears it.
        var originalClipboard = SnapshotClipboard();
        var window = new MainWindow([]);
        try
        {
            var countBefore = Entries(window).Count;
            Call(window, "CompleteCapture", CreateImage(100, 64, Colors.Orange), CaptureAction.Edit);
            var oldEntry = (HistoryEntry)Field(window, "_currentHistory")!;
            var surface = Control<AnnotationSurface>(window, "Surface");
            var oldSource = surface.Source;
            Check(Entries(window).Count == countBefore + 1 && window.IsVisible
                && Control<FrameworkElement>(window, "EditorPanel").Visibility == Visibility.Visible,
                "Choosing Edit retains the screenshot and opens its editor");
            AddCover(surface, Colors.Navy);
            Control<TextBox>(window, "OcrText").Text = "旧截图的未完成编辑";

            Call(window, "CompleteCapture", CreateImage(64, 40, Colors.Magenta), CaptureAction.Copy);
            var copiedEntry = Entries(window).Cast<HistoryEntry>().First();
            var store = (HistoryService)Field(window, "_history")!;
            var copiedDocument = store.Load(copiedEntry);
            Check(Entries(window).Count == countBefore + 2 && copiedEntry.Id != oldEntry.Id
                && copiedDocument.Source.PixelWidth == 64 && copiedDocument.Source.PixelHeight == 40
                && Pixel(copiedDocument.Rendered, 8, 8) == Colors.Magenta,
                "Choosing Copy retains the new raw screenshot in history");
            Check(ReferenceEquals(Field(window, "_currentHistory"), oldEntry)
                && ReferenceEquals(surface.Source, oldSource) && Marks(surface).Count == 1
                && Pixel(surface.Export(), 8, 8) == Colors.Navy
                && Control<TextBox>(window, "OcrText").Text == "旧截图的未完成编辑",
                "Choosing Copy preserves the previous editing image, annotations and OCR text");
            var copiedClipboard = System.Windows.Clipboard.GetImage();
            Check(!window.IsVisible && copiedClipboard != null
                && copiedClipboard.PixelWidth == 64 && copiedClipboard.PixelHeight == 40
                && Pixel(copiedClipboard, 8, 8) == Colors.Magenta,
                "Choosing Copy places the raw image on the clipboard and keeps the editor hidden");
            var oldDocument = store.Load(oldEntry);
            Check(oldDocument.Text == "旧截图的未完成编辑" && Pixel(oldDocument.Rendered, 8, 8) == Colors.Navy,
                "Choosing Copy flushes previous pending edits to their own history entry");

            Call(window, "CompleteCapture", CreateImage(96, 72, Colors.Cyan), CaptureAction.Edit);
            Check(Entries(window).Count == countBefore + 3 && window.IsVisible
                && !ReferenceEquals(Field(window, "_currentHistory"), oldEntry)
                && surface.Source!.PixelWidth == 96 && surface.Source.PixelHeight == 72
                && Pixel(surface.Export(), 8, 8) == Colors.Cyan && Control<TextBox>(window, "OcrText").Text.Length == 0,
                "Choosing Edit after Copy opens a fresh document without losing the old history");
            var afterEditing = System.Windows.Clipboard.GetImage();
            Check(afterEditing != null && afterEditing.PixelWidth == 64 && Pixel(afterEditing, 8, 8) == Colors.Magenta,
                "Choosing Edit leaves the existing clipboard image unchanged");
        }
        finally
        {
            window.Close();
            if (originalClipboard == null) System.Windows.Clipboard.Clear();
            else System.Windows.Clipboard.SetDataObject(originalClipboard, true);
        }
    }

    private static DataObject? SnapshotClipboard()
    {
        var original = System.Windows.Clipboard.GetDataObject();
        if (original == null) return null;
        var copy = new DataObject();
        foreach (var format in original.GetFormats(false))
        {
            var value = original.GetData(format, false);
            if (value == null) throw new InvalidOperationException("Cannot preserve clipboard format: " + format);
            if (value is BitmapSource bitmap)
            {
                var clone = bitmap.CloneCurrentValue();
                clone.Freeze();
                value = clone;
            }
            else if (value is MemoryStream memory) value = new MemoryStream(memory.ToArray());
            else if (value is Array array) value = array.Clone();
            else if (value is System.Drawing.Bitmap drawing) value = drawing.Clone();
            copy.SetData(format, value, false);
        }
        return copy;
    }

    private static void VerifyUpgradeBackup()
    {
        var backupDirectory = Path.Combine(App.DataDirectory, "upgrade-fixture");
        Directory.CreateDirectory(backupDirectory);
        var imageFile = Path.Combine(backupDirectory, "image.png");
        var textFile = Path.Combine(backupDirectory, "text.txt");
        var expectedText = "升级前已编辑的文字\n保留中文与换行";
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(CreateImage(72, 54, Colors.Purple)));
        using (var stream = File.Create(imageFile)) encoder.Save(stream);
        File.WriteAllText(textFile, expectedText);

        var importer = new MainWindow([]);
        Call(importer, "RestoreUpgradeBackup", backupDirectory);
        var restored = (HistoryEntry)Field(importer, "_currentHistory")!;
        Check(Entries(importer).Count == 3 && restored.Origin == "升级前编辑",
            "Upgrade backup creates one additional labelled history entry");
        var saved = ((HistoryService)Field(importer, "_history")!).Load(restored);
        Check(saved.Rendered.PixelWidth == 72 && saved.Rendered.PixelHeight == 54
            && Pixel(saved.Rendered, 8, 8) == Colors.Purple,
            "Upgrade restore retains the completed backup image pixels and dimensions");
        Check(saved.Text == expectedText && Control<TextBox>(importer, "OcrText").Text == expectedText,
            "Upgrade restore immediately persists edited OCR text");
        Check(Control<FrameworkElement>(importer, "HistoryPanel").Visibility == Visibility.Visible
            && Control<FrameworkElement>(importer, "EditorPanel").Visibility == Visibility.Collapsed,
            "Upgrade restore finishes in the screenshot gallery");
        Check(File.Exists(imageFile) && File.ReadAllText(textFile) == expectedText,
            "Upgrade restore leaves its backup files available");
        importer.Close();

        var restarted = new MainWindow([]);
        var restoredAgain = Entries(restarted).Cast<HistoryEntry>().Single(entry => entry.Id == restored.Id);
        Check(Entries(restarted).Count == 3 && restoredAgain.Origin == "升级前编辑",
            "Upgrade-restored history survives application restart");
        Call(restarted, "OpenHistory", restoredAgain);
        Check(Control<TextBox>(restarted, "OcrText").Text == expectedText
            && Pixel(Control<AnnotationSurface>(restarted, "Surface").Export(), 8, 8) == Colors.Purple,
            "Upgrade-restored image and edited text reopen after restart");
        restarted.Close();

        // Reproduce interruption after the initial image/index commit but before
        // the edited document/text commit, then retry against the same backup.
        var store = new HistoryService(Path.Combine(App.DataDirectory, "history"));
        var unfinished = store.Create(DecodeImage(File.ReadAllBytes(imageFile)), "升级前编辑");
        var retry = new MainWindow([]);
        Call(retry, "RestoreUpgradeBackup", backupDirectory);
        var recovered = (HistoryEntry)Field(retry, "_currentHistory")!;
        var recoveredDocument = store.Load(recovered);
        Check(Entries(retry).Count == 4 && recovered.Id == unfinished.Id
            && recoveredDocument.Text == expectedText && Pixel(recoveredDocument.Rendered, 8, 8) == Colors.Purple,
            "Retrying an unfinished upgrade import completes the existing entry without duplicating it");
        retry.Close();
    }

    private static object? Call(object target, string method, params object?[] args)
    {
        var candidates = target.GetType().GetMethods(InstanceMembers).Where(candidate => candidate.Name == method && candidate.GetParameters().Length == args.Length).ToArray();
        if (candidates.Length != 1) throw new MissingMethodException(target.GetType().Name, method);
        return candidates[0].Invoke(target, args);
    }

    private static object? Field(object target, string field) => target.GetType().GetField(field, InstanceMembers)?.GetValue(target)
        ?? throw new MissingFieldException(target.GetType().Name, field);

    private static T Control<T>(MainWindow window, string name) where T : class => window.FindName(name) as T
        ?? throw new InvalidOperationException("UI control not found: " + name);

    private static List<object> Entries(MainWindow window) => ((IEnumerable)Field(window, "_historyEntries")!).Cast<object>().ToList();
    private static IList Marks(AnnotationSurface surface) => (IList)Field(surface, "_marks")!;

    private static void AddCover(AnnotationSurface surface, Color color)
    {
        var markType = typeof(AnnotationSurface).GetNestedType("Mark", BindingFlags.NonPublic)!;
        var mark = Activator.CreateInstance(markType, true)!;
        markType.GetProperty("Tool")!.SetValue(mark, AnnotationTool.Cover);
        markType.GetProperty("Start")!.SetValue(mark, new Point(2, 2));
        markType.GetProperty("End")!.SetValue(mark, new Point(25, 25));
        markType.GetProperty("Color")!.SetValue(mark, color);
        Marks(surface).Add(mark);
        Call(surface, "NotifyChange", true);
    }

    private static BitmapSource CreateImage(int width, int height, Color color)
    {
        var pixels = new byte[width * height * 4];
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = color.B; pixels[offset + 1] = color.G; pixels[offset + 2] = color.R; pixels[offset + 3] = color.A;
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private static Color Pixel(BitmapSource image, int x, int y)
    {
        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        converted.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    private static BitmapSource DecodeImage(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static void VerifyCanceledCapture(MainWindow window, object current)
    {
        // Opening the selector while old OCR or browser delivery is in progress must
        // still work. Dismiss the selector in code so no desktop image is saved.
        using var recognition = new CancellationTokenSource();
        var token = recognition.Token;
        typeof(MainWindow).GetField("_recognition", InstanceMembers)!.SetValue(window, recognition);
        typeof(MainWindow).GetField("_recognizing", InstanceMembers)!.SetValue(window, true);
        typeof(MainWindow).GetField("_sending", InstanceMembers)!.SetValue(window, true);
        Task? capture = null;
        var openedSelector = false;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) =>
        {
            var selector = System.Windows.Application.Current.Windows.OfType<CaptureWindow>().FirstOrDefault(candidate => candidate.IsVisible);
            if (selector != null)
            {
                openedSelector = true;
                selector.DialogResult = false;
            }
            if (capture?.IsCompleted == true || DateTime.UtcNow >= deadline) frame.Continue = false;
        };
        Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => capture = (Task)Call(window, "CaptureAsync")!));
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally
        {
            timer.Stop();
            typeof(MainWindow).GetField("_sending", InstanceMembers)!.SetValue(window, false);
        }
        if (capture?.IsCompleted != true) throw new TimeoutException("Screenshot selection did not complete within the bounded test.");
        capture.GetAwaiter().GetResult();
        Check(openedSelector, "New capture starts while OCR and browser delivery are pending");
        Check(token.IsCancellationRequested, "Starting a new capture cancels the previous OCR request");
        Check(Entries(window).Count == 2 && ReferenceEquals(Field(window, "_currentHistory"), current),
            "Canceling a screenshot preserves the current document and creates no history entry");
        Check(Control<TextBox>(window, "OcrText").Text == "已经修改的文字\n第二行",
            "Canceling a screenshot retains the current edited OCR text");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException("History UI flow failed: " + name);
        Passed.Add(name);
        Console.WriteLine("PASS: " + name);
    }

    // Application schedules Startup as soon as its dispatcher is pumped. Suppress
    // the real app's startup path so the running user process, tray and mutex stay
    // completely separate from this isolated test application.
    private sealed class HarnessApplication : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e) { }
    }
}
