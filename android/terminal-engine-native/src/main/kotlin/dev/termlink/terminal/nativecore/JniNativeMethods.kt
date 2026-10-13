package dev.termlink.terminal.nativecore

/** The deliberately small JVM-to-C++ surface. No native address is exposed as a Kotlin handle. */
internal object JniNativeMethods {
    init {
        System.loadLibrary("termlink_terminal_jni")
    }

    external fun abiVersion(): Int
    external fun failureDiagnosticV1(token: Long, outDiagnostic: Array<ByteArray?>): Int
    external fun failureDiagnosticV2(token: Long, outDiagnostic: Array<ByteArray?>): Int
    external fun failureDiagnosticV3(token: Long, outDiagnostic: Array<ByteArray?>): Int
    external fun renderFrameV1FormatVersion(): Int
    external fun renderFrameV1MaxBytes(): Long
    external fun renderFrameV3FormatVersion(): Int
    external fun renderFrameV4FormatVersion(): Int
    external fun renderFrameV3MaxBytes(): Long
    external fun renderFrameV4MaxBytes(): Long
    external fun hostEffectFrameV2FormatVersion(): Int
    external fun hostEffectFrameV2MaxBytes(): Long
    external fun mouseInputV1StructSize(): Int
    external fun inputV1StructSize(): Int
    external fun inputV2StructSize(): Int
    external fun inputV3StructSize(): Int
    external fun configV3StructSize(): Int
    external fun configV4StructSize(): Int

    external fun createGraphicsV1(config: LongArray, outAllocation: LongArray): Int
    external fun importSnapshotGraphicsV1(checkpoint: ByteArray, outAllocation: LongArray): Int
    external fun maxGraphicsSnapshotBytes(): Long
    external fun deltaExportV1(base: ByteArray, target: ByteArray, contextId: ByteArray, profile: Int, maxBytes: Long, outDelta: Array<ByteArray?>): Int
    external fun deltaApplyV1(current: ByteArray, contextId: ByteArray, profile: Int, delta: ByteArray, maxBytes: Long, outInfo: LongArray, outSnapshot: Array<ByteArray?>, outSha256: ByteArray): Int
    external fun renderFrameV5FormatVersion(): Int
    external fun renderFrameV5MaxBytes(): Long
    external fun renderV5(token: Long, flags: Int, outFrame: Array<ByteArray?>): Int
    external fun renderV5AtScrollback(token: Long, offsetRows: Long, flags: Int, outInfo: LongArray, outFrame: Array<ByteArray?>): Int
    external fun historyPageV2(request: LongArray, outRequired: LongArray, outPage: Array<ByteArray?>): Int
    external fun historyViewportV2(capture: Long, owner: Long, revision: Long, offset: Long, outInfo: LongArray, outFrame: Array<ByteArray?>): Int
    external fun exportCheckpointGraphicsV1(token: Long, maxBytes: Long, outInfo: LongArray, outPayload: Array<ByteArray?>): Int

    external fun create(columns: Int, rows: Int, initialSequence: Long, outAllocation: LongArray): Int
    external fun createV3(config: LongArray, outAllocation: LongArray): Int
    external fun createV4(config: LongArray, outAllocation: LongArray): Int
    external fun importCheckpoint(checkpoint: ByteArray, outAllocation: LongArray): Int
    external fun destroy(token: Long): Int
    external fun applyBatch(token: Long, events: LongArray, payloads: Array<ByteArray?>): Int
    external fun applyBrokerUnsupportedV1(
        token: Long,
        sourceSequence: Long,
        rawOutput: ByteArray,
        outEffects: Array<ByteArray?>,
        outDiagnostic: Array<ByteArray?>,
    ): Int
    external fun applyBrokerCapturedV1(
        token: Long,
        sourceSequence: Long,
        rawOutput: ByteArray,
        identities: Array<ByteArray>,
        outcomes: IntArray,
        captures: Array<ByteArray>,
        outEffects: Array<ByteArray?>,
        outDiagnostic: Array<ByteArray?>,
    ): Int
    external fun applyBatchWithHostEffectsV2(
        token: Long,
        events: LongArray,
        payloads: Array<ByteArray?>,
        outFrame: Array<ByteArray?>,
    ): Int
    external fun render(token: Long, maxBytes: Long, outFrame: Array<ByteArray?>): Int
    external fun historyBegin(token: Long, outInfo: LongArray): Int
    external fun historyPage(request: LongArray, outRequired: LongArray, outPage: Array<ByteArray?>): Int
    external fun historyRelease(capture: Long, owner: Long): Int
    external fun historyResolveAnchor(capture: Long, owner: Long, revision: Long, line: Long, cell: Long, outOffset: LongArray): Int
    external fun historyViewport(capture: Long, owner: Long, revision: Long, offset: Long, outInfo: LongArray, outFrame: Array<ByteArray?>): Int
    external fun renderV3(token: Long, maxBytes: Long, outFrame: Array<ByteArray?>): Int
    external fun hostSignalsV1(token: Long, outState: Array<ByteArray?>): Int
    external fun shellStateV1(token: Long, maxBytes: Long, outState: Array<ByteArray?>): Int
    external fun renderV4(token: Long, maxBytes: Long, outFrame: Array<ByteArray?>): Int
    external fun renderV3AtScrollback(
        token: Long,
        offsetRows: Long,
        maxBytes: Long,
        outInfo: LongArray,
        outFrame: Array<ByteArray?>,
    ): Int
    external fun renderV4AtScrollback(
        token: Long,
        offsetRows: Long,
        maxBytes: Long,
        outInfo: LongArray,
        outFrame: Array<ByteArray?>,
    ): Int
    external fun encodeInputV1(
        token: Long,
        input: LongArray,
        data: ByteArray,
        outPayload: Array<ByteArray?>,
    ): Int
    external fun encodeInputV1WithDiagnostic(
        token: Long,
        input: LongArray,
        data: ByteArray,
        outPayload: Array<ByteArray?>,
        outDiagnostic: Array<ByteArray?>,
    ): Int
    external fun encodeMouse(token: Long, input: LongArray, outPayload: Array<ByteArray?>): Int
    external fun encodeMouseWithDiagnostic(
        token: Long,
        input: LongArray,
        outPayload: Array<ByteArray?>,
        outDiagnostic: Array<ByteArray?>,
    ): Int
    external fun encodeInputV2(token: Long, input: LongArray, outPayload: Array<ByteArray?>): Int
    external fun encodeInputV2WithDiagnostic(
        token: Long,
        input: LongArray,
        outPayload: Array<ByteArray?>,
        outDiagnostic: Array<ByteArray?>,
    ): Int
    external fun encodeInputV3(
        token: Long,
        input: LongArray,
        associatedText: ByteArray,
        outPayload: Array<ByteArray?>,
    ): Int
    external fun encodeInputV3WithDiagnostic(
        token: Long,
        input: LongArray,
        associatedText: ByteArray,
        outPayload: Array<ByteArray?>,
        outDiagnostic: Array<ByteArray?>,
    ): Int
    external fun exportCheckpoint(
        token: Long,
        maxBytes: Long,
        outInfo: LongArray,
        outPayload: Array<ByteArray?>,
    ): Int
}
