using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace ScreenNote;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _ownsMutex;
    public static bool IsTestMode { get; private set; }
    public static string? PreviewPath { get; private set; }
    public static string DefaultDataDirectory => Path.Combine(AppContext.BaseDirectory, "data");
    public static string DataDirectory { get; private set; } = DefaultDataDirectory;
    public static string StorageStartupMessage { get; private set; } = "";
    internal static void SetDataDirectory(string path) => DataDirectory = Path.GetFullPath(path);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        IsTestMode = e.Args.Contains("--self-test") || e.Args.Contains("--preview");
        var dataArg = Array.IndexOf(e.Args, "--data-dir");
        if (dataArg >= 0 && dataArg + 1 < e.Args.Length) DataDirectory = Path.GetFullPath(e.Args[dataArg + 1]);
        var previewArg = Array.IndexOf(e.Args, "--preview");
        if (previewArg >= 0 && previewArg + 1 < e.Args.Length) PreviewPath = Path.GetFullPath(e.Args[previewArg + 1]);
        if (!IsTestMode)
        {
            _mutex = new Mutex(true, "Local\\ScreenNote.Desktop", out _ownsMutex);
            if (!_ownsMutex)
            {
                if (!DesktopActivation.TryShowExisting()) MessageBox.Show("拾屏已经在运行，请从系统托盘打开。", "拾屏");
                Shutdown();
                return;
            }
        }
        DispatcherUnhandledException += (_, args) =>
        {
            if (IsTestMode) { File.WriteAllText(Path.Combine(DataDirectory, "failure.txt"), args.Exception.ToString()); Shutdown(1); }
            else MessageBox.Show("操作未完成，内容仍留在窗口中。\n" + args.Exception.Message, "拾屏");
            args.Handled = true;
        };
        if (IsTestMode || dataArg >= 0) Directory.CreateDirectory(DataDirectory);
        else InitializeStorageLocation();
        var main = new MainWindow(e.Args);
        MainWindow = main;
        main.Show();
        var recoveryArg = Array.IndexOf(e.Args, "--recover-edit");
        if (recoveryArg >= 0 && recoveryArg + 1 < e.Args.Length) main.RestoreUpgradeBackup(e.Args[recoveryArg + 1]);
        if (e.Args.Contains("--tray") && !IsTestMode) main.Hide();
    }

    private static void InitializeStorageLocation()
    {
        var settings = SettingsService.Load();
        var legacy = SettingsService.ConfigurationDirectory;
        var previous = string.IsNullOrWhiteSpace(settings.LastDataDirectory) ? legacy : settings.LastDataDirectory;
        try
        {
            var destination = StorageLocationService.NormalizeDataDirectory(string.IsNullOrWhiteSpace(settings.StorageDirectory) ? DefaultDataDirectory : settings.StorageDirectory);
            StorageLocationService.EnsureWritableDataDirectory(destination);
            var migrated = (!settings.StorageInitialized || !StorageLocationService.IsSameDirectory(previous, destination))
                ? StorageLocationService.CopyHistory(previous, destination) : 0;
            if (!settings.StorageInitialized || !StorageLocationService.IsSameDirectory(settings.LastDataDirectory, destination))
            {
                settings.StorageInitialized = true; settings.LastDataDirectory = destination;
                SettingsService.Save(settings);
            }
            DataDirectory = destination;
            if (migrated > 0) StorageStartupMessage = $"已把 {migrated} 张历史截图迁移到新的资料文件夹，旧资料仍保留。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or FormatException)
        {
            var retained = false;
            try { StorageLocationService.EnsureWritableDataDirectory(previous); DataDirectory = Path.GetFullPath(previous); retained = true; }
            catch
            {
                // A readable library stays available even when writes fail. The settings
                // flow can migrate committed records together with unsaved in-memory edits.
                try
                {
                    if (!Directory.Exists(previous)) throw new DirectoryNotFoundException();
                    var history = Path.Combine(previous, "history");
                    if (Directory.Exists(history))
                        foreach (var pointer in Directory.EnumerateFiles(history, "current.json", SearchOption.AllDirectories))
                            using (File.OpenRead(pointer)) { }
                    DataDirectory = Path.GetFullPath(previous); retained = true;
                }
                catch
                {
                    // A separate empty recovery library avoids restoring stale legacy revisions.
                    var recoveryRoot = IsTestMode ? legacy + "-recovery"
                        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenNote-Recovery");
                    DataDirectory = Path.Combine(recoveryRoot, Guid.NewGuid().ToString("N"));
                    StorageLocationService.EnsureWritableDataDirectory(DataDirectory);
                }
            }
            StorageStartupMessage = retained
                ? "新的资料文件夹暂不可用，保留原资料位置。请在设置中选择可写文件夹：" + ex.Message
                : "原资料位置暂不可用，新的截图暂存到恢复文件夹。请在设置中检查存放位置：" + ex.Message;
            try
            {
                settings.StorageInitialized = true; settings.LastDataDirectory = DataDirectory;
                SettingsService.Save(settings);
            }
            catch (Exception saveError) { StorageStartupMessage += " 暂存位置偏好未保存：" + saveError.Message; }
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
