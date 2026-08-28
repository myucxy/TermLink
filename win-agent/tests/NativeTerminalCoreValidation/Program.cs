using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using TermLink.TerminalCore;

namespace TermLink.NativeTerminalCore.Validation
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                HostEffectFrameV1Validation.Run();
                HostEffectFrameV2Validation.Run();
                RenderFrameV1Validation.Run();
                RenderFrameV2Validation.Run();
                RenderFrameV3Validation.Run();
                ValidateAbiAndSequenceOne();
                ValidateHostEffectApplication();
                ValidateHostEffectV2Application();
                ValidateConfigV2LayoutAndScrollback();
                ValidateConfigV3OutputProfiles();
                ValidateAtomicBatchApplication();
                ValidateAtomicBatchFailurePolicies();
                ValidateAtomicBatchBounds();
                ValidateRenderFrameExport();
                ValidateInputEncoding();
                ValidateKittyInputEncoding();
                ValidateWin32InputEncoding();
                ValidateMouseInputEncoding();
                ValidateFocusInputEncoding();
                ValidateSequenceAndGapFailures();
                ValidateTemporaryCheckpointIneligibility();
                ValidatePresentationEligibility();
                ValidateCheckpointSuffixEquivalence();
                ValidateWidePrimaryReflow();
                ValidateCheckpointImmutability();
                ValidateOwnedBufferFailuresAndUnknownStatusPolicy();
                ValidateDisposeAndInvalidArguments();
                Console.WriteLine("Native terminal core validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static void ValidateAbiAndSequenceOne()
        {
            var abi = NativeTerminalCoreV2.ValidateAbi();
            Require(abi.AbiVersion == 1, "Unexpected native ABI version.");
            Require(abi.MaxSnapshotBytes == 64UL * 1024UL * 1024UL,
                "Unexpected native maximum snapshot size.");
            Require(abi.RenderFrameV1FormatVersion == 1,
                "Unexpected render frame format version.");
            Require(abi.MaxRenderFrameV1Bytes == 16UL * 1024UL * 1024UL,
                "Unexpected native maximum render frame v1 size.");
            Require(abi.RenderFrameV2FormatVersion == 2,
                "Unexpected render frame v2 format version.");
            Require(abi.MaxRenderFrameV2Bytes == 16UL * 1024UL * 1024UL,
                "Unexpected native maximum render frame v2 size.");
            Require(abi.RenderFrameV3FormatVersion == 3,
                "Unexpected render frame v3 format version.");
            Require(abi.MaxRenderFrameV3Bytes == 16UL * 1024UL * 1024UL,
                "Unexpected native maximum render frame v3 size.");
            Require(abi.HostEffectFrameV1FormatVersion == 1,
                "Unexpected host effect frame v1 format version.");
            Require(abi.MaxHostEffectFrameV1Bytes == 65_710_862UL,
                "Unexpected native maximum host effect frame v1 size.");
            Require(abi.HostEffectFrameV2FormatVersion == 2,
                "Unexpected host effect frame v2 format version.");
            Require(abi.MaxHostEffectFrameV2Bytes == 123_033_008UL,
                "Unexpected native maximum host effect frame v2 size.");
            Require(abi.ConfigV1StructSize == 40, "Unexpected config v1 struct size.");
            Require(abi.ConfigV2StructSize == 64, "Unexpected config v2 struct size.");
            Require(abi.ConfigV3StructSize == 88, "Unexpected config v3 struct size.");
            Require(abi.EventStructSize == 56, "Unexpected event struct size.");
            Require(abi.BufferStructSize == 24, "Unexpected buffer struct size.");
            Require(abi.CheckpointInfoStructSize == 40, "Unexpected checkpoint info size.");
            Require(abi.PresentationInfoV1StructSize == 48,
                "Unexpected presentation info v1 size.");
            Require(abi.InputV1StructSize == 56, "Unexpected input v1 struct size.");
            Require(abi.InputV2StructSize == 96, "Unexpected input v2 struct size.");
            Require(abi.InputV3StructSize == 160, "Unexpected input v3 struct size.");
            Require(abi.MouseInputV1StructSize == 64, "Unexpected mouse input v1 struct size.");

            using var terminal = NativeTerminalCoreV2.Create(8, 3);
            using (var initial = terminal.CaptureCheckpoint())
            {
                Require(initial.Info.Revision == 1, "Default initial sequence was not one.");
                Require(initial.Info.NextSourceSequence == 1, "Initial next sequence was not one.");
            }
            terminal.ApplyOutput(1, Bytes("A"));
            terminal.ApplyResize(2, 10, 4);
            terminal.ApplyAdvance(3, 25);
            using var final = terminal.CaptureCheckpoint();
            Require(final.Info.Revision == 4, "OUTPUT/RESIZE/ADVANCE did not consume sequences.");

            using var custom = NativeTerminalCoreV2.Create(4, 2, 19);
            custom.ApplyOutput(19, Bytes("x"));
            using var customCheckpoint = custom.CaptureCheckpoint();
            Require(customCheckpoint.Info.NextSourceSequence == 20,
                "Custom initial sequence was not preserved.");
        }

        private static void ValidateHostEffectApplication()
        {
            ValidateSingleHostEffectReplies();
            ValidateBatchHostEffectMetadata();
            ValidateZeroAndLegacyHostEffectBehavior();
            ValidateHostEffectFailurePolicies();
            ValidateHostEffectBufferRelease();
        }

        private static void ValidateSingleHostEffectReplies()
        {
            using var terminal = NativeTerminalCoreV2.Create(4, 2, 7);
            var frame = terminal.ApplyOutputWithHostEffectsV1(
                7,
                Bytes("\x1b[c\x1b[>0c\x1b[5n\x1b[6n\x1b[?5n\x1b[?6n"));
            var expectedPayloads = new[]
            {
                Bytes("\x1b[?1;2c"),
                Bytes("\x1b[>0;0;0c"),
                Bytes("\x1b[0n"),
                Bytes("\x1b[1;1R"),
                Bytes("\x1b[?0n"),
                Bytes("\x1b[?1;1R")
            };
            var expectedOffsets = new uint[] { 3, 8, 12, 16, 21, 26 };
            Require(frame.Records.Count == expectedPayloads.Length,
                "The native single-event host effect reply count diverged.");
            for (var index = 0; index < expectedPayloads.Length; index++)
            {
                var record = frame.Records[index];
                Require(record.Kind == NativeTerminalHostEffectKindV1.PtyReply &&
                    record.SourceSequence == 7 &&
                    record.SourceOffsetEnd == expectedOffsets[index] &&
                    record.Ordinal == 0 &&
                    record.Payload.SequenceEqual(expectedPayloads[index]),
                    "A native single-event host effect reply or source key diverged.");
            }

            using var checkpoint = terminal.CaptureCheckpoint();
            Require(checkpoint.Info.NextSourceSequence == 8,
                "Effect-aware single apply did not commit exactly one source event.");
        }

        private static void ValidateBatchHostEffectMetadata()
        {
            using var terminal = NativeTerminalCoreV2.Create(4, 2, 10);
            var frame = terminal.ApplyBatchWithHostEffectsV1(
                NativeTerminalBatchEvent.Output(10, Bytes("\x1b[")),
                NativeTerminalBatchEvent.Output(11, Bytes("c\x1b[5")),
                NativeTerminalBatchEvent.Output(12, Bytes("n\x1b[>c")));
            var expectedPayloads = new[]
            {
                Bytes("\x1b[?1;2c"),
                Bytes("\x1b[0n"),
                Bytes("\x1b[>0;0;0c")
            };
            var expectedSequences = new ulong[] { 11, 12, 12 };
            var expectedOffsets = new uint[] { 1, 1, 5 };
            Require(frame.Records.Count == expectedPayloads.Length,
                "The native batch host effect reply count diverged.");
            for (var index = 0; index < expectedPayloads.Length; index++)
            {
                var record = frame.Records[index];
                Require(record.SourceSequence == expectedSequences[index] &&
                    record.SourceOffsetEnd == expectedOffsets[index] &&
                    record.Ordinal == 0 &&
                    record.Payload.SequenceEqual(expectedPayloads[index]),
                    "A native batch host effect reply or source key diverged.");
            }

            using var checkpoint = terminal.CaptureCheckpoint();
            Require(checkpoint.Info.NextSourceSequence == 13,
                "Effect-aware batch apply did not commit every source event.");
        }

        private static void ValidateZeroAndLegacyHostEffectBehavior()
        {
            using (var terminal = NativeTerminalCoreV2.Create(4, 2))
            {
                var output = terminal.ApplyOutputWithHostEffectsV1(1, Bytes("plain"));
                Require(output.Records.Count == 0,
                    "A plain output did not produce the canonical zero-effect frame.");
                var resize = terminal.ApplyResizeWithHostEffectsV1(2, 5, 3);
                Require(resize.Records.Count == 0,
                    "A resize unexpectedly emitted a host effect.");
                var advance = terminal.ApplyAdvanceWithHostEffectsV1(3, 25);
                Require(advance.Records.Count == 0,
                    "A logical-time advance unexpectedly emitted a host effect.");

                Throws<ArgumentOutOfRangeException>(
                    () => terminal.ApplyBatchWithHostEffectsV1(),
                    "Effect-aware batch apply bypassed the shared empty-batch gate.");
                var afterRejectedBatch = terminal.ApplyAdvanceWithHostEffectsV1(4, 0);
                Require(afterRejectedBatch.Records.Count == 0,
                    "A rejected effect-aware batch retired a healthy wrapper.");
            }

            using (var legacy = NativeTerminalCoreV2.Create(4, 2))
            {
                legacy.ApplyOutput(1, Bytes("\x1b[c"));
                legacy.ApplyBatch(NativeTerminalBatchEvent.Output(2, Bytes("\x1b[5n")));
                var following = legacy.ApplyAdvanceWithHostEffectsV1(3, 0);
                Require(following.Records.Count == 0,
                    "Legacy apply queued host effects for a later effect-aware call.");
                using var checkpoint = legacy.CaptureCheckpoint();
                Require(checkpoint.Info.NextSourceSequence == 4,
                    "Legacy single or batch apply behavior changed after host effect wiring.");
            }

            using (var gap = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.Gap,
                    () => gap.ApplyGapWithHostEffectsV1(1),
                    "Effect-aware GAP apply did not preserve the native GAP status.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => gap.ApplyAdvanceWithHostEffectsV1(1, 0),
                    "Effect-aware GAP apply did not retire the wrapper.");
            }
        }

        private static void ValidateHostEffectFailurePolicies()
        {
            using (var dimensions = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.InvalidDimensions,
                    () => dimensions.ApplyResizeWithHostEffectsV1(1, 0, 2),
                    "An invalid effect-aware resize was accepted.");
                var retry = dimensions.ApplyResizeWithHostEffectsV1(1, 5, 2);
                Require(retry.Records.Count == 0,
                    "An invalid effect-aware resize consumed its source sequence.");
            }

            using (var stale = NativeTerminalCoreV2.Create(4, 2))
            {
                _ = stale.ApplyOutputWithHostEffectsV1(1, Bytes("first"));
                ThrowsNative(
                    NativeTerminalStatus.SequenceMismatch,
                    () => stale.ApplyOutputWithHostEffectsV1(1, Bytes("stale")),
                    "A stale effect-aware sequence was accepted.");
                var retry = stale.ApplyOutputWithHostEffectsV1(2, Bytes("retry"));
                Require(retry.Records.Count == 0,
                    "A retryable stale sequence retired the effect-aware wrapper.");
            }

            using (var forward = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.SequenceMismatch,
                    () => forward.ApplyOutputWithHostEffectsV1(2, Bytes("forward")),
                    "A forward effect-aware sequence discontinuity was accepted.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => forward.ApplyOutputWithHostEffectsV1(1, Bytes("after-forward")),
                    "Native failed-closed state was not surfaced after a forward sequence.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => forward.ApplyAdvanceWithHostEffectsV1(1, 0),
                    "A native failed-closed response did not retire the wrapper.");
            }

            using (var staleBatch = NativeTerminalCoreV2.Create(4, 2))
            {
                _ = staleBatch.ApplyOutputWithHostEffectsV1(1, Bytes("first"));
                ThrowsNative(
                    NativeTerminalStatus.SequenceMismatch,
                    () => staleBatch.ApplyBatchWithHostEffectsV1(
                        NativeTerminalBatchEvent.Output(1, Bytes("stale")),
                        NativeTerminalBatchEvent.Advance(2, 0)),
                    "A stale effect-aware batch was accepted.");
                _ = staleBatch.ApplyAdvanceWithHostEffectsV1(2, 0);
            }

            using (var poisonedBatch = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.Unsupported,
                    () => poisonedBatch.ApplyBatchWithHostEffectsV1(
                        NativeTerminalBatchEvent.Output(1, Bytes("prefix")),
                        NativeTerminalBatchEvent.Output(2, Bytes("\x1b[999m"))),
                    "A poisoning effect-aware batch was accepted.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => poisonedBatch.ApplyAdvanceWithHostEffectsV1(1, 0),
                    "A poisoning effect-aware batch did not retire the wrapper.");
            }

            var disposed = NativeTerminalCoreV2.Create(4, 2);
            disposed.Dispose();
            Throws<ObjectDisposedException>(
                () => disposed.ApplyOutputWithHostEffectsV1(1, Bytes("x")),
                "A disposed terminal accepted effect-aware output.");
            Throws<ObjectDisposedException>(
                () => disposed.ApplyBatchWithHostEffectsV1(
                    NativeTerminalBatchEvent.Advance(1, 0)),
                "A disposed terminal accepted an effect-aware batch.");
        }

        private static void ValidateHostEffectBufferRelease()
        {
            var nativeHandle = IntPtr.Zero;
            var config = NativeTerminalInterop.CreateConfig(4, 2, 1);
            var status = NativeTerminalInterop.TerminalCreate(in config, ref nativeHandle);
            NativeTerminalInterop.RequireEmptyFailedTerminalHandle(
                status,
                nativeHandle,
                nameof(NativeTerminalInterop.TerminalCreate));
            NativeTerminalInterop.ThrowIfFailed(
                status,
                nameof(NativeTerminalInterop.TerminalCreate));
            using var handle = NativeTerminalInterop.OwnTerminal(nativeHandle);
            var terminalEvent = NativeTerminalInterop.CreateAdvanceEvent(1, 0);
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                status = NativeTerminalInterop.TerminalApplyEventWithHostEffectsV1(
                    handle,
                    in terminalEvent,
                    ref buffer);
                NativeTerminalInterop.ThrowIfFailed(
                    status,
                    nameof(NativeTerminalInterop.TerminalApplyEventWithHostEffectsV1));
                Require(buffer.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                    buffer.StructSize == NativeTerminalInterop.BufferStructSize &&
                    buffer.Data != IntPtr.Zero &&
                    buffer.Length == 64,
                    "The raw native zero-effect frame did not publish an owned 64-byte buffer.");
                var freeStatus = NativeTerminalInterop.TerminalBufferFree(ref buffer);
                NativeTerminalInterop.ThrowIfFailed(
                    freeStatus,
                    nameof(NativeTerminalInterop.TerminalBufferFree));
                Require(buffer.Data == IntPtr.Zero && buffer.Length == 0,
                    "Freeing a raw native host effect buffer did not clear its ownership fields.");
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                    _ = NativeTerminalInterop.TerminalBufferFree(ref buffer);
            }
        }

        private static void ValidateHostEffectV2Application()
        {
            ValidateHostEffectV2VersionRetry();
            ValidateHostEffectV2QueriesAndOrdinals();
            ValidateHostEffectV2Batch();
            ValidateHostEffectV2BufferRelease();
        }

        private static void ValidateHostEffectV2VersionRetry()
        {
            using var terminal = NativeTerminalCoreV2.Create(8, 2);
            var query = Bytes("\x1b[4$p");
            ThrowsNative(
                NativeTerminalStatus.HostEffectVersionRequired,
                () => terminal.ApplyOutputWithHostEffectsV1(1, query),
                "A V2-only host effect did not return status 14 through V1.");

            using (var unchanged = terminal.CaptureCheckpoint())
            {
                Require(unchanged.Info.NextSourceSequence == 1 &&
                    unchanged.Info.Revision == 1,
                    "Status 14 committed the V2-only source event.");
            }

            var frame = terminal.ApplyOutputWithHostEffectsV2(1, query);
            Require(frame.Records.Count == 1,
                "Retrying the V2-only event did not capture one reply.");
            var record = frame.Records[0];
            Require(record.Kind == NativeTerminalHostEffectKindV2.PtyReply &&
                record.SourceSequence == 1 &&
                record.SourceOffsetEnd == (uint)query.Length &&
                record.Ordinal == 0 &&
                record.Payload.SequenceEqual(Bytes("\x1b[4;2$y")),
                "The status-14 V2 retry reply or source key diverged.");

            using var committed = terminal.CaptureCheckpoint();
            Require(committed.Info.NextSourceSequence == 2 && committed.Info.Revision == 2,
                "The V2 retry did not commit exactly once.");

            var empty = terminal.ApplyAdvanceWithHostEffectsV2(2, 0);
            Require(empty.Records.Count == 0 && empty.RawBytes.Count == 64,
                "A V2 zero-effect event did not return the canonical empty frame.");
        }

        private static void ValidateHostEffectV2QueriesAndOrdinals()
        {
            using var terminal = NativeTerminalCoreV2.Create(8, 2, 10);
            var osc = Bytes("\x1b]4;1;#f00;1;?;1;#0f0;1;?\x1b\\");
            var colors = terminal.ApplyOutputWithHostEffectsV2(10, osc);
            Require(colors.Records.Count == 2,
                "Two ordered OSC palette queries did not produce two V2 replies.");
            Require(colors.Records[0].SourceSequence == 10 &&
                colors.Records[1].SourceSequence == 10 &&
                colors.Records[0].SourceOffsetEnd == colors.Records[1].SourceOffsetEnd &&
                colors.Records[0].Ordinal == 0 &&
                colors.Records[1].Ordinal == 1,
                "V2 did not preserve repeated-query source keys and ordinals.");
            Require(
                colors.Records[0].Payload.SequenceEqual(
                    Bytes("\x1b]4;1;rgb:ffff/0000/0000\x1b\\")) &&
                colors.Records[1].Payload.SequenceEqual(
                    Bytes("\x1b]4;1;rgb:0000/ffff/0000\x1b\\")),
                "The ordered OSC palette query replies diverged.");

            var queries = Bytes(
                "\x1b[4$p" +
                "\x1b[?25$p" +
                "\x1b[?9999$p" +
                "\x1bP$qm\x1b\\" +
                "\x1bP$qr\x1b\\" +
                "\x1bP+q436f;524742\x1b\\");
            var frame = terminal.ApplyOutputWithHostEffectsV2(11, queries);
            var expected = new[]
            {
                Bytes("\x1b[4;2$y"),
                Bytes("\x1b[?25;1$y"),
                Bytes("\x1b[?9999;0$y"),
                Bytes("\x1bP1$r0m\x1b\\"),
                Bytes("\x1bP1$r1;2r\x1b\\"),
                Bytes("\x1bP1+r436f=323536;524742=382f382f38\x1b\\")
            };
            Require(frame.Records.Count == expected.Length,
                "The V2 mode/DCS query reply count diverged.");
            for (var index = 0; index < expected.Length; index++)
            {
                Require(frame.Records[index].SourceSequence == 11 &&
                    frame.Records[index].Ordinal == 0 &&
                    frame.Records[index].Payload.SequenceEqual(expected[index]),
                    "A V2 mode/DCS query reply diverged.");
            }
        }

        private static void ValidateHostEffectV2Batch()
        {
            using var terminal = NativeTerminalCoreV2.Create(8, 2, 30);
            Throws<ArgumentOutOfRangeException>(
                () => terminal.ApplyBatchWithHostEffectsV2(),
                "The V2 host-effect path bypassed the shared empty-batch preflight.");
            var frame = terminal.ApplyBatchWithHostEffectsV2(
                NativeTerminalBatchEvent.Output(30, Bytes("\x1b[")),
                NativeTerminalBatchEvent.Output(31, Bytes("4$p")));
            Require(frame.Records.Count == 1,
                "A split V2 query batch did not produce exactly one reply.");
            var record = frame.Records[0];
            Require(record.SourceSequence == 31 &&
                record.SourceOffsetEnd == 3 &&
                record.Ordinal == 0 &&
                record.Payload.SequenceEqual(Bytes("\x1b[4;2$y")),
                "The split V2 batch reply did not retain its completing source key.");
            using var checkpoint = terminal.CaptureCheckpoint();
            Require(checkpoint.Info.NextSourceSequence == 32,
                "The successful V2 host-effect batch did not commit atomically.");
        }

        private static void ValidateHostEffectV2BufferRelease()
        {
            var nativeHandle = IntPtr.Zero;
            var config = NativeTerminalInterop.CreateConfig(4, 2, 1);
            var status = NativeTerminalInterop.TerminalCreate(in config, ref nativeHandle);
            NativeTerminalInterop.RequireEmptyFailedTerminalHandle(
                status,
                nativeHandle,
                nameof(NativeTerminalInterop.TerminalCreate));
            NativeTerminalInterop.ThrowIfFailed(
                status,
                nameof(NativeTerminalInterop.TerminalCreate));
            using var handle = NativeTerminalInterop.OwnTerminal(nativeHandle);
            var terminalEvent = NativeTerminalInterop.CreateAdvanceEvent(1, 0);
            var buffer = NativeTerminalInterop.CreateEmptyBuffer();
            try
            {
                status = NativeTerminalInterop.TerminalApplyEventWithHostEffectsV2(
                    handle,
                    in terminalEvent,
                    ref buffer);
                NativeTerminalInterop.ThrowIfFailed(
                    status,
                    nameof(NativeTerminalInterop.TerminalApplyEventWithHostEffectsV2));
                Require(buffer.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                    buffer.StructSize == NativeTerminalInterop.BufferStructSize &&
                    buffer.Data != IntPtr.Zero &&
                    buffer.Length == 64,
                    "The raw native V2 zero-effect call did not publish an owned frame.");
                var freeStatus = NativeTerminalInterop.TerminalBufferFree(ref buffer);
                NativeTerminalInterop.ThrowIfFailed(
                    freeStatus,
                    nameof(NativeTerminalInterop.TerminalBufferFree));
                Require(buffer.Data == IntPtr.Zero && buffer.Length == 0,
                    "Freeing a raw V2 host-effect frame did not clear ownership fields.");
            }
            finally
            {
                if (buffer.Data != IntPtr.Zero || buffer.Length != 0)
                    _ = NativeTerminalInterop.TerminalBufferFree(ref buffer);
            }
        }

        private static void ValidateConfigV2LayoutAndScrollback()
        {
            Require(Marshal.SizeOf<TlTerminalConfigV2>() == 64,
                "The managed config v2 ABI is not 64 bytes.");
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.AbiVersion), 0);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.StructSize), 4);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.Columns), 8);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.Rows), 12);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.InitialSourceSequence), 16);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.ReservedV1Compat0), 24);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.ReservedV1Compat1), 32);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.HistoryCapacityRows), 40);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.Reserved0), 48);
            RequireConfigV2Offset(nameof(TlTerminalConfigV2.Reserved1), 56);

            var config = NativeTerminalInterop.CreateConfigV2(3, 2, 17, ulong.MaxValue);
            Require(config.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                config.StructSize == NativeTerminalInterop.ConfigV2StructSize &&
                config.Columns == 3 &&
                config.Rows == 2 &&
                config.InitialSourceSequence == 17 &&
                config.HistoryCapacityRows == ulong.MaxValue,
                "The managed config v2 factory truncated or changed a value.");
            Require(config.ReservedV1Compat0 == 0 &&
                config.ReservedV1Compat1 == 0 &&
                config.Reserved0 == 0 &&
                config.Reserved1 == 0,
                "The managed config v2 factory did not zero reserved fields.");

            ThrowsNative(
                NativeTerminalStatus.InvalidArgument,
                () => NativeTerminalCoreV2.Create(
                    3,
                    2,
                    17,
                    (ulong)uint.MaxValue + 1UL).Dispose(),
                "A history capacity above the native u32 bound was truncated.");

            using var legacy = NativeTerminalCoreV2.Create(3, 2, 17);
            using var history = NativeTerminalCoreV2.Create(
                3,
                2,
                initialSourceSequence: 17,
                historyCapacityRows: 2);
            var scrollingOutput = Bytes("A\r\nB\r\nC");
            legacy.ApplyOutput(17, scrollingOutput);
            history.ApplyOutput(17, scrollingOutput);
            using var legacyCheckpoint = legacy.CaptureCheckpoint();
            using var historyCheckpoint = history.CaptureCheckpoint();
            Require(historyCheckpoint.Info.NextSourceSequence == 18,
                "The history-enabled terminal did not checkpoint the scrolling event.");
            var legacySnapshot = legacyCheckpoint.ExportSnapshot();
            var historySnapshot = historyCheckpoint.ExportSnapshot();
            Require(!legacySnapshot.SequenceEqual(historySnapshot),
                "Configured scrollback did not affect the checkpoint snapshot.");

            using var restored = NativeTerminalCoreV2.ImportSnapshot(historySnapshot);
            using var restoredCheckpoint = restored.CaptureCheckpoint();
            Equal(historySnapshot, restoredCheckpoint.ExportSnapshot(),
                "The history-enabled checkpoint did not round-trip canonically.");
        }

        private static void RequireConfigV2Offset(string fieldName, int expectedOffset)
        {
            Require(Marshal.OffsetOf<TlTerminalConfigV2>(fieldName).ToInt32() == expectedOffset,
                "Unexpected config v2 offset for " + fieldName + ".");
        }

        private static void ValidateConfigV3OutputProfiles()
        {
            Require(Marshal.SizeOf<TlTerminalConfigV3>() == 88,
                "The managed config v3 ABI is not 88 bytes.");
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.AbiVersion), 0);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.StructSize), 4);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.Columns), 8);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.Rows), 12);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.InitialSourceSequence), 16);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.ReservedV1Compat0), 24);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.ReservedV1Compat1), 32);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.HistoryCapacityRows), 40);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.ReservedV2Compat0), 48);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.ReservedV2Compat1), 56);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.OutputProfile), 64);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.ReservedV3Padding), 68);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.ReservedV3Tail0), 72);
            RequireConfigV3Offset(nameof(TlTerminalConfigV3.ReservedV3Tail1), 80);
            Require(NativeTerminalInterop.ConfigV3StructSize == 88,
                "The managed config v3 size discovery contract diverged.");

            var canonical = NativeTerminalInterop.CreateConfigV3(
                8,
                2,
                1,
                3,
                NativeTerminalOutputProfile.Utf8WithStandaloneC1);
            Require(canonical.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                canonical.StructSize == NativeTerminalInterop.ConfigV3StructSize &&
                canonical.Columns == 8 &&
                canonical.Rows == 2 &&
                canonical.InitialSourceSequence == 1 &&
                canonical.HistoryCapacityRows == 3 &&
                canonical.OutputProfile ==
                    (uint)NativeTerminalOutputProfile.Utf8WithStandaloneC1,
                "The managed config v3 factory changed a configured value.");
            Require(canonical.ReservedV1Compat0 == 0 &&
                canonical.ReservedV1Compat1 == 0 &&
                canonical.ReservedV2Compat0 == 0 &&
                canonical.ReservedV2Compat1 == 0 &&
                canonical.ReservedV3Padding == 0 &&
                canonical.ReservedV3Tail0 == 0 &&
                canonical.ReservedV3Tail1 == 0,
                "The managed config v3 factory did not zero every reserved field.");

            var malformed = canonical;
            malformed.OutputProfile = 2;
            RequireConfigV3Rejected(malformed, "An unknown config v3 output profile was accepted.");
            malformed = canonical;
            malformed.OutputProfile = uint.MaxValue;
            RequireConfigV3Rejected(malformed,
                "The maximum unknown config v3 output profile was accepted.");
            malformed = canonical;
            malformed.StructSize = 87;
            RequireConfigV3Rejected(malformed, "A short config v3 structure was accepted.");
            malformed = canonical;
            malformed.StructSize = 89;
            RequireConfigV3Rejected(malformed, "A config v3 structure with a tail was accepted.");
            malformed = canonical;
            malformed.ReservedV1Compat0 = 1;
            RequireConfigV3Rejected(malformed, "Config v3 V1 reserved field zero was not enforced.");
            malformed = canonical;
            malformed.ReservedV1Compat1 = 1;
            RequireConfigV3Rejected(malformed, "Config v3 V1 reserved field one was not enforced.");
            malformed = canonical;
            malformed.ReservedV2Compat0 = 1;
            RequireConfigV3Rejected(malformed, "Config v3 V2 reserved field zero was not enforced.");
            malformed = canonical;
            malformed.ReservedV2Compat1 = 1;
            RequireConfigV3Rejected(malformed, "Config v3 V2 reserved field one was not enforced.");
            malformed = canonical;
            malformed.ReservedV3Padding = 1;
            RequireConfigV3Rejected(malformed, "Config v3 reserved padding was not enforced.");
            malformed = canonical;
            malformed.ReservedV3Tail0 = 1;
            RequireConfigV3Rejected(malformed, "Config v3 tail reserved field zero was not enforced.");
            malformed = canonical;
            malformed.ReservedV3Tail1 = 1;
            RequireConfigV3Rejected(malformed, "Config v3 tail reserved field one was not enforced.");

            var occupied = IntPtr.Zero;
            var occupiedConfig = NativeTerminalInterop.CreateConfigV3(
                4,
                2,
                1,
                0,
                NativeTerminalOutputProfile.Utf8);
            try
            {
                Require(NativeTerminalInterop.TerminalCreateV3(in occupiedConfig, ref occupied) ==
                        NativeTerminalStatus.Ok && occupied != IntPtr.Zero,
                    "A canonical config v3 did not create a native terminal.");
                var original = occupied;
                var occupiedStatus = NativeTerminalInterop.TerminalCreateV3(
                    in occupiedConfig,
                    ref occupied);
                Require(occupiedStatus == NativeTerminalStatus.InvalidArgument && occupied == original,
                    "Config v3 creation overwrote or accepted an occupied output handle.");
            }
            finally
            {
                if (occupied != IntPtr.Zero) NativeTerminalInterop.TerminalDestroy(occupied);
            }

            ThrowsNative(
                NativeTerminalStatus.InvalidArgument,
                () => NativeTerminalCoreV2.Create(
                    4,
                    2,
                    1,
                    0,
                    (NativeTerminalOutputProfile)2).Dispose(),
                "The public config v3 overload accepted an unknown output profile.");

            var c1Title = C1OscTitle("standalone-c1");
            using var utf8 = NativeTerminalCoreV2.Create(
                16,
                2,
                1,
                0,
                NativeTerminalOutputProfile.Utf8);
            using var standalone = NativeTerminalCoreV2.Create(
                16,
                2,
                1,
                0,
                NativeTerminalOutputProfile.Utf8WithStandaloneC1);
            utf8.ApplyOutput(1, c1Title);
            standalone.ApplyOutput(1, c1Title);
            Require(CurrentTitle(utf8).Length == 0,
                "The UTF-8 profile interpreted standalone C1 OSC bytes.");
            Require(CurrentTitle(standalone) == "standalone-c1",
                "The standalone-C1 profile did not interpret C1 OSC/ST bytes.");

            byte[] snapshot;
            using (var checkpoint = standalone.CaptureCheckpoint())
                snapshot = checkpoint.ExportSnapshot();
            using var restored = NativeTerminalCoreV2.ImportSnapshot(snapshot);
            restored.ApplyOutput(2, C1OscTitle("restored-c1"));
            Require(CurrentTitle(restored) == "restored-c1",
                "Snapshot import did not preserve the standalone-C1 output profile.");
        }

        private static void RequireConfigV3Offset(string fieldName, int expectedOffset)
        {
            Require(Marshal.OffsetOf<TlTerminalConfigV3>(fieldName).ToInt32() == expectedOffset,
                "Unexpected config v3 offset for " + fieldName + ".");
        }

        private static void RequireConfigV3Rejected(TlTerminalConfigV3 config, string message)
        {
            var terminal = IntPtr.Zero;
            try
            {
                var status = NativeTerminalInterop.TerminalCreateV3(in config, ref terminal);
                Require(status == NativeTerminalStatus.InvalidArgument && terminal == IntPtr.Zero,
                    message);
            }
            finally
            {
                if (terminal != IntPtr.Zero) NativeTerminalInterop.TerminalDestroy(terminal);
            }
        }

        private static byte[] C1OscTitle(string title)
        {
            var payload = Encoding.UTF8.GetBytes("0;" + title);
            var bytes = new byte[payload.Length + 2];
            bytes[0] = 0x9d;
            Buffer.BlockCopy(payload, 0, bytes, 1, payload.Length);
            bytes[bytes.Length - 1] = 0x9c;
            return bytes;
        }

        private static string CurrentTitle(NativeTerminal terminal)
        {
            using var checkpoint = terminal.CaptureCheckpoint();
            return NativeTerminalRenderFrameV1Decoder.Decode(
                checkpoint.ExportRenderFrameV1()).Title;
        }

        private static void ValidateRenderFrameExport()
        {
            using var terminal = NativeTerminalCoreV2.Create(8, 2);
            terminal.ApplyOutput(
                1,
                Bytes("\x1b[6 q\x1b[2;4:3;53;58:2::1:2:3mframe"));
            using var checkpoint = terminal.CaptureCheckpoint();
            var v1Alias = checkpoint.ExportRenderFrame();
            var v1 = checkpoint.ExportRenderFrameV1();
            Equal(v1, v1Alias, "The unsuffixed render frame export is not a V1 alias.");
            Require(v1.Length >= 96, "Render frame v1 was shorter than its header.");
            Equal(Bytes("TLRFRM1\0"), v1.Take(8).ToArray(),
                "Render frame v1 magic diverged.");
            Require(BitConverter.ToUInt32(v1, 20) == v1.Length,
                "Render frame v1 total length diverged.");
            Require(BitConverter.ToUInt64(v1, 24) == checkpoint.Info.Revision,
                "Render frame v1 revision diverged from its checkpoint.");
            Require(BitConverter.ToUInt64(v1, 32) == checkpoint.Info.NextSourceSequence,
                "Render frame v1 sequence diverged from its checkpoint.");

            var v2 = checkpoint.ExportRenderFrameV2();
            Require(v2.Length >= 96, "Render frame v2 was shorter than its header.");
            Equal(Bytes("TLRFRM2\0"), v2.Take(8).ToArray(),
                "Render frame v2 magic diverged.");
            Require(BitConverter.ToUInt16(v2, 8) == 2,
                "Render frame v2 major version diverged.");
            Require(BitConverter.ToUInt32(v2, 20) == v2.Length,
                "Render frame v2 total length diverged.");
            Require(BitConverter.ToUInt64(v2, 24) == checkpoint.Info.Revision,
                "Render frame v2 revision diverged from its checkpoint.");
            Require(BitConverter.ToUInt64(v2, 32) == checkpoint.Info.NextSourceSequence,
                "Render frame v2 sequence diverged from its checkpoint.");
            Require(v2[68] == (byte)NativeTerminalRenderCursorShapeV2.Bar && v2[69] == 0,
                "Render frame v2 cursor style diverged.");
            Require(BitConverter.ToUInt64(v2, 72) == 0x3f,
                "Render frame v2 required features diverged.");
            var decodedV2 = NativeTerminalRenderFrameV2Decoder.Decode(v2);
            Require(decodedV2.Cursor.Shape == NativeTerminalRenderCursorShapeV2.Bar &&
                !decodedV2.Cursor.Blinking,
                "The exported render frame v2 cursor style did not decode.");

            var v3 = checkpoint.ExportRenderFrameV3();
            Require(v3.Length >= 96, "Render frame v3 was shorter than its header.");
            Equal(Bytes("TLRFRM3\0"), v3.Take(8).ToArray(),
                "Render frame v3 magic diverged.");
            Require(BitConverter.ToUInt16(v3, 8) == 3 &&
                BitConverter.ToUInt32(v3, 20) == v3.Length &&
                BitConverter.ToUInt64(v3, 24) == checkpoint.Info.Revision &&
                BitConverter.ToUInt64(v3, 32) == checkpoint.Info.NextSourceSequence &&
                BitConverter.ToUInt64(v3, 72) == 0x7f,
                "Render frame v3 discovery or checkpoint metadata diverged.");
            var decodedV3 = NativeTerminalRenderFrameV3Decoder.Decode(v3);
            Require(decodedV3.Cursor.Shape == NativeTerminalRenderCursorShapeV2.Bar &&
                !decodedV3.Cursor.Blinking,
                "The exported render frame v3 cursor style did not decode.");
            var richV3 = decodedV3.Renditions.Single(rendition => rendition.Faint);
            Require(richV3.Overline &&
                richV3.UnderlineStyle == NativeTerminalRenderUnderlineStyleV3.Curly &&
                richV3.UnderlineColor.Kind == NativeTerminalRenderColorKindV1.Rgb &&
                richV3.UnderlineColor.Value == 0x010203,
                "The native V3 export lost extended rendition state.");

            terminal.ApplyOutput(2, Bytes("\x1b[3 q-mutated"));
            Equal(v1, checkpoint.ExportRenderFrameV1(),
                "A live terminal mutation changed an immutable render frame v1.");
            Equal(v2, checkpoint.ExportRenderFrameV2(),
                "A live terminal mutation changed an immutable render frame v2.");
            Equal(v3, checkpoint.ExportRenderFrameV3(),
                "A live terminal mutation changed an immutable render frame v3.");
            ThrowsNative(
                NativeTerminalStatus.LimitExceeded,
                () => checkpoint.ExportRenderFrameV1((ulong)v1.Length - 1UL),
                "A render frame v1 exceeded the requested export bound.");
            ThrowsNative(
                NativeTerminalStatus.LimitExceeded,
                () => checkpoint.ExportRenderFrameV2((ulong)v2.Length - 1UL),
                "A render frame v2 exceeded the requested export bound.");
            ThrowsNative(
                NativeTerminalStatus.LimitExceeded,
                () => checkpoint.ExportRenderFrameV3((ulong)v3.Length - 1UL),
                "A render frame v3 exceeded the requested export bound.");
            Throws<ArgumentOutOfRangeException>(
                () => checkpoint.ExportRenderFrame(0),
                "A zero render frame bound was accepted.");
            Throws<ArgumentOutOfRangeException>(
                () => checkpoint.ExportRenderFrameV2(0),
                "A zero render frame v2 bound was accepted.");
            Throws<ArgumentOutOfRangeException>(
                () => checkpoint.ExportRenderFrameV3(0),
                "A zero render frame v3 bound was accepted.");
        }

        private static void ValidateAtomicBatchApplication()
        {
            using var batched = NativeTerminalCoreV2.Create(4, 2);
            using var sequential = NativeTerminalCoreV2.Create(4, 2);
            var events = new[]
            {
                NativeTerminalBatchEvent.Output(1, Bytes("ab")),
                NativeTerminalBatchEvent.Resize(2, 5, 2),
                NativeTerminalBatchEvent.Advance(3, 17)
            };
            batched.ApplyBatch(events);
            sequential.ApplyOutput(1, Bytes("ab"));
            sequential.ApplyResize(2, 5, 2);
            sequential.ApplyAdvance(3, 17);

            using var batchedCheckpoint = batched.CaptureCheckpoint();
            using var sequentialCheckpoint = sequential.CaptureCheckpoint();
            Equal(
                sequentialCheckpoint.ExportSnapshot(),
                batchedCheckpoint.ExportSnapshot(),
                "Atomic batch application diverged from successful sequential application.");
        }

        private static void ValidateAtomicBatchFailurePolicies()
        {
            using (var retryable = NativeTerminalCoreV2.Create(4, 2))
            {
                byte[] before;
                using (var checkpoint = retryable.CaptureCheckpoint())
                    before = checkpoint.ExportSnapshot();
                ThrowsNative(
                    NativeTerminalStatus.InvalidDimensions,
                    () => retryable.ApplyBatch(
                        NativeTerminalBatchEvent.Output(1, Bytes("prefix")),
                        NativeTerminalBatchEvent.Resize(2, 0, 2)),
                    "A late retryable batch failure was not reported.");
                using (var checkpoint = retryable.CaptureCheckpoint())
                    Equal(before, checkpoint.ExportSnapshot(),
                        "A late retryable batch failure retained a committed prefix.");
                retryable.ApplyOutput(1, Bytes("retry"));
            }

            using (var prevalidated = NativeTerminalCoreV2.Create(4, 2))
            {
                Throws<ArgumentException>(
                    () => prevalidated.ApplyBatch(
                        NativeTerminalBatchEvent.Output(1, new byte[] { 0x01 }),
                        NativeTerminalBatchEvent.Advance(3, 0)),
                    "A late non-contiguous event was not rejected before native apply.");
                prevalidated.ApplyOutput(1, Bytes("still-live"));
            }

            using (var poisoned = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.Gap,
                    () => poisoned.ApplyBatch(
                        NativeTerminalBatchEvent.Output(1, Bytes("prefix")),
                        NativeTerminalBatchEvent.Gap(2)),
                    "A late GAP batch failure was not reported.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => poisoned.ApplyBatch(NativeTerminalBatchEvent.Advance(1, 0)),
                    "A poisoning batch failure did not latch failed-closed.");
            }

            var disposed = NativeTerminalCoreV2.Create(4, 2);
            disposed.Dispose();
            Throws<ObjectDisposedException>(
                () => disposed.ApplyBatch(NativeTerminalBatchEvent.Advance(1, 0)),
                "A disposed terminal accepted a batch.");
        }

        private static void ValidateAtomicBatchBounds()
        {
            using (var countBoundary = NativeTerminalCoreV2.Create(1, 1))
            {
                Throws<ArgumentOutOfRangeException>(
                    () => countBoundary.ApplyBatch(),
                    "An empty batch was accepted.");
                var excessive = Enumerable.Range(1, NativeTerminal.MaxBatchEventCount + 1)
                    .Select(sequence => NativeTerminalBatchEvent.Advance(
                        checked((ulong)sequence),
                        0))
                    .ToArray();
                Throws<ArgumentOutOfRangeException>(
                    () => countBoundary.ApplyBatch(excessive),
                    "A batch above the event-count bound was accepted.");
                var maximum = excessive.Take(NativeTerminal.MaxBatchEventCount).ToArray();
                countBoundary.ApplyBatch(maximum);
                using var checkpoint = countBoundary.CaptureCheckpoint();
                Require(checkpoint.Info.NextSourceSequence == 257,
                    "The maximum-size event batch did not commit every event.");
            }

            Throws<ArgumentOutOfRangeException>(
                () => NativeTerminalBatchEvent.Output(1, Array.Empty<byte>()),
                "An empty batch output payload was accepted.");
            Throws<ArgumentOutOfRangeException>(
                () => NativeTerminalBatchEvent.Output(
                    1,
                    new byte[NativeTerminalBatchEvent.MaxOutputBytes + 1]),
                "A batch output above 1 MiB was accepted.");

            var maximumOutput = Enumerable.Repeat(
                    (byte)'x',
                    NativeTerminalBatchEvent.MaxOutputBytes)
                .ToArray();
            using (var totalBoundary = NativeTerminalCoreV2.Create(1, 1))
            {
                totalBoundary.ApplyBatch(
                    NativeTerminalBatchEvent.Output(1, maximumOutput),
                    NativeTerminalBatchEvent.Output(2, maximumOutput),
                    NativeTerminalBatchEvent.Output(3, maximumOutput),
                    NativeTerminalBatchEvent.Output(4, maximumOutput));
                using var checkpoint = totalBoundary.CaptureCheckpoint();
                Require(checkpoint.Info.NextSourceSequence == 5,
                    "The exact 4 MiB batch-output boundary was rejected.");
            }

            using (var excessiveTotal = NativeTerminalCoreV2.Create(1, 1))
            {
                Throws<ArgumentOutOfRangeException>(
                    () => excessiveTotal.ApplyBatch(
                        NativeTerminalBatchEvent.Output(1, maximumOutput),
                        NativeTerminalBatchEvent.Output(2, maximumOutput),
                        NativeTerminalBatchEvent.Output(3, maximumOutput),
                        NativeTerminalBatchEvent.Output(4, maximumOutput),
                        NativeTerminalBatchEvent.Output(5, Bytes("x"))),
                    "A batch above the 4 MiB total-output bound was accepted.");
                excessiveTotal.ApplyOutput(1, Bytes("still-live"));
            }
        }

        private static void ValidateInputEncoding()
        {
            using var terminal = NativeTerminalCoreV2.Create(8, 2);
            Equal(Bytes("\x1b[A"), terminal.EncodeKey(NativeTerminalKey.ArrowUp),
                "Normal cursor input did not use CSI.");
            Equal(Bytes("\u4f60"), terminal.EncodeCharacter(new Rune('\u4f60')),
                "Unicode character input was not UTF-8.");
            Equal(new byte[] { 0x03 }, terminal.EncodeCharacter(
                    new Rune('c'), NativeTerminalModifiers.Control),
                "Control-character encoding diverged.");

            terminal.ApplyOutput(1, Bytes("\x1b[?1h\x1b[20h\x1b[?2004h\x1b="));
            Equal(Bytes("\x1bOA"), terminal.EncodeKey(NativeTerminalKey.ArrowUp),
                "Application cursor input did not use SS3.");
            Equal(Bytes("\r\n"), terminal.EncodeKey(NativeTerminalKey.Enter),
                "Newline mode did not affect Enter.");
            Equal(Bytes("\x1bOw"), terminal.EncodeKey(NativeTerminalKey.Keypad7),
                "Application keypad input did not use SS3.");
            Equal(
                Bytes("\x1b[200~paste\rbytes\x1b[201~"),
                terminal.EncodePaste(Bytes("paste\r\nbytes")),
                "Bracketed paste delimiters were not mode-sensitive.");

            ThrowsNative(
                NativeTerminalStatus.Unsupported,
                () => terminal.EncodeKey(
                    NativeTerminalKey.Enter,
                    NativeTerminalModifiers.Control),
                "Unsupported key modifiers were accepted.");
            Equal(Bytes("\x1bOA"), terminal.EncodeKey(NativeTerminalKey.ArrowUp),
                "A rejected input retired an otherwise healthy terminal.");
            ThrowsNative(
                NativeTerminalStatus.InvalidArgument,
                () => terminal.EncodePaste(Array.Empty<byte>()),
                "An empty paste event was accepted.");
        }

        private static void ValidateKittyInputEncoding()
        {
            ValidateKittyInputV3Layout();
            ValidateKittyLegacyAndNegotiatedVectors();
            ValidateInvalidKittyDtosAndNativeBounds();
        }

        private static void ValidateKittyInputV3Layout()
        {
            Require(Marshal.SizeOf<TlTerminalInputV3>() == 160,
                "The managed input v3 ABI is not 160 bytes.");
            RequireKittyInputOffset(nameof(TlTerminalInputV3.AbiVersion), 0);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.StructSize), 4);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Kind), 8);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Key), 12);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Modifiers), 16);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Character), 20);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Data), 24);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.DataLength), 32);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ReservedV1Compat0), 40);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ReservedV1Compat1), 48);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Win32VirtualKey), 56);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Win32ScanCode), 60);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Win32UnicodeCharacter), 64);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Win32KeyDown), 68);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Win32ControlKeyState), 72);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.Win32RepeatCount), 76);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ReservedV2Compat0), 80);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ReservedV2Compat1), 88);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.KeyEventType), 96);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.KittyModifiers), 100);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ShiftedKey), 104);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.BaseLayoutKey), 108);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.AssociatedText), 112);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.AssociatedTextLength), 120);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ReservedV30), 128);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ReservedV31), 136);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ReservedV32), 144);
            RequireKittyInputOffset(nameof(TlTerminalInputV3.ReservedV33), 152);

            var keyEvent = new NativeTerminalKittyKeyEvent(
                NativeTerminalKittyKeyEventType.Repeat,
                NativeTerminalKittyModifiers.Shift |
                    NativeTerminalKittyModifiers.Control |
                    NativeTerminalKittyModifiers.CapsLock,
                NativeTerminalKittyKeyIdentity.Character(new Rune('a')),
                new Rune('A'),
                new Rune('q'),
                "A");
            var text = Bytes("A");
            var textPin = GCHandle.Alloc(text, GCHandleType.Pinned);
            try
            {
                var textPointer = textPin.AddrOfPinnedObject();
                var input = NativeTerminalInterop.CreateKittyKeyInput(
                    keyEvent,
                    textPointer,
                    checked((ulong)text.LongLength));
                Require(input.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                    input.StructSize == NativeTerminalInterop.InputV3StructSize &&
                    input.Kind == 6 && input.Key == (uint)NativeTerminalKey.Character &&
                    input.Character == (uint)'a',
                    "The Kitty input v3 header or character identity was not canonical.");
                Require(input.Modifiers == 0 && input.Data == IntPtr.Zero &&
                    input.DataLength == 0 && input.ReservedV1Compat0 == 0 &&
                    input.ReservedV1Compat1 == 0 && input.Win32VirtualKey == 0 &&
                    input.Win32ScanCode == 0 && input.Win32UnicodeCharacter == 0 &&
                    input.Win32KeyDown == 0 && input.Win32ControlKeyState == 0 &&
                    input.Win32RepeatCount == 0 && input.ReservedV2Compat0 == 0 &&
                    input.ReservedV2Compat1 == 0,
                    "The Kitty input builder populated a V1/V2-only field.");
                Require(input.KeyEventType == 2 &&
                    input.KittyModifiers == (1U | 4U | 64U) &&
                    input.ShiftedKey == (uint)'A' &&
                    input.BaseLayoutKey == (uint)'q' &&
                    input.AssociatedText == textPointer &&
                    input.AssociatedTextLength == 1 &&
                    input.ReservedV30 == 0 && input.ReservedV31 == 0 &&
                    input.ReservedV32 == 0 && input.ReservedV33 == 0,
                    "The Kitty input v3 tail did not preserve its semantic fields exactly.");
            }
            finally
            {
                textPin.Free();
            }
        }

        private static void ValidateKittyLegacyAndNegotiatedVectors()
        {
            var character = NativeTerminalKittyKeyIdentity.Character(new Rune('a'));
            using var terminal = NativeTerminalCoreV2.Create(8, 2);
            Equal(Bytes("A"), terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    character,
                    associatedText: "A")),
                "A flags-zero Kitty press did not preserve legacy associated text.");
            Equal(Bytes("A"), terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Repeat,
                    NativeTerminalKittyModifiers.None,
                    character,
                    associatedText: "A")),
                "A flags-zero Kitty repeat did not preserve legacy associated text.");
            Equal(Array.Empty<byte>(), terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Release,
                    NativeTerminalKittyModifiers.None,
                    character)),
                "A flags-zero Kitty release emitted bytes.");
            Equal(Bytes("\x1bOR"), terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    NativeTerminalKittyKeyIdentity.Functional(NativeTerminalKey.F3))),
                "A flags-zero Kitty F3 did not preserve legacy SS3 encoding.");
            Equal(Bytes("你"), terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    NativeTerminalKittyKeyIdentity.UnidentifiedText,
                    associatedText: "你")),
                "A flags-zero unidentified-text event did not preserve UTF-8 text.");

            terminal.ApplyOutput(1, Bytes("\x1b[=31u"));
            ThrowsNative(
                NativeTerminalStatus.Unsupported,
                () => terminal.EncodeKey(NativeTerminalKey.F3),
                "Legacy semantic key input stayed ambiguous after Kitty negotiation.");

            Equal(Bytes("\x1b[97;1:1u"), terminal.EncodeKittyKey(
                    new NativeTerminalKittyKeyEvent(
                        NativeTerminalKittyKeyEventType.Press,
                        NativeTerminalKittyModifiers.None,
                        character)),
                "Negotiated Kitty press event encoding diverged.");
            Equal(Bytes("\x1b[97;1:2u"), terminal.EncodeKittyKey(
                    new NativeTerminalKittyKeyEvent(
                        NativeTerminalKittyKeyEventType.Repeat,
                        NativeTerminalKittyModifiers.None,
                        character)),
                "Negotiated Kitty repeat event encoding diverged.");
            Equal(Bytes("\x1b[97;1:3u"), terminal.EncodeKittyKey(
                    new NativeTerminalKittyKeyEvent(
                        NativeTerminalKittyKeyEventType.Release,
                        NativeTerminalKittyModifiers.None,
                        character)),
                "Negotiated Kitty release event encoding diverged.");
            Equal(Bytes("\x1b[97:65:113;2:1u"), terminal.EncodeKittyKey(
                    new NativeTerminalKittyKeyEvent(
                        NativeTerminalKittyKeyEventType.Press,
                        NativeTerminalKittyModifiers.Shift,
                        character,
                        new Rune('A'),
                        new Rune('q'))),
                "Negotiated Kitty shifted/base alternate encoding diverged.");
            Equal(Bytes("\x1b[97;1:1;97:20320u"), terminal.EncodeKittyKey(
                    new NativeTerminalKittyKeyEvent(
                        NativeTerminalKittyKeyEventType.Press,
                        NativeTerminalKittyModifiers.None,
                        character,
                        associatedText: "a你")),
                "Negotiated Kitty associated-text encoding diverged.");

            var allModifiers = NativeTerminalKittyModifiers.Shift |
                NativeTerminalKittyModifiers.Alt |
                NativeTerminalKittyModifiers.Control |
                NativeTerminalKittyModifiers.Super |
                NativeTerminalKittyModifiers.Hyper |
                NativeTerminalKittyModifiers.Meta |
                NativeTerminalKittyModifiers.CapsLock |
                NativeTerminalKittyModifiers.NumLock;
            Equal(Bytes("\x1b[120;256:1u"), terminal.EncodeKittyKey(
                    new NativeTerminalKittyKeyEvent(
                        NativeTerminalKittyKeyEventType.Press,
                        allModifiers,
                        NativeTerminalKittyKeyIdentity.Character(new Rune('x')))),
                "Negotiated Kitty input did not preserve all eight modifier bits.");
            Equal(Bytes("\x1b[57358;1:1u"), terminal.EncodeKittyKey(
                    new NativeTerminalKittyKeyEvent(
                        NativeTerminalKittyKeyEventType.Press,
                        NativeTerminalKittyModifiers.None,
                        NativeTerminalKittyKeyIdentity.Functional(57_358U))),
                "A Kitty PUA functional key did not preserve its protocol codepoint.");

            var f3 = terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                NativeTerminalKittyKeyEventType.Press,
                NativeTerminalKittyModifiers.None,
                NativeTerminalKittyKeyIdentity.Functional(NativeTerminalKey.F3)));
            Equal(Bytes("\x1b[13;1:1~"), f3,
                "Negotiated common F3 encoding diverged.");
            Require(!f3.SequenceEqual(Bytes("\x1b[1;1:1R")) &&
                (f3.Length == 0 || f3[f3.Length - 1] != (byte)'R'),
                "Negotiated F3 collided with a CSI row/column R reply shape.");
            Equal(Bytes("文"), terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    NativeTerminalKittyKeyIdentity.UnidentifiedText,
                    associatedText: "文")),
                "Negotiated unidentified text was not emitted exactly.");
        }

        private static void ValidateInvalidKittyDtosAndNativeBounds()
        {
            var character = NativeTerminalKittyKeyIdentity.Character(new Rune('a'));
            Throws<ArgumentException>(
                () => new NativeTerminalKittyKeyEvent(
                    (NativeTerminalKittyKeyEventType)0,
                    NativeTerminalKittyModifiers.None,
                    character),
                "An unknown Kitty event type was accepted.");
            Throws<ArgumentException>(
                () => new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    (NativeTerminalKittyModifiers)256,
                    character),
                "An unknown Kitty modifier bit was accepted.");
            Throws<ArgumentException>(
                () => new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    character,
                    new Rune('A')),
                "A shifted alternate without Shift was accepted.");
            Throws<ArgumentException>(
                () => new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.Shift,
                    NativeTerminalKittyKeyIdentity.Functional(NativeTerminalKey.F1),
                    new Rune('A')),
                "A functional key alternate was accepted.");
            Throws<ArgumentException>(
                () => new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Release,
                    NativeTerminalKittyModifiers.None,
                    character,
                    associatedText: "a"),
                "Associated text on a Kitty release was accepted.");
            Throws<ArgumentException>(
                () => new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Repeat,
                    NativeTerminalKittyModifiers.None,
                    NativeTerminalKittyKeyIdentity.UnidentifiedText,
                    associatedText: "a"),
                "A repeating unidentified-text event was accepted.");
            Throws<ArgumentException>(
                () => new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    NativeTerminalKittyKeyIdentity.UnidentifiedText),
                "An unidentified-text event without text was accepted.");
            Throws<ArgumentException>(
                () => NativeTerminalKittyKeyIdentity.Character(new Rune('\u001b')),
                "A control character Kitty identity was accepted.");
            Throws<ArgumentException>(
                () => new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    character,
                    associatedText: "a\u0085"),
                "C1 associated text was accepted.");
            Throws<ArgumentException>(
                () => NativeTerminalKittyKeyIdentity.Functional(NativeTerminalKey.Character),
                "The common functional-key factory accepted Character.");
            Throws<ArgumentException>(
                () => NativeTerminalKittyKeyIdentity.Functional(57_455U),
                "The Kitty PUA functional-key factory accepted an out-of-range value.");

            using var terminal = NativeTerminalCoreV2.Create(8, 2);
            var oversized = new NativeTerminalKittyKeyEvent(
                NativeTerminalKittyKeyEventType.Press,
                NativeTerminalKittyModifiers.None,
                NativeTerminalKittyKeyIdentity.UnidentifiedText,
                associatedText: new string('a', 4097));
            ThrowsNative(
                NativeTerminalStatus.LimitExceeded,
                () => terminal.EncodeKittyKey(oversized),
                "The native Input V3 associated-text bound was bypassed.");
            Equal(Bytes("ok"), terminal.EncodeKittyKey(new NativeTerminalKittyKeyEvent(
                    NativeTerminalKittyKeyEventType.Press,
                    NativeTerminalKittyModifiers.None,
                    NativeTerminalKittyKeyIdentity.UnidentifiedText,
                    associatedText: "ok")),
                "A native Kitty text-bound rejection retired the terminal.");
        }

        private static void ValidateWin32InputEncoding()
        {
            Require(Marshal.SizeOf<TlTerminalInputV2>() == 96,
                "The managed input v2 ABI is not 96 bytes.");
            RequireWin32InputOffset(nameof(TlTerminalInputV2.AbiVersion), 0);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.StructSize), 4);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Kind), 8);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Key), 12);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Modifiers), 16);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Character), 20);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Data), 24);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.DataLength), 32);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.ReservedV1Compat0), 40);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.ReservedV1Compat1), 48);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Win32VirtualKey), 56);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Win32ScanCode), 60);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Win32UnicodeCharacter), 64);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Win32KeyDown), 68);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Win32ControlKeyState), 72);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Win32RepeatCount), 76);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Reserved0), 80);
            RequireWin32InputOffset(nameof(TlTerminalInputV2.Reserved1), 88);

            var zero = new NativeTerminalWin32KeyEvent(0, 0, '\0', false, 0, 0);
            var maximum = new NativeTerminalWin32KeyEvent(
                ushort.MaxValue,
                ushort.MaxValue,
                char.MaxValue,
                true,
                uint.MaxValue,
                ushort.MaxValue);
            Require(maximum.VirtualKey == ushort.MaxValue &&
                maximum.ScanCode == ushort.MaxValue &&
                maximum.UnicodeCharacter == char.MaxValue &&
                maximum.KeyDown &&
                maximum.ControlKeyState == uint.MaxValue &&
                maximum.RepeatCount == ushort.MaxValue,
                "The public Win32 key DTO changed a maximum field value.");

            var canonical = NativeTerminalInterop.CreateWin32KeyInput(maximum);
            Require(canonical.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                canonical.StructSize == NativeTerminalInterop.InputV2StructSize &&
                canonical.Kind == 5,
                "The Win32 input v2 header was not canonical.");
            Require(canonical.Key == 0 &&
                canonical.Modifiers == 0 &&
                canonical.Character == 0 &&
                canonical.Data == IntPtr.Zero &&
                canonical.DataLength == 0 &&
                canonical.ReservedV1Compat0 == 0 &&
                canonical.ReservedV1Compat1 == 0,
                "The Win32 input v2 builder populated a V1 semantic field.");
            Require(canonical.Win32VirtualKey == ushort.MaxValue &&
                canonical.Win32ScanCode == ushort.MaxValue &&
                canonical.Win32UnicodeCharacter == char.MaxValue &&
                canonical.Win32KeyDown == 1 &&
                canonical.Win32ControlKeyState == uint.MaxValue &&
                canonical.Win32RepeatCount == ushort.MaxValue &&
                canonical.Reserved0 == 0 &&
                canonical.Reserved1 == 0,
                "The Win32 input v2 builder did not preserve its fields exactly.");

            using (var terminal = NativeTerminalCoreV2.Create(8, 2))
            {
                var ctrlF1 = new NativeTerminalWin32KeyEvent(112, 59, '\0', true, 8, 1);
                ThrowsNative(
                    NativeTerminalStatus.Unsupported,
                    () => terminal.EncodeWin32Key(ctrlF1),
                    "Disabled Win32 input mode accepted a Win32 key record.");

                Equal(Bytes("\x1b[1;5P"), terminal.EncodeKey(
                        NativeTerminalKey.F1,
                        NativeTerminalModifiers.Control),
                    "The V1 semantic Ctrl+F1 encoder changed before mode 9001.");
                Equal(Bytes("v1"), terminal.EncodePaste(Bytes("v1")),
                    "The V1 paste encoder changed before mode 9001.");

                terminal.ApplyOutput(1, Bytes("\x1b[?9001h"));
                var vectors = new[]
                {
                    new NativeTerminalWin32KeyEvent(17, 29, '\0', true, 8, 1),
                    ctrlF1,
                    new NativeTerminalWin32KeyEvent(112, 59, '\0', false, 8, 1),
                    new NativeTerminalWin32KeyEvent(17, 29, '\0', false, 0, 1)
                };
                var expected = new[]
                {
                    "\x1b[17;29;0;1;8;1_",
                    "\x1b[112;59;0;1;8;1_",
                    "\x1b[112;59;0;0;8;1_",
                    "\x1b[17;29;0;0;0;1_"
                };
                for (var index = 0; index < vectors.Length; index++)
                    Equal(Bytes(expected[index]), terminal.EncodeWin32Key(vectors[index]),
                        "A documented Ctrl+F1 Win32 record encoded incorrectly.");

                Equal(Bytes("\x1b[0;0;0;0;0;0_"), terminal.EncodeWin32Key(zero),
                    "A zero-valued Win32 key record was reinterpreted.");
                Equal(
                    Bytes("\x1b[65535;65535;65535;1;4294967295;65535_"),
                    terminal.EncodeWin32Key(maximum),
                    "A maximum-valued Win32 key record was truncated or reinterpreted.");

                Equal(Bytes("\x1b[1;5P"), terminal.EncodeKey(
                        NativeTerminalKey.F1,
                        NativeTerminalModifiers.Control),
                    "Mode 9001 changed the V1 semantic Ctrl+F1 encoder.");
                Equal(Bytes("v1"), terminal.EncodePaste(Bytes("v1")),
                    "Mode 9001 changed the V1 paste encoder.");

                using (var checkpoint = terminal.CaptureCheckpoint())
                using (var restored = NativeTerminalCoreV2.ImportSnapshot(
                    checkpoint.ExportSnapshot()))
                {
                    Equal(Bytes(expected[1]), restored.EncodeWin32Key(ctrlF1),
                        "Snapshot restore lost Win32 input mode.");
                    Equal(
                        Bytes("\x1b[65535;65535;65535;1;4294967295;65535_"),
                        restored.EncodeWin32Key(maximum),
                        "Snapshot restore changed Win32 key field preservation.");
                }

                terminal.ApplyOutput(2, Bytes("\x1b[?9001l"));
                ThrowsNative(
                    NativeTerminalStatus.Unsupported,
                    () => terminal.EncodeWin32Key(ctrlF1),
                    "Disabling mode 9001 did not restore Unsupported.");
                Equal(Bytes("\x1b[1;5P"), terminal.EncodeKey(
                        NativeTerminalKey.F1,
                        NativeTerminalModifiers.Control),
                    "A rejected Win32 record retired or changed the V1 encoder.");
            }

            using (var failedClosed = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.Gap,
                    () => failedClosed.ApplyGap(1),
                    "A GAP event was accepted while preparing Win32 failure validation.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => failedClosed.EncodeWin32Key(zero),
                    "A failed-closed terminal encoded Win32 input.");
            }

            var disposed = NativeTerminalCoreV2.Create(4, 2);
            disposed.Dispose();
            Throws<ObjectDisposedException>(
                () => disposed.EncodeWin32Key(zero),
                "A disposed terminal encoded Win32 input.");
        }

        private static void ValidateMouseInputEncoding()
        {
            Require(Marshal.SizeOf<TlTerminalMouseInputV1>() == 64,
                "The managed mouse input ABI is not 64 bytes.");
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.AbiVersion), 0);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.StructSize), 4);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.Action), 8);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.Button), 12);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.Wheel), 16);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.ButtonsHeld), 20);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.PositionFlags), 24);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.Modifiers), 28);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.CellColumn), 32);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.CellRow), 36);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.PixelX), 40);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.PixelY), 44);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.Reserved0), 48);
            RequireMouseOffset(nameof(TlTerminalMouseInputV1.Reserved1), 56);

            var cell = NativeTerminalMousePosition.Cell(3, 1);
            var pixel = NativeTerminalMousePosition.CellAndPixel(3, 1, 49, 29);
            Require(cell.CellColumn == 3 && cell.CellRow == 1 &&
                !cell.HasPixelPosition && cell.PixelX == 0 && cell.PixelY == 0,
                "The public cell-only mouse position was not canonical.");
            Require(pixel.CellColumn == 3 && pixel.CellRow == 1 &&
                pixel.HasPixelPosition && pixel.PixelX == 49 && pixel.PixelY == 29,
                "The public cell-and-pixel mouse position was not canonical.");

            var allModifiers = NativeTerminalModifiers.Shift |
                NativeTerminalModifiers.Alt |
                NativeTerminalModifiers.Control |
                NativeTerminalModifiers.Meta;
            var pressEvent = NativeTerminalMouseEvent.Press(
                NativeTerminalMouseButton.Left,
                NativeTerminalMouseButtons.Left |
                    NativeTerminalMouseButtons.Middle |
                    NativeTerminalMouseButtons.Right,
                cell,
                allModifiers);
            Require(pressEvent.Action == NativeTerminalMouseAction.Press &&
                pressEvent.Button == NativeTerminalMouseButton.Left &&
                pressEvent.WheelDirection == null &&
                pressEvent.Modifiers == allModifiers,
                "The public semantic mouse DTO lost press payload fields.");
            var canonicalPress = NativeTerminalInterop.CreateMouseInput(pressEvent);
            Require(canonicalPress.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                canonicalPress.StructSize == NativeTerminalInterop.MouseInputV1StructSize &&
                canonicalPress.Action == 1 &&
                canonicalPress.Button == 1 &&
                canonicalPress.Wheel == 0 &&
                canonicalPress.ButtonsHeld == 7 &&
                canonicalPress.PositionFlags == 0 &&
                canonicalPress.Modifiers == 15 &&
                canonicalPress.CellColumn == 3 &&
                canonicalPress.CellRow == 1 &&
                canonicalPress.PixelX == 0 &&
                canonicalPress.PixelY == 0 &&
                canonicalPress.Reserved0 == 0 &&
                canonicalPress.Reserved1 == 0,
                "The managed mouse DTO did not produce a canonical native struct.");

            using (var noTracking = NativeTerminalCoreV2.Create(8, 4))
            {
                Equal(Array.Empty<byte>(), noTracking.EncodeMouse(pressEvent),
                    "Disabled mouse tracking did not return empty success.");
                Equal(Array.Empty<byte>(), noTracking.EncodeMouseMove(
                        NativeTerminalMouseButtons.None,
                        NativeTerminalMousePosition.Cell(0, 0)),
                    "A second disabled-tracking result was not independently releasable.");
            }

            using (var sgr = NativeTerminalCoreV2.Create(512, 4))
            {
                sgr.ApplyOutput(1, Bytes("\x1b[?1003h\x1b[?1006h"));
                Equal(Bytes("\x1b[<28;4;2M"), sgr.EncodeMouse(pressEvent),
                    "SGR 1006 press/modifier encoding diverged.");
                Equal(Bytes("\x1b[<2;4;2m"), sgr.EncodeMouseRelease(
                        NativeTerminalMouseButton.Right,
                        NativeTerminalMouseButtons.Left,
                        cell),
                    "SGR 1006 release encoding diverged.");
                Equal(Bytes("\x1b[<33;4;2M"), sgr.EncodeMouseMove(
                        NativeTerminalMouseButtons.Middle,
                        cell),
                    "SGR 1006 held-button move encoding diverged.");
                Equal(Bytes("\x1b[<65;4;2M"), sgr.EncodeMouseWheel(
                        NativeTerminalMouseWheelDirection.Down,
                        NativeTerminalMouseButtons.Left |
                            NativeTerminalMouseButtons.Right,
                        cell),
                    "SGR 1006 wheel encoding diverged.");

                var malformed = canonicalPress;
                malformed.Reserved0 = 1;
                ThrowsNative(
                    NativeTerminalStatus.InvalidArgument,
                    () => sgr.EncodeMouseInput(in malformed),
                    "A malformed raw mouse ABI payload was accepted by the real DLL.");
                var shortInput = canonicalPress;
                shortInput.StructSize = 63;
                ThrowsNative(
                    NativeTerminalStatus.InvalidArgument,
                    () => sgr.EncodeMouseInput(in shortInput),
                    "A short mouse ABI payload was accepted by the real DLL.");
                Equal(Bytes("\x1b[<0;4;2M"), sgr.EncodeMousePress(
                        NativeTerminalMouseButton.Left,
                        NativeTerminalMouseButtons.Left,
                        cell),
                    "Retryable malformed mouse input retired the terminal.");
            }

            using (var pixels = NativeTerminalCoreV2.Create(512, 4))
            {
                pixels.ApplyOutput(1, Bytes("\x1b[?1000h\x1b[?1016h"));
                ThrowsNative(
                    NativeTerminalStatus.InvalidArgument,
                    () => pixels.EncodeMousePress(
                        NativeTerminalMouseButton.Left,
                        NativeTerminalMouseButtons.Left,
                        cell),
                    "Mode 1016 accepted a report without pixel coordinates.");
                Equal(Bytes("\x1b[<0;50;30M"), pixels.EncodeMousePress(
                        NativeTerminalMouseButton.Left,
                        NativeTerminalMouseButtons.Left,
                        pixel),
                    "SGR-pixel 1016 encoding diverged.");
                ThrowsNative(
                    NativeTerminalStatus.InvalidArgument,
                    () => pixels.EncodeMousePress(
                        NativeTerminalMouseButton.Left,
                        NativeTerminalMouseButtons.Left,
                        NativeTerminalMousePosition.CellAndPixel(512, 1, 49, 29)),
                    "An out-of-bounds cell coordinate was accepted.");
                ThrowsNative(
                    NativeTerminalStatus.LimitExceeded,
                    () => pixels.EncodeMousePress(
                        NativeTerminalMouseButton.Left,
                        NativeTerminalMouseButtons.Left,
                        NativeTerminalMousePosition.CellAndPixel(
                            3,
                            1,
                            uint.MaxValue,
                            29)),
                    "An unencodable pixel coordinate did not preserve the native limit status.");
                Equal(Bytes("\x1b[<0;50;30M"), pixels.EncodeMousePress(
                        NativeTerminalMouseButton.Left,
                        NativeTerminalMouseButtons.Left,
                        pixel),
                    "Retryable coordinate failure retired the terminal.");
            }

            Throws<ArgumentException>(
                () => NativeTerminalMouseEvent.Press(
                    NativeTerminalMouseButton.Left,
                    NativeTerminalMouseButtons.None,
                    cell),
                "A press whose held state excludes its button passed managed validation.");
            Throws<ArgumentException>(
                () => NativeTerminalMouseEvent.Release(
                    NativeTerminalMouseButton.Left,
                    NativeTerminalMouseButtons.Left,
                    cell),
                "A release whose held state includes its button passed managed validation.");
            Throws<ArgumentOutOfRangeException>(
                () => NativeTerminalMouseEvent.Move(
                    (NativeTerminalMouseButtons)8,
                    cell),
                "Unknown held-button flags passed managed validation.");
            Throws<ArgumentOutOfRangeException>(
                () => NativeTerminalMouseEvent.Wheel(
                    (NativeTerminalMouseWheelDirection)0,
                    NativeTerminalMouseButtons.None,
                    cell),
                "An unknown wheel direction passed managed validation.");
            Throws<ArgumentOutOfRangeException>(
                () => NativeTerminalMouseEvent.Press(
                    (NativeTerminalMouseButton)0,
                    NativeTerminalMouseButtons.Left,
                    cell),
                "An unknown mouse button passed managed validation.");
            Throws<ArgumentOutOfRangeException>(
                () => NativeTerminalMouseEvent.Move(
                    NativeTerminalMouseButtons.None,
                    cell,
                    (NativeTerminalModifiers)16),
                "Unknown mouse modifier flags passed managed validation.");
            using (var invalidDto = NativeTerminalCoreV2.Create(4, 2))
            {
                Throws<ArgumentException>(
                    () => invalidDto.EncodeMouse(default),
                    "A default DTO with an unknown action reached native mouse encoding.");
                invalidDto.ApplyOutput(1, Bytes("still-live"));
            }

            using (var failedClosed = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.Gap,
                    () => failedClosed.ApplyGap(1),
                    "A GAP event was accepted while preparing mouse failure validation.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => failedClosed.EncodeMouseMove(
                        NativeTerminalMouseButtons.None,
                        NativeTerminalMousePosition.Cell(0, 0)),
                    "Mouse input was accepted by a retired terminal.");
            }

            var disposed = NativeTerminalCoreV2.Create(4, 2);
            disposed.Dispose();
            Throws<ObjectDisposedException>(
                () => disposed.EncodeMouseMove(
                    NativeTerminalMouseButtons.None,
                    NativeTerminalMousePosition.Cell(0, 0)),
                "A disposed terminal encoded mouse input.");
        }

        private static void RequireMouseOffset(string fieldName, int expectedOffset)
        {
            Require(Marshal.OffsetOf<TlTerminalMouseInputV1>(fieldName).ToInt32() == expectedOffset,
                "Unexpected mouse input offset for " + fieldName + ".");
        }

        private static void RequireWin32InputOffset(string fieldName, int expectedOffset)
        {
            Require(Marshal.OffsetOf<TlTerminalInputV2>(fieldName).ToInt32() == expectedOffset,
                "Unexpected input v2 offset for " + fieldName + ".");
        }

        private static void RequireKittyInputOffset(string fieldName, int expectedOffset)
        {
            Require(Marshal.OffsetOf<TlTerminalInputV3>(fieldName).ToInt32() == expectedOffset,
                "Unexpected input v3 offset for " + fieldName + ".");
        }

        private static void ValidateSequenceAndGapFailures()
        {
            using (var stale = NativeTerminalCoreV2.Create(4, 2))
            {
                stale.ApplyOutput(1, Bytes("a"));
                ThrowsNative(
                    NativeTerminalStatus.SequenceMismatch,
                    () => stale.ApplyOutput(1, Bytes("duplicate")),
                    "A stale sequence was accepted.");
                stale.ApplyOutput(2, Bytes("b"));
            }

            using (var future = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.SequenceMismatch,
                    () => future.ApplyOutput(2, Bytes("jump")),
                    "A future sequence was accepted.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => future.ApplyOutput(1, Bytes("after-jump")),
                    "A terminal survived a forward sequence gap.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => future.TryCaptureCheckpoint(out _, out _),
                    "A permanently failed terminal looked temporarily ineligible.");
            }

            using (var gap = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.Gap,
                    () => gap.ApplyGap(1),
                    "A GAP event was accepted.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => gap.ApplyAdvance(1, 1),
                    "A terminal survived GAP.");
            }

            using (var unsupported = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.Unsupported,
                    () => unsupported.ApplyOutput(1, new byte[] { 0x01 }),
                    "Unsupported output was accepted.");
                ThrowsNative(
                    NativeTerminalStatus.FailedClosed,
                    () => unsupported.ApplyOutput(2, Bytes("after-unsupported")),
                    "A terminal survived unsupported output.");
            }
        }

        private static void ValidateFocusInputEncoding()
        {
            ValidateCanonicalFocusInput(
                NativeTerminalInterop.CreateFocusGainedInput(),
                3,
                "focus gained");
            ValidateCanonicalFocusInput(
                NativeTerminalInterop.CreateFocusLostInput(),
                4,
                "focus lost");

            using var terminal = NativeTerminalCoreV2.Create(8, 2);
            Equal(Array.Empty<byte>(), terminal.EncodeFocusGained(),
                "Disabled focus reporting produced focus-gained bytes.");
            Equal(Array.Empty<byte>(), terminal.EncodeFocusLost(),
                "Disabled focus reporting produced focus-lost bytes.");

            terminal.ApplyOutput(1, Bytes("\x1b[?1004h"));
            Equal(Bytes("\x1b[I"), terminal.EncodeFocusGained(),
                "Enabled focus reporting did not encode focus gained.");
            Equal(Bytes("\x1b[O"), terminal.EncodeFocusLost(),
                "Enabled focus reporting did not encode focus lost.");

            using var checkpoint = terminal.CaptureCheckpoint();
            using var restored = NativeTerminalCoreV2.ImportSnapshot(
                checkpoint.ExportSnapshot());
            Equal(Bytes("\x1b[I"), restored.EncodeFocusGained(),
                "Snapshot restore lost focus-reporting mode for focus gained.");
            Equal(Bytes("\x1b[O"), restored.EncodeFocusLost(),
                "Snapshot restore lost focus-reporting mode for focus lost.");

            using var failedClosed = NativeTerminalCoreV2.Create(4, 2);
            ThrowsNative(
                NativeTerminalStatus.Gap,
                () => failedClosed.ApplyGap(1),
                "A GAP event was accepted while preparing focus failure validation.");
            ThrowsNative(
                NativeTerminalStatus.FailedClosed,
                () => failedClosed.EncodeFocusGained(),
                "Focus gained was accepted by a failed-closed terminal.");
            ThrowsNative(
                NativeTerminalStatus.FailedClosed,
                () => failedClosed.EncodeFocusLost(),
                "Focus lost was accepted by a failed-closed terminal.");

            var disposed = NativeTerminalCoreV2.Create(4, 2);
            disposed.Dispose();
            Throws<ObjectDisposedException>(
                () => disposed.EncodeFocusGained(),
                "A disposed terminal encoded focus gained.");
            Throws<ObjectDisposedException>(
                () => disposed.EncodeFocusLost(),
                "A disposed terminal encoded focus lost.");
        }

        private static void ValidateCanonicalFocusInput(
            TlTerminalInputV1 input,
            uint expectedKind,
            string name)
        {
            Require(input.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                input.StructSize == NativeTerminalInterop.InputV1StructSize &&
                input.Kind == expectedKind,
                "The " + name + " input header was not canonical.");
            Require(input.Key == 0 &&
                input.Modifiers == 0 &&
                input.Character == 0 &&
                input.Data == IntPtr.Zero &&
                input.DataLength == 0 &&
                input.Reserved0 == 0 &&
                input.Reserved1 == 0,
                "The " + name + " input carried a non-canonical payload.");
        }

        private static void ValidateTemporaryCheckpointIneligibility()
        {
            using var terminal = NativeTerminalCoreV2.Create(8, 2);
            terminal.ApplyOutput(1, new byte[] { 0x1b });
            var captured = terminal.TryCaptureCheckpoint(out var pending, out var status);
            Require(!captured, "A pending ESC produced a checkpoint.");
            Require(pending == null, "A pending ESC returned a checkpoint handle.");
            Require(status == NativeTerminalStatus.SnapshotNotEligible,
                "A pending ESC did not preserve status 12.");
            ThrowsNative(
                NativeTerminalStatus.SnapshotNotEligible,
                () => terminal.CaptureCheckpoint(),
                "CaptureCheckpoint swallowed status 12.");

            terminal.ApplyOutput(2, Bytes("[31mR"));
            Require(terminal.TryCaptureCheckpoint(out var completed, out status),
                "A completed escape sequence remained ineligible.");
            using (completed)
            {
                Require(status == NativeTerminalStatus.Ok, "Successful capture did not report OK.");
                Require(completed.Info.NextSourceSequence == 3,
                    "Completed escape capture has the wrong sequence.");
            }
        }

        private static void ValidatePresentationEligibility()
        {
            Require(Marshal.SizeOf<TlTerminalPresentationInfoV1>() == 48,
                "The managed presentation info v1 ABI is not 48 bytes.");
            RequirePresentationInfoOffset(nameof(TlTerminalPresentationInfoV1.AbiVersion), 0);
            RequirePresentationInfoOffset(nameof(TlTerminalPresentationInfoV1.StructSize), 4);
            RequirePresentationInfoOffset(nameof(TlTerminalPresentationInfoV1.Flags), 8);
            RequirePresentationInfoOffset(nameof(TlTerminalPresentationInfoV1.Reserved0), 12);
            RequirePresentationInfoOffset(nameof(TlTerminalPresentationInfoV1.NextSourceSequence), 16);
            RequirePresentationInfoOffset(nameof(TlTerminalPresentationInfoV1.LogicalTimeMicros), 24);
            RequirePresentationInfoOffset(
                nameof(TlTerminalPresentationInfoV1.SynchronizedOutputDeadlineMicros),
                32);
            RequirePresentationInfoOffset(nameof(TlTerminalPresentationInfoV1.Reserved), 40);

            var canonical = NativeTerminalInterop.CreateEmptyPresentationInfoV1();
            Require(canonical.AbiVersion == NativeTerminalCoreV2.AbiVersion &&
                canonical.StructSize == NativeTerminalInterop.PresentationInfoV1StructSize &&
                canonical.Flags == 0 &&
                canonical.Reserved0 == 0 &&
                canonical.NextSourceSequence == 0 &&
                canonical.LogicalTimeMicros == 0 &&
                canonical.SynchronizedOutputDeadlineMicros == 0 &&
                canonical.Reserved == 0,
                "The managed presentation info factory was not canonical.");

            using (var synchronized = NativeTerminalCoreV2.Create(8, 2))
            {
                var initial = synchronized.GetPresentationInfo();
                Require(initial.Flags == NativeTerminalPresentationFlags.None &&
                    initial.NextSourceSequence == 1 &&
                    initial.LogicalTimeMicros == 0 &&
                    initial.SynchronizedOutputDeadlineMicros == 0,
                    "A new terminal reported non-canonical presentation metadata.");

                synchronized.ApplyOutput(1, Bytes("\x1b[?2026h"));
                var deferred = synchronized.GetPresentationInfo();
                Require(deferred.Flags == NativeTerminalPresentationFlags.SynchronizedOutput &&
                    deferred.NextSourceSequence == 2 &&
                    deferred.LogicalTimeMicros == 0 &&
                    deferred.SynchronizedOutputDeadlineMicros == 1_000_000,
                    "DEC synchronized output did not expose its presentation deadline.");
                Require(!synchronized.TryCaptureCheckpoint(out var pending, out var status) &&
                    pending == null &&
                    status == NativeTerminalStatus.PresentationDeferred,
                    "Synchronized output did not preserve presentation-deferred status 13.");

                synchronized.ApplyOutput(2, Bytes("partial"));
                Require(!synchronized.TryCaptureCheckpoint(out pending, out status) &&
                    pending == null &&
                    status == NativeTerminalStatus.PresentationDeferred,
                    "Additional synchronized output retired the wrapper or became presentable.");

                synchronized.ApplyOutput(3, Bytes("\x1b[?2026l"));
                var ready = synchronized.GetPresentationInfo();
                Require(ready.Flags == NativeTerminalPresentationFlags.None &&
                    ready.NextSourceSequence == 4 &&
                    ready.SynchronizedOutputDeadlineMicros == 0,
                    "Ending synchronized output did not restore presentation eligibility.");
                Require(synchronized.TryCaptureCheckpoint(out var completed, out status),
                    "Ending synchronized output did not restore checkpoint eligibility.");
                using (completed)
                {
                    Require(status == NativeTerminalStatus.Ok,
                        "Ready presentation capture did not report OK.");
                }
            }

            using (var parserIncomplete = NativeTerminalCoreV2.Create(8, 2))
            {
                parserIncomplete.ApplyOutput(1, new byte[] { 0x1b });
                var parserInfo = parserIncomplete.GetPresentationInfo();
                Require(parserInfo.Flags == NativeTerminalPresentationFlags.ParserIncomplete,
                    "An incomplete parser did not expose its presentation flag.");
                Require(!parserIncomplete.TryCaptureCheckpoint(out var pending, out var status) &&
                    pending == null &&
                    status == NativeTerminalStatus.SnapshotNotEligible,
                    "An incomplete parser did not preserve snapshot-not-eligible status 12.");
            }

            using (var failed = NativeTerminalCoreV2.Create(4, 2))
            {
                ThrowsNative(
                    NativeTerminalStatus.Gap,
                    () => failed.ApplyGap(1),
                    "A GAP event was accepted while preparing presentation failure validation.");
                var failedInfo = failed.GetPresentationInfo();
                Require(failedInfo.Flags == NativeTerminalPresentationFlags.FailedClosed,
                    "Presentation metadata was unavailable after managed fail-closed retirement.");
            }

            var disposed = NativeTerminalCoreV2.Create(4, 2);
            disposed.Dispose();
            Throws<ObjectDisposedException>(
                () => disposed.GetPresentationInfo(),
                "A disposed terminal returned presentation metadata.");

            Require(!NativeTerminalInterop.RequiresTerminalRetirement(
                    NativeTerminalStatus.PresentationDeferred),
                "Presentation-deferred status retired the terminal wrapper.");
        }

        private static void RequirePresentationInfoOffset(string fieldName, int expectedOffset)
        {
            Require(
                Marshal.OffsetOf<TlTerminalPresentationInfoV1>(fieldName).ToInt32() == expectedOffset,
                "Unexpected presentation info offset for " + fieldName + ".");
        }

        private static void ValidateCheckpointSuffixEquivalence()
        {
            using var original = NativeTerminalCoreV2.Create(12, 3);
            original.ApplyOutput(1, Bytes("prefix"));
            original.ApplyResize(2, 10, 4);
            original.ApplyAdvance(3, 500);
            using var prefix = original.CaptureCheckpoint();
            var prefixSnapshot = prefix.ExportSnapshot();

            original.ApplyOutput(4, Bytes("-suffix\r\nnext"));
            using var originalFinal = original.CaptureCheckpoint();
            var originalBytes = originalFinal.ExportSnapshot();

            using var restored = NativeTerminalCoreV2.ImportSnapshot(prefixSnapshot);
            Array.Fill(prefixSnapshot, (byte)0);
            restored.ApplyOutput(4, Bytes("-suffix\r\nnext"));
            using var restoredFinal = restored.CaptureCheckpoint();
            var restoredBytes = restoredFinal.ExportSnapshot();
            Equal(originalBytes, restoredBytes,
                "Checkpoint import plus suffix did not match uninterrupted execution.");
        }

        private static void ValidateWidePrimaryReflow()
        {
            var wideLine = Bytes("ab\u754ccdEF");
            using var reflowed = NativeTerminalCoreV2.Create(6, 3);
            reflowed.ApplyOutput(1, wideLine);
            reflowed.ApplyResize(2, 4, 4);
            using var reflowedCheckpoint = reflowed.CaptureCheckpoint();

            using var direct = NativeTerminalCoreV2.Create(4, 4);
            direct.ApplyOutput(1, wideLine);
            direct.ApplyAdvance(2, 0);
            using var directCheckpoint = direct.CaptureCheckpoint();
            Equal(
                directCheckpoint.ExportSnapshot(),
                reflowedCheckpoint.ExportSnapshot(),
                "Wide primary-screen reflow diverged from direct layout.");
        }

        private static void ValidateCheckpointImmutability()
        {
            using var terminal = NativeTerminalCoreV2.Create(8, 3);
            terminal.ApplyOutput(1, Bytes("frozen"));
            using var frozen = terminal.CaptureCheckpoint();
            var frozenInfo = frozen.Info;
            var before = frozen.ExportSnapshot();

            terminal.ApplyOutput(2, Bytes("-live-change"));
            terminal.ApplyResize(3, 10, 4);
            var after = frozen.ExportSnapshot();
            Equal(before, after, "A live terminal mutation changed an existing checkpoint.");
            Require(frozen.Info.Revision == frozenInfo.Revision &&
                frozen.Info.NextSourceSequence == frozenInfo.NextSourceSequence,
                "A live terminal mutation changed checkpoint metadata.");
        }

        private static void ValidateOwnedBufferFailuresAndUnknownStatusPolicy()
        {
            using var terminal = NativeTerminalCoreV2.Create(8, 2);
            terminal.ApplyOutput(1, Bytes("owned-buffer"));
            using var checkpoint = terminal.CaptureCheckpoint();
            var expected = checkpoint.ExportSnapshot();

            Throws<InvalidOperationException>(
                () => checkpoint.ExportSnapshot((_, _, _) =>
                    throw new InvalidOperationException("Injected managed copy failure.")),
                "A managed snapshot copy failure was swallowed.");
            Equal(expected, checkpoint.ExportSnapshot(),
                "A managed snapshot copy failure damaged the checkpoint.");
            ThrowsNative(
                NativeTerminalStatus.Unsupported,
                () => checkpoint.ExportDelta(),
                "Reserved delta export did not preserve UNSUPPORTED.");
            Equal(expected, checkpoint.ExportSnapshot(),
                "Unsupported delta export damaged the checkpoint.");

            var unknown = (NativeTerminalStatus)254;
            Require(NativeTerminalInterop.RequiresTerminalRetirement(unknown),
                "An unknown native status did not retire the terminal handle.");
            Require(!NativeTerminalInterop.RequiresTerminalRetirement(
                    NativeTerminalStatus.SequenceMismatch),
                "A retryable stale sequence retired the terminal handle.");
            var exception = new NativeTerminalException("validation", unknown);
            Require((uint)exception.Status == 254 && exception.Message.Contains("254", StringComparison.Ordinal),
                "An unknown native status was not preserved by the typed exception.");
        }

        private static void ValidateDisposeAndInvalidArguments()
        {
            ThrowsNative(
                NativeTerminalStatus.InvalidDimensions,
                () => NativeTerminalCoreV2.Create(0, 2).Dispose(),
                "Invalid dimensions were accepted.");
            Throws<ArgumentNullException>(
                () => NativeTerminalCoreV2.ImportSnapshot((byte[])null),
                "A null managed snapshot was accepted.");
            using (var output = NativeTerminalCoreV2.Create(4, 2))
            {
                Throws<ArgumentNullException>(
                    () => output.ApplyOutput(1, (byte[])null),
                    "Null managed output was accepted.");
                output.ApplyOutput(1, Bytes("x"));
            }
            ThrowsNative(
                NativeTerminalStatus.MalformedSnapshot,
                () => NativeTerminalCoreV2.ImportSnapshot(Bytes("not-a-snapshot")).Dispose(),
                "A truncated snapshot was accepted.");
            ThrowsNative(
                NativeTerminalStatus.MalformedSnapshot,
                () => NativeTerminalCoreV2.ImportSnapshot(Array.Empty<byte>()).Dispose(),
                "An empty snapshot was accepted.");
            ThrowsNative(
                NativeTerminalStatus.IncompatibleSnapshot,
                () => NativeTerminalCoreV2.ImportSnapshot(new byte[104]).Dispose(),
                "A full header with an incompatible magic value was accepted.");

            var terminal = NativeTerminalCoreV2.Create(4, 2);
            var checkpoint = terminal.CaptureCheckpoint();
            checkpoint.Dispose();
            checkpoint.Dispose();
            Throws<ObjectDisposedException>(
                () => checkpoint.ExportSnapshot(),
                "A disposed checkpoint exported a snapshot.");
            terminal.Dispose();
            terminal.Dispose();
            Throws<ObjectDisposedException>(
                () => terminal.ApplyOutput(1, Bytes("x")),
                "A disposed terminal accepted output.");
        }

        private static byte[] Bytes(string value)
        {
            return Encoding.UTF8.GetBytes(value);
        }

        private static void Equal(byte[] expected, byte[] actual, string message)
        {
            Require(expected.SequenceEqual(actual), message);
        }

        private static void ThrowsNative(
            NativeTerminalStatus expected,
            Action action,
            string message)
        {
            try
            {
                action();
            }
            catch (NativeTerminalException error) when (error.Status == expected)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Throws<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// Managed-only strict validation for Render Frame V3. The fixture deliberately assigns
    /// distinct V3-only attributes to rendition IDs which would collapse if decoded as V1.
    /// </summary>
    internal static class RenderFrameV3Validation
    {
        private const int HeaderSize = 96;
        private const int DirectoryOffset = 96;
        private const int DirectoryEntrySize = 24;
        private const int SectionCount = 6;
        private const int RenditionCount = 6;
        private const int RenditionRecordSize = 16;
        private const uint Crc32CPolynomial = 0x82f6_3b78;

        internal static void Run()
        {
            ValidateCanonicalFixtureAndImmutability();
            ValidateVersionIsolation();
            ValidateHeaderAndSectionVersions();
            ValidateRenditionStrictness();
            ValidateSharedPayloadStrictness();
        }

        private static void ValidateCanonicalFixtureAndImmutability()
        {
            var source = CreateCanonicalFrame();
            Require(source.SequenceEqual(CreateCanonicalFrame()),
                "The canonical V3 fixture was not byte-deterministic.");
            var decoded = NativeTerminalRenderFrameV3Decoder.Decode(
                (ReadOnlySpan<byte>)source);

            Require(NativeTerminalRenderFrameV3.FormatVersion == 3 &&
                NativeTerminalRenderFrameV3Decoder.MaximumFrameBytes == 16 * 1024 * 1024,
                "V3 public format constants diverged.");
            Require(decoded.PresentationRevision == 13 &&
                decoded.NextSourceSequence == 17 &&
                decoded.Columns == RenditionCount && decoded.Rows == 1 &&
                decoded.ActiveScreen == NativeTerminalRenderScreenV1.Alternate &&
                decoded.ReverseVideo,
                "V3 common frame metadata decoded incorrectly.");
            Require(decoded.Cursor.Row == 0 &&
                decoded.Cursor.Column == RenditionCount - 1 &&
                decoded.Cursor.Visible && decoded.Cursor.WrapPending &&
                decoded.Cursor.Shape == NativeTerminalRenderCursorShapeV2.Bar &&
                !decoded.Cursor.Blinking,
                "V3 cursor metadata decoded incorrectly.");
            Require(decoded.Graphemes.Count == 1 &&
                decoded.Graphemes[0].Text == "A" &&
                decoded.Graphemes[0].DisplayWidth == 1 &&
                decoded.Renditions.Count == RenditionCount &&
                decoded.RenderRows.Count == 1 &&
                decoded.RenderRows[0].Cells.Count == RenditionCount &&
                decoded.Title == "v3",
                "V3 payload sections decoded incorrectly.");

            for (var index = 0; index < RenditionCount; index++)
            {
                Require((int)decoded.Renditions[index].UnderlineStyle == index,
                    "V3 underline style " + index.ToString() + " was not preserved.");
                Require(decoded.Renditions[index].Underline == (index != 0),
                    "V3 underline-present semantics diverged.");
                Require(decoded.RenderRows[0].Cells[index].RenditionId == index,
                    "V3 rendition IDs collapsed during shared-section validation.");
            }
            Require(decoded.Renditions[1].Foreground.Kind ==
                    NativeTerminalRenderColorKindV1.Indexed &&
                decoded.Renditions[1].Foreground.Value == 7,
                "V3 foreground color decoded incorrectly.");
            Require(decoded.Renditions[2].Background.Kind ==
                    NativeTerminalRenderColorKindV1.Rgb &&
                decoded.Renditions[2].Background.Value == 0x112233,
                "V3 background color decoded incorrectly.");
            Require(decoded.Renditions[3].UnderlineColor.Kind ==
                    NativeTerminalRenderColorKindV1.Rgb &&
                decoded.Renditions[3].UnderlineColor.Value == 0x010203,
                "V3 underline color decoded incorrectly.");
            var allAttributes = decoded.Renditions[5];
            Require(allAttributes.Bold && allAttributes.Blink && allAttributes.Italic &&
                allAttributes.Inverse && allAttributes.Conceal &&
                allAttributes.Strikethrough && allAttributes.Faint &&
                allAttributes.Overline &&
                allAttributes.UnderlineStyle ==
                    NativeTerminalRenderUnderlineStyleV3.Dashed,
                "V3 boolean rendition attributes decoded incorrectly.");

            var renditionOffset = SectionOffset(source, 2);
            Array.Clear(source, renditionOffset, RenditionCount * RenditionRecordSize);
            source[SectionOffset(source, 6)] = (byte)'X';
            Require(decoded.Renditions[5].Faint && decoded.Title == "v3",
                "Mutating source bytes changed the immutable V3 DTO.");
            Throws<NotSupportedException>(
                () => ((IList<NativeTerminalRenderRenditionV3>)decoded.Renditions)[0] = default,
                "The V3 rendition collection was mutable.");
            Throws<NotSupportedException>(
                () => ((IList<NativeTerminalRenderCellV1>)
                    decoded.RenderRows[0].Cells)[0] = default,
                "A V3 reused row exposed mutable cell storage.");
        }

        private static void ValidateVersionIsolation()
        {
            var v3 = CreateCanonicalFrame();
            Throws<NativeTerminalRenderFrameV1DecodeException>(
                () => NativeTerminalRenderFrameV1Decoder.Decode(v3),
                "The V1 decoder accepted a V3 frame.");
            Throws<NativeTerminalRenderFrameV2DecodeException>(
                () => NativeTerminalRenderFrameV2Decoder.Decode(v3),
                "The V2 decoder accepted a V3 frame.");

            var v2 = RenderFrameV2Validation.CreateCanonicalFrame();
            Reject(v2);
            var v1 = (byte[])v2.Clone();
            Encoding.ASCII.GetBytes("TLRFRM1\0").CopyTo(v1, 0);
            PutUInt16(v1, 8, 1);
            Array.Clear(v1, 68, 4);
            PutUInt64(v1, 72, 0x1f);
            RefreshCrc(v1);
            _ = NativeTerminalRenderFrameV1Decoder.Decode(v1);
            Reject(v1);
        }

        private static void ValidateHeaderAndSectionVersions()
        {
            Throws<ArgumentNullException>(
                () => NativeTerminalRenderFrameV3Decoder.Decode((byte[])null),
                "A null V3 frame was accepted.");
            Reject(new byte[HeaderSize - 1]);
            Reject(new byte[NativeTerminalRenderFrameV3Decoder.MaximumFrameBytes + 1]);
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 6, (byte)'2'));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 8, 2));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 10, 1));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 12, HeaderSize + 8));
            Reject(PutUInt16AndRefresh(
                CreateCanonicalFrame(), 14, DirectoryEntrySize + 8));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 16, SectionCount - 1));
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 68, 3));
            Reject(MutateAndRefresh(CreateCanonicalFrame(), 69, 2));
            Reject(PutUInt16AndRefresh(CreateCanonicalFrame(), 70, 1));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 72, 0x3f));
            Reject(PutUInt64AndRefresh(CreateCanonicalFrame(), 72, 0xff));

            Reject(PutUInt16AndRefresh(
                CreateCanonicalFrame(),
                DirectoryOffset + 4,
                2));
            Reject(PutUInt16AndRefresh(
                CreateCanonicalFrame(),
                DirectoryOffset + DirectoryEntrySize + 4,
                1));
            Reject(PutUInt16AndRefresh(
                CreateCanonicalFrame(),
                DirectoryOffset + 2 * DirectoryEntrySize + 4,
                2));
            Reject(PutUInt16AndRefresh(
                CreateCanonicalFrame(),
                DirectoryOffset + DirectoryEntrySize + 6,
                0));

            var badHeaderCrc = CreateCanonicalFrame();
            badHeaderCrc[24] ^= 1;
            Reject(badHeaderCrc);
            var badPayloadCrc = CreateCanonicalFrame();
            badPayloadCrc[badPayloadCrc.Length - 1] ^= 1;
            Reject(badPayloadCrc);
        }

        private static void ValidateRenditionStrictness()
        {
            var template = CreateCanonicalFrame();
            var rendition = SectionOffset(template, 2);

            Reject(PutUInt32AndRefresh(template, rendition + 16, 1U << 9));
            Reject(PutUInt32AndRefresh(template, rendition + 16, 1U << 4));
            Reject(PutUInt32AndRefresh(
                template,
                rendition + 5 * RenditionRecordSize,
                (6U << 9) | (1U << 4)));
            Reject(PutUInt32AndRefresh(
                template,
                rendition + 5 * RenditionRecordSize,
                1U << 12));

            Reject(PutUInt32AndRefresh(template, rendition + 4, 1));
            Reject(PutUInt32AndRefresh(template, rendition + 16 + 4, 0x0100_0100));
            Reject(PutUInt32AndRefresh(template, rendition + 2 * 16 + 8, 0x0300_0000));
            Reject(PutUInt32AndRefresh(template, rendition + 3 * 16 + 12, 0x0100_0100));

            var duplicate = (byte[])template.Clone();
            duplicate.AsSpan(rendition, RenditionRecordSize).CopyTo(
                duplicate.AsSpan(rendition + RenditionRecordSize, RenditionRecordSize));
            RefreshCrc(duplicate);
            Reject(duplicate);

            var reversed = (byte[])template.Clone();
            var first = reversed.AsSpan(rendition, RenditionRecordSize).ToArray();
            reversed.AsSpan(rendition + RenditionRecordSize, RenditionRecordSize).CopyTo(
                reversed.AsSpan(rendition, RenditionRecordSize));
            first.CopyTo(reversed, rendition + RenditionRecordSize);
            RefreshCrc(reversed);
            Reject(reversed);
        }

        private static void ValidateSharedPayloadStrictness()
        {
            var template = CreateCanonicalFrame();
            Reject(PutUInt16AndRefresh(template, 18, 4));
            Reject(PutUInt32AndRefresh(template, 20, uint.MaxValue));
            Reject(PutUInt32AndRefresh(template, 60, 2));
            Reject(PutUInt32AndRefresh(template, 64, DirectoryOffset + 8));
            Reject(PutUInt64AndRefresh(template, 80, 1));
            Reject(PutUInt32AndRefresh(
                template,
                DirectoryOffset + DirectoryEntrySize + 8,
                248));

            var invalidUtf8 = (byte[])template.Clone();
            invalidUtf8[SectionOffset(invalidUtf8, 1) + 8] = 0xff;
            RefreshCrc(invalidUtf8);
            Reject(invalidUtf8);

            var nonzeroPadding = (byte[])template.Clone();
            var grapheme = SectionOffset(nonzeroPadding, 1);
            nonzeroPadding[grapheme + 9] = 1;
            RefreshCrc(nonzeroPadding);
            Reject(nonzeroPadding);

            var rows = SectionOffset(template, 5);
            var firstCell = rows + 8 + 8;
            Reject(PutUInt32AndRefresh(template, firstCell + 4, RenditionCount));
            Reject(PutUInt32AndRefresh(template, firstCell + 8, 1U << 13));
            Reject(PutUInt32AndRefresh(
                template,
                firstCell + (RenditionCount - 1) * 12 + 4,
                RenditionCount - 2));
        }

        private static byte[] CreateCanonicalFrame()
        {
            var grapheme = new byte[9];
            PutUInt32(grapheme, 0, 8);
            PutUInt16(grapheme, 4, 1);
            grapheme[6] = 1;
            grapheme[8] = (byte)'A';

            var rendition = new byte[RenditionCount * RenditionRecordSize];
            for (var style = 1; style < RenditionCount; style++)
            {
                var flags = (1U << 4) | ((uint)style << 9);
                if (style == 5) flags |= 0x1ef;
                PutUInt32(rendition, style * RenditionRecordSize, flags);
            }
            PutUInt32(rendition, RenditionRecordSize + 4, 0x0100_0007);
            PutUInt32(rendition, 2 * RenditionRecordSize + 8, 0x0211_2233);
            PutUInt32(rendition, 3 * RenditionRecordSize + 12, 0x0201_0203);

            var palette = new byte[259 * 4];
            for (var index = 0; index < 259; index++)
                PutUInt32(palette, index * 4, uint.MaxValue);

            var rows = new byte[8 + 8 + RenditionCount * 12];
            PutUInt32(rows, 0, 8);
            PutUInt32(rows, 4, 12);
            PutUInt32(rows, 8, RenditionCount);
            for (var index = 0; index < RenditionCount; index++)
            {
                PutUInt32(rows, 16 + index * 12, 1);
                PutUInt32(rows, 16 + index * 12 + 4, index);
            }

            var title = Encoding.UTF8.GetBytes("v3");
            return Assemble(new[]
            {
                new SectionBytes(1, 8, grapheme),
                new SectionBytes(RenditionCount, RenditionRecordSize, rendition),
                new SectionBytes(259, 4, palette),
                new SectionBytes(0, 16, Array.Empty<byte>()),
                new SectionBytes(RenditionCount, 0, rows),
                new SectionBytes(title.Length, 0, title)
            });
        }

        private static byte[] Assemble(SectionBytes[] sections)
        {
            Require(sections.Length == SectionCount, "The V3 fixture needs six sections.");
            var offsets = new int[SectionCount];
            var total = HeaderSize + SectionCount * DirectoryEntrySize;
            for (var index = 0; index < SectionCount; index++)
            {
                total = Align8(total);
                offsets[index] = total;
                total = Align8(checked(total + sections[index].Bytes.Length));
            }

            var frame = new byte[total];
            Encoding.ASCII.GetBytes("TLRFRM3\0").CopyTo(frame, 0);
            PutUInt16(frame, 8, 3);
            PutUInt16(frame, 10, 0);
            PutUInt16(frame, 12, HeaderSize);
            PutUInt16(frame, 14, DirectoryEntrySize);
            PutUInt16(frame, 16, SectionCount);
            PutUInt16(frame, 18, 3);
            PutUInt32(frame, 20, frame.Length);
            PutUInt64(frame, 24, 13);
            PutUInt64(frame, 32, 17);
            PutUInt32(frame, 40, RenditionCount);
            PutUInt32(frame, 44, 1);
            PutUInt32(frame, 48, 0);
            PutUInt32(frame, 52, RenditionCount - 1);
            PutUInt32(frame, 56, 3);
            PutUInt32(frame, 60, 1);
            PutUInt32(frame, 64, DirectoryOffset);
            frame[68] = (byte)NativeTerminalRenderCursorShapeV2.Bar;
            frame[69] = 0;
            PutUInt64(frame, 72, 0x7f);

            for (var index = 0; index < SectionCount; index++)
            {
                var entry = DirectoryOffset + index * DirectoryEntrySize;
                PutUInt32(frame, entry, index + 1);
                PutUInt16(frame, entry + 4, index == 1 ? 2 : 1);
                PutUInt16(frame, entry + 6, 1);
                PutUInt32(frame, entry + 8, offsets[index]);
                PutUInt32(frame, entry + 12, sections[index].Bytes.Length);
                PutUInt32(frame, entry + 16, sections[index].Count);
                PutUInt32(frame, entry + 20, sections[index].RecordSize);
                sections[index].Bytes.CopyTo(frame, offsets[index]);
            }
            RefreshCrc(frame);
            return frame;
        }

        private static int SectionOffset(byte[] frame, int tag)
        {
            return checked((int)ReadUInt32(
                frame,
                DirectoryOffset + (tag - 1) * DirectoryEntrySize + 8));
        }

        private static byte[] MutateAndRefresh(byte[] frame, int offset, byte value)
        {
            var result = (byte[])frame.Clone();
            result[offset] = value;
            RefreshCrc(result);
            return result;
        }

        private static byte[] PutUInt16AndRefresh(byte[] frame, int offset, int value)
        {
            var result = (byte[])frame.Clone();
            PutUInt16(result, offset, value);
            RefreshCrc(result);
            return result;
        }

        private static byte[] PutUInt32AndRefresh(byte[] frame, int offset, int value)
        {
            return PutUInt32AndRefresh(frame, offset, checked((uint)value));
        }

        private static byte[] PutUInt32AndRefresh(byte[] frame, int offset, uint value)
        {
            var result = (byte[])frame.Clone();
            PutUInt32(result, offset, value);
            RefreshCrc(result);
            return result;
        }

        private static byte[] PutUInt64AndRefresh(byte[] frame, int offset, ulong value)
        {
            var result = (byte[])frame.Clone();
            PutUInt64(result, offset, value);
            RefreshCrc(result);
            return result;
        }

        private static void RefreshCrc(byte[] frame)
        {
            PutUInt32(frame, 88, Crc32C(frame.AsSpan(HeaderSize)));
            PutUInt32(frame, 92, Crc32C(frame.AsSpan(0, 92)));
        }

        private static uint Crc32C(ReadOnlySpan<byte> bytes)
        {
            var crc = uint.MaxValue;
            for (var index = 0; index < bytes.Length; index++)
            {
                crc ^= bytes[index];
                for (var bit = 0; bit < 8; bit++)
                {
                    var mask = unchecked(0U - (crc & 1U));
                    crc = (crc >> 1) ^ (Crc32CPolynomial & mask);
                }
            }
            return ~crc;
        }

        private static uint ReadUInt32(byte[] bytes, int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        }

        private static void PutUInt16(byte[] bytes, int offset, int value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                bytes.AsSpan(offset, 2),
                checked((ushort)value));
        }

        private static void PutUInt32(byte[] bytes, int offset, int value)
        {
            PutUInt32(bytes, offset, checked((uint)value));
        }

        private static void PutUInt32(byte[] bytes, int offset, uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
        }

        private static void PutUInt64(byte[] bytes, int offset, ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset, 8), value);
        }

        private static int Align8(int value)
        {
            return checked(value + 7) & ~7;
        }

        private static void Reject(byte[] bytes)
        {
            Throws<NativeTerminalRenderFrameV3DecodeException>(
                () => NativeTerminalRenderFrameV3Decoder.Decode(bytes),
                "A malformed Render Frame V3 payload was accepted.");
        }

        private static void Throws<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private readonly struct SectionBytes
        {
            internal SectionBytes(int count, int recordSize, byte[] bytes)
            {
                Count = count;
                RecordSize = recordSize;
                Bytes = bytes;
            }

            internal int Count { get; }
            internal int RecordSize { get; }
            internal byte[] Bytes { get; }
        }
    }
}
