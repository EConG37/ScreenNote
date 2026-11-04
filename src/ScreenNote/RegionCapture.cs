using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace ScreenNote;

/// <summary>Captures a screen region with the mouse cursor composited in, optionally downscaled.
/// Shared by the GIF and video recording paths.</summary>
internal sealed class RegionCapture : IDisposable
{
    private readonly Drawing.Rectangle _region;
    private readonly Drawing.Bitmap _desktop, _scaled;
    private readonly Drawing.Graphics _graphics, _resize;
    internal Drawing.Size Size { get; }
    internal RegionCapture(Drawing.Rectangle region) : this(region, GifRecorder.OutputSize(region.Size)) { }
    internal RegionCapture(Drawing.Rectangle region, Drawing.Size outputSize)
    {
        Size = outputSize; _region = region;
        _desktop = new Drawing.Bitmap(region.Width, region.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
        _scaled = new Drawing.Bitmap(Size.Width, Size.Height, Drawing.Imaging.PixelFormat.Format32bppRgb);
        _graphics = Drawing.Graphics.FromImage(_desktop); _resize = Drawing.Graphics.FromImage(_scaled);
        _resize.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    }
    internal BitmapSource NextFrame()
    {
        _graphics.CopyFromScreen(_region.Location, Drawing.Point.Empty, _region.Size, Drawing.CopyPixelOperation.SourceCopy);
        var cursor = new CursorInfo { Size = Marshal.SizeOf<CursorInfo>() };
        if (GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0 && GetIconInfo(cursor.Cursor, out var icon))
        {
            try
            {
                var hdc = _graphics.GetHdc();
                try { DrawIconEx(hdc, cursor.Position.X - _region.X - (int)icon.HotspotX, cursor.Position.Y - _region.Y - (int)icon.HotspotY, cursor.Cursor, 0, 0, 0, 0, 3); }
                finally { _graphics.ReleaseHdc(hdc); }
            }
            finally { if (icon.Mask != 0) DeleteObject(icon.Mask); if (icon.Color != 0) DeleteObject(icon.Color); }
        }
        _resize.DrawImage(_desktop, new Drawing.Rectangle(Drawing.Point.Empty, Size), 0, 0, _region.Width, _region.Height, Drawing.GraphicsUnit.Pixel);
        var data = _scaled.LockBits(new Drawing.Rectangle(Drawing.Point.Empty, Size), Drawing.Imaging.ImageLockMode.ReadOnly, Drawing.Imaging.PixelFormat.Format32bppRgb);
        try
        {
            var image = BitmapSource.Create(Size.Width, Size.Height, 96, 96, PixelFormats.Bgr32, null, data.Scan0, data.Stride * Size.Height, data.Stride);
            image.Freeze(); return image;
        }
        finally { _scaled.UnlockBits(data); }
    }
    public void Dispose() { _resize.Dispose(); _graphics.Dispose(); _scaled.Dispose(); _desktop.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { public int Size, Flags; public nint Cursor; public Drawing.Point Position; }
    [StructLayout(LayoutKind.Sequential)] private struct IconInfo { [MarshalAs(UnmanagedType.Bool)] public bool Icon; public uint HotspotX, HotspotY; public nint Mask, Color; }
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(nint icon, out IconInfo info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(nint hdc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint handle);
}
