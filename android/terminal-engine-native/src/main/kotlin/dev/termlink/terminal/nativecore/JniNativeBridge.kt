package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalSize

/** Production bridge backed by the single packaged `libtermlink_terminal_jni.so`. */
object JniNativeBridge : NativeBridge {
    override val renderFrameV5FormatVersion: Int
        get() = optionalGraphicsDiscovery { JniNativeMethods.renderFrameV5FormatVersion() }
    override val maxRenderFrameV5Bytes: Int
        get() = optionalGraphicsDiscovery { JniNativeMethods.renderFrameV5MaxBytes().checkedInt("render frame v5 maximum") }
    override val maxGraphicsSnapshotBytes: Int
        get() = optionalGraphicsDiscovery { JniNativeMethods.maxGraphicsSnapshotBytes().checkedInt("graphics snapshot maximum") }

    override fun deltaExportV1(base: ByteArray, target: ByteArray, contextId: ByteArray, profile: Int, maxBytes: Int): NativeBridgeResult<ByteArray> =
        optionalGraphicsCall {
            payloadResult("deltaExportV1") {
                JniNativeMethods.deltaExportV1(base, target, contextId, profile, maxBytes.toLong(), it)
            }.mapPayload { payload ->
                require(payload.isNotEmpty() && payload.size <= maxBytes) { "Delta exceeds requested bound" }
                payload
            }
        }

    override fun deltaApplyV1(current: ByteArray, contextId: ByteArray, profile: Int, delta: ByteArray, maxBytes: Int): NativeBridgeResult<NativeBridgeDeltaApply> =
        optionalGraphicsCall {
            val info = LongArray(3)
            val output = arrayOfNulls<ByteArray>(1)
            val sha256 = ByteArray(32)
            statusResult(JniNativeMethods.deltaApplyV1(current, contextId, profile, delta, maxBytes.toLong(), info, output, sha256)) {
                val snapshot = output.requirePayload("deltaApplyV1")
                require(snapshot.size <= TERMLINK_GRAPHICS_SNAPSHOT_MAX_BYTES) { "Delta snapshot exceeds graphics bound" }
                if (info[0] !in 1L..2L) throw NativeTerminalContractException("deltaApplyV1", "unknown native outcome")
                NativeBridgeDeltaApply(info[0].toInt(), info[1], info[2], snapshot, sha256)
            }
        }

    override fun createGraphicsV1(config: NativeTerminalConfigV4): NativeBridgeResult<NativeBridgeTerminal> =
        optionalGraphicsCall { allocationResult { JniNativeMethods.createGraphicsV1(config.toWords(), it) } }

    override fun importSnapshotGraphicsV1(checkpoint: ByteArray): NativeBridgeResult<NativeBridgeTerminal> =
        optionalGraphicsCall { allocationResult { JniNativeMethods.importSnapshotGraphicsV1(checkpoint, it) } }

    override fun renderV5(handle: NativeBridgeHandle, request: NativeRenderRequest): NativeBridgeResult<NativeBridgeRenderFrame> =
        optionalGraphicsCall {
            payloadResult("renderV5") { JniNativeMethods.renderV5(handle.value, 0, it) }
                .mapPayload { NativeBridgeRenderFrame(it.boundedGraphicsPayload(request.maxPayloadBytes)) }
        }

    override fun renderV5AtScrollback(handle: NativeBridgeHandle, offsetRows: Long, request: NativeRenderRequest): NativeBridgeResult<NativeBridgeScrollbackFrame> =
        optionalGraphicsCall {
            val info = LongArray(3)
            val output = arrayOfNulls<ByteArray>(1)
            statusResult(JniNativeMethods.renderV5AtScrollback(handle.value, offsetRows, 0, info, output)) {
                NativeBridgeScrollbackFrame(output.requirePayload("renderV5AtScrollback").boundedGraphicsPayload(request.maxPayloadBytes), info[0], info[1], info[2])
            }
        }

    override fun historyViewportV2(info: NativeHistoryCaptureInfo, offsetRows: Long): NativeBridgeResult<NativeBridgeScrollbackFrame> =
        optionalGraphicsCall {
            val metadata = LongArray(3)
            val output = arrayOfNulls<ByteArray>(1)
            statusResult(JniNativeMethods.historyViewportV2(info.captureId, info.ownerId, info.revision, offsetRows, metadata, output)) {
                NativeBridgeScrollbackFrame(output.requirePayload("historyViewportV2").boundedGraphicsPayload(TERMLINK_RENDER_FRAME_V5_MAX_BYTES), metadata[0], metadata[1], metadata[2])
            }
        }

    override fun historyPageV2(info: NativeHistoryCaptureInfo, beforeLineId: Long, maxLines: Int, maxCells: Int, maxUtf8Bytes: Int): NativeBridgeResult<NativeHistoryRead> =
        optionalGraphicsCall {
            val required = LongArray(2)
            val output = arrayOfNulls<ByteArray>(1)
            val status = JniNativeMethods.historyPageV2(longArrayOf(info.captureId, info.ownerId, info.revision, beforeLineId,
                maxLines.toLong(), maxCells.toLong(), maxUtf8Bytes.toLong()), required, output)
            if (status == NativeTerminalStatus.HISTORY_PAGE_BUDGET.code) NativeBridgeResult.Success(NativeHistoryRead.Budget(required[0], required[1]))
            else statusResult(status) { NativeHistoryRead.Page(output.requirePayload("historyPageV2").boundedGraphicsPayload(NativeHistoryPageDecoder.MAX_V2_BYTES)) }
        }

    override fun exportCheckpointGraphicsV1(handle: NativeBridgeHandle, maxPayloadBytes: Int): NativeBridgeResult<NativeBridgeCheckpoint> =
        optionalGraphicsCall {
            val info = LongArray(2)
            val output = arrayOfNulls<ByteArray>(1)
            statusResult(JniNativeMethods.exportCheckpointGraphicsV1(handle.value, maxPayloadBytes.toLong(), info, output)) {
                val payload = output.requirePayload("exportCheckpointGraphicsV1")
                require(payload.size <= minOf(maxPayloadBytes, TERMLINK_GRAPHICS_SNAPSHOT_MAX_BYTES)) { "Graphics snapshot exceeds requested bound" }
                NativeBridgeCheckpoint(info[1], info[0], payload)
            }
        }

    override val supportsHostSignals = true
    override val supportsHistoryCapture = true
    override fun historyResolveAnchor(info: NativeHistoryCaptureInfo, line: Long, cell: Long): NativeBridgeResult<Long> {
        val offset = LongArray(1)
        return statusResult(JniNativeMethods.historyResolveAnchor(info.captureId, info.ownerId, info.revision, line, cell, offset)) { offset[0] }
    }
    override fun historyViewport(info: NativeHistoryCaptureInfo, offsetRows: Long): NativeBridgeResult<NativeBridgeScrollbackFrame> {
        val metadata = LongArray(3)
        val output = arrayOfNulls<ByteArray>(1)
        return statusResult(JniNativeMethods.historyViewport(info.captureId, info.ownerId, info.revision, offsetRows, metadata, output)) {
            NativeBridgeScrollbackFrame(output.requirePayload("historyViewport"), metadata[0], metadata[1], metadata[2])
        }
    }
    override fun historyBegin(handle: NativeBridgeHandle): NativeBridgeResult<NativeHistoryCaptureInfo> {
        val info = LongArray(5)
        return statusResult(JniNativeMethods.historyBegin(handle.value, info)) {
            NativeHistoryCaptureInfo(info[0], info[1], info[2], info[3], info[4])
        }
    }
    override fun historyPage(info: NativeHistoryCaptureInfo, beforeLineId: Long, maxLines: Int, maxCells: Int, maxUtf8Bytes: Int): NativeBridgeResult<NativeHistoryRead> {
        val required = LongArray(2)
        val output = arrayOfNulls<ByteArray>(1)
        val status = JniNativeMethods.historyPage(longArrayOf(info.captureId, info.ownerId, info.revision, beforeLineId,
            maxLines.toLong(), maxCells.toLong(), maxUtf8Bytes.toLong()), required, output)
        if (status == NativeTerminalStatus.HISTORY_PAGE_BUDGET.code) {
            return NativeBridgeResult.Success(NativeHistoryRead.Budget(required[0], required[1]))
        }
        return statusResult(status) { NativeHistoryRead.Page(output.requirePayload("historyPage")) }
    }
    override fun historyRelease(info: NativeHistoryCaptureInfo): NativeBridgeResult<Unit> =
        statusResult(JniNativeMethods.historyRelease(info.captureId, info.ownerId)) { Unit }
    override val abiVersion: Int get() = JniNativeMethods.abiVersion()
    override val renderFrameFormatVersion: Int get() = JniNativeMethods.renderFrameV1FormatVersion()
    override val maxRenderFrameBytes: Int
        get() = JniNativeMethods.renderFrameV1MaxBytes().checkedInt("render frame maximum")
    override val renderFrameV3FormatVersion: Int
        get() = JniNativeMethods.renderFrameV3FormatVersion()
    override val maxRenderFrameV3Bytes: Int
        get() = JniNativeMethods.renderFrameV3MaxBytes().checkedInt("render frame v3 maximum")
    override val renderFrameV4FormatVersion: Int
        get() = JniNativeMethods.renderFrameV4FormatVersion()
    override val maxRenderFrameV4Bytes: Int
        get() = JniNativeMethods.renderFrameV4MaxBytes().checkedInt("render frame v4 maximum")
    override val hostEffectFrameV2FormatVersion: Int
        get() = JniNativeMethods.hostEffectFrameV2FormatVersion()
    override val maxHostEffectFrameV2Bytes: Int
        get() = JniNativeMethods.hostEffectFrameV2MaxBytes().checkedInt("host-effect frame maximum")
    override val mouseInputV1StructSize: Int get() = JniNativeMethods.mouseInputV1StructSize()
    override val inputV1StructSize: Int get() = JniNativeMethods.inputV1StructSize()
    override val inputV2StructSize: Int get() = JniNativeMethods.inputV2StructSize()
    override val inputV3StructSize: Int get() = JniNativeMethods.inputV3StructSize()
    override val configV3StructSize: Int get() = JniNativeMethods.configV3StructSize()
    override val configV4StructSize: Int get() = JniNativeMethods.configV4StructSize()

    override fun create(
        size: TerminalSize,
        initialNextSourceSequence: Long,
    ): NativeBridgeResult<NativeBridgeTerminal> = allocationResult {
        JniNativeMethods.create(size.columns, size.rows, initialNextSourceSequence, it)
    }

    override fun createV3(config: NativeTerminalConfigV3): NativeBridgeResult<NativeBridgeTerminal> =
        allocationResult { output -> JniNativeMethods.createV3(config.toWords(), output) }

    override fun createV4(config: NativeTerminalConfigV4): NativeBridgeResult<NativeBridgeTerminal> =
        allocationResult { output -> JniNativeMethods.createV4(config.toWords(), output) }

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
        }.withFailureDiagnostic(handle)
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
        }.withFailureDiagnostic(handle)
    }

    override fun applyBrokerCapturedV1(
        handle: NativeBridgeHandle,
        output: NativeBrokerOutputV1,
    ): NativeBridgeResult<NativeBridgeApplyWithHostEffectsV2> = optionalGraphicsCall {
        val rawOutput = output.copyOutput()
        val identities = ArrayList<ByteArray>(output.captures.size)
        val captures = ArrayList<ByteArray>(output.captures.size)
        val effects = arrayOfNulls<ByteArray>(1)
        val diagnostic = arrayOfNulls<ByteArray>(1)
        try {
            output.captures.forEach { capture ->
                identities += capture.copyIdentity()
                captures += capture.copyBytes()
            }
            val outcomes = IntArray(output.captures.size) { output.captures[it].outcome.code }
            val status = JniNativeMethods.applyBrokerCapturedV1(handle.value, output.sourceSequence,
                rawOutput, identities.toTypedArray(), outcomes, captures.toTypedArray(), effects, diagnostic)
            if (status != NativeTerminalStatus.OK.code && effects[0] != null) {
                throw NativeTerminalContractException("applyBrokerCapturedV1", "failed broker apply published effects")
            }
            statusResult(status) {
                require(diagnostic[0] == null) { "successful broker apply published a diagnostic" }
                NativeBridgeApplyWithHostEffectsV2(
                    NativeBridgeApplyReceipt(output.sourceSequence + 1, output.sourceSequence + 1),
                    effects.requirePayload("applyBrokerCapturedV1"),
                )
            }
        } finally {
            rawOutput.fill(0)
            identities.forEach { it.fill(0) }
            captures.forEach { it.fill(0) }
            effects[0]?.fill(0)
            diagnostic[0]?.fill(0)
        }
    }

    private fun <T : Any> NativeBridgeResult<T>.withFailureDiagnostic(
        handle: NativeBridgeHandle,
    ): NativeBridgeResult<T> {
        if (this !is NativeBridgeResult.Failure) return this
        if (statusCode != NativeTerminalStatus.UNSUPPORTED.code &&
            statusCode != NativeTerminalStatus.LIMIT_EXCEEDED.code &&
            statusCode != NativeTerminalStatus.FAILED_CLOSED.code
        ) return this
        val text = try {
            val output = arrayOfNulls<ByteArray>(1)
            val result = try { JniNativeMethods.failureDiagnosticV3(handle.value, output) }
                catch (_: UnsatisfiedLinkError) {
                    try { JniNativeMethods.failureDiagnosticV2(handle.value, output) }
                    catch (_: UnsatisfiedLinkError) { JniNativeMethods.failureDiagnosticV1(handle.value, output) }
                }
            if (result != NativeTerminalStatus.OK.code) null
            else output[0]?.takeIf { it.size <= 8192 }?.decodeToString(throwOnInvalidSequence = true)
        } catch (_: Exception) {
            null // Diagnostic retrieval must not replace the original failure.
        } catch (_: UnsatisfiedLinkError) {
            null // Additive API: an older paired library has no diagnostic export.
        }
        return copy(diagnostic = dev.termlink.terminal.api.TerminalProtocolDiagnostic.parse(text)?.text)
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

    override fun renderV3AtScrollback(
        handle: NativeBridgeHandle,
        offsetRows: Long,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeScrollbackFrame> {
        val info = LongArray(3)
        val output = arrayOfNulls<ByteArray>(1)
        return statusResult(
            JniNativeMethods.renderV3AtScrollback(
                handle.value,
                offsetRows,
                request.maxPayloadBytes.toLong(),
                info,
                output,
            ),
        ) {
            NativeBridgeScrollbackFrame(
                payload = output.requirePayload("renderV3AtScrollback"),
                availableRows = info[0],
                generationRows = info[1],
                appliedOffsetRows = info[2],
            )
        }
    }

    override fun hostSignalsV1(handle: NativeBridgeHandle): NativeBridgeResult<NativeBridgeRenderFrame> =
        payloadResult("hostSignalsV1") { output -> JniNativeMethods.hostSignalsV1(handle.value, output) }.mapPayload(::NativeBridgeRenderFrame)
    override fun shellStateV1(handle: NativeBridgeHandle): NativeBridgeResult<NativeBridgeRenderFrame> =
        payloadResult("shellStateV1") { output ->
            JniNativeMethods.shellStateV1(handle.value, dev.termlink.terminal.api.TerminalShellState.MAX_BYTES.toLong(), output)
        }.mapPayload(::NativeBridgeRenderFrame)

    override fun renderV4(
        handle: NativeBridgeHandle,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeRenderFrame> = payloadResult("renderV4") { output ->
        JniNativeMethods.renderV4(handle.value, request.maxPayloadBytes.toLong(), output)
    }.mapPayload(::NativeBridgeRenderFrame)

    override fun renderV4AtScrollback(
        handle: NativeBridgeHandle,
        offsetRows: Long,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeScrollbackFrame> {
        val info = LongArray(3)
        val output = arrayOfNulls<ByteArray>(1)
        return statusResult(
            JniNativeMethods.renderV4AtScrollback(
                handle.value,
                offsetRows,
                request.maxPayloadBytes.toLong(),
                info,
                output,
            ),
        ) {
            NativeBridgeScrollbackFrame(
                payload = output.requirePayload("renderV4AtScrollback"),
                availableRows = info[0],
                generationRows = info[1],
                appliedOffsetRows = info[2],
            )
        }
    }

    override fun encodeInputV1(
        handle: NativeBridgeHandle,
        input: NativeTerminalInputV1,
    ): NativeBridgeResult<NativeBridgeEncodedInput> {
        val data = input.copyData()
        return try {
            inputDiagnosticResult("encodeInputV1") { output, diagnostic ->
                JniNativeMethods.encodeInputV1WithDiagnostic(
                    handle.value, input.toWords(), data, output, diagnostic)
            }.mapPayload(::NativeBridgeEncodedInput)
        } finally { data.fill(0) }
    }

    override fun encodeMouse(
        handle: NativeBridgeHandle,
        input: NativeMouseInputV1,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = inputDiagnosticResult("encodeMouse") { output, diagnostic ->
        JniNativeMethods.encodeMouseWithDiagnostic(handle.value, input.toWords(), output, diagnostic)
    }.mapPayload(::NativeBridgeEncodedInput)

    override fun encodeInputV2(
        handle: NativeBridgeHandle,
        input: NativeWin32KeyInputV2,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = inputDiagnosticResult("encodeInputV2") { output, diagnostic ->
        JniNativeMethods.encodeInputV2WithDiagnostic(handle.value, input.toWords(), output, diagnostic)
    }.mapPayload(::NativeBridgeEncodedInput)

    override fun encodeInputV3(
        handle: NativeBridgeHandle,
        input: NativeKittyKeyInputV3,
    ): NativeBridgeResult<NativeBridgeEncodedInput> {
        val associatedText = input.copyAssociatedText()
        return try {
            inputDiagnosticResult("encodeInputV3") { output, diagnostic ->
                JniNativeMethods.encodeInputV3WithDiagnostic(
                    handle.value, input.toWords(), associatedText, output, diagnostic)
            }.mapPayload(::NativeBridgeEncodedInput)
        } finally { associatedText.fill(0) }
    }

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

    private fun inputDiagnosticResult(
        operation: String,
        call: (Array<ByteArray?>, Array<ByteArray?>) -> Int,
    ): NativeBridgeResult<ByteArray> {
        val output = arrayOfNulls<ByteArray>(1)
        val diagnostic = arrayOfNulls<ByteArray>(1)
        try {
            val status = call(output, diagnostic)
            if (status == NativeTerminalStatus.OK.code) {
                if (diagnostic[0] != null) {
                    output[0]?.fill(0)
                    throw NativeTerminalContractException(operation, "successful input encode published a diagnostic")
                }
                return NativeBridgeResult.Success(output.requirePayload(operation))
            }
            if (output[0] != null) {
                output[0]?.fill(0)
                throw NativeTerminalContractException(operation, "failed input encode published a payload")
            }
            val text = diagnostic[0]?.takeIf { it.size <= 2048 }?.let { bytes ->
                runCatching { bytes.decodeToString(throwOnInvalidSequence = true) }.getOrNull()
            }
            val parsed = dev.termlink.terminal.api.TerminalInputDiagnostic.parse(text)
            val expectedStatus = when (status) {
                NativeTerminalStatus.UNSUPPORTED.code -> "unsupported"
                NativeTerminalStatus.INVALID_ARGUMENT.code -> "invalid_argument"
                NativeTerminalStatus.LIMIT_EXCEEDED.code -> "limit_exceeded"
                NativeTerminalStatus.FAILED_CLOSED.code -> "failed_closed"
                else -> null
            }
            return NativeBridgeResult.Failure(status,
                parsed?.text?.takeIf { expectedStatus != null && parsed.status == expectedStatus })
        } finally {
            diagnostic[0]?.fill(0)
        }
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
            is NativeSourceEvent.HostTheme -> {
                words[offset + 1] = 6
                payloads[index] = event.theme.encode()
            }
            is NativeSourceEvent.HostGeometry -> {
                words[offset + 1] = 5
                payloads[index] = event.geometry.encode()
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

private fun NativeTerminalConfigV4.toWords(): LongArray = longArrayOf(
    abiVersion.toLong(), structSize.toLong(), columns.toLong(), rows.toLong(),
    initialSourceSequence, reservedV1Compat0, reservedV1Compat1, historyCapacityRows,
    reservedV2Compat0, reservedV2Compat1, outputProfile.toLong(), reserved0.toLong(),
    reservedV3Compat0, reservedV3Compat1, widthProfile.toLong(), reserved1.toLong(), reserved2, reserved3,
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

private inline fun optionalGraphicsDiscovery(read: () -> Int): Int =
    try { read() } catch (_: UnsatisfiedLinkError) { 0 }

private inline fun <T : Any> optionalGraphicsCall(call: () -> NativeBridgeResult<T>): NativeBridgeResult<T> =
    try { call() } catch (_: UnsatisfiedLinkError) { NativeBridgeResult.Failure(NativeTerminalStatus.UNSUPPORTED.code) }

private fun ByteArray.boundedGraphicsPayload(maxBytes: Int): ByteArray = also {
    require(size <= minOf(maxBytes, TERMLINK_RENDER_FRAME_V5_MAX_BYTES)) { "Graphics payload exceeds requested bound" }
}
