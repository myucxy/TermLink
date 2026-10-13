using System;
using System.Buffers.Binary;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeGraphicsBinding.Validation;

internal static class Program
{
    private static int assertions;

    private static void Main(string[] args)
    {
        KittyReplyValidation.RunManaged();
        if (args.Contains("--kitty-replies-only"))
        {
            KittyReplyValidation.RunNative();
            return;
        }
        ManagedBoundaries();
        HistoryHeaderValidation();
        if (!args.Contains("--managed-only"))
        {
            bool graphics = NativeTerminalCoreV2.SupportsGraphicsV1();
            Require(graphics != args.Contains("--expect-legacy-native"), "DLL graphics capability");
            LegacyNative();
            if (graphics) { KittyReplyValidation.RunNative(); GraphicsNative(); }
            else Throws<NotSupportedException>(() => CreateGraphics().Dispose(), "legacy DLL graphics factory");
        }
        Console.WriteLine("PASS graphics binding: " + assertions + " assertions");
    }

    private static NativeTerminal CreateGraphics(uint columns = 8, uint rows = 3) =>
        NativeTerminalCoreV2.CreateGraphicsV1(columns, rows, 1, 1000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);

    private static void ManagedBoundaries()
    {
        Throws<ArgumentNullException>(() => NativeTerminalCoreV2.ImportGraphicsSnapshot((byte[])null), "null graphics snapshot");
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 22);
        Status(NativeTerminalStatus.IncompatibleSnapshot, () => NativeTerminalCoreV2.ImportSnapshot(header), "legacy rejects 2.22");
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), 21);
        Status(NativeTerminalStatus.IncompatibleSnapshot, () => NativeTerminalCoreV2.ImportGraphicsSnapshot(header), "graphics rejects 2.21");
        Status(NativeTerminalStatus.IncompatibleSnapshot, () => NativeTerminalCoreV2.ImportGraphicsSnapshot(Array.Empty<byte>()), "graphics requires 2.22");
        var oversized = new byte[NativeTerminalCoreV2.MaximumGraphicsSnapshotBytes + 1];
        Throws<ArgumentOutOfRangeException>(() => NativeTerminalCoreV2.ImportSnapshot(oversized.AsSpan(0,
            NativeTerminalCoreV2.MaximumSnapshotBytes + 1)), "legacy 64 MiB budget");
        Throws<ArgumentOutOfRangeException>(() => NativeTerminalCoreV2.ImportGraphicsSnapshot(oversized), "graphics 128 MiB budget");
        Require(NativeTerminalHistoryPage.MaximumBytes == 16 * 1024 * 1024 &&
            NativeTerminalHistoryPageV2.MaximumBytes == 64 * 1024 * 1024, "separate history page budgets");
    }

    private static void HistoryHeaderValidation()
    {
        byte[] page = EmptyHistoryPage();
        var decoded = NativeTerminalHistoryPageV2.Decode(page, 7);
        Require(decoded.RequiredFeatures == 1 && decoded.Columns == 8 && decoded.Frames.Count == 0 &&
            decoded.NextBeforeLineId == ulong.MaxValue, "empty TLHPV2 page");
        Throws<NativeTerminalAbiException>(() => NativeTerminalHistoryPage.Decode(page, 7), "V1 rejects V2");
        Throws<NativeTerminalAbiException>(() => NativeTerminalHistoryPageV2.Decode(page, 8), "capture mismatch");
        for (int length = 0; length < page.Length; length++)
        {
            int truncatedLength = length;
            Throws<NativeTerminalAbiException>(() => NativeTerminalHistoryPageV2.Decode(page.AsSpan(0, truncatedLength), 7), "truncated page");
        }
        foreach (int offset in new[] { 0, 8, 10, 12, 16, 48, 52, 60, 64, 68, 72 })
        {
            var bad = page.ToArray();
            bad[offset] ^= 2;
            Seal(bad);
            Throws<NativeTerminalAbiException>(() => NativeTerminalHistoryPageV2.Decode(bad, 7), "invalid page field " + offset);
        }
        for (int offset = 73; offset < 92; offset++)
        {
            var bad = page.ToArray();
            bad[offset] = 1;
            Seal(bad);
            Throws<NativeTerminalAbiException>(() => NativeTerminalHistoryPageV2.Decode(bad, 7), "unknown feature/reserved byte");
        }
        page[92] ^= 1;
        Throws<NativeTerminalAbiException>(() => NativeTerminalHistoryPageV2.Decode(page, 7), "CRC mismatch");
    }

    private static void LegacyNative()
    {
        Require(NativeTerminalCoreV2.ValidateAbi().MaxSnapshotBytes == 64UL * 1024 * 1024, "legacy ABI budget unchanged");
        using var terminal = NativeTerminalCoreV2.Create(8, 3, 1, 1000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);
        Require(!terminal.HasGraphicsProfile, "default remains text profile");
        terminal.ApplyOutput(1, Encoding.UTF8.GetBytes("one\r\ntwo\r\nthree\r\nfour\r\n"));
        using var checkpoint = terminal.CaptureCheckpoint();
        using var restored = NativeTerminalCoreV2.ImportSnapshot(checkpoint.ExportSnapshot());
        Require(!checkpoint.HasGraphicsProfile && !restored.HasGraphicsProfile, "legacy checkpoint/import profile");
        Require(NativeTerminalRenderFrameV4Decoder.Decode(checkpoint.ExportRenderFrameV4()).Columns == 8, "legacy V4 render");
        Throws<InvalidOperationException>(() => checkpoint.ExportGraphicsSnapshot(), "text rejects graphics export");
        Throws<InvalidOperationException>(() => checkpoint.ExportRenderFrameV5(), "text rejects experimental V5");
        using var capture = terminal.CaptureHistory();
        Require(capture.Read().Lines.Count > 0, "legacy history reads");
        Throws<InvalidOperationException>(() => capture.ReadV2(), "text history requires explicit profile");
    }

    private static void GraphicsNative()
    {
        using var terminal = CreateGraphics();
        Require(terminal.HasGraphicsProfile, "explicit graphics profile");
        terminal.ApplyBatch(NativeTerminalBatchEvent.HostGeometry(1, new NativeTerminalHostGeometryV1(10, 20, 0, 0, 0, 0)));
        terminal.ApplyOutput(2, Encoding.ASCII.GetBytes("\u001b_Ga=T,f=32,s=1,v=1,i=11,c=1,r=1,q=2;/wAA/w==\u001b\\"));
        using var checkpoint = terminal.CaptureCheckpoint();
        Require(checkpoint.HasGraphicsProfile, "graphics checkpoint profile");
        var frame = checkpoint.RenderFrameV5();
        Require(frame.Images.Count == 1 && frame.Resources.Entries.Count == 1 &&
            frame.Text.WidthProfile == NativeTerminalWidthProfile.Unicode16, "Kitty image and text in V5");
        Require(checkpoint.RenderFrameV5AtScrollback(0).Images.Count == 1, "V5 scrollback entry point");
        Throws<ArgumentOutOfRangeException>(() => checkpoint.ExportRenderFrameV5(1), "V5 reserved flags");
        Throws<InvalidOperationException>(() => checkpoint.ExportSnapshot(), "graphics rejects legacy snapshot export");
        Throws<InvalidOperationException>(() => checkpoint.ExportRenderFrameV4(), "graphics rejects V4");
        Throws<InvalidOperationException>(() => checkpoint.ExportRenderFrameV4AtScrollback(0), "graphics rejects V4 scrollback");
        var snapshot = checkpoint.ExportGraphicsSnapshot();
        Require(snapshot.AsSpan(0, 8).SequenceEqual("TLSNP2S\0"u8) &&
            BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(10)) == 22, "graphics snapshot is 2.22");
        Status(NativeTerminalStatus.IncompatibleSnapshot, () => NativeTerminalCoreV2.ImportSnapshot(snapshot), "legacy snapshot downgrade rejected");
        using var restored = NativeTerminalCoreV2.ImportGraphicsSnapshot(snapshot);
        using var restoredCheckpoint = restored.CaptureCheckpoint();
        Require(restored.HasGraphicsProfile && restoredCheckpoint.ExportGraphicsSnapshot().SequenceEqual(snapshot), "graphics snapshot roundtrip");
        Require(restoredCheckpoint.ExportRenderFrameV5().SequenceEqual(checkpoint.ExportRenderFrameV5()), "graphics render roundtrip");
        terminal.ApplyOutput(3, Encoding.UTF8.GetBytes("image\r\nsecond\r\nthird\r\nfourth\r\nfifth\r\n"));
        using var history = terminal.CaptureHistory();
        var pageBytes = history.ReadBytesV2();
        var viewportBytes = history.ViewportV2(3);
        var page = history.ReadV2();
        Require(page.Lines.Count > 0 && page.Frames.Any(fragment => fragment.Images.Count > 0 &&
            fragment.Resources.Entries.Count > 0), "history V5 retains image resource closure");
        Require(history.RenderViewportV2(3).Columns == 8, "frozen V5 viewport decode");
        Throws<InvalidOperationException>(() => history.Read(), "graphics rejects history V1");
        Throws<InvalidOperationException>(() => history.Viewport(0), "graphics rejects viewport V1");
        Throws<NativeTerminalHistoryBudgetException>(() => history.ReadV2(maxCells: 1), "history complete-line budget");
        terminal.ApplyOutput(4, Encoding.ASCII.GetBytes("\u001b[3J\u001b[?1049hchanged"));
        terminal.Dispose();
        Require(history.ReadBytesV2().SequenceEqual(pageBytes) && history.ViewportV2(3).SequenceEqual(viewportBytes),
            "history stays frozen after clear, alternate screen and owner disposal");
        history.Dispose();
        Throws<ObjectDisposedException>(() => history.ReadV2(), "disposed history capture");
    }

    private static byte[] EmptyHistoryPage()
    {
        var bytes = new byte[96];
        "TLHPV2\0\0"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 0x00600002);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 96);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 7);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(24), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(40), ulong.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(56), 8);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(72), 1);
        Seal(bytes);
        return bytes;
    }

    private static void Seal(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (index >= 92 && index < 96) continue;
            crc ^= bytes[index];
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82f63b78u & (0u - (crc & 1)));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(92), ~crc);
    }

    private static void Status(NativeTerminalStatus status, Action action, string detail)
    {
        try { action(); }
        catch (NativeTerminalException error) { Require(error.Status == status, detail); return; }
        throw new InvalidOperationException("Accepted " + detail);
    }

    private static void Throws<T>(Action action, string detail) where T : Exception
    {
        try { action(); }
        catch (T) { assertions++; return; }
        throw new InvalidOperationException("Accepted " + detail);
    }

    private static void Require(bool valid, string detail)
    {
        if (!valid) throw new InvalidOperationException(detail);
        assertions++;
    }
}
