#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TermLink.TerminalCore;

public sealed record NativeTerminalShellPoint(ulong Sequence, ulong LineId, uint CellOffset, byte Page, bool Alternate);
public sealed record NativeTerminalShellCommand(ulong Id, byte Phase, NativeTerminalShellPoint Prompt,
    NativeTerminalShellPoint? Input, NativeTerminalShellPoint? Output, NativeTerminalShellPoint? End, int? ExitCode, string CwdUri);

/// <summary>Untrusted, inert committed shell annotations.</summary>
public sealed record NativeTerminalShellState(string CwdUri, IReadOnlyList<NativeTerminalShellCommand> Commands, ulong NextCommandId)
{
    public const int MaximumBytes = 273696;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static void Check(bool valid) { if (!valid) throw new InvalidDataException("Invalid TLSHV1 shell state."); }
    public static NativeTerminalShellState Decode(byte[] bytes, ulong nextSequence)
    {
        Check(bytes.Length >= 32 && bytes.Length <= MaximumBytes);
        Check(bytes.AsSpan(0, 8).SequenceEqual(new byte[] {84, 76, 83, 72, 86, 49, 0, 0}));
        ulong U64(int at) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(at, 8));
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
        var next = U64(8); var count = U32(20); var offset = 32;
        Check(next != 0 && count <= 64 && U64(24) == 0);
        string Cwd(uint length)
        {
            Check(length <= 4096 && length <= bytes.Length - offset);
            var value = Utf8.GetString(bytes, offset, (int)length); offset += (int)length;
            if (value.Length != 0) ParseDirectoryUri(value);
            return value;
        }
        var current = Cwd(U32(16));
        ulong previousId = 0, previousSequence = 0;
        var commands = new List<NativeTerminalShellCommand>((int)count);
        for (var c = 0; c < count; c++)
        {
            Check(bytes.Length - offset >= 116);
            var start = offset; offset += 116;
            var id = U64(start); var phase = bytes[start + 8]; var flags = bytes[start + 9]; var code = unchecked((int)U32(start + 12));
            Check(id > previousId && id < next && phase <= 4 && (flags & ~15) == 0 && bytes[start + 10] == 0 && bytes[start + 11] == 0 &&
                ((flags & 8) != 0 || code == 0) && ((flags & 8) == 0 || (flags & 4) != 0) &&
                ((phase == 3) == ((flags & 4) != 0)) && (phase != 0 || flags == 0) &&
                (phase != 1 || flags == 1) && (phase != 2 || (flags & 2) != 0));
            var points = new NativeTerminalShellPoint?[4];
            for (var p = 0; p < 4; p++)
            {
                var at = start + 16 + p * 24;
                if (p != 0 && (flags & (1 << (p - 1))) == 0)
                {
                    Check(U64(at) == 0 && U64(at + 8) == 0 && U64(at + 16) == 0); continue;
                }
                var sequence = U64(at); var page = bytes[at + 20]; var alternate = bytes[at + 21];
                Check(sequence >= previousSequence && sequence < nextSequence && page >= 1 && page <= 6 &&
                    alternate <= 1 && (alternate == 0 || page == 1) && bytes[at + 22] == 0 && bytes[at + 23] == 0);
                points[p] = new(sequence, U64(at + 8), U32(at + 16), page, alternate != 0); previousSequence = sequence;
            }
            commands.Add(new(id, phase, points[0]!, points[1], points[2], points[3], (flags & 8) != 0 ? code : null, Cwd(U32(start + 112))));
            previousId = id;
        }
        Check(offset == bytes.Length);
        return new(current, commands.AsReadOnly(), next);
    }

    private static (string Host, string Path) ParseDirectoryUri(string uri)
    {
        Check(uri.StartsWith("file://", StringComparison.Ordinal) && Utf8.GetByteCount(uri) <= 4096);
        var slash = uri.IndexOf('/', 7); Check(slash >= 0);
        var host = uri.Substring(7, slash - 7);
        Check(host.All(c => c <= 127 && (char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_')));
        Check(!uri.Any(c => char.IsControl(c) || c == '\\' || c == '?' || c == '#'));
        var raw = Utf8.GetBytes(uri.Substring(slash)); using var decoded = new MemoryStream(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '%') { decoded.WriteByte(raw[i]); continue; }
            Check(i + 2 < raw.Length);
            int Hex(byte b) => b >= '0' && b <= '9' ? b - '0' : b >= 'a' && b <= 'f' ? b - 'a' + 10 : b >= 'A' && b <= 'F' ? b - 'A' + 10 : -1;
            var hi = Hex(raw[++i]); var lo = Hex(raw[++i]); Check(hi >= 0 && lo >= 0); decoded.WriteByte((byte)(hi * 16 + lo));
        }
        var path = Utf8.GetString(decoded.ToArray()); Check(!path.Any(char.IsControl));
        return (host, path);
    }

    public static string? LocalWindowsDirectory(string uri)
    {
        try
        {
            var (host, path) = ParseDirectoryUri(uri);
            Check(host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
            Check(path.Length >= 4 && path[0] == '/' && char.IsLetter(path[1]) && path[1] < 128 && path[2] == ':' && path[3] == '/');
            Check(!path.Substring(3).Any(c => "\\:<>\"|?*".Contains(c)));
            Check(!path.Substring(4).Split('/').Any(p => p == "." || p == ".." || p.EndsWith('.') || p.EndsWith(' ')));
            return path.Substring(1).Replace('/', '\\');
        }
        catch (Exception e) when (e is InvalidDataException || e is ArgumentException) { return null; }
    }
}

public sealed partial class NativeTerminal
{
    public NativeTerminalShellState ShellState()
    {
        lock (sync) {
            return NativeTerminalShellState.Decode(ExportShellStateV1(), GetPresentationInfo().NextSourceSequence);
        }
    }

    public byte[] ExportShellStateV1(ulong maxBytes = NativeTerminalShellState.MaximumBytes)
    {
        if (maxBytes == 0 || maxBytes > NativeTerminalShellState.MaximumBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        lock (sync) {
            ThrowIfUnavailable();
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try {
                NativeTerminalInterop.ThrowIfFailed(GetShellState(handle, maxBytes, ref buffer), "shellState");
                if (buffer.AbiVersion != NativeTerminalCoreV2.AbiVersion ||
                    buffer.StructSize != NativeTerminalInterop.BufferStructSize || buffer.Data == IntPtr.Zero ||
                    buffer.Length == 0 || buffer.Length > maxBytes)
                    throw new NativeTerminalAbiException("Invalid shell state buffer.");
                var bytes = new byte[checked((int)buffer.Length)];
                Marshal.Copy(buffer.Data, bytes, 0, bytes.Length);
                return bytes;
            } finally {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0) {
                    NativeTerminalInterop.ThrowIfFailed(NativeTerminalInterop.TerminalBufferFree(ref buffer), "shellState.free");
                    if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                        throw new NativeTerminalAbiException("The native terminal did not clear a released shell state buffer.");
                }
            }
        }
    }

    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_get_shell_state_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus GetShellState(SafeTlTerminalHandle terminal, ulong maxBytes, ref TlTerminalBuffer output);
}
