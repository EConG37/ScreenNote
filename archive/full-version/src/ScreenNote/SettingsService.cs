using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ScreenNote;

public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Alt+A";
    public bool PreferScreenshotHotkey { get; set; } = true;
    public int HotkeyPreferenceVersion { get; set; }
    public string OcrEngine { get; set; } = "rapid";
    public string AiTarget { get; set; } = "deepseek";
    public bool StartWithWindows { get; set; }
    public string StorageDirectory { get; set; } = "";
    public bool StorageInitialized { get; set; }
    public string LastDataDirectory { get; set; } = "";
    public string Color { get; set; } = "#EA6257";
    public double StrokeWidth { get; set; } = 3;
    public double TextSize { get; set; } = 24;
}

public static class SettingsService
{
    private static string? _testConfigurationDirectory;
    public static string ConfigurationDirectory => App.IsTestMode
        ? _testConfigurationDirectory ??= App.DataDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenNote");
    private static string FilePath => Path.Combine(ConfigurationDirectory, "settings.json");
    public static AppSettings Load()
    {
        try { return File.Exists(FilePath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { return new(); }
    }
    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(ConfigurationDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, true);
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法找到软件路径。");
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("请从已生成的 ScreenNote.exe 打开软件后设置自启。");
            key.SetValue("ScreenNote", $"\"{executable}\" --tray");
        }
        else key.DeleteValue("ScreenNote", false);
    }
    public static bool StartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return key?.GetValue("ScreenNote") is string;
    }
}
