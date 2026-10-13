using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeGraphicsBinding.Validation;

internal static class KittyReplyValidation
{
    private static int assertions;

    internal static void RunManaged()
    {
        foreach (var body in new[]
        {
            "i=1;OK", "I=4294967295;OK", "i=4294967295,I=9,p=2;OK", "I=2,p=3;OK",
            "i=1;EINVAL: invalid image", "i=1;ENOTSUP: broker medium", "i=1;ENOSPC: resource budget",
            "i=1;EINVAL: " + new string('x', 232)
        })
        {
            var sevenBit = Bytes("\u001b_G" + body + "\u001b\\");
            var eightBit = new byte[] { 0x9f }.Concat(sevenBit.AsSpan(2, sevenBit.Length - 4).ToArray()).Concat(new byte[] { 0x9c }).ToArray();
            foreach (var payload in new[] { sevenBit, eightBit })
            {
                var frame = NativeTerminalHostEffectFrameV2Decoder.Decode(Frame(payload));
                Require(frame.Records.Single().Payload.SequenceEqual(payload), "canonical Kitty reply preserved");
                try { NativeTerminalHostEffectFrameV1Decoder.Decode(Frame(payload, 1)); throw new Exception("V1 accepted Kitty graphics"); }
                catch (NativeTerminalHostEffectFrameV1DecodeException) { assertions++; }
            }
        }
        foreach (var body in new[]
        {
            ";OK", "p=1;OK", "i=0;OK", "I=0;OK", "i=01;OK", "i=+1;OK", "i=4294967296;OK",
            "i=1,i=2;OK", "I=2,i=1;OK", "i=1,p=2,I=3;OK", "i=1,p=0;OK", "i=1,;OK", ",i=1;OK",
            "i=1,,p=2;OK", "i=1,q=2;OK", "i=1;", "i=1;ok", "i=1;OK extra", "i=1;ENOENT: missing",
            "i=1;EINVAL: ", "i=1;ENOTSUP: ", "i=1;ENOSPC: ", "i=1;EINVAL: " + new string('x', 233),
            "i=1;EINVAL: bad\u001b[2J", "i=1;EINVAL: bad\n"
        }) Reject(Bytes("\u001b_G" + body + "\u001b\\"));
        Reject(Bytes("\u001b_anything\u001b\\"));
        Reject(Bytes("\u001b_Gi=1;OK\u0007"));
        Reject(new byte[] { 0x9f }.Concat(Bytes("Gi=1;OK\u001b\\")).ToArray());
        Reject(Bytes("\u001b_Gi=1;OK").Concat(new byte[] { 0x9c }).ToArray());
        Reject(Bytes("\u001b_Gi=1;EINVAL: ").Concat(new byte[] { 0x80 }).Concat(Bytes("\u001b\\")).ToArray());
        Console.WriteLine("PASS canonical Kitty PTY replies: " + assertions + " assertions");
    }

    internal static void RunNative()
    {
        using var terminal = NativeTerminalCoreV2.CreateGraphicsV1(8, 3, 1, 1000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);
        terminal.ApplyBatch(NativeTerminalBatchEvent.HostGeometry(1, new NativeTerminalHostGeometryV1(10, 20, 0, 0, 0, 0)));
        var transmitted = terminal.ApplyOutputWithHostEffectsV2(2,
            Bytes("\u001b_Ga=T,f=32,s=1,v=1,i=11,c=1,r=1;AQIDBA==\u001b\\"));
        Require(transmitted.Records.Single().Payload.SequenceEqual(Bytes("\u001b_Gi=11;OK\u001b\\")),
            "authority emits nonquiet Kitty upload acknowledgement");
        var query = terminal.ApplyOutputWithHostEffectsV2(3,
            Bytes("\u001b%@\u001b G\u001b_Ga=q,f=32,s=1,v=1,i=12;AQIDBA==\u001b\\"));
        Require(query.Records.Single().Payload.SequenceEqual(new byte[] { 0x9f }.Concat(Bytes("Gi=12;OK")).Concat(new byte[] { 0x9c })),
            "authority emits canonical C1 APC acknowledgement");
        var unsupported = terminal.ApplyOutputWithHostEffectsV2(4, Bytes("\u001b_Ga=q,f=100,t=f,i=13;QzovbWlzc2luZy5wbmc=\u001b\\"));
        var response = unsupported.Records.Single().Payload.ToArray();
        var normalized = NativeTerminalTraditionalProtocol.NormalizeC1Reply(response);
        Require(normalized != null && normalized.AsSpan().StartsWith("\u001b_Gi=13;ENOTSUP: "u8),
            "authority protocol errors remain canonical");
        using var checkpoint = terminal.CaptureCheckpoint();
        Require(checkpoint.RenderFrameV5().Images.Count == 1, "reply decoding does not reapply source output");
        Console.WriteLine("PASS native nonquiet Kitty HostEffect V2: 4 assertions");
    }

    private static byte[] Frame(byte[] payload, ushort version = 2)
    {
        var bytes = new byte[104 + payload.Length];
        Bytes("TLHEF" + version + "\0\0").CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), version);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(20), (uint)bytes.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 64);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 104);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(64), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(80), 12);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(88), 104);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(96), (uint)payload.Length);
        payload.CopyTo(bytes, 104);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), Crc(bytes.AsSpan(64)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(60), Crc(bytes.AsSpan(0, 60)));
        return bytes;
    }

    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82f63b78u & (0u - (crc & 1)));
        }
        return ~crc;
    }

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);
    private static void Reject(byte[] payload)
    {
        try { NativeTerminalHostEffectFrameV2Decoder.Decode(Frame(payload)); }
        catch (NativeTerminalHostEffectFrameV2DecodeException) { assertions++; return; }
        throw new InvalidOperationException("Accepted malformed Kitty reply");
    }
    private static void Require(bool value, string detail)
    {
        if (!value) throw new InvalidOperationException(detail);
        assertions++;
    }
}
