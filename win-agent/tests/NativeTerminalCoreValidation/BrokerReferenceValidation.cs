using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;

internal static class BrokerReferenceValidation
{
    internal static void Run()
    {
        var output = Encoding.ASCII.GetBytes("synthetic broker output");
        var objectId = Guid.Parse("7a2c772e-e768-401f-a323-2f4bcd309c2b");
        var hash = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        using var external = NativeTerminalBrokerOutputReferenceV1.External(2, output, objectId, 244, hash);
        var encoded = external.Encode();
        using (var decoded = NativeTerminalBrokerOutputReferenceV1.Decode(encoded))
        {
            Require(!decoded.IsInline && decoded.SourceSequence == 2 && decoded.ObjectId == objectId &&
                decoded.CaptureBytes == 244 && decoded.CopyOutput().SequenceEqual(output) &&
                decoded.CopySha256().SequenceEqual(hash) && decoded.Encode().SequenceEqual(encoded),
                "External capture reference roundtrip changed identity or data.");
            Require(Convert.ToHexString(encoded.AsSpan(24, 16)) == "7A2C772EE768401FA3232F4BCD309C2B",
                "Capture UUID did not use RFC4122 byte order.");
        }
        foreach (var offset in new[] { 0, 8, 12, 24, 40, 44, 80, 95 })
        {
            var bad = (byte[])encoded.Clone();
            if (offset == 24) Array.Clear(bad, 24, 16);
            else if (offset == 40) BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(40), uint.MaxValue);
            else bad[offset] ^= 0xff;
            Reject(() => NativeTerminalBrokerOutputReferenceV1.Decode(bad).Dispose());
        }
        Reject(() => NativeTerminalBrokerOutputReferenceV1.Decode(encoded[..^1]).Dispose());
        Reject(() => NativeTerminalBrokerOutputReferenceV1.Decode(encoded.Concat(new byte[1]).ToArray()).Dispose());
        Reject(() => NativeTerminalBrokerOutputReferenceV1.External(0, output, objectId, 244, hash));
        Reject(() => NativeTerminalBrokerOutputReferenceV1.External(long.MaxValue, output, objectId, 244, hash));
        Reject(() => NativeTerminalBrokerOutputReferenceV1.External(2, new byte[65537], objectId, 244, hash));

        var failures = new byte[136];
        "TLCAPV1\0"u8.CopyTo(failures);
        BinaryPrimitives.WriteUInt32LittleEndian(failures.AsSpan(8), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(failures.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(failures.AsSpan(16), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(failures.AsSpan(24), (uint)failures.Length);
        var record = failures.AsSpan(32);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], 96);
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], 'f');
        BinaryPrimitives.WriteUInt64LittleEndian(record[16..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(record[24..], (uint)output.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[96..], 4);
        using var inline = NativeTerminalBrokerOutputReferenceV1.InlineFailures(2, output, failures);
        var inlineBytes = inline.Encode();
        using (var decoded = NativeTerminalBrokerOutputReferenceV1.Decode(inlineBytes))
            Require(decoded.IsInline && decoded.CopyInlineCaptures().SequenceEqual(failures) &&
                decoded.Encode().SequenceEqual(inlineBytes), "Quota denial is not durably replayable inline.");
        foreach (var offset in new[] { 48, inlineBytes.Length - 8, inlineBytes.Length - 1 })
        {
            var bad = (byte[])inlineBytes.Clone(); bad[offset] ^= 0xff;
            Reject(() => NativeTerminalBrokerOutputReferenceV1.Decode(bad).Dispose());
        }
        foreach (var offset in new[] { 0, 8, 12, 16, 24, 28, 32, 36, 40, 44, 48, 56, 60, 108, 112, 128, 132 })
        {
            var bad = (byte[])failures.Clone(); bad[offset] ^= 0xff;
            Reject(() => NativeTerminalBrokerOutputReferenceV1.InlineFailures(2, output, bad).Dispose());
        }
        var success = (byte[])failures.Clone(); success[128] = 0;
        Reject(() => NativeTerminalBrokerOutputReferenceV1.InlineFailures(2, output, success).Dispose());
        inline.Dispose();
        try { inline.Encode(); throw new InvalidOperationException("Closed reference remained readable."); }
        catch (ObjectDisposedException) { }
        Console.WriteLine("Broker Output reference bounds, UUID, inline denial and corruption validation passed.");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid broker reference was accepted.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
