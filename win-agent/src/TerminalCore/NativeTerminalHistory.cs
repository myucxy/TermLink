using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TermLink.TerminalCore;

public sealed partial class NativeTerminal
{
    public NativeTerminalHistoryCapture CaptureHistory()
    {
        lock (sync) { ThrowIfUnavailable(); return NativeTerminalHistoryCapture.Begin(handle, HasGraphicsProfile); }
    }
}

public sealed class NativeTerminalHistoryBudgetException : InvalidOperationException
{
    internal NativeTerminalHistoryBudgetException(uint cells, uint utf8)
        : base($"The next complete history line requires {cells} cells and {utf8} UTF-8 bytes.")
    { RequiredCells = cells; RequiredUtf8Bytes = utf8; }
    public uint RequiredCells { get; }
    public uint RequiredUtf8Bytes { get; }
}

/// <summary>Frozen primary history; reads never extend its five-minute lease.</summary>
public sealed class NativeTerminalHistoryCapture : IDisposable
{
    private readonly object sync = new();
    private CaptureInfo info;
    private bool disposed;
    private NativeTerminalHistoryCapture(CaptureInfo info, bool hasGraphicsProfile)
    { this.info = info; HasGraphicsProfile = hasGraphicsProfile; }
    public bool HasGraphicsProfile { get; }
    public ulong CaptureId => info.CaptureId;
    public ulong Revision => info.Revision;
    public ulong LeaseMilliseconds => info.LeaseMillis;
    public ulong AccountedBytes => info.AccountedBytes;
    public const ulong LatestAnchor = ulong.MaxValue;

    internal static NativeTerminalHistoryCapture Begin(SafeTlTerminalHandle handle, bool hasGraphicsProfile)
    {
        var info = new CaptureInfo { AbiVersion = 1, StructSize = 48 };
        NativeTerminalInterop.ThrowIfFailed(BeginNative(handle, ref info), "historyBegin");
        try
        {
            if (info.AbiVersion != 1 || info.StructSize != 48 || info.CaptureId == 0 || info.OwnerId == 0 ||
                info.AccountedBytes > (ulong)(hasGraphicsProfile ? NativeTerminalCoreV2.MaximumGraphicsSnapshotBytes : NativeTerminalCoreV2.MaximumSnapshotBytes) || info.LeaseMillis != 300000)
                throw new NativeTerminalAbiException("Invalid history capture metadata.");
            return new NativeTerminalHistoryCapture(info, hasGraphicsProfile);
        }
        catch { ReleaseNative(info.CaptureId, info.OwnerId); throw; }
    }

    public NativeTerminalHistoryPage Read(ulong beforeLineId = LatestAnchor, uint maxLines = 128,
        uint maxCells = 65536, uint maxUtf8Bytes = 4 * 1024 * 1024) =>
        NativeTerminalHistoryPage.Decode(ReadBytes(beforeLineId, maxLines, maxCells, maxUtf8Bytes), CaptureId);

    public byte[] ReadBytes(ulong beforeLineId = LatestAnchor, uint maxLines = 128,
        uint maxCells = 65536, uint maxUtf8Bytes = 4 * 1024 * 1024) =>
        ReadBytesCore(false, beforeLineId, maxLines, maxCells, maxUtf8Bytes);

    public NativeTerminalHistoryPageV2 ReadV2(ulong beforeLineId = LatestAnchor, uint maxLines = 128,
        uint maxCells = 65536, uint maxUtf8Bytes = 4 * 1024 * 1024) =>
        NativeTerminalHistoryPageV2.Decode(ReadBytesV2(beforeLineId, maxLines, maxCells, maxUtf8Bytes), CaptureId);

    public byte[] ReadBytesV2(ulong beforeLineId = LatestAnchor, uint maxLines = 128,
        uint maxCells = 65536, uint maxUtf8Bytes = 4 * 1024 * 1024) =>
        ReadBytesCore(true, beforeLineId, maxLines, maxCells, maxUtf8Bytes);

    private byte[] ReadBytesCore(bool graphics, ulong beforeLineId, uint maxLines, uint maxCells, uint maxUtf8Bytes)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(NativeTerminalHistoryCapture));
            RequireProfile(graphics);
            var query = new PageRequest { AbiVersion = 1, StructSize = 64, CaptureId = info.CaptureId,
                OwnerId = info.OwnerId, Revision = info.Revision, BeforeLineId = beforeLineId,
                MaxLines = maxLines, MaxCells = maxCells, MaxUtf8Bytes = maxUtf8Bytes };
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                uint cells, utf8;
                var status = graphics ? PageV2Native(ref query, ref buffer, out cells, out utf8)
                    : PageNative(ref query, ref buffer, out cells, out utf8);
                if (status == NativeTerminalStatus.HistoryPageBudget) throw new NativeTerminalHistoryBudgetException(cells, utf8);
                NativeTerminalInterop.ThrowIfFailed(status, "historyPage");
                if (buffer.AbiVersion != 1 || buffer.StructSize != NativeTerminalInterop.BufferStructSize ||
                    buffer.Data == IntPtr.Zero || buffer.Length < 96 ||
                    buffer.Length > (ulong)(graphics ? NativeTerminalHistoryPageV2.MaximumBytes : NativeTerminalHistoryPage.MaximumBytes))
                    throw new NativeTerminalAbiException("Invalid history page buffer.");
                var result = new byte[checked((int)buffer.Length)];
                Marshal.Copy(buffer.Data, result, 0, result.Length);
                return result;
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                    NativeTerminalInterop.ThrowIfFailed(NativeTerminalInterop.TerminalBufferFree(ref buffer), "historyPage.free");
            }
        }
    }
    public uint ResolveAnchor(ulong lineId, uint cellOffset)
    {
        lock (sync) {
            if (disposed) throw new ObjectDisposedException(nameof(NativeTerminalHistoryCapture));
            NativeTerminalInterop.ThrowIfFailed(ResolveAnchorNative(info.CaptureId, info.OwnerId, info.Revision, lineId, cellOffset, out var offset), "historyResolveAnchor");
            return offset;
        }
    }
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_history_resolve_anchor_v1", CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus ResolveAnchorNative(ulong capture, ulong owner, ulong revision, ulong line, uint cell, out uint offset);

    public void Dispose() { Release(); GC.SuppressFinalize(this); }
    public void CheckLease()
    {
        lock (sync) {
            if (disposed) throw new ObjectDisposedException(nameof(NativeTerminalHistoryCapture));
            NativeTerminalInterop.ThrowIfFailed(CheckNative(info.CaptureId,info.OwnerId,info.Revision), "historyCheck");
        }
    }
    public byte[] Viewport(uint offsetRows) => ViewportCore(offsetRows, false);
    public byte[] ViewportV2(uint offsetRows) => ViewportCore(offsetRows, true);
    public NativeTerminalRenderFrameV5 RenderViewportV2(uint offsetRows) =>
        NativeTerminalRenderFrameV5Decoder.Decode(ViewportV2(offsetRows));

    private byte[] ViewportCore(uint offsetRows, bool graphics)
    {
        lock (sync)
        {
            if (disposed) throw new ObjectDisposedException(nameof(NativeTerminalHistoryCapture));
            RequireProfile(graphics);
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                var status = graphics
                    ? ViewportV2Native(info.CaptureId, info.OwnerId, info.Revision, offsetRows, ref buffer, out _, out _)
                    : ViewportNative(info.CaptureId, info.OwnerId, info.Revision, offsetRows, ref buffer, out _, out _);
                NativeTerminalInterop.ThrowIfFailed(status, "historyViewport");
                if (buffer.AbiVersion != 1 || buffer.StructSize != NativeTerminalInterop.BufferStructSize ||
                    buffer.Data == IntPtr.Zero || buffer.Length < 96 ||
                    buffer.Length > (ulong)(graphics ? NativeTerminalRenderFrameV5Decoder.MaximumFrameBytes : NativeTerminalRenderFrameV4Decoder.MaximumFrameBytes))
                    throw new NativeTerminalAbiException("Invalid frozen viewport buffer.");
                var bytes = new byte[checked((int)buffer.Length)];
                Marshal.Copy(buffer.Data,bytes,0,bytes.Length);
                return bytes;
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                    NativeTerminalInterop.ThrowIfFailed(NativeTerminalInterop.TerminalBufferFree(ref buffer), "historyViewport.free");
            }
        }
    }
    private void RequireProfile(bool graphics)
    {
        if (graphics != HasGraphicsProfile)
            throw new InvalidOperationException("Use history V2 for graphics captures and history V1 for text captures.");
        if (graphics) NativeTerminalInterop.RequireGraphicsV1Abi();
    }
    private void Release()
    {
        lock (sync)
        {
            if (disposed) return;
            disposed = true;
            // A lease that already expired is an equally completed release.
            ReleaseNative(info.CaptureId, info.OwnerId);
        }
    }
    ~NativeTerminalHistoryCapture() { try { Release(); } catch { /* No exceptions on finalizer thread. */ } }

    [StructLayout(LayoutKind.Sequential)] private struct CaptureInfo
    { public uint AbiVersion, StructSize; public ulong CaptureId, OwnerId, Revision, AccountedBytes, LeaseMillis; }
    [StructLayout(LayoutKind.Sequential)] private struct PageRequest
    {
        public uint AbiVersion, StructSize; public ulong CaptureId, OwnerId, Revision, BeforeLineId;
        public uint MaxLines, MaxCells, MaxUtf8Bytes, Reserved0; public ulong Reserved;
    }
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_history_begin_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus BeginNative(SafeTlTerminalHandle terminal, ref CaptureInfo info);
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_history_page_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus PageNative(ref PageRequest request, ref TlTerminalBuffer page, out uint cells, out uint utf8);
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_history_page_v2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus PageV2Native(ref PageRequest request, ref TlTerminalBuffer page, out uint cells, out uint utf8);
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_history_release_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus ReleaseNative(ulong capture, ulong owner);
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_history_check_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus CheckNative(ulong capture, ulong owner, ulong revision);
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_history_viewport_v1", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus ViewportNative(ulong capture, ulong owner, ulong revision, uint offset,
        ref TlTerminalBuffer frame, out uint rows, out ulong generation);
    [DllImport("termlink_terminal_ffi", EntryPoint = "tl_terminal_history_viewport_v2", ExactSpelling = true, CallingConvention = CallingConvention.Cdecl)]
    private static extern NativeTerminalStatus ViewportV2Native(ulong capture, ulong owner, ulong revision, uint offset,
        ref TlTerminalBuffer frame, out uint rows, out ulong generation);
}

public sealed record NativeTerminalHistoryLine(ulong Id, ulong SealedRevision, uint FirstRow,
    uint RowCount, uint FirstCellOffset, bool HeadTruncated);

public sealed class NativeTerminalHistoryPage
{
    public const int MaximumBytes = 16 * 1024 * 1024;
    public ulong Revision { get; private init; }
    public ulong RequestedAnchor { get; private init; }
    public ulong NextBeforeLineId { get; private init; }
    public bool HasMoreBefore { get; private init; }
    public bool HeadTruncated { get; private init; }
    public uint Columns { get; private init; }
    public uint Cells { get; private init; }
    public uint Utf8Bytes { get; private init; }
    public IReadOnlyList<NativeTerminalHistoryLine> Lines { get; private init; }
    public IReadOnlyList<NativeTerminalRenderFrameV4> Frames { get; private init; }

    public static NativeTerminalHistoryPage Decode(ReadOnlySpan<byte> bytes, ulong captureId)
        => DecodeCore(bytes, captureId, false, out _);

    internal static NativeTerminalHistoryPage DecodeCore(ReadOnlySpan<byte> bytes, ulong captureId,
        bool graphics, out IReadOnlyList<NativeTerminalRenderFrameV5> graphicsFrames)
    {
        Require(bytes.Length >= 96 && bytes.Length <= (graphics ? NativeTerminalHistoryPageV2.MaximumBytes : MaximumBytes) && captureId != 0);
        Require(bytes[..8].SequenceEqual(graphics ? "TLHPV2\0\0"u8 : "TLHPV1\0\0"u8) &&
            U32(bytes, 8) == (graphics ? 0x00600002u : 0x00600001u) && U32(bytes, 12) == bytes.Length && U64(bytes, 16) == captureId);
        uint crc = uint.MaxValue;
        for (int index = 0; index < bytes.Length; index++)
        {
            if (index >= 92 && index < 96) continue;
            crc ^= bytes[index];
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0x82f63b78u & (0u - (crc & 1)));
        }
        Require(~crc == U32(bytes, 92));
        Require(U64(bytes, 72) == (graphics ? 1UL : 0UL));
        for (int i = 80; i < 92; i++) Require(bytes[i] == 0);
        uint flags = U32(bytes, 68), lineCount = U32(bytes, 48), frameCount = U32(bytes, 52);
        uint columns = U32(bytes, 56), cells = U32(bytes, 60), utf8 = U32(bytes, 64);
        Require(flags <= 3 && lineCount <= 128 && frameCount <= cells && cells <= 65536 && columns >= 1 && columns <= 512 && utf8 <= 4 * 1024 * 1024);
        int offset = 96 + (int)lineCount * 40;
        Require(offset <= bytes.Length && frameCount <= (bytes.Length - offset) / 100);
        ulong revision = U64(bytes, 24), anchor = U64(bytes, 32), next = U64(bytes, 40);
        var lines = new List<NativeTerminalHistoryLine>((int)lineCount);
        var ids = new HashSet<ulong>();
        ulong totalRows = 0;
        for (int i = 0; i < lineCount; i++)
        {
            int p = 96 + i * 40;
            var line = new NativeTerminalHistoryLine(U64(bytes,p),U64(bytes,p+8),U32(bytes,p+16),U32(bytes,p+20),U32(bytes,p+24),U32(bytes,p+28) == 1);
            Require(line.Id != ulong.MaxValue && line.Id != anchor && ids.Add(line.Id) && line.SealedRevision > 0 && line.SealedRevision <= revision &&
                line.FirstRow == totalRows && line.RowCount > 0 && U32(bytes,p+28) <= 1 && U64(bytes,p+32) == 0 &&
                (line.HeadTruncated || line.FirstCellOffset == 0) && (ulong)line.FirstCellOffset + (ulong)line.RowCount * columns <= uint.MaxValue);
            totalRows += line.RowCount;
            lines.Add(line);
        }
        Require(totalRows * columns == cells && ((flags & 2) != 0) == (lines.Count > 0 && lines[0].HeadTruncated) && next == (lines.Count == 0 ? anchor : lines[0].Id));
        Require(lines.Count > 0 || (flags == 0 && frameCount == 0 && cells == 0 && utf8 == 0));
        var frames = new List<NativeTerminalRenderFrameV4>();
        var retainedGraphicsFrames = new List<NativeTerminalRenderFrameV5>();
        ulong seenRows = 0, textBytes = 0;
        int lineIndex = 0;
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            Require(bytes.Length - offset >= 4);
            uint length = U32(bytes, offset); offset += 4;
            Require(length >= 96 && length <= bytes.Length - offset);
            NativeTerminalRenderFrameV4 frame;
            if (graphics)
            {
                var graphicsFrame = NativeTerminalRenderFrameV5Decoder.Decode(bytes.Slice(offset, (int)length));
                Require(graphicsFrame.Revision == revision);
                retainedGraphicsFrames.Add(graphicsFrame);
                frame = graphicsFrame.Text;
            }
            else frame = NativeTerminalRenderFrameV4Decoder.Decode(bytes.Slice(offset, (int)length));
            offset += (int)length;
            Require(frame.PresentationRevision == revision && frame.NextSourceSequence == revision && frame.Columns == columns && !frame.Cursor.Visible && !frame.Cursor.WrapPending);
            Require(frames.Count == 0 || frame.WidthProfile == frames[0].WidthProfile);
            for (int rowIndex = 0; rowIndex < frame.Rows; rowIndex++)
            {
                while (lineIndex < lines.Count && seenRows >= (ulong)lines[lineIndex].FirstRow + lines[lineIndex].RowCount) lineIndex++;
                Require(lineIndex < lines.Count);
                var row = frame.RenderRows[rowIndex];
                Require(row.SoftWrapped == (seenRows + 1 < (ulong)lines[lineIndex].FirstRow + lines[lineIndex].RowCount && rowIndex + 1 < frame.Rows));
                for (int col = 0; col < row.MeaningfulEnd; col++)
                    if (!row.Cells[col].WideContinuation) textBytes += (uint)Encoding.UTF8.GetByteCount(frame.Graphemes[row.Cells[col].GraphemeId - 1].Text);
                seenRows++;
            }
            frames.Add(frame);
        }
        Require(offset == bytes.Length && seenRows == totalRows && textBytes == utf8);
        graphicsFrames = retainedGraphicsFrames.AsReadOnly();
        return new NativeTerminalHistoryPage { Revision = revision, RequestedAnchor = anchor, NextBeforeLineId = next,
            Columns = columns, Cells = cells, Utf8Bytes = utf8, HeadTruncated = (flags & 2) != 0, HasMoreBefore = (flags & 1) != 0,
            Lines = lines.AsReadOnly(), Frames = frames.AsReadOnly() };
    }
    private static uint U32(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
    private static ulong U64(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(offset, 8));
    private static void Require(bool valid) { if (!valid) throw new NativeTerminalAbiException("Malformed immutable history page."); }
}

/// <summary>A graphics history page retains every V5 fragment and its resource closure.</summary>
public sealed class NativeTerminalHistoryPageV2
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    private readonly NativeTerminalHistoryPage textPage;
    private NativeTerminalHistoryPageV2(NativeTerminalHistoryPage textPage, IReadOnlyList<NativeTerminalRenderFrameV5> frames)
    { this.textPage = textPage; Frames = frames; }
    public ulong RequiredFeatures => 1;
    public ulong Revision => textPage.Revision;
    public ulong RequestedAnchor => textPage.RequestedAnchor;
    public ulong NextBeforeLineId => textPage.NextBeforeLineId;
    public bool HasMoreBefore => textPage.HasMoreBefore;
    public bool HeadTruncated => textPage.HeadTruncated;
    public uint Columns => textPage.Columns;
    public uint Cells => textPage.Cells;
    public uint Utf8Bytes => textPage.Utf8Bytes;
    public IReadOnlyList<NativeTerminalHistoryLine> Lines => textPage.Lines;
    public IReadOnlyList<NativeTerminalRenderFrameV5> Frames { get; }

    public static NativeTerminalHistoryPageV2 Decode(ReadOnlySpan<byte> bytes, ulong captureId)
    {
        var text = NativeTerminalHistoryPage.DecodeCore(bytes, captureId, true, out var frames);
        return new NativeTerminalHistoryPageV2(text, frames);
    }
}
