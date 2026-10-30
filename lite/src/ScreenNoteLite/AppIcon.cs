using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ScreenNote;

/// <summary>Loads the same bundled artwork for windows and the Windows notification area.</summary>
public static class AppIcon
{
    private static readonly Uri PngResource = new("pack://application:,,,/ScreenNoteLite;component/assets/app.png", UriKind.Absolute);
    private static readonly Uri IcoResource = new("pack://application:,,,/ScreenNoteLite;component/assets/app.ico", UriKind.Absolute);
    private static readonly Lazy<BitmapFrame?> CachedWindowImage = new(LoadWindowImage);

    public static BitmapFrame? WindowImage => CachedWindowImage.Value;

    /// <summary>The caller owns this icon and should dispose it after the tray icon is disposed.</summary>
    public static Icon LoadTrayIcon()
    {
        var resource = Application.GetResourceStream(IcoResource)
            ?? throw new FileNotFoundException("拾屏托盘图标资源不存在。", IcoResource.ToString());
        using var stream = resource.Stream;
        using var loaded = new Icon(stream, new System.Drawing.Size(32, 32));
        return (Icon)loaded.Clone();
    }

    private static BitmapFrame? LoadWindowImage()
    {
        var resource = Application.GetResourceStream(PngResource);
        if (resource is null) return null;
        using var stream = resource.Stream;
        var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        image.Freeze();
        return image;
    }
}
