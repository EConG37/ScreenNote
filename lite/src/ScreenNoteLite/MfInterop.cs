using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenNote;

// Minimal Media Foundation interop for writing H.264/AAC MP4 files.
// Interfaces declare the full inherited vtable explicitly: .NET does not flatten
// base COM interfaces when computing vtable slots, so inheritance would misroute calls.
internal static class Mf
{
    internal const uint Version = 0x00020070;

    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern int MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url,
        nint byteStream, IMFAttributes? attributes, out IMFSinkWriter writer);

    internal static void Check(int hr, string operation)
    {
        if (hr < 0) throw new InvalidOperationException($"系统视频编码组件调用失败（{operation}，0x{unchecked((uint)hr):X8}）。");
    }

    internal static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    internal static class MediaTypes
    {
        internal static readonly Guid Video = new("73646976-0000-0010-8000-00AA00389B71");
        internal static readonly Guid Audio = new("73647561-0000-0010-8000-00AA00389B71");
        internal static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
        internal static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
        internal static readonly Guid Pcm = new("00000001-0000-0010-8000-00AA00389B71");
        internal static readonly Guid Aac = new("00001610-0000-0010-8000-00AA00389B71");
    }

    internal static class Keys
    {
        internal static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        internal static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        internal static readonly Guid AverageBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        internal static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        internal static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
        internal static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        internal static readonly Guid PixelAspectRatio = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        internal static readonly Guid AllSamplesIndependent = new("c9173739-5e56-461c-b713-46fb995cb95f");
        internal static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        internal static readonly Guid AudioSamplesPerSecond = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        internal static readonly Guid AudioBitsPerSample = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        internal static readonly Guid AudioBlockAlignment = new("322de230-9eeb-43bd-ab7a-ff412251541d");
        internal static readonly Guid AudioAverageBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        internal static readonly Guid EnableHardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
        internal static readonly Guid DisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
        // Forces the MP4 muxer regardless of the output file extension (recordings start as .partial).
        internal static readonly Guid TranscodeContainerType = new("150ff23f-4abc-478b-ac4f-e1916fba1cca");
        internal static readonly Guid ContainerTypeMpeg4 = new("dc6cd05d-b9d0-40ef-bd35-fa622c1ab28a");
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3")]
    internal interface IMFAttributes
    {
        [PreserveSig] int GetItem(ref Guid key, nint value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, nint value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint bufferSize, nint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, nint value, nint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, nint buffer, uint bufferSize, nint size);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, nint buffer, nint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, nint value);
        [PreserveSig] int SetItem(ref Guid key, nint value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, nint buffer, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, nint value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, nint value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555")]
    internal interface IMFMediaType
    {
        // IMFAttributes vtable
        [PreserveSig] int GetItem(ref Guid key, nint value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, nint value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint bufferSize, nint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, nint value, nint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, nint buffer, uint bufferSize, nint size);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, nint buffer, nint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, nint value);
        [PreserveSig] int SetItem(ref Guid key, nint value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, nint buffer, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, nint value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, nint value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
        // IMFMediaType vtable
        [PreserveSig] int GetMajorType(out Guid majorType);
        [PreserveSig] int IsCompressedFormat(out int compressed);
        [PreserveSig] int IsEqual(IMFMediaType type, out uint flags);
        [PreserveSig] int GetRepresentation(ref Guid representation, nint value);
        [PreserveSig] int FreeRepresentation(ref Guid representation, nint value);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("045fa593-8799-42b8-bc8d-8968c6453507")]
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out nint buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out uint currentLength);
        [PreserveSig] int SetCurrentLength(uint currentLength);
        [PreserveSig] int GetMaxLength(out uint maxLength);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4")]
    internal interface IMFSample
    {
        // IMFAttributes vtable
        [PreserveSig] int GetItem(ref Guid key, nint value);
        [PreserveSig] int GetItemType(ref Guid key, out int type);
        [PreserveSig] int CompareItem(ref Guid key, nint value, out int result);
        [PreserveSig] int Compare(IMFAttributes theirs, int matchType, out int result);
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        [PreserveSig] int GetDouble(ref Guid key, out double value);
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        [PreserveSig] int GetStringLength(ref Guid key, out uint length);
        [PreserveSig] int GetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder value, uint bufferSize, nint length);
        [PreserveSig] int GetAllocatedString(ref Guid key, nint value, nint length);
        [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
        [PreserveSig] int GetBlob(ref Guid key, nint buffer, uint bufferSize, nint size);
        [PreserveSig] int GetAllocatedBlob(ref Guid key, nint buffer, nint size);
        [PreserveSig] int GetUnknown(ref Guid key, ref Guid iid, nint value);
        [PreserveSig] int SetItem(ref Guid key, nint value);
        [PreserveSig] int DeleteItem(ref Guid key);
        [PreserveSig] int DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        [PreserveSig] int SetDouble(ref Guid key, double value);
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        [PreserveSig] int SetString(ref Guid key, [MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int SetBlob(ref Guid key, nint buffer, uint size);
        [PreserveSig] int SetUnknown(ref Guid key, nint value);
        [PreserveSig] int LockStore();
        [PreserveSig] int UnlockStore();
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetItemByIndex(uint index, out Guid key, nint value);
        [PreserveSig] int CopyAllItems(IMFAttributes destination);
        // IMFSample vtable
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
        [PreserveSig] int RemoveBufferByIndex(uint index);
        [PreserveSig] int RemoveAllBuffers();
        [PreserveSig] int GetTotalLength(out uint totalLength);
        [PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d")]
    internal interface IMFSinkWriter
    {
        [PreserveSig] int AddStream(IMFMediaType targetType, out uint streamIndex);
        [PreserveSig] int SetInputMediaType(uint streamIndex, IMFMediaType inputType, IMFAttributes? encodingParameters);
        [PreserveSig] int BeginWriting();
        [PreserveSig] int WriteSample(uint streamIndex, IMFSample sample);
        [PreserveSig] int SendStreamTick(uint streamIndex, long timestamp);
        [PreserveSig] int PlaceMarker(uint streamIndex, nint context);
        [PreserveSig] int NotifyEndOfSegment(uint streamIndex);
        [PreserveSig] int Flush(uint streamIndex);
        [PreserveSig] int Finalize();
        [PreserveSig] int GetServiceForStream(uint streamIndex, ref Guid service, ref Guid iid, nint value);
        [PreserveSig] int GetStatistics(uint streamIndex, nint statistics);
    }
}
