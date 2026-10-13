using System;
using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;

namespace TermLink.TerminalCore;

/// <summary>A bounded ordered Output referencing immutable, path-free capture results.</summary>
public sealed class NativeTerminalBrokerOutputReferenceV1 : IDisposable
{
    public const int HeaderBytes = 96;
    public const int MaximumOutputBytes = 64 * 1024;
    public const int MaximumCaptureBytes = 32 * 1024 * 1024;
    public const int MaximumInlineBytes = 32 + 64 * 104;
    private readonly object sync = new();
    private readonly byte[] output;
    private readonly byte[] sha256;
    private readonly byte[] inline;
    private bool disposed;

    private NativeTerminalBrokerOutputReferenceV1(ulong sequence, ReadOnlySpan<byte> output,
        Guid objectId, int captureBytes, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> inline)
    {
        SourceSequence = sequence;
        ObjectId = objectId;
        CaptureBytes = captureBytes;
        this.output = output.ToArray();
        sha256 = hash.ToArray();
        this.inline = inline.ToArray();
    }

    public ulong SourceSequence { get; }
    public Guid ObjectId { get; }
    public int CaptureBytes { get; }
    public bool IsInline => ObjectId == Guid.Empty;

    public static NativeTerminalBrokerOutputReferenceV1 External(ulong sequence,
        ReadOnlySpan<byte> output, Guid objectId, int captureBytes, ReadOnlySpan<byte> hash)
    {
        Validate(sequence, output.Length, captureBytes, hash.Length);
        if (objectId == Guid.Empty) throw new InvalidDataException("Broker capture object UUID is empty.");
        return new(sequence, output, objectId, captureBytes, hash, ReadOnlySpan<byte>.Empty);
    }

    public static NativeTerminalBrokerOutputReferenceV1 InlineFailures(ulong sequence,
        ReadOnlySpan<byte> output, ReadOnlySpan<byte> captures)
    {
        Validate(sequence, output.Length, captures.Length, 32);
        ValidateFailures(captures, sequence, output.Length);
        var hash = SHA256.HashData(captures);
        try { return new(sequence, output, Guid.Empty, captures.Length, hash, captures); }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    public static NativeTerminalBrokerOutputReferenceV1 Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderBytes ||
            bytes.Length > HeaderBytes + MaximumOutputBytes + MaximumInlineBytes ||
            !bytes[..8].SequenceEqual("TLBROV1\0"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != HeaderBytes ||
            bytes.Slice(80, 16).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Broker Output reference header is invalid.");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        var sequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]);
        var captureBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[40..]);
        var outputBytes = BinaryPrimitives.ReadUInt32LittleEndian(bytes[44..]);
        if (flags > 1 || captureBytes > MaximumCaptureBytes || outputBytes > MaximumOutputBytes)
            throw new InvalidDataException("Broker Output reference bounds are invalid.");
        Validate(sequence, (int)outputBytes, (int)captureBytes, 32);
        var objectId = Guid.ParseExact(Convert.ToHexString(bytes.Slice(24, 16)), "N");
        var inlineBytes = flags == 1 ? (int)captureBytes : 0;
        if ((flags == 1) != (objectId == Guid.Empty) || inlineBytes > MaximumInlineBytes ||
            bytes.Length != HeaderBytes + (int)outputBytes + inlineBytes)
            throw new InvalidDataException("Broker Output reference layout is invalid.");
        var output = bytes.Slice(HeaderBytes, (int)outputBytes);
        var hash = bytes.Slice(48, 32);
        var inline = bytes.Slice(HeaderBytes + (int)outputBytes, inlineBytes);
        if (flags == 1)
        {
            ValidateFailures(inline, sequence, output.Length);
            var actualHash = SHA256.HashData(inline);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(hash, actualHash))
                    throw new InvalidDataException("Broker inline capture hash mismatch.");
            }
            finally { CryptographicOperations.ZeroMemory(actualHash); }
        }
        return new(sequence, output, objectId, (int)captureBytes, hash, inline);
    }

    public byte[] Encode()
    {
        lock (sync)
        {
            RequireOpen();
            var result = new byte[HeaderBytes + output.Length + inline.Length];
            "TLBROV1\0"u8.CopyTo(result);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), HeaderBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), IsInline ? 1u : 0u);
            BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(16), SourceSequence);
            Convert.FromHexString(ObjectId.ToString("N")).CopyTo(result, 24);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(40), (uint)CaptureBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(44), (uint)output.Length);
            sha256.CopyTo(result, 48);
            output.CopyTo(result, HeaderBytes);
            inline.CopyTo(result, HeaderBytes + output.Length);
            return result;
        }
    }

    public byte[] CopyOutput() { lock (sync) { RequireOpen(); return (byte[])output.Clone(); } }
    public byte[] CopySha256() { lock (sync) { RequireOpen(); return (byte[])sha256.Clone(); } }
    public byte[] CopyInlineCaptures() { lock (sync) { RequireOpen(); return (byte[])inline.Clone(); } }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            CryptographicOperations.ZeroMemory(output);
            CryptographicOperations.ZeroMemory(sha256);
            CryptographicOperations.ZeroMemory(inline);
        }
    }

    private void RequireOpen() => ObjectDisposedException.ThrowIf(disposed, this);

    private static void Validate(ulong sequence, int outputBytes, int captureBytes, int hashBytes)
    {
        if (sequence == 0 || sequence >= long.MaxValue || outputBytes is < 1 or > MaximumOutputBytes ||
            captureBytes is < 136 or > MaximumCaptureBytes || hashBytes != 32)
            throw new InvalidDataException("Broker Output reference identity or size is invalid.");
    }

    // Quota failures must remain replayable even when no new sidecar can be stored.
    private static void ValidateFailures(ReadOnlySpan<byte> bytes, ulong sequence, int outputBytes)
    {
        if (bytes.Length is < 136 or > MaximumInlineBytes || !bytes[..8].SequenceEqual("TLCAPV1\0"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != 32 ||
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]) != sequence ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[24..]) != bytes.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[28..]) != 0)
            throw new InvalidDataException("Broker inline failure object is invalid.");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        if (count is < 1 or > 64 || bytes.Length != 32 + count * 104)
            throw new InvalidDataException("Broker inline failure count is invalid.");
        for (uint index = 0; index < count; index++)
        {
            var record = bytes.Slice(32 + (int)index * 104, 104);
            var medium = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[24..]);
            if (BinaryPrimitives.ReadUInt32LittleEndian(record) != 1 ||
                BinaryPrimitives.ReadUInt32LittleEndian(record[4..]) != 96 ||
                BinaryPrimitives.ReadUInt32LittleEndian(record[8..]) != 1 ||
                (medium != 'f' && medium != 't' && medium != 's') ||
                BinaryPrimitives.ReadUInt64LittleEndian(record[16..]) != sequence ||
                offset == 0 || offset > outputBytes ||
                BinaryPrimitives.ReadUInt32LittleEndian(record[28..]) != index ||
                BinaryPrimitives.ReadUInt32LittleEndian(record[76..]) > 1 ||
                record.Slice(80, 16).IndexOfAnyExcept((byte)0) >= 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(record[96..]) is < 1 or > 4 ||
                BinaryPrimitives.ReadUInt32LittleEndian(record[100..]) != 0)
                throw new InvalidDataException("Broker inline object contains an invalid failure record.");
        }
    }
}
