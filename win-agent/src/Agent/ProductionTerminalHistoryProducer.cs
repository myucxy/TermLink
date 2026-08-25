using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    internal sealed class ProductionTerminalHistoryProductionResult
    {
        public Guid SessionId { get; set; }
        public ulong LastSequence { get; set; }
        public bool SnapshotEligible { get; set; }
        public string IneligibilityCode { get; set; }
        public int TranscriptPageCount { get; set; }
    }

    internal sealed class ProductionTerminalHistoryProducer
    {
        private const int DefaultColumns = 80;
        private const int DefaultRows = 24;
        private const int MaximumWireColumns = 512;
        private const int MaximumWireRows = 256;
        private const int MaximumTranscriptRowsPerPage = 200;
        private readonly ProductionTerminalHistoryStore store;
        private readonly Guid pcDeviceId;
        private readonly ConcurrentDictionary<Guid, ProductionState> states =
            new ConcurrentDictionary<Guid, ProductionState>();

        private sealed class ProductionState
        {
            public readonly object Sync = new object();
            public readonly PortableTerminalModelV1 Model =
                new PortableTerminalModelV1(DefaultColumns, DefaultRows);
            public TerminalLifecycleV1 Lifecycle = TerminalLifecycleV1.Running;
            public bool HasExitCode;
            public int ExitCode;
            public ulong LocalInputEpoch;
            public ulong AppliedSequence;
            public bool HasOutput;
            public readonly SortedDictionary<ulong, LocalFrame> PendingFrames =
                new SortedDictionary<ulong, LocalFrame>();
        }

        public ProductionTerminalHistoryProducer(ProductionTerminalHistoryStore store, Guid pcDeviceId)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            if (pcDeviceId == Guid.Empty) throw new ArgumentException("PC device ID is required.", nameof(pcDeviceId));
            this.pcDeviceId = pcDeviceId;
        }

        public void AppendCommittedFrame(LocalFrame frame) => store.AppendCommittedFrame(frame);

        public IList<LocalFrame> ReadFrames(Guid sessionId) => store.ReadFrames(sessionId);

        public ProductionTerminalHistoryProductionResult ProcessSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            var state = states.GetOrAdd(sessionId, _ => new ProductionState());
            lock (state.Sync)
            {
                // Only decrypt WAL entries beyond the in-memory projection.
                // A fresh process naturally performs one complete recovery;
                // steady-state production applies each durable frame once.
                var frames = store.ReadFramesAfter(sessionId, state.AppliedSequence);
                return ProcessFrames(sessionId, state, frames);
            }
        }

        public ProductionTerminalHistoryProductionResult ProcessSession(
            Guid sessionId,
            IList<LocalFrame> committedFrames)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            if (committedFrames == null) throw new ArgumentNullException(nameof(committedFrames));
            var state = states.GetOrAdd(sessionId, _ => new ProductionState());
            lock (state.Sync)
            {
                // A late-created producer must recover the prefix once. Once
                // warm, the history worker supplies only its coalesced append
                // batch, eliminating repeated WAL directory/decrypt scans.
                IList<LocalFrame> frames = committedFrames;
                if (state.AppliedSequence == 0 && state.PendingFrames.Count == 0 &&
                    committedFrames.Count > 0 && committedFrames.Min(value => value.Sequence) > 1)
                {
                    frames = store.ReadFrames(sessionId);
                }
                return ProcessFrames(sessionId, state, frames);
            }
        }

        private ProductionTerminalHistoryProductionResult ProcessFrames(
            Guid sessionId,
            ProductionState state,
            IList<LocalFrame> frames)
        {
            foreach (var frame in frames)
            {
                if (frame == null || frame.SessionId != sessionId || frame.Sequence == 0)
                    throw new InvalidDataException("Incremental terminal history frame identity is invalid.");
                if (frame.Sequence > state.AppliedSequence)
                    state.PendingFrames[frame.Sequence] = frame;
            }
            if (state.PendingFrames.Count == 0 && !state.HasOutput)
            {
                return new ProductionTerminalHistoryProductionResult
                {
                    SessionId = sessionId,
                    IneligibilityCode = "NO_DURABLE_FRAMES"
                };
            }

            var outputAnchor = state.PendingFrames.Values
                .Where(value => value.Type == LocalFrameType.Output)
                .Select(value => value.Sequence)
                .DefaultIfEmpty(0UL)
                .Max();
            if (outputAnchor == 0 && !state.HasOutput)
            {
                var rejected = new ProductionTerminalHistoryProductionResult
                {
                    SessionId = sessionId,
                    LastSequence = state.PendingFrames.Keys.Last(),
                    IneligibilityCode = "NO_OUTPUT_FRAME"
                };
                SaveRejectedCheckpoint(rejected);
                return rejected;
            }
            var applied = outputAnchor == 0
                ? Array.Empty<ulong>()
                : state.PendingFrames.Keys.Where(value => value <= outputAnchor).ToArray();
            foreach (var sequence in applied)
            {
                var frame = state.PendingFrames[sequence];
                ApplyFrame(state.Model, frame);
                ApplyProjection(
                    frame,
                    ref state.Lifecycle,
                    ref state.HasExitCode,
                    ref state.ExitCode,
                    ref state.LocalInputEpoch);
                state.AppliedSequence = frame.Sequence;
                if (frame.Type == LocalFrameType.Output) state.HasOutput = true;
                state.PendingFrames.Remove(sequence);
            }

            return ProduceCheckpoint(sessionId, state);
        }

        private ProductionTerminalHistoryProductionResult ProduceCheckpoint(
            Guid sessionId,
            ProductionState state)
        {
            var result = new ProductionTerminalHistoryProductionResult
            {
                SessionId = sessionId,
                // Snapshot S is deliberately the latest Output sequence. REST
                // last_output_seq therefore selects the same state, while any
                // Resize/Title/Lifecycle at S+1 is replayed as a normal suffix.
                LastSequence = state.AppliedSequence
            };
            if (state.Model.Columns > MaximumWireColumns || state.Model.Rows > MaximumWireRows)
            {
                result.IneligibilityCode = "WIRE_DIMENSIONS_UNSUPPORTED";
                SaveRejectedCheckpoint(result);
                return result;
            }
            if (!state.Model.TryCreateSnapshot(out var portable))
            {
                result.IneligibilityCode = string.IsNullOrWhiteSpace(state.Model.PermanentRejectionCode)
                    ? state.Model.ParserBoundaryCode
                    : state.Model.PermanentRejectionCode;
                SaveRejectedCheckpoint(result);
                return result;
            }

            var styleTable = BuildStyleTable(portable);
            var palette = CreatePalette();
            var snapshot = BuildSnapshot(
                portable,
                styleTable,
                palette,
                state.Lifecycle,
                state.HasExitCode,
                state.ExitCode,
                state.LocalInputEpoch);
            SnapshotCrypto.ValidateSnapshotForProducer(snapshot);
            var pages = BuildTranscriptPages(portable, styleTable, palette).ToList();
            for (var index = 0; index < pages.Count; index++)
            {
                var exclusiveBefore = index + 1 < pages.Count
                    ? pages[index + 1].FirstLineId
                    : snapshot.PrimaryScreen.VisibleRows[0].LineId;
                SnapshotCrypto.ValidateTranscriptForRequest(pages[index], snapshot.SnapshotSeq, exclusiveBefore);
            }

            var snapshotBytes = snapshot.ToByteArray();
            var pageBytes = pages.Select(value => value.ToByteArray()).ToList();
            try
            {
                store.SaveCheckpoint(new ProductionTerminalHistoryCheckpoint
                {
                    SessionId = sessionId,
                    LastSequence = portable.Sequence,
                    SnapshotEligible = true,
                    IneligibilityCode = string.Empty,
                    SnapshotPlaintext = snapshotBytes,
                    TranscriptPlaintexts = pageBytes
                });
                store.SaveOutbox(BuildOutbox(sessionId, snapshot, pages, snapshotBytes, pageBytes));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(snapshotBytes);
                foreach (var bytes in pageBytes) CryptographicOperations.ZeroMemory(bytes);
            }
            result.SnapshotEligible = true;
            result.TranscriptPageCount = pages.Count;
            result.IneligibilityCode = string.Empty;
            return result;
        }

        public ProductionTerminalHistoryPreparedUpload PrepareNextUpload(
            Guid sessionId,
            ProductionHistoryRecipient recipient)
        {
            if (recipient == null || recipient.PairwiseKey == null || recipient.PairwiseKey.Length != 32 ||
                recipient.DeviceId == Guid.Empty || recipient.KeyEpoch < 1)
                throw new ArgumentException("A valid terminal history recipient is required.", nameof(recipient));
            return store.PrepareNextUpload(
                sessionId,
                recipient.DeviceId,
                recipient.KeyEpoch,
                artifact => SealArtifact(sessionId, recipient, artifact));
        }

        public void MarkUploaded(ProductionTerminalHistoryPreparedUpload upload) => store.MarkUploaded(upload);

        public bool AreAllUploaded(Guid sessionId, ulong snapshotSequence, ProductionHistoryRecipient recipient)
        {
            if (recipient == null) return false;
            return store.AreAllUploaded(sessionId, snapshotSequence, recipient.DeviceId, recipient.KeyEpoch);
        }

        public void RetainDeliveries(
            Guid sessionId,
            IEnumerable<(Guid RecipientDeviceId, long KeyEpoch)> recipients) =>
            store.RetainDeliveries(sessionId, recipients);

        public ProductionTerminalHistoryCheckpoint LoadCheckpoint(Guid sessionId) => store.LoadCheckpoint(sessionId);

        public ProductionTerminalHistoryOutbox LoadOutbox(Guid sessionId) => store.LoadOutbox(sessionId);

        private void SaveRejectedCheckpoint(ProductionTerminalHistoryProductionResult result)
        {
            store.SaveCheckpoint(new ProductionTerminalHistoryCheckpoint
            {
                SessionId = result.SessionId,
                LastSequence = result.LastSequence,
                SnapshotEligible = false,
                IneligibilityCode = result.IneligibilityCode,
                SnapshotPlaintext = null,
                TranscriptPlaintexts = new List<byte[]>()
            });
        }

        private static void ApplyFrame(PortableTerminalModelV1 model, LocalFrame frame)
        {
            switch (frame.Type)
            {
                case LocalFrameType.Output:
                    model.ApplyOutput(frame.Sequence, frame.Payload);
                    return;
                case LocalFrameType.Resize:
                    LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                    model.ApplyResize(frame.Sequence, columns, rows);
                    return;
                case LocalFrameType.Gap:
                    model.MarkSourceGap(frame.Sequence);
                    return;
                default:
                    model.AdvanceNonTerminalEvent(frame.Sequence);
                    return;
            }
        }

        private static void ApplyProjection(
            LocalFrame frame,
            ref TerminalLifecycleV1 lifecycle,
            ref bool hasExitCode,
            ref int exitCode,
            ref ulong localInputEpoch)
        {
            if (frame.Type == LocalFrameType.LocalInputObserved)
            {
                localInputEpoch = LocalPayloadCodec.ParseLocalInputEpoch(frame.Payload);
            }
            else if (frame.Type == LocalFrameType.Lifecycle)
            {
                var state = LocalPayloadCodec.ParseLifecycle(frame.Payload, out var value);
                lifecycle = state switch
                {
                    LocalLifecycleState.Running => TerminalLifecycleV1.Running,
                    LocalLifecycleState.Interrupted => TerminalLifecycleV1.Interrupted,
                    LocalLifecycleState.Ended => TerminalLifecycleV1.Ended,
                    _ => throw new InvalidDataException("Terminal lifecycle state is unsupported.")
                };
                hasExitCode = state == LocalLifecycleState.Ended;
                exitCode = value;
            }
        }

        private static List<PortableTerminalStyleV1> BuildStyleTable(PortableTerminalSnapshotV1 snapshot)
        {
            var result = new List<PortableTerminalStyleV1>();
            AddStyle(result, PortableTerminalStyleV1.Default);
            AddStyle(result, snapshot.CurrentStyle);
            foreach (var row in snapshot.ScrollbackRows)
                foreach (var cell in row.Cells) AddStyle(result, cell.Style);
            foreach (var row in snapshot.ScreenRows)
                foreach (var cell in row.Cells) AddStyle(result, cell.Style);
            return result;
        }

        private static void AddStyle(ICollection<PortableTerminalStyleV1> styles, PortableTerminalStyleV1 value)
        {
            if (!styles.Contains(value)) styles.Add(value);
        }

        private static TerminalSnapshotV1 BuildSnapshot(
            PortableTerminalSnapshotV1 source,
            IList<PortableTerminalStyleV1> styles,
            TerminalPaletteV1 palette,
            TerminalLifecycleV1 lifecycle,
            bool hasExitCode,
            int exitCode,
            ulong localInputEpoch)
        {
            var charset = CreateCharset();
            var snapshot = new TerminalSnapshotV1
            {
                FormatVersion = SnapshotCrypto.FormatVersion,
                SnapshotSeq = source.Sequence,
                Columns = checked((uint)source.Columns),
                Rows = checked((uint)source.Rows),
                ActiveScreen = TerminalScreenKindV1.Primary,
                PrimaryScreen = CreatePrimaryScreen(source, styles, charset),
                AlternateScreen = CreateBlankAlternateScreen(source, styles, charset),
                Modes = new TerminalModesV1
                {
                    AutoWrap = true,
                    BracketedPaste = source.BracketedPaste,
                    FocusReporting = source.FocusReporting,
                    MouseTracking = TerminalMouseTrackingV1.None,
                    MouseEncoding = TerminalMouseEncodingV1.Default
                },
                Palette = palette.Clone(),
                ScrollbackFirstLineId = 0,
                NextTranscriptLineId = source.NextLineId,
                ScrollbackTruncated = false,
                Title = string.Empty,
                Projection = new TerminalSessionProjectionV1
                {
                    Lifecycle = lifecycle,
                    ObservedLocalInputEpoch = localInputEpoch
                },
                ParserState = SnapshotParserStateV1.Ground
            };
            if (hasExitCode) snapshot.Projection.ExitCode = exitCode;
            foreach (var style in styles) snapshot.StyleTable.Add(ToWireStyle(style));
            for (var column = 8; column < source.Columns; column += 8)
                snapshot.TabStops.Add(checked((uint)column));
            return snapshot;
        }

        private static TerminalScreenV1 CreatePrimaryScreen(
            PortableTerminalSnapshotV1 source,
            IList<PortableTerminalStyleV1> styles,
            TerminalCharsetStateV1 charset)
        {
            var screen = new TerminalScreenV1
            {
                Cursor = CreateCursor(
                    checked((uint)source.CursorColumn),
                    checked((uint)source.CursorRow),
                    source.CursorVisible,
                    source.CursorStyle,
                    source.CursorBlink,
                    checked((uint)styles.IndexOf(source.CurrentStyle)),
                    source.PendingAutoWrap,
                    charset),
                SavedCursor = CreateCursor(
                    0,
                    0,
                    source.CursorVisible,
                    source.CursorStyle,
                    source.CursorBlink,
                    0,
                    false,
                    charset),
                ScrollTop = 0,
                ScrollBottom = checked((uint)(source.Rows - 1)),
                MarginLeft = 0,
                MarginRight = checked((uint)(source.Columns - 1)),
                CurrentStyleIndex = checked((uint)styles.IndexOf(source.CurrentStyle)),
                PendingWrap = source.PendingAutoWrap,
                LastPrintedGrapheme = string.Empty,
                Charset = charset.Clone()
            };
            foreach (var row in source.ScreenRows) screen.VisibleRows.Add(ToWireRow(row, styles));
            return screen;
        }

        private static TerminalScreenV1 CreateBlankAlternateScreen(
            PortableTerminalSnapshotV1 source,
            IList<PortableTerminalStyleV1> styles,
            TerminalCharsetStateV1 charset)
        {
            var currentStyleIndex = checked((uint)styles.IndexOf(source.CurrentStyle));
            var screen = new TerminalScreenV1
            {
                // Termux keeps one retained cursor while switching between the primary and
                // alternate buffers.  Although this buffer is blank, its current cursor must
                // therefore mirror the active primary cursor exactly.
                Cursor = CreateCursor(
                    checked((uint)source.CursorColumn),
                    checked((uint)source.CursorRow),
                    source.CursorVisible,
                    source.CursorStyle,
                    source.CursorBlink,
                    currentStyleIndex,
                    source.PendingAutoWrap,
                    charset),
                SavedCursor = CreateCursor(
                    0,
                    0,
                    source.CursorVisible,
                    source.CursorStyle,
                    source.CursorBlink,
                    0,
                    false,
                    charset),
                ScrollTop = 0,
                ScrollBottom = checked((uint)(source.Rows - 1)),
                MarginLeft = 0,
                MarginRight = checked((uint)(source.Columns - 1)),
                CurrentStyleIndex = currentStyleIndex,
                PendingWrap = source.PendingAutoWrap,
                LastPrintedGrapheme = string.Empty,
                Charset = charset.Clone()
            };
            for (var row = 0; row < source.Rows; row++)
            {
                var wireRow = new TerminalRowV1();
                for (var column = 0; column < source.Columns; column++)
                    wireRow.Runs.Add(new TerminalCellRunV1 { Repeat = 1, Grapheme = " ", DisplayWidth = 1, StyleIndex = 0 });
                screen.VisibleRows.Add(wireRow);
            }
            return screen;
        }

        private static TerminalCursorStateV1 CreateCursor(
            uint column,
            uint row,
            bool visible,
            PortableTerminalCursorStyleV1 cursorStyle,
            bool cursorBlink,
            uint styleIndex,
            bool pendingWrap,
            TerminalCharsetStateV1 charset)
        {
            return new TerminalCursorStateV1
            {
                Column = column,
                Row = row,
                Visible = visible,
                Style = cursorStyle switch
                {
                    PortableTerminalCursorStyleV1.Block => TerminalCursorStyleV1.Block,
                    PortableTerminalCursorStyleV1.Underline => TerminalCursorStyleV1.Underline,
                    PortableTerminalCursorStyleV1.Bar => TerminalCursorStyleV1.Bar,
                    _ => throw new InvalidDataException("Terminal cursor style is unsupported.")
                },
                Blink = cursorBlink,
                RenditionStyleIndex = styleIndex,
                Charset = charset.Clone(),
                OriginMode = false,
                PendingWrap = pendingWrap
            };
        }

        private static IEnumerable<TerminalTranscriptPageV1> BuildTranscriptPages(
            PortableTerminalSnapshotV1 source,
            IList<PortableTerminalStyleV1> styles,
            TerminalPaletteV1 palette)
        {
            for (var offset = 0; offset < source.ScrollbackRows.Count; offset += MaximumTranscriptRowsPerPage)
            {
                var count = Math.Min(MaximumTranscriptRowsPerPage, source.ScrollbackRows.Count - offset);
                var first = source.ScrollbackRows[offset];
                var last = source.ScrollbackRows[offset + count - 1];
                var page = new TerminalTranscriptPageV1
                {
                    FormatVersion = SnapshotCrypto.FormatVersion,
                    AsOfSnapshotSeq = source.Sequence,
                    FirstLineId = first.LineId,
                    LastLineId = last.LineId,
                    HasMoreBefore = offset > 0,
                    NextBeforeLineId = offset > 0 ? first.LineId : 0,
                    Palette = palette.Clone()
                };
                foreach (var style in styles) page.StyleTable.Add(ToWireStyle(style));
                for (var index = 0; index < count; index++)
                    page.Rows.Add(ToWireRow(source.ScrollbackRows[offset + index], styles));
                yield return page;
            }
        }

        private static TerminalRowV1 ToWireRow(
            PortableTerminalRowV1 source,
            IList<PortableTerminalStyleV1> styles)
        {
            var row = new TerminalRowV1
            {
                LineId = source.LineId,
                SealedAtSeq = source.SealedAtSequence,
                SoftWrapped = source.Wrapped
            };
            foreach (var cell in source.Cells)
            {
                var styleIndex = styles.IndexOf(cell.Style);
                if (styleIndex < 0) throw new InvalidDataException("Terminal row style is absent from its style table.");
                row.Runs.Add(new TerminalCellRunV1
                {
                    Repeat = 1,
                    Grapheme = cell.Character,
                    DisplayWidth = checked((uint)cell.DisplayWidth),
                    StyleIndex = checked((uint)styleIndex),
                    WideContinuation = cell.WideContinuation
                });
            }
            return row;
        }

        private static TerminalStyleV1 ToWireStyle(PortableTerminalStyleV1 source)
        {
            return new TerminalStyleV1
            {
                Foreground = ToWireColor(source.Foreground),
                Background = ToWireColor(source.Background),
                UnderlineColor = new TerminalColorRefV1 { DefaultColor = true },
                Attributes = (uint)source.Attributes,
                UnderlineStyle = (source.Attributes & PortableTerminalAttributesV1.Underline) != 0
                    ? TerminalUnderlineStyleV1.Single
                    : TerminalUnderlineStyleV1.None,
                HyperlinkIndexPlusOne = 0
            };
        }

        private static TerminalColorRefV1 ToWireColor(int color)
        {
            if (color == PortableTerminalStyleV1.DefaultColor)
                return new TerminalColorRefV1 { DefaultColor = true };
            if (PortableTerminalStyleV1.TryGetRgb(color, out var rgb))
                return new TerminalColorRefV1 { Rgb = checked((uint)rgb) };
            return new TerminalColorRefV1 { PaletteIndex = checked((uint)color) };
        }

        private static TerminalCharsetStateV1 CreateCharset()
        {
            return new TerminalCharsetStateV1
            {
                G0 = TerminalCharsetV1.Ascii,
                G1 = TerminalCharsetV1.Ascii,
                G2 = TerminalCharsetV1.Ascii,
                G3 = TerminalCharsetV1.Ascii,
                Gl = 0,
                // The Android Termux renderer does not expose an independent GR selector.
                // Emit the portable subset that can be restored without changing semantics.
                Gr = 0
            };
        }

        private static TerminalPaletteV1 CreatePalette()
        {
            var palette = new TerminalPaletteV1
            {
                DefaultForegroundRgb = 0x00c0c0c0,
                DefaultBackgroundRgb = 0x00000000,
                DefaultCursorRgb = 0x00c0c0c0
            };
            uint[] ansi =
            {
                0x00000000, 0x00800000, 0x00008000, 0x00808000,
                0x00000080, 0x00800080, 0x00008080, 0x00c0c0c0,
                0x00808080, 0x00ff0000, 0x0000ff00, 0x00ffff00,
                0x000000ff, 0x00ff00ff, 0x0000ffff, 0x00ffffff
            };
            palette.ColorsRgb.Add(ansi);
            uint[] levels = { 0, 95, 135, 175, 215, 255 };
            foreach (var red in levels)
                foreach (var green in levels)
                    foreach (var blue in levels)
                        palette.ColorsRgb.Add((red << 16) | (green << 8) | blue);
            for (uint index = 0; index < 24; index++)
            {
                var level = 8 + index * 10;
                palette.ColorsRgb.Add((level << 16) | (level << 8) | level);
            }
            return palette;
        }

        private static ProductionTerminalHistoryOutbox BuildOutbox(
            Guid sessionId,
            TerminalSnapshotV1 snapshot,
            IList<TerminalTranscriptPageV1> pages,
            byte[] snapshotBytes,
            IList<byte[]> pageBytes)
        {
            var outbox = new ProductionTerminalHistoryOutbox
            {
                SessionId = sessionId,
                SnapshotSequence = snapshot.SnapshotSeq
            };
            var initialTranscriptBeforeLineId = SnapshotCrypto.InitialTranscriptBeforeLineId(snapshot);
            var order = 0;
            // Android can render the tail immediately after this anchor lands;
            // older transcript pages are background backfill and must never
            // delay the usable snapshot.
            outbox.Artifacts.Add(new ProductionTerminalHistoryArtifact
            {
                ArtifactId = "snapshot-" + snapshot.SnapshotSeq.ToString("D20"),
                Kind = ProductionTerminalHistoryArtifactKind.Snapshot,
                UploadOrder = order++,
                SnapshotSequence = snapshot.SnapshotSeq,
                InitialTranscriptBeforeLineId = initialTranscriptBeforeLineId,
                TranscriptPageCount = pages.Count,
                Columns = checked((int)snapshot.Columns),
                Rows = checked((int)snapshot.Rows),
                FormatName = SnapshotCrypto.SnapshotFormatName,
                FormatVersion = checked((int)SnapshotCrypto.FormatVersion),
                Plaintext = (byte[])snapshotBytes.Clone()
            });
            for (var index = 0; index < pages.Count; index++)
            {
                var page = pages[index];
                outbox.Artifacts.Add(new ProductionTerminalHistoryArtifact
                {
                    ArtifactId = "transcript-" + snapshot.SnapshotSeq.ToString("D20") + "-" +
                        page.FirstLineId.ToString("D20") + "-" + page.LastLineId.ToString("D20"),
                    Kind = ProductionTerminalHistoryArtifactKind.Transcript,
                    UploadOrder = order++,
                    SnapshotSequence = snapshot.SnapshotSeq,
                    FirstLineId = page.FirstLineId,
                    LastLineId = page.LastLineId,
                    ExclusiveBeforeLineId = index + 1 < pages.Count
                        ? pages[index + 1].FirstLineId
                        : snapshot.PrimaryScreen.VisibleRows[0].LineId,
                    HasMoreBefore = page.HasMoreBefore,
                    NextBeforeLineId = page.NextBeforeLineId,
                    FormatName = SnapshotCrypto.TranscriptFormatName,
                    FormatVersion = checked((int)SnapshotCrypto.FormatVersion),
                    Plaintext = (byte[])pageBytes[index].Clone()
                });
            }
            return outbox;
        }

        private byte[] SealArtifact(
            Guid sessionId,
            ProductionHistoryRecipient recipient,
            ProductionTerminalHistoryArtifact artifact)
        {
            if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript)
            {
                var page = TerminalTranscriptPageV1.Parser.ParseFrom(artifact.Plaintext);
                return SnapshotCrypto.SealTranscript(
                    recipient.PairwiseKey,
                    sessionId,
                    pcDeviceId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    page,
                    artifact.ExclusiveBeforeLineId);
            }
            if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
            {
                var snapshot = TerminalSnapshotV1.Parser.ParseFrom(artifact.Plaintext);
                return SnapshotCrypto.SealSnapshot(
                    recipient.PairwiseKey,
                    sessionId,
                    pcDeviceId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    snapshot);
            }
            throw new InvalidDataException("Terminal history artifact kind is unsupported.");
        }
    }
}
