using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TermLink.Agent;

namespace TermLink.PortableTerminalModel.Validation
{
    internal static class Program
    {
        private sealed class GoldenEvent
        {
            public ulong Sequence;
            public string Type;
            public int Columns;
            public int Rows;
            public int DataOffset;
            public int DataLength;
        }

        public static int Main(string[] args)
        {
            try
            {
                var repositoryRoot = ParseRepositoryRoot(args);
                ValidateSupportedSnapshotRoundTrip();
                ValidateIncompleteBoundariesAndRejections();
                ValidateTopBottomScrollMargins();
                ValidateEditingModesTabsAndAlternateScreen();
                ValidateCodexControlAndColorCompatibility();
                ValidateUnicodeWidthAndCombining();
                ValidateOscControlStrings();
                ValidateClearCursorAndResizeContracts();
                ValidateParserInterruptAndDcsContracts();
                ValidateSavedCursorAndAlternateContracts();
                ValidateMarginRectangleAndEraseContracts();
                ValidateTabAndCharsetContracts();
                ValidateResizeAndScrollbackInvariants();
                ValidateOscSafetyLimits();
                var golden = ValidateGoldenStreams(repositoryRoot);
                Console.WriteLine(
                    "Portable terminal model validation passed. " +
                    "golden_cases=" + golden.Cases +
                    "; snapshot_suffix_checks=" + golden.Checkpoints +
                    "; rejected_cases=" + golden.RejectedCases + ".");
                PrintCoverageMatrix();
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidateSupportedSnapshotRoundTrip()
        {
            var full = new PortableTerminalModelV1(8, 4);
            full.AdvanceNonTerminalEvent(1);
            full.ApplyOutput(2, Bytes("ab\r\ncd\u001b[31;1mR\u001b[0m"));
            Require(full.SnapshotEligible, "Supported text/SGR stream was not snapshot eligible.");
            Require(full.TryCreateSnapshot(out var snapshot), "Supported state did not produce a snapshot.");
            var json = JsonSerializer.Serialize(snapshot);
            Require(json.Contains(PortableTerminalSnapshotV1.CurrentFormatName, StringComparison.Ordinal),
                "Snapshot DTO did not serialize its format identity.");

            full.ApplyOutput(3, Bytes("\u001b[3;8HZQ"));
            var restored = PortableTerminalModelV1.Restore(snapshot);
            restored.ApplyOutput(3, Bytes("\u001b[3;8HZQ"));
            Require(full.ValidationFingerprint() == restored.ValidationFingerprint(),
                "Snapshot plus suffix did not match full replay for the supported subset.");

            Require(restored.TryCreateSnapshot(out var second), "Restored supported state was not eligible.");
            Require(second.ScreenRows.Count == 4 && second.ScreenRows[2].Wrapped,
                "Auto-wrap state was not retained through snapshot restoration.");
        }

        private static void ValidateIncompleteBoundariesAndRejections()
        {
            var splitCsi = new PortableTerminalModelV1(8, 4);
            splitCsi.ApplyOutput(1, Bytes("ok\u001b["));
            Require(!splitCsi.SnapshotEligible && splitCsi.ParserBoundaryCode == "INCOMPLETE_CSI",
                "An incomplete CSI was accepted as a snapshot boundary.");
            splitCsi.ApplyOutput(2, Bytes("32mgreen\u001b[0m"));
            Require(splitCsi.SnapshotEligible && splitCsi.ParserBoundaryCode == "GROUND",
                "A completed supported CSI did not restore the safe boundary.");

            var splitUtf8 = new PortableTerminalModelV1(8, 4);
            splitUtf8.ApplyOutput(1, new byte[] { 0xE4, 0xB8 });
            Require(!splitUtf8.SnapshotEligible && splitUtf8.ParserBoundaryCode == "INCOMPLETE_UTF8",
                "An incomplete UTF-8 scalar was accepted as a snapshot boundary.");
            splitUtf8.ApplyOutput(2, new byte[] { 0xAD });
            Require(splitUtf8.SnapshotEligible && splitUtf8.ParserBoundaryCode == "GROUND",
                "A completed valid UTF-8 scalar did not return to a safe boundary.");
            Require(splitUtf8.TryCreateSnapshot(out var splitSnapshot) &&
                splitSnapshot.ScreenRows[0].Cells[0].Character == "中" &&
                splitSnapshot.ScreenRows[0].Cells[0].DisplayWidth == 2 &&
                splitSnapshot.ScreenRows[0].Cells[1].WideContinuation,
                "A split CJK scalar was not represented as one width-two grapheme.");

            RequireRejected("DCS_UNSUPPORTED", Bytes("\u001bP1;2|data\u001b\\"));
            RequireRejected("DCS_SIXEL_UNSUPPORTED", Bytes("\u001bPq#0;2;0;0;0~\u001b\\"));
            RequireRejected("C0_CONTROL_UNSUPPORTED", new byte[] { 0x01 });
            RequireRejected("INVALID_UTF8", new byte[] { 0xE0, 0x80, 0x80 });
            RequireRejected("INVALID_UTF8", new byte[] { 0xED, 0xA0, 0x80 });

            var truncatedUtf8 = new PortableTerminalModelV1(8, 4);
            truncatedUtf8.ApplyOutput(1, new byte[] { 0xF0, 0x9F });
            Require(!truncatedUtf8.SnapshotEligible && truncatedUtf8.ParserBoundaryCode == "INCOMPLETE_UTF8",
                "A truncated UTF-8 scalar did not fail closed at the snapshot boundary.");
            truncatedUtf8.ApplyOutput(2, Bytes("x"));
            Require(truncatedUtf8.PermanentRejectionCode == "INVALID_UTF8",
                "A non-continuation after truncated UTF-8 was not rejected permanently.");

            var gap = new PortableTerminalModelV1(8, 4);
            gap.AdvanceNonTerminalEvent(2);
            Require(gap.PermanentRejectionCode == "SEQUENCE_NOT_CONTIGUOUS",
                "A non-contiguous producer sequence remained eligible.");
        }

        private static void ValidateTopBottomScrollMargins()
        {
            var model = new PortableTerminalModelV1(6, 5);
            model.ApplyOutput(1, Bytes("0\r\n1\r\n2\r\n3\r\n4"));
            model.ApplyOutput(2, Bytes("\u001b[2;4r\u001b[4;1H\n"));
            Require(model.TryCreateSnapshot(out var snapshot),
                "DECSTBM made a common terminal stream permanently ineligible.");
            Require(snapshot.ScrollTop == 1 && snapshot.ScrollBottomExclusive == 4,
                "DECSTBM margins were not retained in the portable checkpoint.");
            Require(snapshot.ScreenRows[0].Cells[0].Character == "0" &&
                snapshot.ScreenRows[1].Cells[0].Character == "2" &&
                snapshot.ScreenRows[2].Cells[0].Character == "3" &&
                snapshot.ScreenRows[3].Cells[0].Character == " " &&
                snapshot.ScreenRows[4].Cells[0].Character == "4",
                "A line feed did not scroll only the configured vertical region.");

            var restored = PortableTerminalModelV1.Restore(snapshot);
            model.ApplyOutput(3, Bytes("X\u001b[r\u001b[5;1HY"));
            restored.ApplyOutput(3, Bytes("X\u001b[r\u001b[5;1HY"));
            Require(model.ValidationFingerprint() == restored.ValidationFingerprint(),
                "DECSTBM state diverged after checkpoint restoration.");
            Require(restored.TryCreateSnapshot(out var reset) &&
                reset.ScrollTop == 0 && reset.ScrollBottomExclusive == 5,
                "CSI r did not restore the full-screen scrolling region.");
        }

        private static void ValidateCodexControlAndColorCompatibility()
        {
            var model = new PortableTerminalModelV1(16, 4);
            model.ApplyOutput(1, Bytes(
                "abc\bZ\tX" +
                "\u001b[?2004h\u001b[?1004l" +
                "\u001b[0 q\u001b[25l" +
                "\u001b[38;2;246;226;183mF" +
                "\u001b[48;5;42mB\u001b[m"));
            Require(model.TryCreateSnapshot(out var snapshot),
                "Codex control/color subset did not produce a checkpoint.");
            Require(snapshot.ScreenRows[0].Cells[2].Character == "Z" &&
                snapshot.ScreenRows[0].Cells[8].Character == "X",
                "Backspace or horizontal tab did not preserve the Codex grid.");
            Require(snapshot.BracketedPaste && !snapshot.FocusReporting && !snapshot.CursorVisible &&
                snapshot.CursorStyle == PortableTerminalCursorStyleV1.Block && snapshot.CursorBlink,
                "Codex terminal modes were not captured in the checkpoint.");
            Require(PortableTerminalStyleV1.TryGetRgb(snapshot.ScreenRows[0].Cells[9].Style.Foreground, out var rgb) &&
                rgb == 0x00f6e2b7 && snapshot.ScreenRows[0].Cells[10].Style.Background == 42,
                "Codex true-color or 256-color SGR was not captured losslessly.");

            model.ApplyOutput(2, Bytes("\u001b[?2026hpartial"));
            Require(!model.SnapshotEligible && model.ParserBoundaryCode == "SYNCHRONIZED_OUTPUT_ACTIVE",
                "Synchronized output exposed a partial presentation checkpoint.");
            model.ApplyOutput(3, Bytes("\u001b[?2026l"));
            Require(model.TryCreateSnapshot(out var synchronizedSnapshot),
                "Synchronized output did not publish after the atomic batch ended.");

            var restored = PortableTerminalModelV1.Restore(synchronizedSnapshot);
            model.ApplyOutput(4, Bytes("!"));
            restored.ApplyOutput(4, Bytes("!"));
            Require(model.ValidationFingerprint() == restored.ValidationFingerprint(),
                "Codex modes/colors diverged after checkpoint restoration.");
        }

        private static void ValidateEditingModesTabsAndAlternateScreen()
        {
            var characters = new PortableTerminalModelV1(10, 5);
            characters.ApplyOutput(1, Bytes("abcdef\u001b[1;3H\u001b[2@XY\u001b[1;5H\u001b[2P\u001b[1;9H\u001b[2X"));
            Require(characters.TryCreateSnapshot(out var characterSnapshot) &&
                RowText(characterSnapshot.ScreenRows[0]) == "abXYef    ",
                "ICH/DCH/ECH did not preserve Windows Terminal character-edit semantics.");

            var lines = new PortableTerminalModelV1(6, 5);
            lines.ApplyOutput(1, Bytes("0\r\n1\r\n2\r\n3\r\n4"));
            lines.ApplyOutput(2, Bytes("\u001b[2;4r\u001b[3;1H\u001b[LX\u001b[M\u001b[S\u001b[T"));
            Require(lines.TryCreateSnapshot(out var lineSnapshot) &&
                lineSnapshot.ScrollTop == 1 && lineSnapshot.ScrollBottomExclusive == 4,
                "IL/DL/SU/SD rejected a valid scrolling-region stream.");

            var modes = new PortableTerminalModelV1(8, 5);
            modes.ApplyOutput(1, Bytes(
                "\u001b[2;4r\u001b[?6h\u001b[2;2H" +
                "\u001b[4hAB\u001b[4l" +
                "\u001b[?7l\u001b[1;8HXY\u001b[?7h" +
                "\u001b7\u001b[4;4HZ\u001b8Q"));
            Require(modes.TryCreateSnapshot(out var modeSnapshot) &&
                modeSnapshot.OriginMode && modeSnapshot.AutoWrap && !modeSnapshot.InsertMode &&
                modeSnapshot.CursorRow == 1 && modeSnapshot.PendingAutoWrap,
                "Origin/insert/autowrap or DECSC/DECRC state was not modeled.");

            var tabs = new PortableTerminalModelV1(16, 4);
            tabs.ApplyOutput(1, Bytes("\u001b[3g\u001b[1;5H\u001bH\r\tA\u001b[1;5H\u001b[g\r\tB"));
            Require(tabs.TryCreateSnapshot(out var tabSnapshot) &&
                tabSnapshot.TabStops.Count == 0 &&
                tabSnapshot.ScreenRows[0].Cells[4].Character == "A" &&
                tabSnapshot.ScreenRows[0].Cells[15].Character == "B",
                "HTS/TBC did not update the dynamic tab-stop bitmap.");

            var alternate = new PortableTerminalModelV1(8, 4);
            alternate.ApplyOutput(1, Bytes("main\u001b[?1049halt"));
            Require(alternate.TryCreateSnapshot(out var alternateSnapshot) &&
                alternateSnapshot.ActiveAlternateScreen &&
                RowText(alternateSnapshot.ScreenRows[0]).StartsWith("main", StringComparison.Ordinal) &&
                RowText(alternateSnapshot.AlternateScreenRows[0]).Substring(4).StartsWith("alt", StringComparison.Ordinal),
                "DECSET 1049 did not preserve independent primary and alternate grids.");
            var resumed = PortableTerminalModelV1.Restore(alternateSnapshot);
            alternate.ApplyOutput(2, Bytes("\u001b[?1049lX"));
            resumed.ApplyOutput(2, Bytes("\u001b[?1049lX"));
            Require(alternate.ValidationFingerprint() == resumed.ValidationFingerprint() &&
                resumed.TryCreateSnapshot(out var restored) &&
                !restored.ActiveAlternateScreen &&
                RowText(restored.ScreenRows[0]).StartsWith("mainX", StringComparison.Ordinal),
                "DECRST 1049 did not restore the primary cursor and grid after checkpoint restore.");

            var charset = new PortableTerminalModelV1(12, 4);
            charset.ApplyOutput(1, Bytes("\u001b(0lqqk\u001b(B \u001b)0\u000ex\u000f"));
            Require(charset.TryCreateSnapshot(out var charsetSnapshot) &&
                RowText(charsetSnapshot.ScreenRows[0]).StartsWith("┌──┐ │", StringComparison.Ordinal) &&
                charsetSnapshot.LineDrawingG1 && charsetSnapshot.CharsetGl == 0,
                "DEC special graphics designation or SI/SO selection was not preserved.");

            var rectangles = new PortableTerminalModelV1(8, 4);
            rectangles.ApplyOutput(1, Bytes(
                "\u001b[35;1;2;2;4$x" +
                "\u001b[2;3;2;3$z" +
                "\u001b[1;2;1;4;1;3;5;1$v"));
            Require(rectangles.TryCreateSnapshot(out var rectangleSnapshot) &&
                RowText(rectangleSnapshot.ScreenRows[0]) == " ###    " &&
                RowText(rectangleSnapshot.ScreenRows[1]) == " # #    " &&
                RowText(rectangleSnapshot.ScreenRows[2]) == "    ### ",
                "DECFRA/DECERA/DECCRA did not produce the expected rectangular grid edits.");
        }

        private static void ValidateUnicodeWidthAndCombining()
        {
            var model = new PortableTerminalModelV1(14, 4);
            model.ApplyOutput(1, Bytes("命令: 中文"));
            Require(model.SnapshotEligible, "Common Chinese UTF-8 output was not snapshot eligible.");
            Require(model.TryCreateSnapshot(out var snapshot),
                "Common Chinese UTF-8 output was not snapshot eligible.");
            Require(snapshot.ScreenRows[0].Cells[0].Character == "命" &&
                snapshot.ScreenRows[0].Cells[0].DisplayWidth == 2 &&
                snapshot.ScreenRows[0].Cells[1].WideContinuation &&
                snapshot.ScreenRows[0].Cells[2].Character == "令" &&
                snapshot.CursorColumn == 10,
                "Common CJK graphemes did not consume two columns deterministically.");

            model.ApplyOutput(2, Bytes("\r\ne\u0301 中\uFE0F"));
            Require(model.SnapshotEligible && model.TryCreateSnapshot(out snapshot),
                "Combining marks or variation selectors made the model ineligible.");
            Require(snapshot.ScreenRows[1].Cells[0].Character == "e\u0301" &&
                snapshot.ScreenRows[1].Cells[0].DisplayWidth == 1,
                "A combining mark did not remain attached to its width-one base grapheme.");
            Require(snapshot.ScreenRows[1].Cells[2].Character == "中\uFE0F" &&
                snapshot.ScreenRows[1].Cells[2].DisplayWidth == 2 &&
                snapshot.ScreenRows[1].Cells[3].WideContinuation,
                "A variation selector did not remain attached to its CJK base grapheme.");
            var restored = PortableTerminalModelV1.Restore(snapshot);
            model.ApplyOutput(3, Bytes("!"));
            restored.ApplyOutput(3, Bytes("!"));
            Require(model.ValidationFingerprint() == restored.ValidationFingerprint(),
                "Unicode width/combining state diverged after snapshot restoration.");

            var emojiVariation = new PortableTerminalModelV1(8, 4);
            emojiVariation.ApplyOutput(1, Bytes("\u2764\uFE0F!"));
            Require(emojiVariation.TryCreateSnapshot(out var emojiSnapshot) &&
                emojiSnapshot.ScreenRows[0].Cells[0].Character == "\u2764\uFE0F" &&
                emojiSnapshot.ScreenRows[0].Cells[0].DisplayWidth == 2 &&
                emojiSnapshot.ScreenRows[0].Cells[1].WideContinuation &&
                emojiSnapshot.ScreenRows[0].Cells[2].Character == "!",
                "Emoji presentation selection did not widen the preceding grapheme safely.");

            var wideWrap = new PortableTerminalModelV1(4, 3);
            wideWrap.ApplyOutput(1, Bytes("abc\U00020000Z"));
            Require(wideWrap.TryCreateSnapshot(out var wideSnapshot) &&
                wideSnapshot.ScreenRows[0].Wrapped &&
                wideSnapshot.ScreenRows[1].Cells[0].Character == "\U00020000" &&
                wideSnapshot.ScreenRows[1].Cells[0].DisplayWidth == 2 &&
                wideSnapshot.ScreenRows[1].Cells[1].WideContinuation &&
                wideSnapshot.ScreenRows[1].Cells[2].Character == "Z",
                "A supplementary CJK grapheme at the right margin did not wrap safely.");

            var splitCombining = new PortableTerminalModelV1(8, 4);
            splitCombining.ApplyOutput(1, Bytes("e"));
            splitCombining.ApplyOutput(2, new byte[] { 0xCC });
            Require(!splitCombining.SnapshotEligible && splitCombining.ParserBoundaryCode == "INCOMPLETE_UTF8",
                "A split combining scalar was accepted before completion.");
            splitCombining.ApplyOutput(3, new byte[] { 0x81 });
            Require(splitCombining.TryCreateSnapshot(out var combined) &&
                combined.ScreenRows[0].Cells[0].Character == "e\u0301",
                "A split combining scalar was not attached after completion.");

            RequireRejected("COMBINING_MARK_WITHOUT_BASE", Bytes("\u0301"));
        }

        private static void ValidateOscControlStrings()
        {
            var bel = new PortableTerminalModelV1(12, 4);
            bel.ApplyOutput(1, Bytes("A\u001b]0;中文标题\u0007B"));
            Require(bel.SnapshotEligible && bel.TryCreateSnapshot(out var belSnapshot) &&
                belSnapshot.ScreenRows[0].Cells[0].Character == "A" &&
                belSnapshot.ScreenRows[0].Cells[1].Character == "B" &&
                belSnapshot.Title == "中文标题",
                "A BEL-terminated OSC title swallowed following terminal output.");

            var st = new PortableTerminalModelV1(12, 4);
            st.ApplyOutput(1, Bytes("X\u001b]2;中文\u001b\\Y"));
            Require(st.SnapshotEligible && st.TryCreateSnapshot(out var stSnapshot) &&
                stSnapshot.ScreenRows[0].Cells[0].Character == "X" &&
                stSnapshot.ScreenRows[0].Cells[1].Character == "Y",
                "An ST-terminated OSC title swallowed following terminal output.");

            var c1 = new PortableTerminalModelV1(12, 4);
            c1.ApplyOutput(1, Bytes("M\u009D2;中文\u009CN"));
            Require(c1.TryCreateSnapshot(out var c1Snapshot) &&
                c1Snapshot.ScreenRows[0].Cells[0].Character == "M" &&
                c1Snapshot.ScreenRows[0].Cells[1].Character == "N",
                "A C1 OSC/ST control string swallowed following terminal output.");

            var split = new PortableTerminalModelV1(12, 4);
            var prefix = Bytes("P\u001b]0;");
            var first = new byte[prefix.Length + 2];
            Array.Copy(prefix, first, prefix.Length);
            first[first.Length - 2] = 0xE4;
            first[first.Length - 1] = 0xB8;
            split.ApplyOutput(1, first);
            Require(!split.SnapshotEligible && split.ParserBoundaryCode == "INCOMPLETE_UTF8",
                "A split OSC UTF-8 scalar did not keep the snapshot boundary closed.");
            split.ApplyOutput(2, new byte[] { 0xAD, 0x1B });
            Require(!split.SnapshotEligible && split.ParserBoundaryCode == "INCOMPLETE_OSC",
                "A split OSC ST terminator was accepted before its final byte.");
            split.ApplyOutput(3, Bytes("\\Q"));
            Require(split.SnapshotEligible && split.TryCreateSnapshot(out var splitSnapshot) &&
                splitSnapshot.ScreenRows[0].Cells[0].Character == "P" &&
                splitSnapshot.ScreenRows[0].Cells[1].Character == "Q",
                "A chunked UTF-8 OSC title did not resume ordinary output after ST.");

            RequireRejected("INVALID_UTF8", new byte[] { 0x1B, (byte)']', (byte)'0', (byte)';', 0xE0, 0x80, 0x80 });

            var presentation = new PortableTerminalModelV1(12, 4);
            presentation.ApplyOutput(1, Bytes(
                "\u001b]4;1;rgb:ff/22/33\u0007" +
                "\u001b]10;#abcdef\u001b\\" +
                "\u001b]8;id=docs;https://example.test/docs\u001b\\link" +
                "\u001b]8;;\u001b\\"));
            Require(presentation.TryCreateSnapshot(out var presentationSnapshot) &&
                presentationSnapshot.PaletteRgb[1] == 0x00ff2233 &&
                presentationSnapshot.DefaultForegroundRgb == 0x00abcdef &&
                presentationSnapshot.Hyperlinks.Count == 1 &&
                presentationSnapshot.Hyperlinks[0].Uri == "https://example.test/docs" &&
                presentationSnapshot.ScreenRows[0].Cells[0].Style.HyperlinkIndexPlusOne == 1 &&
                presentationSnapshot.CurrentStyle.HyperlinkIndexPlusOne == 0,
                "OSC palette/default color or hyperlink state was not retained.");
            var restoredPresentation = PortableTerminalModelV1.Restore(presentationSnapshot);
            presentation.ApplyOutput(2, Bytes("!"));
            restoredPresentation.ApplyOutput(2, Bytes("!"));
            Require(presentation.ValidationFingerprint() == restoredPresentation.ValidationFingerprint(),
                "OSC presentation state diverged after checkpoint restoration.");
        }

        private static void ValidateClearCursorAndResizeContracts()
        {
            var pristine = new PortableTerminalModelV1(5, 3);
            pristine.ApplyResize(1, 7, 4);
            Require(pristine.SnapshotEligible && pristine.Columns == 7 && pristine.Rows == 4,
                "A pristine resize was not modeled exactly.");
            pristine.ApplyOutput(2, Bytes("abcdefgX"));
            pristine.ApplyOutput(3, Bytes("\u001b[1;3H\u001b[0K"));
            Require(pristine.SnapshotEligible, "Supported cursor/erase operations became ineligible.");
            Require(pristine.TryCreateSnapshot(out var snapshot), "Cursor/erase state did not snapshot.");
            Require(snapshot.ScreenRows[0].Cells[0].Character == "a" &&
                snapshot.ScreenRows[0].Cells[1].Character == "b" &&
                snapshot.ScreenRows[0].Cells[2].Character == " ",
                "Erase-in-line did not preserve the prefix and clear the requested suffix.");

            var reflow = new PortableTerminalModelV1(5, 3);
            reflow.ApplyOutput(1, Bytes("content中!"));
            reflow.ApplyResize(2, 6, 4);
            Require(reflow.TryCreateSnapshot(out var reflowed) &&
                reflowed.Columns == 6 && reflowed.Rows == 4 &&
                RowText(reflowed.ScreenRows[0]) == "conten" && reflowed.ScreenRows[0].Wrapped &&
                reflowed.ScreenRows[1].Cells[1].Character == "中" &&
                reflowed.ScreenRows[1].Cells[1].DisplayWidth == 2 &&
                reflowed.ScreenRows[1].Cells[2].WideContinuation,
                "A non-empty resize did not reflow text and wide graphemes safely.");
            var restored = PortableTerminalModelV1.Restore(reflowed);
            reflow.ApplyOutput(3, Bytes("?"));
            restored.ApplyOutput(3, Bytes("?"));
            Require(reflow.ValidationFingerprint() == restored.ValidationFingerprint(),
                "A reflowed checkpoint diverged after suffix replay.");
        }

        private static void ValidateParserInterruptAndDcsContracts()
        {
            var crossEventCan = new PortableTerminalModelV1(8, 4);
            crossEventCan.ApplyOutput(1, Bytes("\u001bP1;2"));
            Require(crossEventCan.PermanentRejectionCode.Length == 0 &&
                crossEventCan.ParserBoundaryCode == "INCOMPLETE_DCS",
                "A split DCS identifier was not retained as a temporary boundary before CAN.");
            crossEventCan.ApplyOutput(2, Bytes("\u0018X"));
            Require(crossEventCan.TryCreateSnapshot(out var crossEventCanSnapshot) &&
                crossEventCanSnapshot.ScreenRows[0].Cells[0].Character == "X",
                "CAN did not cancel an incomplete DCS across ApplyOutput events.");

            var crossEventSub = new PortableTerminalModelV1(8, 4);
            crossEventSub.ApplyOutput(1, Bytes("\u001bP1;2"));
            Require(crossEventSub.PermanentRejectionCode.Length == 0 &&
                crossEventSub.ParserBoundaryCode == "INCOMPLETE_DCS",
                "A split DCS identifier was not retained as a temporary boundary before SUB.");
            crossEventSub.ApplyOutput(2, Bytes("\u001aY"));
            Require(crossEventSub.TryCreateSnapshot(out var crossEventSubSnapshot) &&
                crossEventSubSnapshot.ScreenRows[0].Cells[0].Character == "Y",
                "SUB did not cancel an incomplete DCS across ApplyOutput events.");

            var crossEventEscape = new PortableTerminalModelV1(8, 4);
            crossEventEscape.ApplyOutput(1, Bytes("A\u001bP1;2"));
            Require(crossEventEscape.PermanentRejectionCode.Length == 0 &&
                crossEventEscape.ParserBoundaryCode == "INCOMPLETE_DCS",
                "A split DCS identifier was not retained as a temporary boundary before ESC.");
            crossEventEscape.ApplyOutput(2, Bytes("\u001b[2JX"));
            Require(crossEventEscape.TryCreateSnapshot(out var crossEventEscapeSnapshot) &&
                crossEventEscapeSnapshot.ScreenRows[0].Cells[0].Character == " " &&
                crossEventEscapeSnapshot.ScreenRows[0].Cells[1].Character == "X",
                "ESC did not terminate an incomplete DCS across ApplyOutput events.");

            var crossEventFinal = new PortableTerminalModelV1(8, 4);
            crossEventFinal.ApplyOutput(1, Bytes("\u001bP1;2"));
            Require(crossEventFinal.PermanentRejectionCode.Length == 0 &&
                crossEventFinal.ParserBoundaryCode == "INCOMPLETE_DCS",
                "A split DCS identifier was rejected before its final byte arrived.");
            crossEventFinal.ApplyOutput(2, Bytes("|"));
            Require(crossEventFinal.PermanentRejectionCode == "DCS_UNSUPPORTED" &&
                crossEventFinal.PermanentRejectionSequence == 2,
                "An unsupported DCS final arriving in a later event was not rejected permanently.");
            crossEventFinal.ApplyOutput(3, Bytes("\u0018Z"));
            Require(crossEventFinal.PermanentRejectionCode == "DCS_UNSUPPORTED" &&
                !crossEventFinal.TryCreateSnapshot(out _),
                "CAN incorrectly cleared a permanent rejection after a DCS final was recognized.");

            var can = new PortableTerminalModelV1(8, 4);
            can.ApplyOutput(1, Bytes("\u001bP1;2\u0018X"));
            Require(can.TryCreateSnapshot(out var canSnapshot) &&
                canSnapshot.ScreenRows[0].Cells[0].Character == "X",
                "CAN did not cancel an in-progress DCS from anywhere.");

            var sub = new PortableTerminalModelV1(8, 4);
            sub.ApplyOutput(1, Bytes("\u001bP1;2\u001aY"));
            Require(sub.TryCreateSnapshot(out var subSnapshot) &&
                subSnapshot.ScreenRows[0].Cells[0].Character == "Y",
                "SUB did not cancel an in-progress DCS from anywhere.");

            var dcsEscape = new PortableTerminalModelV1(8, 4);
            dcsEscape.ApplyOutput(1, Bytes("A\u001bP1;2\u001b[2JX"));
            Require(dcsEscape.TryCreateSnapshot(out var dcsEscapeSnapshot) &&
                dcsEscapeSnapshot.ScreenRows[0].Cells[0].Character == " " &&
                dcsEscapeSnapshot.ScreenRows[0].Cells[1].Character == "X",
                "ESC inside DCS did not start a fresh escape sequence.");

            var oscEscape = new PortableTerminalModelV1(8, 4);
            oscEscape.ApplyOutput(1, Bytes("A\u001b]2;bad\u001b[2JX"));
            Require(oscEscape.TryCreateSnapshot(out var oscEscapeSnapshot) &&
                oscEscapeSnapshot.Title.Length == 0 &&
                oscEscapeSnapshot.ScreenRows[0].Cells[0].Character == " " &&
                oscEscapeSnapshot.ScreenRows[0].Cells[1].Character == "X",
                "A non-ST ESC inside OSC did not start a fresh escape sequence.");

            var xtGetTcap = new PortableTerminalModelV1(8, 4);
            xtGetTcap.ApplyOutput(1, Bytes("\u001bP+q544e\u001b\\Z"));
            Require(xtGetTcap.PermanentRejectionCode == "DCS_UNSUPPORTED",
                "XTGETTCAP DCS +q was misclassified as SIXEL.");
        }

        private static void ValidateSavedCursorAndAlternateContracts()
        {
            var originCharset = new PortableTerminalModelV1(8, 5);
            originCharset.ApplyOutput(1, Bytes(
                "\u001b[2;4r\u001b[?6h\u001b[2;2H\u001b(0\u001b7" +
                "\u001b(B\u001b[?6l\u001b8\u001b[Hq"));
            Require(originCharset.TryCreateSnapshot(out var originSnapshot) &&
                originSnapshot.SavedCursorOriginMode && originSnapshot.SavedCursorLineDrawingG0 &&
                originSnapshot.SavedCursorCharsetGl == 0 && originSnapshot.OriginMode &&
                originSnapshot.ScreenRows[1].Cells[0].Character == "─",
                "DECSC/DECRC did not restore origin-relative position and DEC charset state.");

            var delayedWrap = new PortableTerminalModelV1(3, 3);
            delayedWrap.ApplyOutput(1, Bytes("ABC\u001b7\r\u001b8X"));
            Require(delayedWrap.TryCreateSnapshot(out var delayedSnapshot) &&
                delayedSnapshot.SavedCursorPendingAutoWrap &&
                delayedSnapshot.ScreenRows[1].Cells[0].Character == "X",
                "DECSC/DECRC did not restore delayed EOL wrap.");

            var savedAutoWrap = new PortableTerminalModelV1(4, 3);
            savedAutoWrap.ApplyOutput(1, Bytes("\u001b[?7l\u001b7\u001b[?7h\u001b8"));
            Require(savedAutoWrap.TryCreateSnapshot(out var autoWrapSnapshot) &&
                !autoWrapSnapshot.SavedCursorAutoWrap && autoWrapSnapshot.AutoWrap,
                "Saved-cursor DECAWM metadata was lost or DECRC incorrectly restored DECAWM.");

            var alternate = new PortableTerminalModelV1(8, 5);
            alternate.ApplyOutput(1, Bytes(
                "\u001b[2;4r\u001b[3;4H\u001b[31;1m\u001b[?1049hX"));
            Require(alternate.TryCreateSnapshot(out var alternateSnapshot) &&
                alternateSnapshot.ActiveAlternateScreen &&
                alternateSnapshot.ScrollTop == 1 && alternateSnapshot.AlternateScrollTop == 1 &&
                alternateSnapshot.ScrollBottomExclusive == 4 &&
                alternateSnapshot.AlternateScrollBottomExclusive == 4 &&
                alternateSnapshot.AlternateScreenRows[2].Cells[3].Character == "X" &&
                alternateSnapshot.AlternateScreenRows[2].Cells[3].Style.Foreground == 1 &&
                alternateSnapshot.AlternateScreenRows[2].Cells[3].Style.Attributes ==
                    PortableTerminalAttributesV1.None,
                "DECSET 1049 did not copy cursor/erase colors or retain global margins.");
            alternate.ApplyOutput(2, Bytes("\u001b[?1049lY"));
            Require(alternate.TryCreateSnapshot(out var mainSnapshot) &&
                mainSnapshot.ScreenRows[2].Cells[3].Character == "Y" &&
                (mainSnapshot.ScreenRows[2].Cells[3].Style.Attributes &
                    PortableTerminalAttributesV1.Bold) != 0,
                "DECRST 1049 did not restore the complete primary saved cursor state.");

            var alternateSaved = new PortableTerminalModelV1(6, 4);
            alternateSaved.ApplyOutput(1, Bytes(
                "\u001b[?1049h\u001b[2;2H\u001b7\u001b[?1049l" +
                "\u001b[?1049h\u001b8Z"));
            Require(alternateSaved.TryCreateSnapshot(out var alternateSavedSnapshot) &&
                alternateSavedSnapshot.AlternateScreenRows[1].Cells[1].Character == "Z",
                "The alternate screen's independent saved cursor state did not survive re-entry.");
        }

        private static void ValidateMarginRectangleAndEraseContracts()
        {
            var movement = new PortableTerminalModelV1(6, 5);
            movement.ApplyOutput(1, Bytes("\u001b[2;4r\u001b[3;1H\u001b[99BX"));
            Require(movement.TryCreateSnapshot(out var movementSnapshot) &&
                movementSnapshot.ScreenRows[3].Cells[0].Character == "X" &&
                movementSnapshot.ScreenRows[4].Cells[0].Character == " ",
                "CUD crossed the bottom margin while origin mode was reset.");

            var fill = new PortableTerminalModelV1(5, 5);
            fill.ApplyOutput(1, Bytes("\u001b[2;3r\u001b[?6h\u001b[88$x"));
            Require(fill.TryCreateSnapshot(out var fillSnapshot) &&
                fillSnapshot.ScreenRows[1].Cells[0].Character == "X" &&
                fillSnapshot.ScreenRows[2].Cells[4].Character == "X" &&
                fillSnapshot.ScreenRows[0].Cells[0].Character == " " &&
                fillSnapshot.ScreenRows[3].Cells[0].Character == " ",
                "Origin-relative DECFRA escaped the vertical margins.");

            var copy = new PortableTerminalModelV1(5, 5);
            copy.ApplyOutput(1, Bytes(
                "\u001b[3;4r\u001b[?6h" +
                "\u001b[65;1;1;1;1$x" +
                "\u001b[1;1;1;1;1;2;1;1$v"));
            Require(copy.TryCreateSnapshot(out var copySnapshot) &&
                copySnapshot.ScreenRows[2].Cells[0].Character == "A" &&
                copySnapshot.ScreenRows[3].Cells[0].Character == "A" &&
                copySnapshot.ScreenRows[1].Cells[0].Character == " ",
                "DECCRA did not apply origin mode to its destination rectangle.");

            var erase = new PortableTerminalModelV1(8, 4);
            erase.ApplyOutput(1, Bytes(
                "\u001b]8;;https://example.test\u001b\\\u001b[31;1;4mZ\r\u001b[X"));
            Require(erase.TryCreateSnapshot(out var eraseSnapshot) &&
                eraseSnapshot.ScreenRows[0].Cells[0].Character == " " &&
                eraseSnapshot.ScreenRows[0].Cells[0].Style.Foreground == 1 &&
                eraseSnapshot.ScreenRows[0].Cells[0].Style.Attributes ==
                    PortableTerminalAttributesV1.None &&
                eraseSnapshot.ScreenRows[0].Cells[0].Style.HyperlinkIndexPlusOne == 0,
                "ECH did not use standard erase attributes.");

            var insertLine = new PortableTerminalModelV1(6, 4);
            insertLine.ApplyOutput(1, Bytes("\u001b[2;3H\u001b[LX"));
            Require(insertLine.TryCreateSnapshot(out var insertSnapshot) &&
                insertSnapshot.ScreenRows[1].Cells[0].Character == "X",
                "IL did not move the cursor to the left margin.");
            var deleteLine = new PortableTerminalModelV1(6, 4);
            deleteLine.ApplyOutput(1, Bytes("\u001b[2;3H\u001b[MY"));
            Require(deleteLine.TryCreateSnapshot(out var deleteSnapshot) &&
                deleteSnapshot.ScreenRows[1].Cells[0].Character == "Y",
                "DL did not move the cursor to the left margin.");
        }

        private static void ValidateTabAndCharsetContracts()
        {
            var clearedTabs = new PortableTerminalModelV1(10, 3);
            clearedTabs.ApplyOutput(1, Bytes("\u001b[3g"));
            clearedTabs.ApplyResize(2, 20, 3);
            clearedTabs.ApplyOutput(3, Bytes("\r\tX"));
            Require(clearedTabs.TryCreateSnapshot(out var clearedSnapshot) &&
                !clearedSnapshot.DefaultTabStopsEnabled && clearedSnapshot.TabStops.Count == 0 &&
                clearedSnapshot.ScreenRows[0].Cells[19].Character == "X",
                "Resize recreated default tab stops after TBC 3.");

            var offscreenTabs = new PortableTerminalModelV1(20, 3);
            offscreenTabs.ApplyOutput(1, Bytes("\u001b[3g\u001b[1;16H\u001bH"));
            offscreenTabs.ApplyResize(2, 10, 3);
            Require(offscreenTabs.TryCreateSnapshot(out var narrowSnapshot) &&
                narrowSnapshot.TabStopCapacity == 20 &&
                narrowSnapshot.TabStops.Contains(15),
                "Tab-stop capacity or a custom stop was discarded outside the current width.");
            var restoredTabs = PortableTerminalModelV1.Restore(narrowSnapshot);
            offscreenTabs.ApplyResize(3, 20, 3);
            restoredTabs.ApplyResize(3, 20, 3);
            offscreenTabs.ApplyOutput(4, Bytes("\u001b[1;1H\tX"));
            restoredTabs.ApplyOutput(4, Bytes("\u001b[1;1H\tX"));
            Require(offscreenTabs.ValidationFingerprint() == restoredTabs.ValidationFingerprint() &&
                restoredTabs.TryCreateSnapshot(out var wideTabs) &&
                wideTabs.ScreenRows[0].Cells[15].Character == "X",
                "An offscreen custom tab stop did not survive checkpoint and re-expansion.");

            var tabWrap = new PortableTerminalModelV1(3, 3);
            tabWrap.ApplyOutput(1, Bytes("ABC\tX"));
            Require(tabWrap.TryCreateSnapshot(out var tabWrapSnapshot) &&
                tabWrapSnapshot.ScreenRows[1].Cells[0].Character == "X",
                "HT cleared delayed EOL wrap.");

            var charset = new PortableTerminalModelV1(20, 3);
            charset.ApplyOutput(1, Bytes("\u001b(0_bcdehi\u001b(B"));
            Require(charset.TryCreateSnapshot(out var charsetSnapshot) &&
                RowText(charsetSnapshot.ScreenRows[0]).StartsWith(" ␉␌␍␊␤␋", StringComparison.Ordinal),
                "The DEC Special Graphics control-picture mappings are incomplete.");
        }

        private static void ValidateResizeAndScrollbackInvariants()
        {
            var cursorVisible = new PortableTerminalModelV1(5, 5);
            cursorVisible.ApplyOutput(1, Bytes("A"));
            cursorVisible.ApplyResize(2, 5, 3);
            Require(cursorVisible.TryCreateSnapshot(out var resized) &&
                resized.ScrollbackRows.Count == 0 &&
                resized.ScreenRows[0].Cells[0].Character == "A" && resized.CursorRow == 0,
                "A height shrink detached the cursor from its logical row.");
            var restored = PortableTerminalModelV1.Restore(resized);
            cursorVisible.ApplyOutput(3, Bytes("B"));
            restored.ApplyOutput(3, Bytes("B"));
            Require(cursorVisible.ValidationFingerprint() == restored.ValidationFingerprint(),
                "A cursor-preserving resize did not remain snapshot-restorable.");

            var resizedMargins = new PortableTerminalModelV1(8, 6);
            resizedMargins.ApplyOutput(1, Bytes("\u001b[2;5r"));
            resizedMargins.ApplyResize(2, 8, 4);
            resizedMargins.ApplyResize(3, 8, 7);
            Require(resizedMargins.TryCreateSnapshot(out var resizedMarginSnapshot) &&
                resizedMarginSnapshot.ScrollTop == 1 &&
                resizedMarginSnapshot.ScrollBottomExclusive == 5 &&
                resizedMarginSnapshot.ConfiguredScrollBottomExclusive == 5,
                "Resize reset DECSTBM instead of clamping and retaining the global margins.");

            var explicitScroll = new PortableTerminalModelV1(4, 2);
            explicitScroll.ApplyOutput(1, Bytes("A\r\nB\u001b[S"));
            Require(explicitScroll.TryCreateSnapshot(out var scrollSnapshot) &&
                scrollSnapshot.ScrollbackRows.Count == 0 &&
                scrollSnapshot.ScreenRows[0].Cells[0].Character == "B",
                "Explicit SU incorrectly appended a row to transcript history.");

            var alternateScroll = new PortableTerminalModelV1(4, 2);
            alternateScroll.ApplyOutput(1, Bytes("M\u001b[?1049hA\u001b[S\u001b[?1049l"));
            Require(alternateScroll.TryCreateSnapshot(out var alternateScrollSnapshot) &&
                alternateScrollSnapshot.ScrollbackRows.Count == 0 &&
                alternateScrollSnapshot.ScreenRows[0].Cells[0].Character == "M",
                "Alternate-screen SU polluted primary transcript history.");

            var alternateResize = new PortableTerminalModelV1(4, 3);
            alternateResize.ApplyOutput(1, Bytes("\u001b[?1049hTUI"));
            alternateResize.ApplyResize(2, 6, 4);
            Require(alternateResize.PermanentRejectionCode == "ACTIVE_ALTERNATE_RESIZE_UNSUPPORTED" &&
                !alternateResize.TryCreateSnapshot(out _),
                "An active-alternate resize approximated Windows Terminal's deferred main reflow.");

            var overflow = new PortableTerminalModelV1(1000, 5);
            var longRows = string.Join("\r\n", Enumerable.Repeat(new string('W', 1000), 5));
            overflow.ApplyOutput(1, Bytes(longRows));
            overflow.ApplyResize(2, 2, 5);
            Require(overflow.PermanentRejectionCode == "SCROLLBACK_LIMIT_EXCEEDED" &&
                !overflow.TryCreateSnapshot(out _),
                "Reflow produced a snapshot whose scrollback exceeded the restore bound.");
        }

        private static void ValidateOscSafetyLimits()
        {
            var c0 = new PortableTerminalModelV1(8, 3);
            c0.ApplyOutput(1, Bytes("\u001b]2;a\n\rb\u0007X"));
            Require(c0.TryCreateSnapshot(out var c0Snapshot) && c0Snapshot.Title == "ab" &&
                c0Snapshot.ScreenRows[0].Cells[0].Character == "X",
                "OSC retained invalid C0 controls in its payload.");

            var links = new StringBuilder();
            for (var index = 0; index < 257; index++)
                links.Append("\u001b]8;;u").Append(index).Append("\u001b\\");
            var hyperlinkOverflow = new PortableTerminalModelV1(8, 3);
            hyperlinkOverflow.ApplyOutput(1, Bytes(links.ToString()));
            Require(hyperlinkOverflow.PermanentRejectionCode == "HYPERLINK_LIMIT_EXCEEDED",
                "OSC 8 hyperlink-table overflow silently retained the previous link.");
        }

        private static (int Cases, int Checkpoints, int RejectedCases) ValidateGoldenStreams(string repositoryRoot)
        {
            var goldenRoot = Path.Combine(repositoryRoot, "testdata", "terminal-golden-streams");
            var directories = Directory.GetDirectories(goldenRoot).OrderBy(value => value, StringComparer.Ordinal).ToArray();
            Require(directories.Length >= 10, "The checked-in Golden Stream corpus is incomplete.");
            var checkpoints = 0;
            var rejectedCases = 0;

            foreach (var directory in directories)
            {
                var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "manifest.json")));
                var initialSize = manifest.RootElement.GetProperty("initial_size");
                var events = LoadEvents(Path.Combine(directory, "events.jsonl"));
                var output = File.ReadAllBytes(Path.Combine(directory, "output.bin"));
                var full = new PortableTerminalModelV1(
                    initialSize.GetProperty("columns").GetInt32(),
                    initialSize.GetProperty("rows").GetInt32());
                var snapshots = new List<(int Index, PortableTerminalSnapshotV1 Snapshot)>();
                for (var index = 0; index < events.Count; index++)
                {
                    ApplyGoldenEvent(full, events[index], output);
                    if (full.TryCreateSnapshot(out var snapshot)) snapshots.Add((index, snapshot));
                }

                var expected = full.ValidationFingerprint();
                if (!full.SnapshotEligible) rejectedCases++;
                Require(snapshots.Count > 0,
                    "Golden case did not expose even its pre-output safe checkpoint: " + Path.GetFileName(directory));
                foreach (var checkpoint in snapshots)
                {
                    var resumed = PortableTerminalModelV1.Restore(checkpoint.Snapshot);
                    for (var index = checkpoint.Index + 1; index < events.Count; index++)
                        ApplyGoldenEvent(resumed, events[index], output);
                    Require(resumed.ValidationFingerprint() == expected,
                        "Golden replay diverged after snapshot checkpoint " + checkpoint.Snapshot.Sequence +
                        " for " + Path.GetFileName(directory) + ".");
                    checkpoints++;
                }
            }
            return (directories.Length, checkpoints, rejectedCases);
        }

        private static List<GoldenEvent> LoadEvents(string path)
        {
            var result = new List<GoldenEvent>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var item = new GoldenEvent
                {
                    Sequence = root.GetProperty("seq").GetUInt64(),
                    Type = root.GetProperty("type").GetString() ?? string.Empty
                };
                if (item.Type == "resize")
                {
                    item.Columns = root.GetProperty("columns").GetInt32();
                    item.Rows = root.GetProperty("rows").GetInt32();
                }
                else if (item.Type == "output")
                {
                    item.DataOffset = root.GetProperty("data_offset").GetInt32();
                    item.DataLength = root.GetProperty("data_length").GetInt32();
                }
                result.Add(item);
            }
            return result;
        }

        private static void ApplyGoldenEvent(PortableTerminalModelV1 model, GoldenEvent item, byte[] output)
        {
            if (item.Type == "resize") model.ApplyResize(item.Sequence, item.Columns, item.Rows);
            else if (item.Type == "output")
                model.ApplyOutput(item.Sequence, output.AsSpan(item.DataOffset, item.DataLength));
            else model.AdvanceNonTerminalEvent(item.Sequence);
        }

        private static void RequireRejected(string code, byte[] bytes)
        {
            var model = new PortableTerminalModelV1(8, 4);
            model.ApplyOutput(1, bytes);
            Require(!model.SnapshotEligible && model.PermanentRejectionCode == code,
                "Expected rejection " + code + ", got " + model.PermanentRejectionCode + ".");
        }

        private static string ParseRepositoryRoot(string[] args)
        {
            if (args.Length != 2 || args[0] != "--repo-root")
                throw new ArgumentException("Usage: --repo-root <TermLink repository root>");
            var root = Path.GetFullPath(args[1]);
            if (!File.Exists(Path.Combine(root, "win-agent", "global.json")) ||
                !Directory.Exists(Path.Combine(root, "testdata", "terminal-golden-streams")))
                throw new DirectoryNotFoundException("TermLink repository root is invalid: " + root);
            return root;
        }

        private static byte[] Bytes(string value) => Encoding.UTF8.GetBytes(value);

        private static string RowText(PortableTerminalRowV1 row) =>
            string.Concat(row.Cells.Where(cell => !cell.WideContinuation).Select(cell => cell.Character));

        private static void PrintCoverageMatrix()
        {
            Console.WriteLine("SUPPORTED: Unicode/CJK/emoji/combining; SGR/ANSI16/256/RGB; CSI cursor/erase/edit/scroll/DECSTBM/rectangle; saved cursor; origin/insert/autowrap; alternate screen; dynamic tabs; OSC title/palette/hyperlink; DEC line drawing; non-empty resize/reflow; synchronized output; bounded scrollback.");
            Console.WriteLine("TEMPORARY_INELIGIBLE: incomplete ESC/CSI/OSC/DCS/UTF-8; active synchronized-output batch.");
            Console.WriteLine("PERMANENT_REJECT: invalid UTF-8; orphan/terminal-dependent Unicode; DCS/SIXEL/APC/PM/SOS; unsupported ESC/CSI/private modes/SGR/C0/rectangle; active-alternate resize; scrollback overflow; sequence gap.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
