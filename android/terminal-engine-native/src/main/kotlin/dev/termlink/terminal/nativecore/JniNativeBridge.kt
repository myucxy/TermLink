package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalSize

/** Production bridge backed by the single packaged `libtermlink_terminal_jni.so`. */
object JniNativeBridge : NativeBridge {
    override val abiVersion: Int get() = JniNativeMethods.abiVersion()
    override val renderFrameFormatVersion: Int get() = JniNativeMethods.renderFrameV1FormatVersion()
    override val maxRenderFrameBytes: Int
        get() = JniNativeMethods.renderFrameV1MaxBytes().checkedInt("render frame maximum")
    override val renderFrameV3FormatVersion: Int
        get() = JniNativeMethods.renderFrameV3FormatVersion()
    override val maxRenderFrameV3Bytes: Int
        get() = JniNativeMethods.renderFrameV3MaxBytes().checkedInt("render frame v3 maximum")
    override val hostEffectFrameV2FormatVersion: Int
        get() = JniNativeMethods.hostEffectFrameV2FormatVersion()
    override val maxHostEffectFrameV2Bytes: Int
        get() = JniNativeMethods.hostEffectFrameV2MaxBytes().checkedInt("host-effect frame maximum")
    override val mouseInputV1StructSize: Int get() = JniNativeMethods.mouseInputV1StructSize()
    override val inputV1StructSize: Int get() = JniNativeMethods.inputV1StructSize()
    override val inputV2StructSize: Int get() = JniNativeMethods.inputV2StructSize()
    override val inputV3StructSize: Int get() = JniNativeMethods.inputV3StructSize()
    override val configV3StructSize: Int get() = JniNativeMethods.configV3StructSize()

    override fun create(
        size: TerminalSize,
        initialNextSourceSequence: Long,
    ): NativeBridgeResult<NativeBridgeTerminal> = allocationResult {
        JniNativeMethods.create(size.columns, size.rows, initialNextSourceSequence, it)
    }

    override fun createV3(config: NativeTerminalConfigV3): NativeBridgeResult<NativeBridgeTerminal> =
        allocationResult { output -> JniNativeMethods.createV3(config.toWords(), output) }

    override fun importCheckpoint(checkpoint: ByteArray): NativeBridgeResult<NativeBridgeTerminal> =
        allocationResult { output -> JniNativeMethods.importCheckpoint(checkpoint, output) }

    override fun destroy(handle: NativeBridgeHandle): NativeBridgeResult<Unit> =
        statusResult(JniNativeMethods.destroy(handle.value)) { Unit }

    override fun applyBatch(
        handle: NativeBridgeHandle,
        batch: NativeEventBatch,
    ): NativeBridgeResult<NativeBridgeApplyReceipt> {
        val marshalled = batch.marshal()
        return statusResult(
            JniNativeMethods.applyBatch(handle.value, marshalled.words, marshalled.payloads),
        ) {
            NativeBridgeApplyReceipt(batch.nextSourceSequence, batch.nextSourceSequence)
        }
    }

    override fun applyBatchWithHostEffectsV2(
        handle: NativeBridgeHandle,
        batch: NativeEventBatch,
    ): NativeBridgeResult<NativeBridgeApplyWithHostEffectsV2> {
        val marshalled = batch.marshal()
        val output = arrayOfNulls<ByteArray>(1)
        return statusResult(
            JniNativeMethods.applyBatchWithHostEffectsV2(
                handle.value,
                marshalled.words,
                marshalled.payloads,
                output,
            ),
        ) {
            NativeBridgeApplyWithHostEffectsV2(
                NativeBridgeApplyReceipt(batch.nextSourceSequence, batch.nextSourceSequence),
                output.requirePayload("applyBatchWithHostEffectsV2"),
            )
        }
    }

    override fun render(
        handle: NativeBridgeHandle,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeRenderFrame> = payloadResult("render") { output ->
        JniNativeMethods.render(handle.value, request.maxPayloadBytes.toLong(), output)
    }.mapPayload(::NativeBridgeRenderFrame)

    override fun renderV3(
        handle: NativeBridgeHandle,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeRenderFrame> = payloadResult("renderV3") { output ->
        JniNativeMethods.renderV3(handle.value, request.maxPayloadBytes.toLong(), output)
    }.mapPayload(::NativeBridgeRenderFrame)

    override fun encodeInputV1(
        handle: NativeBridgeHandle,
        input: NativeTerminalInputV1,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = payloadResult("encodeInputV1") { output ->
        JniNativeMethods.encodeInputV1(
            handle.value,
            input.toWords(),
            input.copyData(),
            output,
        )
    }.mapPayload(::NativeBridgeEncodedInput)

    override fun encodeMouse(
        handle: NativeBridgeHandle,
        input: NativeMouseInputV1,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = payloadResult("encodeMouse") { output ->
        JniNativeMethods.encodeMouse(handle.value, input.toWords(), output)
    }.mapPayload(::NativeBridgeEncodedInput)

    override fun encodeInputV2(
        handle: NativeBridgeHandle,
        input: NativeWin32KeyInputV2,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = payloadResult("encodeInputV2") { output ->
        JniNativeMethods.encodeInputV2(handle.value, input.toWords(), output)
    }.mapPayload(::NativeBridgeEncodedInput)

    override fun encodeInputV3(
        handle: NativeBridgeHandle,
        input: NativeKittyKeyInputV3,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = payloadResult("encodeInputV3") { output ->
        JniNativeMethods.encodeInputV3(
            handle.value,
            input.toWords(),
            input.copyAssociatedText(),
            output,
        )
    }.mapPayload(::NativeBridgeEncodedInput)

    override fun exportCheckpoint(
        handle: NativeBridgeHandle,
        maxPayloadBytes: Int,
    ): NativeBridgeResult<NativeBridgeCheckpoint> {
        val info = LongArray(2)
        val output = arrayOfNulls<ByteArray>(1)
        return statusResult(
            JniNativeMethods.exportCheckpoint(
                handle.value,
                maxPayloadBytes.toLong(),
                info,
                output,
            ),
        ) {
            NativeBridgeCheckpoint(
                nextSourceSequence = info[1],
                presentationRevision = info[0],
                payload = output.requirePayload("exportCheckpoint"),
            )
        }
    }

    private fun allocationResult(call: (LongArray) -> Int): NativeBridgeResult<NativeBridgeTerminal> {
        val output = LongArray(3)
        return statusResult(call(output)) {
            NativeBridgeTerminal(NativeBridgeHandle(output[0]), output[1], output[2])
        }
    }

    private fun payloadResult(
        operation: String,
        call: (Array<ByteArray?>) -> Int,
    ): NativeBridgeResult<ByteArray> {
        val output = arrayOfNulls<ByteArray>(1)
        return statusResult(call(output)) { output.requirePayload(operation) }
    }
}

private data class MarshalledBatch(val words: LongArray, val payloads: Array<ByteArray?>)

private fun NativeEventBatch.marshal(): MarshalledBatch {
    val words = LongArray(events.size * EVENT_WORDS)
    val payloads = arrayOfNulls<ByteArray>(events.size)
    events.forEachIndexed { index, event ->
        val offset = index * EVENT_WORDS
        words[offset] = event.sourceSequence
        when (event) {
            is NativeSourceEvent.Output -> {
                words[offset + 1] = 1
                payloads[index] = event.copyBytes()
            }
            is NativeSourceEvent.Resize -> {
                words[offset + 1] = 2
                words[offset + 2] = event.size.columns.toLong()
                words[offset + 3] = event.size.rows.toLong()
            }
            is NativeSourceEvent.Advance -> {
                words[offset + 1] = 3
                words[offset + 4] = event.micros
            }
            is NativeSourceEvent.Gap -> words[offset + 1] = 4
        }
    }
    return MarshalledBatch(words, payloads)
}

private fun NativeTerminalConfigV3.toWords(): LongArray = longArrayOf(
    abiVersion.toLong(), structSize.toLong(), columns.toLong(), rows.toLong(),
    initialSourceSequence, reservedV1Compat0, reservedV1Compat1, historyCapacityRows,
    reservedV2Compat0, reservedV2Compat1, outputProfile.toLong(), reserved0.toLong(),
    reserved1, reserved2,
)

private fun NativeMouseInputV1.toWords(): LongArray = longArrayOf(
    abiVersion.toLong(), structSize.toLong(), action.toLong(), button.toLong(), wheel.toLong(),
    buttonsHeld.toLong(), positionFlags.toLong(), modifiers.toLong(), cellColumn.toLong(),
    cellRow.toLong(), pixelX.toLong(), pixelY.toLong(), reserved0, reserved1,
)

private fun NativeTerminalInputV1.toWords(): LongArray = longArrayOf(
    abiVersion.toLong(), structSize.toLong(), kind.toLong(), key.toLong(), modifiers.toLong(),
    character.toLong(), dataLength, reserved0, reserved1,
)

private fun NativeWin32KeyInputV2.toWords(): LongArray = longArrayOf(
    abiVersion.toLong(), structSize.toLong(), kind.toLong(), key.toLong(), modifiers.toLong(),
    character.toLong(), data, dataLength, reservedV1Compat0, reservedV1Compat1,
    win32VirtualKey.toLong(), win32ScanCode.toLong(), win32UnicodeChar.toLong(),
    win32KeyDown.toLong(), win32ControlKeyState, win32RepeatCount.toLong(), reserved0, reserved1,
)

private fun NativeKittyKeyInputV3.toWords(): LongArray = longArrayOf(
    abiVersion.toLong(), structSize.toLong(), kind.toLong(), key, modifiers.toLong(),
    character.toLong(), data, dataLength, reservedV1Compat0, reservedV1Compat1,
    win32VirtualKey.toLong(), win32ScanCode.toLong(), win32UnicodeChar.toLong(),
    win32KeyDown.toLong(), win32ControlKeyState, win32RepeatCount.toLong(),
    reservedV2Prefix0, reservedV2Prefix1, kittyEventType.toLong(), kittyModifiers.toLong(),
    kittyShiftedKey.toLong(), kittyBaseLayoutKey.toLong(), associatedTextLength,
    reservedV3Tail0, reservedV3Tail1, reservedV3Tail2, reservedV3Tail3,
)

private inline fun <T : Any> statusResult(status: Int, value: () -> T): NativeBridgeResult<T> =
    if (status == NativeTerminalStatus.OK.code) {
        NativeBridgeResult.Success(value())
    } else {
        NativeBridgeResult.Failure(status)
    }

private fun Array<ByteArray?>.requirePayload(operation: String): ByteArray =
    this[0] ?: throw IllegalStateException("JNI operation '$operation' returned no payload")

private inline fun <T : Any> NativeBridgeResult<ByteArray>.mapPayload(
    transform: (ByteArray) -> T,
): NativeBridgeResult<T> = when (this) {
    is NativeBridgeResult.Success -> NativeBridgeResult.Success(transform(value))
    is NativeBridgeResult.Failure -> this
}

private fun Long.checkedInt(field: String): Int {
    check(this in 0..Int.MAX_VALUE.toLong()) { "$field is outside the Kotlin byte-array range" }
    return toInt()
}

private const val EVENT_WORDS = 5
