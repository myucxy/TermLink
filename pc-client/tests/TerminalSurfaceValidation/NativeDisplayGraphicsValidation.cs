using System.Buffers.Binary;
using System.Text;
using TermLink.Desktop;
using TermLink.NativeDisplay;
using TermLink.TerminalCore;

internal static class NativeDisplayGraphicsValidation
{
    private static int assertions;

    internal static void Run(string? fixtureRoot)
    {
        WireBudgets().GetAwaiter().GetResult();
        using var terminal = NativeTerminalCoreV2.Create(4, 2);
        terminal.ApplyOutput(1, Encoding.UTF8.GetBytes("\x1b]2;display-profile\x1b\\"));
        using var checkpoint = terminal.CaptureCheckpoint();
        var legacy = checkpoint.ExportRenderFrameV4();
        var graphics = fixtureRoot is null ? EmptyGraphics(legacy) : File.ReadAllBytes(Path.Combine(fixtureRoot, "empty.v5.bin"));
        var legacyConnection = new NativeTerminalDisplayFrameDecoder();
        var first = legacyConnection.Decode(NativeDisplayProtocol.Frame, legacy);
        Require(first.Graphics is null && first.Text.Title == "display-profile", "legacy frame and title");
        Require(legacyConnection.Decode(NativeDisplayProtocol.Frame, legacy).Text.PresentationRevision == first.Text.PresentationRevision,
            "same legacy profile accepted");
        Reject<InvalidDataException>(() => legacyConnection.Decode(NativeDisplayProtocol.FrameV5, graphics), "V4 to V5 switch");
        var graphicsConnection = new NativeTerminalDisplayFrameDecoder();
        var drawn = graphicsConnection.Decode(NativeDisplayProtocol.FrameV5, graphics);
        Require(drawn.Graphics is not null && ReferenceEquals(drawn.Text, drawn.Graphics.Text), "graphics text is authoritative for title/theme/input");
        Require(graphicsConnection.Decode(NativeDisplayProtocol.FrameV5, graphics).Graphics is not null, "same graphics profile accepted");
        Reject<InvalidDataException>(() => graphicsConnection.Decode(NativeDisplayProtocol.Frame, legacy), "V5 to V4 switch");
        Reject<NativeTerminalRenderFrameV4DecodeException>(() => new NativeTerminalDisplayFrameDecoder().Decode(NativeDisplayProtocol.Frame, graphics),
            "V5 bytes under V4 status");
        Reject<NativeTerminalRenderFrameV5DecodeException>(() => new NativeTerminalDisplayFrameDecoder().Decode(NativeDisplayProtocol.FrameV5, legacy),
            "V4 bytes under V5 status");
        foreach (var kind in new[] { NativeDisplayProtocol.NotReady, NativeDisplayProtocol.Unchanged, NativeDisplayProtocol.Accepted, (byte)255 })
            Reject<InvalidDataException>(() => new NativeTerminalDisplayFrameDecoder().Decode(kind, graphics), "non-frame status cannot be sniffed");
        var malformed = (byte[])graphics.Clone(); malformed[^1] ^= 1;
        Reject<NativeTerminalRenderFrameV5DecodeException>(() => new NativeTerminalDisplayFrameDecoder().Decode(NativeDisplayProtocol.FrameV5, malformed),
            "bad graphics bytes fail without fallback");
        Console.WriteLine($"Native display graphics: {assertions} wire budget, explicit status and immutable window profile assertions passed.");
    }

    private static async Task WireBudgets()
    {
        Require(NativeDisplayProtocol.MaximumFrameBytes == 32 * 1024 * 1024 && NativeDisplayProtocol.MaximumGraphicsFrameBytes == 64 * 1024 * 1024 &&
            NativeDisplayProtocol.MaximumRequestBytes == 1024 * 1024 + 32, "legacy/request budgets unchanged");
        Require(NativeDisplayProtocol.Frame == 2 && NativeDisplayProtocol.Render == 1 && NativeDisplayProtocol.FrameV5 != NativeDisplayProtocol.Frame,
            "legacy wire kinds unchanged");
        var tooLargeForLegacy = new byte[NativeDisplayProtocol.MaximumFrameBytes + 1];
        foreach (var kind in new[] { NativeDisplayProtocol.Frame, NativeDisplayProtocol.Accepted, (byte)255 })
        {
            using var output = new MemoryStream();
            await RejectAsync<InvalidDataException>(() => NativeDisplayProtocol.WriteAsync(output, kind, tooLargeForLegacy,
                NativeDisplayProtocol.MaximumGraphicsFrameBytes, default), "legacy kind write cannot use graphics budget");
            Require(output.Length == 0, "bad write emitted a partial message");
            await RejectHeader(kind, tooLargeForLegacy.Length, NativeDisplayProtocol.MaximumGraphicsFrameBytes);
        }
        await NativeDisplayProtocol.WriteAsync(Stream.Null, NativeDisplayProtocol.FrameV5, tooLargeForLegacy,
            NativeDisplayProtocol.MaximumGraphicsFrameBytes, default);
        assertions++;
        using (var graphicsInput = new ZeroBodyStream(NativeDisplayProtocol.FrameV5, tooLargeForLegacy.Length))
        {
            var accepted = await NativeDisplayProtocol.ReadAsync(graphicsInput, NativeDisplayProtocol.MaximumGraphicsFrameBytes, default);
            Require(accepted.Kind == NativeDisplayProtocol.FrameV5 && accepted.Payload.Length == tooLargeForLegacy.Length,
                "explicit graphics status can exceed legacy frame budget");
        }
        await RejectHeader(NativeDisplayProtocol.FrameV5, tooLargeForLegacy.Length, NativeDisplayProtocol.MaximumFrameBytes);
        await RejectHeader(NativeDisplayProtocol.FrameV5, NativeDisplayProtocol.MaximumGraphicsFrameBytes + 1, int.MaxValue);
        await RejectHeader(NativeDisplayProtocol.Frame, -1, NativeDisplayProtocol.MaximumGraphicsFrameBytes);
        var maximum = new byte[NativeDisplayProtocol.MaximumGraphicsFrameBytes];
        await NativeDisplayProtocol.WriteAsync(Stream.Null, NativeDisplayProtocol.FrameV5, maximum, int.MaxValue, default);
        assertions++;
        using (var exact = new ZeroBodyStream(NativeDisplayProtocol.FrameV5, maximum.Length))
            Require((await NativeDisplayProtocol.ReadAsync(exact, int.MaxValue, default)).Payload.Length == maximum.Length, "exact 64 MiB graphics response accepted");
        await RejectAsync<InvalidDataException>(() => NativeDisplayProtocol.WriteAsync(Stream.Null, NativeDisplayProtocol.FrameV5,
            new byte[NativeDisplayProtocol.MaximumGraphicsFrameBytes + 1], int.MaxValue, default), "graphics write cap is intrinsic");
        var request = new byte[NativeDisplayProtocol.MaximumRequestBytes];
        await NativeDisplayProtocol.WriteAsync(Stream.Null, NativeDisplayProtocol.Paste, request, NativeDisplayProtocol.MaximumRequestBytes, default);
        assertions++;
        await RejectHeader(NativeDisplayProtocol.Paste, request.Length + 1, NativeDisplayProtocol.MaximumRequestBytes);
        await RejectHeader(NativeDisplayProtocol.FrameV5, request.Length + 1, NativeDisplayProtocol.MaximumRequestBytes);
        await RejectAsync<InvalidDataException>(() => NativeDisplayProtocol.WriteAsync(Stream.Null, NativeDisplayProtocol.SignalsQuery,
            new byte[request.Length + 1], NativeDisplayProtocol.MaximumRequestBytes, default), "overlapping request kind still has request budget");
        using var small = new MemoryStream();
        await NativeDisplayProtocol.WriteAsync(small, NativeDisplayProtocol.Frame, new byte[] { 1, 2, 3 }, NativeDisplayProtocol.MaximumGraphicsFrameBytes, default);
        small.Position = 0;
        var old = await NativeDisplayProtocol.ReadAsync(small, NativeDisplayProtocol.MaximumFrameBytes, default);
        Require(old.Kind == NativeDisplayProtocol.Frame && old.Payload.SequenceEqual(new byte[] { 1, 2, 3 }), "old reader budget retains old wire layout");
    }

    private static async Task RejectHeader(byte kind, int length, int maximum)
    {
        var header = Header(kind, length);
        using var input = new MemoryStream(header);
        // Header-only input distinguishes early budget rejection from an attempted body read/allocation.
        await RejectAsync<InvalidDataException>(() => NativeDisplayProtocol.ReadAsync(input, maximum, default), "wire header budget");
        Require(input.Position == 6, "invalid header did not stop at the body boundary");
    }

    private static byte[] EmptyGraphics(byte[] text)
    {
        var bytes = new byte[64 + text.Length + 48];
        "TLRFRM5\0\x05\0\x40\0"u8.CopyTo(bytes);
        W32(bytes, 12, (uint)bytes.Length); W64(bytes, 16, BinaryPrimitives.ReadUInt64LittleEndian(text.AsSpan(24)));
        W32(bytes, 24, BinaryPrimitives.ReadUInt32LittleEndian(text.AsSpan(40))); W32(bytes, 28, BinaryPrimitives.ReadUInt32LittleEndian(text.AsSpan(44)));
        W32(bytes, 32, (uint)text.Length); W32(bytes, 36, 48); W32(bytes, 48, 7); text.CopyTo(bytes, 64);
        var resources = bytes.AsSpan(64 + text.Length);
        "TLGRS1\0\0\x01\0\x30\0"u8.CopyTo(resources); BinaryPrimitives.WriteUInt32LittleEndian(resources.Slice(12), 48);
        BinaryPrimitives.WriteUInt64LittleEndian(resources.Slice(16), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(resources.Slice(40), Crc(resources.Slice(0, 40), resources.Slice(44)));
        W32(bytes, 52, Crc(bytes.AsSpan(0, 52), bytes.AsSpan(56))); return bytes;
    }
    private static byte[] Header(byte kind, int length)
    {
        var header = new byte[6]; header[0] = 1; header[1] = kind; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), length); return header;
    }
    private static uint Crc(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        var crc = uint.MaxValue;
        foreach (var part in new[] { first.ToArray(), second.ToArray() })
            foreach (var value in part) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0U : 0x82f63b78U); }
        return ~crc;
    }
    private static void W32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void W64(byte[] bytes, int offset, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); assertions++; }
    private static void Reject<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { assertions++; return; } throw new Exception("Accepted " + message);
    }
    private static async Task RejectAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); } catch (T) { assertions++; return; } throw new Exception("Accepted " + message);
    }

    private sealed class ZeroBodyStream(byte kind, int length) : Stream
    {
        private readonly byte[] header = Header(kind, length);
        private long position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 6L + length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Length - position);
            var target = buffer.Slice(0, count); target.Clear();
            if (position < header.Length) header.AsSpan((int)position, Math.Min(count, header.Length - (int)position)).CopyTo(target);
            position += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
