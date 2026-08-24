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
                ValidateUnicodeWidthAndCombining();
                ValidateOscControlStrings();
                ValidateClearCursorAndResizeContracts();
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
            RequireRejected("CSI_FINAL_UNSUPPORTED", Bytes("\u001b[2;3r"));
            RequireRejected("C0_CONTROL_UNSUPPORTED", new byte[] { 0x09 });
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
                belSnapshot.ScreenRows[0].Cells[1].Character == "B",
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
            reflow.ApplyOutput(1, Bytes("content"));
            reflow.ApplyResize(2, 6, 4);
            Require(reflow.PermanentRejectionCode == "RESIZE_REFLOW_UNSUPPORTED",
                "A resize requiring reflow incorrectly remained eligible.");
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

        private static void PrintCoverageMatrix()
        {
            Console.WriteLine("SUPPORTED: ASCII and assigned Unicode text; common CJK/emoji width two; combining/variation scalars; BEL/ST-terminated OSC; NUL/BEL; CR/LF; ESC c; basic SGR/ANSI16; CSI A/B/C/D/G/d/H/f; ED 0/1/2; EL 0/1/2; ?25h/?25l; pristine/no-op resize; bounded scrollback.");
            Console.WriteLine("TEMPORARY_INELIGIBLE: incomplete ESC/CSI/OSC; incomplete UTF-8 scalar.");
            Console.WriteLine("PERMANENT_REJECT: invalid UTF-8; orphan combining scalars; unsupported or terminal-dependent Unicode widths; DCS/APC/PM/SOS; unsupported ESC/CSI/private modes/SGR/C0; resize reflow; scrollback overflow; sequence gap.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
