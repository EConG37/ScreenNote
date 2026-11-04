using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading.Tasks;
using System.Windows;

namespace ScreenNote;

internal static class GifClipboard
{
    internal static DataObject CreateData(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new FileNotFoundException("GIF 文件已被移动或删除。", path);
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        data.SetData("Preferred DropEffect", new MemoryStream(new byte[] { 1, 0, 0, 0 }));
        // Do not advertise a bitmap: many chat clients otherwise paste only the first frame.
        return data;
    }

    internal static async Task CopyAsync(string path)
    {
        Exception? failure = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(CreateData(path), copy: true); return; }
            catch (System.Runtime.InteropServices.ExternalException ex) { failure = ex; await Task.Delay(100); }
        }
        throw new IOException("剪贴板正被其他程序占用，请点击「复制 GIF」重试。", failure);
    }
}
