using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ScreenNote;

/// <summary>Only the encrypted pairing credential is persisted; jobs and website data remain transient.</summary>
internal sealed class PairingStore(string file)
{
    private readonly string _file = Path.GetFullPath(file);
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScreenNote/browser-pairing/v1");
    private const int MaximumFileBytes = 16 * 1024;

    internal sealed record Credential(string Token, string ExtensionId);
    private sealed record Envelope(int Version, string ProtectedData);

    public Credential? Load()
    {
        if (!File.Exists(_file))
        {
            // File.Exists also returns false for inaccessible paths; opening detects that failure.
            try { using var absent = File.OpenRead(_file); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        using var stream = File.OpenRead(_file);
        if (stream.Length > MaximumFileBytes) throw new InvalidDataException("配对文件无效。");
        var envelope = JsonSerializer.Deserialize<Envelope>(stream);
        if (envelope?.Version != 1 || string.IsNullOrEmpty(envelope.ProtectedData)) throw new InvalidDataException("配对文件无效。");
        var encrypted = Convert.FromBase64String(envelope.ProtectedData);
        var plaintext = Transform(encrypted, protect: false);
        try
        {
            var credential = JsonSerializer.Deserialize<Credential>(plaintext);
            if (credential is null || !Valid(credential)) throw new InvalidDataException("配对凭据无效。");
            return credential;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Save(Credential credential)
    {
        if (!Valid(credential)) throw new InvalidDataException("配对凭据无效。");
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(credential);
        byte[] encrypted;
        try { encrypted = Transform(plaintext, protect: true); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, Convert.ToBase64String(encrypted)));
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, _file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Delete() => File.Delete(_file);

    private static bool Valid(Credential value) => value.Token is not null && value.ExtensionId is not null
        && Regex.IsMatch(value.Token, "^[A-F0-9]{64}$") && Regex.IsMatch(value.ExtensionId, "^[a-p]{32}$");

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("配对加密仅支持 Windows。");
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        DataBlob output = default;
        try
        {
            var inputBlob = new DataBlob { Length = input.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { Length = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            // UI_FORBIDDEN, without LOCAL_MACHINE: bound to the current Windows user.
            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("Windows 无法加密或读取配对凭据。", new Win32Exception(Marshal.GetLastWin32Error()));
            if (output.Length <= 0 || output.Length > MaximumFileBytes) throw new CryptographicException("配对凭据长度无效。");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            if (output.Data != IntPtr.Zero)
            {
                if (!protect && output.Length > 0 && output.Length <= MaximumFileBytes) Marshal.Copy(new byte[output.Length], 0, output.Data, output.Length);
                LocalFree(output.Data);
            }
            entropyHandle.Free();
            inputHandle.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
