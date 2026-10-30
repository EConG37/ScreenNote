using System;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ScreenNote;

/// <summary>Streams full, opaque frames; WIC supplies quantization and LZW compression.</summary>
internal sealed class AnimatedGifWriter : IDisposable
{
    private readonly BinaryWriter _writer;
    private readonly int _width, _height;
    private bool _finished;
    internal int FrameCount { get; private set; }

    internal AnimatedGifWriter(Stream output, int width, int height)
    {
        if (width < 1 || height < 1 || width > ushort.MaxValue || height > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(width));
        _width = width; _height = height;
        _writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        _writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
        _writer.Write((ushort)width); _writer.Write((ushort)height);
        _writer.Write(new byte[] { 0xf0, 0, 0, 0, 0, 0, 255, 255, 255 }); // Fallback global palette for conservative decoders.
        _writer.Write(new byte[] { 0x21, 0xff, 11 });
        _writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
        _writer.Write(new byte[] { 3, 1, 0, 0, 0 }); // Repeat indefinitely.
    }

    internal void Append(BitmapSource image, int delayCentiseconds, int colors = 256, int left = 0, int top = 0)
    {
        if (_finished) throw new ObjectDisposedException(nameof(AnimatedGifWriter));
        if (left < 0 || top < 0 || left + image.PixelWidth > _width || top + image.PixelHeight > _height)
            throw new ArgumentException("GIF 帧不能超出画布边界。", nameof(image));
        // A per-frame adaptive palette retains text and UI colors much better than a fixed palette.
        var indexed = new FormatConvertedBitmap(image, PixelFormats.Indexed8, new BitmapPalette(image, Math.Clamp(colors, 2, 256)), 0);
        var encoder = new GifBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(indexed));
        using var encoded = new MemoryStream();
        encoder.Save(encoded); encoded.Position = 0;
        using var reader = new BinaryReader(encoded);
        var header = reader.ReadBytes(13);
        var global = (header[10] & 0x80) != 0 ? reader.ReadBytes(3 * (2 << (header[10] & 7))) : [];
        while (true)
        {
            var marker = reader.ReadByte();
            if (marker == 0x21) { reader.ReadByte(); SkipBlocks(reader); continue; }
            if (marker != 0x2c) throw new InvalidDataException("无法读取 GIF 帧。");
            var descriptor = reader.ReadBytes(9);
            var packed = descriptor[8];
            var palette = (packed & 0x80) != 0 ? reader.ReadBytes(3 * (2 << (packed & 7))) : global;
            var sizeCode = (packed & 0x80) != 0 ? packed & 7 : header[10] & 7;
            if (palette.Length != 3 * (2 << sizeCode)) throw new InvalidDataException("GIF 调色板无效。");
            _writer.Write(new byte[] { 0x21, 0xf9, 4, 4 }); // Keep full opaque frame until replaced.
            _writer.Write((ushort)Math.Clamp(delayCentiseconds, 1, ushort.MaxValue));
            _writer.Write(new byte[] { 0, 0, 0x2c });
            _writer.Write((ushort)left); _writer.Write((ushort)top);
            _writer.Write((ushort)image.PixelWidth); _writer.Write((ushort)image.PixelHeight);
            _writer.Write((byte)(0x80 | (packed & 0x40) | sizeCode));
            _writer.Write(palette);
            _writer.Write(reader.ReadByte()); // LZW minimum code size.
            int length;
            do { length = reader.ReadByte(); _writer.Write((byte)length); _writer.Write(reader.ReadBytes(length)); } while (length != 0);
            FrameCount++;
            return;
        }
    }

    private static void SkipBlocks(BinaryReader reader)
    {
        int length;
        while ((length = reader.ReadByte()) != 0) reader.BaseStream.Seek(length, SeekOrigin.Current);
    }

    public void Dispose()
    {
        if (_finished) return;
        _finished = true;
        _writer.Write((byte)0x3b); _writer.Dispose();
    }
}
