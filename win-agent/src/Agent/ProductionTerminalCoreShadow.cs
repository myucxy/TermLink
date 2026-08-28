using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using TermLink.LocalIpc;
using TermLink.TerminalCore;

namespace TermLink.Agent
{
    /// <summary>
    /// A bounded diagnostic emitted by the Windows terminal-core shadow.  None of these fields
    /// participate in Relay publication, history sealing, or Android recovery.
    /// </summary>
    internal sealed class ProductionTerminalCoreShadowResult
    {
        public string Status { get; set; } = "NOT_RUN";
        public string ReasonCode { get; set; } = string.Empty;
        public ulong LastAppliedSequence { get; set; }
        public ulong NextSourceSequence { get; set; }
        public ulong PresentationRevision { get; set; }
        public string SnapshotSha256 { get; set; } = string.Empty;
    }

    /// <summary>
    /// Per-session, fail-isolated terminal-core projection used during the Windows migration.
    ///
    /// The Portable model remains the production authority.  A native failure is retained as a
    /// diagnostic and retires only this shadow instance; it can never reject a durable frame,
    /// change an uploaded object, write a Host Effect to ConPTY, or fail the history worker.
    /// </summary>
    internal sealed class ProductionTerminalCoreShadow : IDisposable
    {
        internal const ulong HistoryCapacityRows = 2_000;

        private NativeTerminal terminal;
        private string terminalFailureCode = string.Empty;
        private ulong lastAppliedSequence;
        private bool structuredTitleSeen;
        private bool disposed;

        public ProductionTerminalCoreShadow(int columns, int rows)
        {
            try
            {
                terminal = NativeTerminalCoreV2.Create(
                    checked((uint)columns),
                    checked((uint)rows),
                    initialSourceSequence: 1,
                    historyCapacityRows: HistoryCapacityRows,
                    outputProfile: NativeTerminalOutputProfile.Utf8);
            }
            catch (Exception error)
            {
                terminalFailureCode = FailureCode(error);
            }
        }

        public void Apply(LocalFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (disposed || terminal == null) return;
            try
            {
                switch (frame.Type)
                {
                    case LocalFrameType.Output:
                        terminal.ApplyOutput(frame.Sequence, frame.Payload);
                        break;
                    case LocalFrameType.Resize:
                        LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                        terminal.ApplyResize(
                            frame.Sequence,
                            checked((uint)columns),
                            checked((uint)rows));
                        break;
                    case LocalFrameType.Gap:
                        terminal.ApplyGap(frame.Sequence);
                        break;
                    case LocalFrameType.Title:
                        // A structured Title frame is out-of-band REST metadata rather than VT
                        // output. It still consumes the common source sequence, but subsequent
                        // comparison must not treat the native OSC title as its equivalent.
                        LocalPayloadCodec.ParseTitle(frame.Payload);
                        structuredTitleSeen = true;
                        terminal.ApplyAdvance(frame.Sequence, 0);
                        break;
                    default:
                        // Title/lifecycle/input metadata consumes the same ordered source
                        // sequence but is not a VT mutation.  OSC title changes are already in
                        // Output; the dedicated Title frame remains the REST metadata authority.
                        terminal.ApplyAdvance(frame.Sequence, 0);
                        break;
                }
                lastAppliedSequence = frame.Sequence;
            }
            catch (Exception error)
            {
                Retire(FailureCode(error));
            }
        }

        public ProductionTerminalCoreShadowResult ObserveCheckpoint(
            PortableTerminalSnapshotV1 portable,
            ulong expectedSequence,
            string portableReasonCode)
        {
            var result = new ProductionTerminalCoreShadowResult
            {
                LastAppliedSequence = lastAppliedSequence
            };
            if (disposed)
            {
                result.Status = "SHADOW_RETIRED";
                result.ReasonCode = "DISPOSED";
                return result;
            }
            if (terminal == null)
            {
                result.Status = "SHADOW_RETIRED";
                result.ReasonCode = string.IsNullOrWhiteSpace(terminalFailureCode)
                    ? "NATIVE_UNAVAILABLE"
                    : terminalFailureCode;
                return result;
            }
            if (lastAppliedSequence != expectedSequence)
            {
                result.Status = "SHADOW_DIVERGED";
                result.ReasonCode = "SOURCE_SEQUENCE";
                return result;
            }

            try
            {
                var presentation = terminal.GetPresentationInfo();
                result.NextSourceSequence = presentation.NextSourceSequence;
                if (!terminal.TryCaptureCheckpoint(out var checkpoint, out var status))
                {
                    result.Status = "SHADOW_DEFERRED";
                    result.ReasonCode = "NATIVE_" + status.ToString().ToUpperInvariant();
                    return result;
                }

                using (checkpoint)
                {
                    var snapshot = checkpoint.ExportSnapshot();
                    try
                    {
                        result.SnapshotSha256 = Convert.ToHexString(
                            SHA256.HashData(snapshot)).ToLowerInvariant();
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(snapshot);
                    }

                    var frameBytes = checkpoint.ExportRenderFrameV3();
                    NativeTerminalRenderFrameV3 frame;
                    try
                    {
                        frame = NativeTerminalRenderFrameV3Decoder.Decode(frameBytes);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(frameBytes);
                    }
                    result.NextSourceSequence = frame.NextSourceSequence;
                    result.PresentationRevision = frame.PresentationRevision;

                    if (portable == null)
                    {
                        result.Status = "SHADOW_READY";
                        result.ReasonCode = string.IsNullOrWhiteSpace(portableReasonCode)
                            ? "PORTABLE_UNAVAILABLE"
                            : "PORTABLE_" + portableReasonCode;
                        return result;
                    }

                    var mismatches = Compare(
                        portable,
                        frame,
                        structuredTitleSeen,
                        out var comparisonLimitation);
                    result.Status = mismatches.Count == 0
                        ? (string.IsNullOrWhiteSpace(comparisonLimitation)
                            ? "SHADOW_MATCH"
                            : "SHADOW_MATCH_LIMITED")
                        : "SHADOW_DIVERGED";
                    result.ReasonCode = mismatches.Count == 0
                        ? comparisonLimitation
                        : string.Join("+", mismatches);
                    return result;
                }
            }
            catch (Exception error)
            {
                var code = FailureCode(error);
                Retire(code);
                result.Status = "SHADOW_RETIRED";
                result.ReasonCode = code;
                return result;
            }
        }

        private static List<string> Compare(
            PortableTerminalSnapshotV1 portable,
            NativeTerminalRenderFrameV3 frame,
            bool structuredTitleSeen,
            out string comparisonLimitation)
        {
            var limitations = new List<string>();
            if (structuredTitleSeen) limitations.Add("TITLE_OUT_OF_BAND");
            var mismatch = new List<string>();
            if (frame.NextSourceSequence != portable.Sequence + 1)
                mismatch.Add("NEXT_SEQUENCE");
            if (frame.Columns != portable.Columns || frame.Rows != portable.Rows)
                mismatch.Add("DIMENSIONS");
            if ((frame.ActiveScreen == NativeTerminalRenderScreenV1.Alternate) !=
                portable.ActiveAlternateScreen)
                mismatch.Add("ACTIVE_SCREEN");
            if (!structuredTitleSeen &&
                !string.Equals(frame.Title, portable.Title, StringComparison.Ordinal))
                mismatch.Add("TITLE");
            if (frame.ReverseVideo) mismatch.Add("GLOBAL_REVERSE_VIDEO");
            var portableCursorColumn = portable.ActiveAlternateScreen
                ? portable.AlternateCursorColumn
                : portable.CursorColumn;
            var portableCursorRow = portable.ActiveAlternateScreen
                ? portable.AlternateCursorRow
                : portable.CursorRow;
            var portablePendingWrap = portable.ActiveAlternateScreen
                ? portable.AlternatePendingAutoWrap
                : portable.PendingAutoWrap;
            if (frame.Cursor.Column != portableCursorColumn ||
                frame.Cursor.Row != portableCursorRow ||
                frame.Cursor.Visible != portable.CursorVisible ||
                frame.Cursor.WrapPending != portablePendingWrap)
                mismatch.Add("CURSOR");
            if ((int)frame.Cursor.Shape != (int)portable.CursorStyle ||
                frame.Cursor.Blinking != portable.CursorBlink)
                mismatch.Add("CURSOR_STYLE");

            var portableRows = portable.ActiveAlternateScreen
                ? portable.AlternateScreenRows
                : portable.ScreenRows;
            if (!RowsEqual(portableRows, portable, frame, out var cellMismatch))
                mismatch.Add(cellMismatch);
            if (!PaletteEqual(portable, frame, out var themeUnresolved)) mismatch.Add("PALETTE");
            if (themeUnresolved) limitations.Add("PALETTE_THEME_UNRESOLVED");
            comparisonLimitation = string.Join("+", limitations);
            return mismatch;
        }

        private static bool RowsEqual(
            IReadOnlyList<PortableTerminalRowV1> portableRows,
            PortableTerminalSnapshotV1 portableSnapshot,
            NativeTerminalRenderFrameV3 frame,
            out string mismatch)
        {
            mismatch = "VISIBLE_CELLS";
            if (portableRows.Count != frame.RenderRows.Count) return false;
            for (var rowIndex = 0; rowIndex < portableRows.Count; rowIndex++)
            {
                var portable = portableRows[rowIndex];
                var native = frame.RenderRows[rowIndex];
                if (portable.Wrapped != native.SoftWrapped ||
                    portable.Cells.Count != native.Cells.Count)
                    return false;
                for (var column = 0; column < portable.Cells.Count; column++)
                {
                    var portableCell = portable.Cells[column];
                    var nativeCell = native.Cells[column];
                    if (portableCell.WideContinuation != nativeCell.WideContinuation)
                        return false;
                    if (nativeCell.IsProtected)
                    {
                        mismatch = "CELL_METADATA";
                        return false;
                    }
                    if (!RenditionEqual(portableCell.Style, nativeCell, frame))
                    {
                        mismatch = "RENDITION";
                        return false;
                    }
                    if (!HyperlinkEqual(
                            portableCell.Style,
                            portableSnapshot,
                            nativeCell,
                            frame))
                    {
                        mismatch = "HYPERLINK";
                        return false;
                    }
                    if (nativeCell.WideContinuation)
                    {
                        if (nativeCell.GraphemeId != 0 || portableCell.Character.Length != 0)
                            return false;
                        continue;
                    }
                    if (nativeCell.GraphemeId < 1 ||
                        nativeCell.GraphemeId > frame.Graphemes.Count)
                        return false;
                    var grapheme = frame.Graphemes[nativeCell.GraphemeId - 1];
                    if (!string.Equals(grapheme.Text, portableCell.Character, StringComparison.Ordinal) ||
                        grapheme.DisplayWidth != portableCell.DisplayWidth)
                        return false;
                }
            }
            return true;
        }

        private static bool RenditionEqual(
            PortableTerminalStyleV1 portable,
            NativeTerminalRenderCellV1 cell,
            NativeTerminalRenderFrameV3 frame)
        {
            if (cell.RenditionId < 0 || cell.RenditionId >= frame.Renditions.Count)
                return false;
            var native = frame.Renditions[cell.RenditionId];
            var attributes = portable.Attributes;
            return ColorEqual(portable.Foreground, native.Foreground) &&
                ColorEqual(portable.Background, native.Background) &&
                native.UnderlineColor.Kind == NativeTerminalRenderColorKindV1.Default &&
                native.Bold == attributes.HasFlag(PortableTerminalAttributesV1.Bold) &&
                native.Blink == attributes.HasFlag(PortableTerminalAttributesV1.Blink) &&
                native.Italic == attributes.HasFlag(PortableTerminalAttributesV1.Italic) &&
                native.Inverse == attributes.HasFlag(PortableTerminalAttributesV1.Inverse) &&
                native.Conceal == attributes.HasFlag(PortableTerminalAttributesV1.Invisible) &&
                native.Strikethrough == attributes.HasFlag(PortableTerminalAttributesV1.Strikethrough) &&
                native.Faint == attributes.HasFlag(PortableTerminalAttributesV1.Dim) &&
                !native.Overline &&
                native.UnderlineStyle ==
                    (attributes.HasFlag(PortableTerminalAttributesV1.Underline)
                        ? NativeTerminalRenderUnderlineStyleV3.Single
                        : NativeTerminalRenderUnderlineStyleV3.None);
        }

        private static bool ColorEqual(int portable, NativeTerminalRenderColorV1 native)
        {
            if (portable == PortableTerminalStyleV1.DefaultColor)
                return native.Kind == NativeTerminalRenderColorKindV1.Default;
            if (PortableTerminalStyleV1.TryGetRgb(portable, out var rgb))
                return native.Kind == NativeTerminalRenderColorKindV1.Rgb && native.Value == rgb;
            return native.Kind == NativeTerminalRenderColorKindV1.Indexed &&
                native.Value == portable;
        }

        private static bool HyperlinkEqual(
            PortableTerminalStyleV1 portable,
            PortableTerminalSnapshotV1 portableSnapshot,
            NativeTerminalRenderCellV1 native,
            NativeTerminalRenderFrameV3 frame)
        {
            if (portable.HyperlinkIndexPlusOne == 0)
                return native.HyperlinkReference == 0;
            var portableIndex = portable.HyperlinkIndexPlusOne - 1;
            if (portableIndex < 0 || portableIndex >= portableSnapshot.Hyperlinks.Count ||
                native.HyperlinkReference == 0)
                return false;
            for (var index = 0; index < frame.Hyperlinks.Count; index++)
            {
                var candidate = frame.Hyperlinks[index];
                if (candidate.Reference == native.HyperlinkReference)
                {
                    return string.Equals(
                        candidate.Uri,
                        portableSnapshot.Hyperlinks[portableIndex].Uri,
                        StringComparison.Ordinal);
                }
            }
            return false;
        }

        private static bool PaletteEqual(
            PortableTerminalSnapshotV1 portable,
            NativeTerminalRenderFrameV3 frame,
            out bool themeUnresolved)
        {
            var native = frame.Palette;
            themeUnresolved = !native.DefaultForegroundRgb.HasValue ||
                !native.DefaultBackgroundRgb.HasValue ||
                !native.DefaultCursorRgb.HasValue ||
                native.ColorsRgb.Count < portable.PaletteRgb.Count;
            if (native.DefaultForegroundRgb.HasValue &&
                native.DefaultForegroundRgb.Value != portable.DefaultForegroundRgb)
                return false;
            if (native.DefaultBackgroundRgb.HasValue &&
                native.DefaultBackgroundRgb.Value != portable.DefaultBackgroundRgb)
                return false;
            if (native.DefaultCursorRgb.HasValue &&
                native.DefaultCursorRgb.Value != portable.DefaultCursorRgb)
                return false;
            var count = Math.Min(native.ColorsRgb.Count, portable.PaletteRgb.Count);
            for (var index = 0; index < count; index++)
            {
                if (!native.ColorsRgb[index].HasValue) themeUnresolved = true;
                if (native.ColorsRgb[index].HasValue &&
                    native.ColorsRgb[index].Value != portable.PaletteRgb[index])
                    return false;
            }
            return true;
        }

        private static string FailureCode(Exception error)
        {
            if (error is NativeTerminalException native)
                return "NATIVE_" + native.Status.ToString().ToUpperInvariant();
            if (error is DllNotFoundException) return "NATIVE_DLL_NOT_FOUND";
            if (error is BadImageFormatException) return "NATIVE_ARCHITECTURE";
            if (error is EntryPointNotFoundException) return "NATIVE_ENTRY_POINT";
            if (error is NativeTerminalAbiException) return "NATIVE_ABI";
            if (error is OverflowException) return "INTEGER_OVERFLOW";
            if (error is ArgumentException) return "INVALID_FRAME";
            return "NATIVE_" + error.GetType().Name.ToUpperInvariant();
        }

        private void Retire(string reasonCode)
        {
            terminalFailureCode = string.IsNullOrWhiteSpace(reasonCode)
                ? "NATIVE_FAILURE"
                : reasonCode;
            var current = terminal;
            terminal = null;
            try { current?.Dispose(); }
            catch { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Retire("DISPOSED");
        }
    }
}
