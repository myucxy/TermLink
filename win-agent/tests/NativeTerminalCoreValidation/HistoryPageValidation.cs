using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation;

internal static class HistoryPageValidation
{
    private const uint FrozenPageMaxLines = 32;
    private const uint FrozenPageMaxCells = 8192;
    private const uint FrozenPageMaxUtf8Bytes = 4 * 1024 * 1024;
    private const ulong FrozenCaptureBudgetBytes = 128UL * 1024UL * 1024UL;

    internal static void Run()
    {
        using var terminal = NativeTerminalCoreV2.Create(8, 3, 1, 1000,
            NativeTerminalOutputProfile.Utf8, NativeTerminalWidthProfile.Unicode16);
        terminal.ApplyOutput(1, Encoding.UTF8.GetBytes("\u001b]8;;https://example.test/\u001b\\👨‍👩‍👦\u001b]8;;\u001b\\\r\n\u001b#6界\r\nthree\r\nfour\r\nfive\r\nsix\r\n"));
        using var capture = terminal.CaptureHistory();
        var first = capture.Read();
        Require(first.Lines.Count > 1 && first.Frames.All(f => f.WidthProfile == NativeTerminalWidthProfile.Unicode16), "Missing immutable Unicode history");
        Require(first.Frames.Any(f => f.Hyperlinks.Count > 0) && first.Frames.Any(f => f.RenderRows.Any(r => r.LineRendition == NativeTerminalLineRendition.DoubleWidth)), "History styles/links lost");
        var original = capture.ReadBytes();
        var originalViewport = capture.Viewport(1);
        capture.CheckLease();
        var latest = capture.Read(maxLines: 1);
        Require(latest.HasMoreBefore && capture.Read(latest.NextBeforeLineId).Lines.Count == first.Lines.Count - 1, "Incorrect exclusive history anchor");
        try { capture.Read(maxCells: 1); throw new Exception("Budget ignored"); }
        catch (NativeTerminalHistoryBudgetException error) { Require(error.RequiredCells == 8, "Missing required budget"); }
        try { capture.Read(999999); throw new Exception("Stale anchor ignored"); }
        catch (NativeTerminalException error) { Require(error.Status == NativeTerminalStatus.HistoryStaleAnchor, "Wrong stale status"); }
        terminal.ApplyOutput(2, Encoding.ASCII.GetBytes("new\r\n\u001b[3J\u001b[?1049h"));
        terminal.Dispose();
        Require(original.SequenceEqual(capture.ReadBytes()), "Capture changed after clear/alternate/dispose");
        Require(originalViewport.SequenceEqual(capture.Viewport(1)), "Frozen viewport moved after output");
        for (int length = 0; length < original.Length; length++)
        {
            try { NativeTerminalHistoryPage.Decode(original.AsSpan(0,length),capture.CaptureId); throw new Exception("Truncated page accepted"); }
            catch (NativeTerminalAbiException) { }
        }
        using var longTerminal = NativeTerminalCoreV2.Create(2,3,1,1000);
        longTerminal.ApplyOutput(1,Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("界",600)) + "\r\nA\r\nB\r\nC\r\n"));
        using var longCapture = longTerminal.CaptureHistory();
        var longPage = longCapture.Read();
        Require(longPage.Lines.Any(l => l.RowCount == 600) && longPage.Frames.Count > 1, "Long line fragmented into logical lines");
        Console.WriteLine("Immutable history DLL validation passed.");
    }

    /// <summary>
    /// Native-only frozen history budget gate. This deliberately exercises the
    /// production graphics limits without claiming Relay or Android evidence.
    /// </summary>
    internal static void RunFrozenCaptureBudget()
    {
        const uint columns = 32;
        const uint rows = 24;
        const ulong historyCapacityRows = 20_000;
        const int logicalLines = 1_400;
        const int charsPerLine = 640;
        const int applyChunkBytes = 16 * 1024;
        Require((ulong)logicalLines * (ulong)((charsPerLine + (int)columns - 1) / (int)columns) > historyCapacityRows,
            "Frozen budget fixture does not exceed the configured physical history capacity");

        try
        {
            using var terminal = NativeTerminalCoreV2.CreateGraphicsV1(
                columns, rows, 1, historyCapacityRows,
                NativeTerminalOutputProfile.Utf8,
                NativeTerminalWidthProfile.Unicode16);
            var builder = new StringBuilder(logicalLines * (charsPerLine + 2));
            for (var line = 0; line < logicalLines; line++)
            {
                var marker = $"L{line:D4} ";
                builder.Append(marker);
                builder.Append('X', charsPerLine - marker.Length);
                builder.Append("\r\n");
            }

            var output = Encoding.ASCII.GetBytes(builder.ToString());
            try
            {
                ulong sequence = 1;
                for (var offset = 0; offset < output.Length; offset += applyChunkBytes)
                {
                    var length = Math.Min(applyChunkBytes, output.Length - offset);
                    terminal.ApplyOutput(sequence++, output.AsSpan(offset, length));
                }

                using var capture = terminal.CaptureHistory();
                Require(capture.AccountedBytes <= FrozenCaptureBudgetBytes,
                    $"Frozen capture exceeded 128 MiB: accounted_bytes={capture.AccountedBytes}");

                var anchors = new HashSet<ulong>();
                var lineIds = new HashSet<ulong>();
                var before = NativeTerminalHistoryCapture.LatestAnchor;
                var pages = 0;
                ulong rowsSeen = 0;
                ulong cellsSeen = 0;
                var headTruncated = false;
                while (true)
                {
                    Require(pages < 256, "Frozen history exceeded 256 pages");
                    Require(anchors.Add(before), "Frozen history repeated an anchor");
                    var bytes = capture.ReadBytesV2(before, FrozenPageMaxLines,
                        FrozenPageMaxCells, FrozenPageMaxUtf8Bytes);
                    NativeTerminalHistoryPageV2 page;
                    try { page = NativeTerminalHistoryPageV2.Decode(bytes, capture.CaptureId); }
                    finally { Array.Clear(bytes, 0, bytes.Length); }
                    pages++;
                    Require(page.RequestedAnchor == before, "Frozen page anchor changed");
                    Require(page.Lines.Count > 0, "Frozen history returned an empty page");
                    foreach (var line in page.Lines)
                        Require(lineIds.Add(line.Id), "Frozen history repeated a line ID");
                    foreach (var line in page.Lines)
                        rowsSeen = checked(rowsSeen + line.RowCount);
                    cellsSeen = checked(cellsSeen + page.Cells);
                    headTruncated |= page.HeadTruncated;
                    if (!page.HasMoreBefore)
                    {
                        Require(page.HeadTruncated, "Frozen history did not report head truncation");
                        break;
                    }
                    Require(page.NextBeforeLineId != NativeTerminalHistoryCapture.LatestAnchor,
                        "Frozen history returned an invalid next anchor");
                    before = page.NextBeforeLineId;
                }

                Require(headTruncated, "Frozen history never reached the truncated head");
                Console.WriteLine($"Frozen capture budget passed: accounted_bytes={capture.AccountedBytes} " +
                    $"pages={pages} rows={rowsSeen} cells={cellsSeen} lines={lineIds.Count} " +
                    $"head_truncated={headTruncated.ToString().ToLowerInvariant()}");
            }
            finally { Array.Clear(output, 0, output.Length); }
        }
        catch (NativeTerminalException error)
        {
            throw new InvalidOperationException(
                $"Frozen capture budget native limit rejected the fixture: status={error.Status} " +
                $"diagnostic_present={(!string.IsNullOrEmpty(error.Diagnostic)).ToString().ToLowerInvariant()}", error);
        }
    }

    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
