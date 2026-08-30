package dev.termlink.terminal.nativecore

/** The deliberately small JVM-to-C++ surface. No native address is exposed as a Kotlin handle. */
internal object JniNativeMethods {
    init {
        System.loadLibrary("termlink_terminal_jni")
    }

    external fun abiVersion(): Int
    external fun renderFrameV1FormatVersion(): Int
    external fun renderFrameV1MaxBytes(): Long
    external fun renderFrameV3FormatVersion(): Int
    external fun renderFrameV3MaxBytes(): Long
    external fun hostEffectFrameV2FormatVersion(): Int
    external fun hostEffectFrameV2MaxBytes(): Long
    external fun mouseInputV1StructSize(): Int
    external fun inputV1StructSize(): Int
    external fun inputV2StructSize(): Int
    external fun inputV3StructSize(): Int
    external fun configV3StructSize(): Int

    external fun create(columns: Int, rows: Int, initialSequence: Long, outAllocation: LongArray): Int
    external fun createV3(config: LongArray, outAllocation: LongArray): Int
    external fun importCheckpoint(checkpoint: ByteArray, outAllocation: LongArray): Int
    external fun destroy(token: Long): Int
    external fun applyBatch(token: Long, events: LongArray, payloads: Array<ByteArray?>): Int
    external fun applyBatchWithHostEffectsV2(
        token: Long,
        events: LongArray,
        payloads: Array<ByteArray?>,
        outFrame: Array<ByteArray?>,
    ): Int
    external fun render(token: Long, maxBytes: Long, outFrame: Array<ByteArray?>): Int
    external fun renderV3(token: Long, maxBytes: Long, outFrame: Array<ByteArray?>): Int
    external fun renderV3AtScrollback(
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
    external fun encodeMouse(token: Long, input: LongArray, outPayload: Array<ByteArray?>): Int
    external fun encodeInputV2(token: Long, input: LongArray, outPayload: Array<ByteArray?>): Int
    external fun encodeInputV3(
        token: Long,
        input: LongArray,
        associatedText: ByteArray,
        outPayload: Array<ByteArray?>,
    ): Int
    external fun exportCheckpoint(
        token: Long,
        maxBytes: Long,
        outInfo: LongArray,
        outPayload: Array<ByteArray?>,
    ): Int
}
