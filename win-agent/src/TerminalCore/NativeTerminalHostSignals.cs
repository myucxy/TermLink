using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace TermLink.TerminalCore;

public sealed record NativeTerminalHostSignals(byte ProgressState, byte ProgressPercent)
{
    public static NativeTerminalHostSignals Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 12 || !bytes.Slice(0,8).SequenceEqual(new byte[] {84,76,72,83,49,0,0,0}) ||
            bytes[8] > 4 || bytes[9] > 100 || bytes[10] != 0 || bytes[11] != 0 || (bytes[8] is 0 or 3 && bytes[9] != 0))
            throw new InvalidDataException("Invalid TLHS1 host signals.");
        return new(bytes[8],bytes[9]);
    }
}

public sealed record NativeTerminalPlatformAction(byte Kind, byte Volume, byte Duration32nds, IReadOnlyList<byte> Notes, string Text = null)
{
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    public static bool IsValid(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 3) {
            if (bytes.Length > 16392 || bytes[1] != 0 || bytes[2] != 0 || bytes[3] != 0 ||
                System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4)) != bytes.Length - 8 || bytes.Slice(8).Contains((byte)0)) return false;
            try { _ = StrictUtf8.GetCharCount(bytes.Slice(8)); return true; } catch (System.Text.DecoderFallbackException) { return false; }
        }
        if (bytes.Length < 4 || bytes.Length > 34) return false;
        if (bytes[0] == 1) return bytes.Length == 4 && bytes[1] == 0 && bytes[2] == 0 && bytes[3] == 0;
        if (bytes[0] != 2 || bytes[1] > 7 || bytes[3] < 1 || bytes[3] > 30 || bytes.Length != 4 + bytes[3]) return false;
        foreach (var note in bytes.Slice(4)) if (note > 25) return false;
        return true;
    }
    public static NativeTerminalPlatformAction Decode(ReadOnlySpan<byte> bytes)
    {
        if (!IsValid(bytes)) throw new InvalidDataException("Invalid terminal platform action.");
        if (bytes[0] == 3) return new(3, 0, 0, Array.Empty<byte>(), StrictUtf8.GetString(bytes.Slice(8)));
        return new(bytes[0],bytes[1],bytes[2],Array.AsReadOnly(bytes.Slice(4).ToArray()));
    }
}

public sealed partial class NativeTerminal
{
    public NativeTerminalHostSignals HostSignals()
    {
        lock (sync) {
            ThrowIfUnavailable(); var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try {
                NativeTerminalInterop.ThrowIfFailed(GetHostSignals(handle, ref buffer), "hostSignals");
                if (buffer.AbiVersion != 1 || buffer.StructSize != NativeTerminalInterop.BufferStructSize || buffer.Length != 12 || buffer.Data == IntPtr.Zero)
                    throw new NativeTerminalAbiException("Invalid host signals buffer.");
                var bytes = new byte[12]; Marshal.Copy(buffer.Data, bytes, 0, 12);
                return NativeTerminalHostSignals.Decode(bytes);
            } finally {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                    NativeTerminalInterop.ThrowIfFailed(NativeTerminalInterop.TerminalBufferFree(ref buffer), "hostSignals.free");
            }
        }
    }
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_get_host_signals_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus GetHostSignals(SafeTlTerminalHandle terminal, ref TlTerminalBuffer output);
}
