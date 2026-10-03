using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ScreenNote;

internal static class AnnotationsValidation
{
    [STAThread]
    private static int Main()
    {
        try
        {
            AnnotationSurface.VerifyRenderingAndHistory();
            var empty = new AnnotationSurface();
            empty.Measure(new Size(32, 32)); empty.Arrange(new Rect(0, 0, 32, 32));
            var blank = new RenderTargetBitmap(32, 32, 96, 96, PixelFormats.Pbgra32);
            blank.Render(empty);
            var pixels = new byte[64 * 48 * 4];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = 31;
                pixels[offset + 1] = 83;
                pixels[offset + 2] = 127;
                pixels[offset + 3] = 255;
            }
            var surface = new AnnotationSurface { Source = BitmapSource.Create(64, 48, 144, 144, PixelFormats.Bgra32, null, pixels, 256) };
            if (surface.Width != 64 || surface.Height != 48) throw new Exception("Canvas does not use source pixel dimensions.");
            var revision = surface.DocumentRevision;
            surface.ClearSelection();
            surface.Tool = AnnotationTool.Pen;
            surface.InkColor = Colors.Blue;
            surface.StrokeWidth = 5;
            if (surface.DocumentRevision != revision) throw new Exception("Selection or defaults changed the document revision.");
            var result = surface.Export();
            if (result.PixelWidth != 64 || result.PixelHeight != 48 || result.DpiX != 96 || result.DpiY != 96)
                throw new Exception("Export dimensions or DPI changed unexpectedly.");
            var actual = new byte[pixels.Length];
            result.CopyPixels(actual, 256, 0);
            for (var offset = 0; offset < pixels.Length; offset++)
                if (actual[offset] != pixels[offset]) throw new Exception("Export resampled image pixels.");
            var initial = new Rect(20, 30, 100, 80);
            var corner = CaptureWindow.ResizeEdges.Left | CaptureWindow.ResizeEdges.Top;
            var resized = CaptureWindow.ResizeSelection(initial, new Vector(-50, -50), corner, 200, 160);
            if (resized != new Rect(0, 0, 120, 110)) throw new Exception("Top/left resizing escaped canvas bounds.");
            corner = CaptureWindow.ResizeEdges.Right | CaptureWindow.ResizeEdges.Bottom;
            resized = CaptureWindow.ResizeSelection(initial, new Vector(200, 200), corner, 200, 160);
            if (resized != new Rect(20, 30, 180, 130)) throw new Exception("Bottom/right resizing escaped canvas bounds.");
            resized = CaptureWindow.ResizeSelection(initial, new Vector(-200, -200), corner, 200, 160);
            if (resized != new Rect(20, 30, 2, 2)) throw new Exception("Shrinking inverted the selection.");
            resized = CaptureWindow.ResizeSelection(initial, new Vector(200, 0), CaptureWindow.ResizeEdges.Left, 200, 160);
            if (resized != new Rect(118, 30, 2, 80)) throw new Exception("Left-edge resizing did not preserve the opposite edge.");
            var crop = CaptureWindow.ToPixelRect(initial, 200, 160, 400, 240);
            if (crop != new Int32Rect(40, 45, 200, 120)) throw new Exception("Capture pixel mapping changed under DPI scaling.");
            Console.WriteLine("PASS: editable marks, full-prefix mosaic, undo/redo, revisions, blank state, 96-DPI pixel export, selection resizing and DPI crop mapping.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
