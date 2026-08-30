package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalHostEffectFrameV2
import dev.termlink.terminal.api.TerminalInputModifiers
import dev.termlink.terminal.api.TerminalKittyKeyInput
import dev.termlink.terminal.api.TerminalKey
import dev.termlink.terminal.api.TerminalMouseButton
import dev.termlink.terminal.api.TerminalMouseButtons
import dev.termlink.terminal.api.TerminalMouseInput
import dev.termlink.terminal.api.TerminalMouseWheelDirection
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize

/**
 * Exclusive, idempotently closeable owner of one opaque native terminal handle.
 *
 * Calls are serialized to prevent `destroy` racing an in-flight bridge call. Event methods keep
 * source sequence ownership in this wrapper, so a discontinuity is rejected before native code is
 * entered.
 */
class NativeTerminalHandle private constructor(
    private val bridge: NativeBridge,
    private val limits: NativeTerminalLimits,
    allocation: NativeBridgeTerminal,
) : AutoCloseable {
    private val lock = Any()
    private var allocation: NativeBridgeTerminal? = allocation
    private var unusable = false

    val isClosed: Boolean
        get() = synchronized(lock) { allocation == null }

    val nextSourceSequence: Long
        get() = synchronized(lock) { requireUsable().nextSourceSequence }

    val presentationRevision: Long
        get() = synchronized(lock) { requireUsable().presentationRevision }

    fun apply(batch: NativeEventBatch): NativeBridgeApplyReceipt = synchronized(lock) {
        val current = requireUsable()
        // A batch may have been created with a different caller's limits. Rebuild it under this
        // handle's limits before any bridge call.
        val boundedBatch = NativeEventBatch.of(batch.events, limits)
        require(boundedBatch.firstSourceSequence == current.nextSourceSequence) {
            "source sequence mismatch: expected ${current.nextSourceSequence}, " +
                "received ${boundedBatch.firstSourceSequence}"
        }

        val receipt = invokeBridge(
            operation = "applyBatch",
            poisonOnException = true,
            poisonOnFailure = { it !in RETRYABLE_APPLY_FAILURES },
        ) {
            bridge.applyBatch(current.handle, boundedBatch)
        }
        try {
            validateApplyReceipt("applyBatch", boundedBatch, receipt)
            allocation = current.copy(
                nextSourceSequence = receipt.nextSourceSequence,
                presentationRevision = receipt.presentationRevision,
            )
            receipt
        } catch (error: Throwable) {
            // Native has already committed the batch. If Kotlin cannot publish
            // the new cursor (including OOM), the old allocation metadata must
            // never be reused for a retry.
            unusable = true
            throw error
        }
    }

    /** Applies one atomic source batch and returns its strictly decoded Host Effect Frame V2. */
    fun applyWithHostEffectsV2(batch: NativeEventBatch): TerminalHostEffectFrameV2 =
        synchronized(lock) {
            val current = requireUsable()
            val boundedBatch = NativeEventBatch.of(batch.events, limits)
            require(boundedBatch.firstSourceSequence == current.nextSourceSequence) {
                "source sequence mismatch: expected ${current.nextSourceSequence}, " +
                    "received ${boundedBatch.firstSourceSequence}"
            }

            val raw = invokeBridge(
                operation = "applyBatchWithHostEffectsV2",
                poisonOnException = true,
                poisonOnFailure = { it !in RETRYABLE_APPLY_FAILURES },
            ) {
                bridge.applyBatchWithHostEffectsV2(current.handle, boundedBatch)
            }
            validateApplyReceipt(
                "applyBatchWithHostEffectsV2",
                boundedBatch,
                raw.receipt,
            )
            if (raw.frameByteCount !in HOST_EFFECT_HEADER_BYTES..
                TERMLINK_HOST_EFFECT_FRAME_V2_MAX_BYTES
            ) {
                failMalformed(
                    "applyBatchWithHostEffectsV2",
                    "host-effect frame is outside the supported byte range",
                )
            }
            try {
                val decoded = try {
                    HostEffectFrameV2Decoder.decode(raw.copyHostEffectFrame())
                } catch (error: RuntimeException) {
                    failMalformed(
                        "applyBatchWithHostEffectsV2",
                        error.message ?: "host-effect frame is malformed",
                    )
                }
                allocation = current.copy(
                    nextSourceSequence = raw.receipt.nextSourceSequence,
                    presentationRevision = raw.receipt.presentationRevision,
                )
                decoded
            } catch (error: Throwable) {
                // The native batch is committed even if frame copy/decoding or
                // cursor publication fails on the JVM side.
                unusable = true
                throw error
            }
        }

    fun apply(
        events: List<NativeSourceEvent>,
    ): NativeBridgeApplyReceipt = apply(NativeEventBatch.of(events, limits))

    fun applyWithHostEffectsV2(
        events: List<NativeSourceEvent>,
    ): TerminalHostEffectFrameV2 = applyWithHostEffectsV2(NativeEventBatch.of(events, limits))

    fun feed(
        sourceSequence: Long,
        bytes: ByteArray,
    ): NativeBridgeApplyReceipt = apply(
        listOf(NativeSourceEvent.Output(sourceSequence, bytes)),
    )

    fun feedWithHostEffectsV2(
        sourceSequence: Long,
        bytes: ByteArray,
    ): TerminalHostEffectFrameV2 = applyWithHostEffectsV2(
        listOf(NativeSourceEvent.Output(sourceSequence, bytes)),
    )

    fun feedBatch(
        firstSourceSequence: Long,
        chunks: List<ByteArray>,
    ): NativeBridgeApplyReceipt {
        require(chunks.isNotEmpty()) { "feed batch must not be empty" }
        require(firstSourceSequence >= 0) { "source sequence must be non-negative" }
        val events = chunks.mapIndexed { index, bytes ->
            val sequence = try {
                Math.addExact(firstSourceSequence, index.toLong())
            } catch (error: ArithmeticException) {
                throw IllegalArgumentException("feed batch source sequence overflow", error)
            }
            NativeSourceEvent.Output(sequence, bytes)
        }
        return apply(events)
    }

    fun feedBatchWithHostEffectsV2(
        firstSourceSequence: Long,
        chunks: List<ByteArray>,
    ): TerminalHostEffectFrameV2 {
        require(chunks.isNotEmpty()) { "feed batch must not be empty" }
        require(firstSourceSequence >= 0) { "source sequence must be non-negative" }
        val events = chunks.mapIndexed { index, bytes ->
            val sequence = try {
                Math.addExact(firstSourceSequence, index.toLong())
            } catch (error: ArithmeticException) {
                throw IllegalArgumentException("feed batch source sequence overflow", error)
            }
            NativeSourceEvent.Output(sequence, bytes)
        }
        return applyWithHostEffectsV2(events)
    }

    fun resize(
        sourceSequence: Long,
        size: TerminalSize,
    ): NativeBridgeApplyReceipt = apply(
        listOf(NativeSourceEvent.Resize(sourceSequence, size)),
    )

    fun resizeWithHostEffectsV2(
        sourceSequence: Long,
        size: TerminalSize,
    ): TerminalHostEffectFrameV2 = applyWithHostEffectsV2(
        listOf(NativeSourceEvent.Resize(sourceSequence, size)),
    )

    fun advance(
        sourceSequence: Long,
        micros: Long,
    ): NativeBridgeApplyReceipt = apply(
        listOf(NativeSourceEvent.Advance(sourceSequence, micros)),
    )

    fun advanceWithHostEffectsV2(
        sourceSequence: Long,
        micros: Long,
    ): TerminalHostEffectFrameV2 = applyWithHostEffectsV2(
        listOf(NativeSourceEvent.Advance(sourceSequence, micros)),
    )

    fun reportGap(sourceSequence: Long): NativeBridgeApplyReceipt = apply(
        listOf(NativeSourceEvent.Gap(sourceSequence)),
    )

    fun reportGapWithHostEffectsV2(sourceSequence: Long): TerminalHostEffectFrameV2 =
        applyWithHostEffectsV2(listOf(NativeSourceEvent.Gap(sourceSequence)))

    fun render(
        maxPayloadBytes: Int = limits.maxRenderFrameBytes,
    ): NativeRenderFrame = synchronized(lock) {
        val current = requireUsable()
        require(maxPayloadBytes in 1..limits.maxRenderFrameBytes) {
            "render payload limit must be in 1..${limits.maxRenderFrameBytes}"
        }
        val request = NativeRenderRequest(maxPayloadBytes = maxPayloadBytes)
        val raw = invokeBridge("render") { bridge.render(current.handle, request) }
        if (raw.payload.size > maxPayloadBytes || raw.payload.size > limits.maxRenderFrameBytes) {
            throw NativeTerminalContractException(
                "render",
                "frame payload exceeds the requested bound",
            )
        }
        val decoded = try {
            RenderFrameV1Decoder.decode(raw.payload)
        } catch (error: RenderFrameV1DecodeException) {
            throw NativeTerminalContractException("render", error.message ?: "malformed frame")
        }
        if (decoded.nextSourceSequence != current.nextSourceSequence) {
            throw NativeTerminalContractException(
                "render",
                "expected next source sequence ${current.nextSourceSequence}, " +
                    "received ${decoded.nextSourceSequence}",
            )
        }
        if (decoded.presentationRevision != current.presentationRevision) {
            throw NativeTerminalContractException(
                "render",
                "expected presentation revision ${current.presentationRevision}, " +
                    "received ${decoded.presentationRevision}",
            )
        }
        try {
            limits.validateSize(decoded.size, "render frame")
        } catch (error: IllegalArgumentException) {
            throw NativeTerminalContractException("render", error.message ?: "invalid frame size")
        }
        NativeRenderFrame(decoded)
    }

    /** Exports the canonical V3 frame consumed by the production Android terminal view. */
    fun renderV3(
        maxPayloadBytes: Int = limits.maxRenderFrameBytes,
    ): NativeRenderFrameV3 = synchronized(lock) {
        val current = requireUsable()
        require(maxPayloadBytes in 1..limits.maxRenderFrameBytes) {
            "render payload limit must be in 1..${limits.maxRenderFrameBytes}"
        }
        val request = NativeRenderRequest(maxPayloadBytes = maxPayloadBytes)
        val raw = invokeBridge("renderV3") { bridge.renderV3(current.handle, request) }
        NativeRenderFrameV3(decodeRenderFrameV3("renderV3", current, raw.payload, maxPayloadBytes))
    }

    /** Exports a local-history V3 projection and its immutable scrollback metadata. */
    fun renderV3AtScrollback(
        offsetRows: Long,
        maxPayloadBytes: Int = limits.maxRenderFrameBytes,
    ): NativeScrollbackFrameV3 = synchronized(lock) {
        val current = requireUsable()
        require(offsetRows in 0..UNSIGNED_INT_MAX) {
            "scrollback offset rows must be in 0..$UNSIGNED_INT_MAX"
        }
        require(maxPayloadBytes in 1..limits.maxRenderFrameBytes) {
            "render payload limit must be in 1..${limits.maxRenderFrameBytes}"
        }
        val request = NativeRenderRequest(maxPayloadBytes = maxPayloadBytes)
        val raw = invokeBridge("renderV3AtScrollback") {
            bridge.renderV3AtScrollback(current.handle, offsetRows, request)
        }
        if (raw.availableRows !in 0..UNSIGNED_INT_MAX || raw.generationRows < 0 ||
            raw.appliedOffsetRows !in 0..raw.availableRows ||
            raw.appliedOffsetRows != offsetRows.coerceAtMost(raw.availableRows)
        ) {
            throw NativeTerminalContractException(
                "renderV3AtScrollback",
                "bridge returned inconsistent scrollback metadata",
            )
        }
        NativeScrollbackFrameV3(
            renderFrame = decodeRenderFrameV3(
                "renderV3AtScrollback",
                current,
                raw.payload,
                maxPayloadBytes,
            ),
            availableRows = raw.availableRows,
            generationRows = raw.generationRows,
            appliedOffsetRows = raw.appliedOffsetRows,
        )
    }

    private fun decodeRenderFrameV3(
        operation: String,
        current: NativeBridgeTerminal,
        payload: ByteArray,
        maxPayloadBytes: Int,
    ) = run {
        if (payload.size > maxPayloadBytes || payload.size > limits.maxRenderFrameBytes) {
            throw NativeTerminalContractException(
                operation,
                "frame payload exceeds the requested bound",
            )
        }
        val decoded = try {
            RenderFrameV3Decoder.decode(payload)
        } catch (error: RenderFrameV3DecodeException) {
            throw NativeTerminalContractException(operation, error.message ?: "malformed frame")
        }
        if (decoded.nextSourceSequence != current.nextSourceSequence) {
            throw NativeTerminalContractException(
                operation,
                "expected next source sequence ${current.nextSourceSequence}, " +
                    "received ${decoded.nextSourceSequence}",
            )
        }
        if (decoded.presentationRevision != current.presentationRevision) {
            throw NativeTerminalContractException(
                operation,
                "expected presentation revision ${current.presentationRevision}, " +
                    "received ${decoded.presentationRevision}",
            )
        }
        try {
            limits.validateSize(decoded.size, "render frame v3")
        } catch (error: IllegalArgumentException) {
            throw NativeTerminalContractException(
                operation,
                error.message ?: "invalid frame size",
            )
        }
        decoded
    }

    /** Encodes one mode-aware semantic key through terminal-core Input V1. */
    fun encodeKey(
        key: TerminalKey,
        modifiers: TerminalInputModifiers = TerminalInputModifiers(),
    ): ByteArray = encodeInputV1(
        NativeTerminalInputV1(
            kind = NATIVE_INPUT_KIND_KEY,
            key = nativeKey(key),
            modifiers = nativeModifiers(modifiers),
        ),
    )

    /** Encodes one Unicode scalar through terminal-core Input V1. */
    fun encodeCharacter(
        codePoint: Int,
        modifiers: TerminalInputModifiers = TerminalInputModifiers(),
    ): ByteArray {
        require(
            Character.isValidCodePoint(codePoint) &&
                codePoint !in Character.MIN_SURROGATE.code..Character.MAX_SURROGATE.code,
        ) { "character must be a Unicode scalar" }
        return encodeInputV1(
            NativeTerminalInputV1(
                kind = NATIVE_INPUT_KIND_KEY,
                key = NATIVE_INPUT_KEY_CHARACTER.toInt(),
                modifiers = nativeModifiers(modifiers),
                character = codePoint,
            ),
        )
    }

    /** Encodes an owned paste payload through terminal-core Input V1. */
    fun encodePaste(bytes: ByteArray): ByteArray {
        require(bytes.isNotEmpty()) { "paste input must not be empty" }
        return encodeInputV1(
            NativeTerminalInputV1(
                kind = NATIVE_INPUT_KIND_PASTE,
                data = bytes,
            ),
        )
    }

    /** Encodes a focus transition, returning an empty payload when focus reporting is disabled. */
    fun encodeFocus(focused: Boolean): ByteArray = encodeInputV1(
        NativeTerminalInputV1(
            kind = if (focused) NATIVE_INPUT_KIND_FOCUS_GAINED else NATIVE_INPUT_KIND_FOCUS_LOST,
        ),
    )

    private fun encodeInputV1(input: NativeTerminalInputV1): ByteArray = synchronized(lock) {
        val current = requireUsable()
        val encoded = invokeBridge(
            operation = "encodeInputV1",
            poisonOnFailure = { it !in RETRYABLE_INPUT_FAILURES },
        ) {
            bridge.encodeInputV1(current.handle, input)
        }
        encoded.payload.copyOf()
    }

    /** Encodes one semantic mouse event against the terminal's current tracking/wire modes. */
    fun encodeMouse(input: TerminalMouseInput): ByteArray = synchronized(lock) {
        val current = requireUsable()
        val nativeInput = input.toNativeMouseInputV1()
        val encoded = invokeBridge(
            operation = "encodeMouse",
            poisonOnFailure = { it !in RETRYABLE_MOUSE_FAILURES },
        ) {
            bridge.encodeMouse(current.handle, nativeInput)
        }
        // A successful no-tracking result is intentionally an owned empty array.
        encoded.payload.copyOf()
    }

    /**
     * Encodes one Win32 key record while DEC private mode 9001 is enabled.
     *
     * [unicodeChar] is one UTF-16 code unit, not a Unicode scalar, so surrogate values are
     * intentionally preserved.
     */
    fun encodeWin32Key(
        virtualKey: Int,
        scanCode: Int,
        unicodeChar: Int,
        keyDown: Boolean,
        controlKeyState: Long,
        repeatCount: Int,
    ): ByteArray = synchronized(lock) {
        val current = requireUsable()
        requireUnsignedShort(virtualKey, "virtual key")
        requireUnsignedShort(scanCode, "scan code")
        requireUnsignedShort(unicodeChar, "Unicode code unit")
        require(controlKeyState in 0..UNSIGNED_INT_MAX) {
            "control key state must be in 0..$UNSIGNED_INT_MAX"
        }
        requireUnsignedShort(repeatCount, "repeat count")
        val input = NativeWin32KeyInputV2(
            win32VirtualKey = virtualKey,
            win32ScanCode = scanCode,
            win32UnicodeChar = unicodeChar,
            win32KeyDown = if (keyDown) 1 else 0,
            win32ControlKeyState = controlKeyState,
            win32RepeatCount = repeatCount,
        )
        val encoded = invokeBridge(
            operation = "encodeWin32Key",
            poisonOnFailure = { it !in RETRYABLE_INPUT_FAILURES },
        ) {
            bridge.encodeInputV2(current.handle, input)
        }
        encoded.payload.copyOf()
    }

    /** Encodes one fully validated semantic Kitty keyboard event through Input V3. */
    fun encodeKittyKey(input: TerminalKittyKeyInput): ByteArray = synchronized(lock) {
        val current = requireUsable()
        val nativeInput = nativeKittyKeyInput(input)
        val encoded = invokeBridge(
            operation = "encodeKittyKey",
            poisonOnFailure = { it !in RETRYABLE_INPUT_FAILURES },
        ) {
            bridge.encodeInputV3(current.handle, nativeInput)
        }
        encoded.payload.copyOf()
    }

    fun exportCheckpoint(
        maxPayloadBytes: Int = limits.maxCheckpointBytes,
    ): NativeCheckpoint = synchronized(lock) {
        val current = requireUsable()
        require(maxPayloadBytes in 1..limits.maxCheckpointBytes) {
            "checkpoint payload limit must be in 1..${limits.maxCheckpointBytes}"
        }
        val raw = invokeBridge("exportCheckpoint") {
            bridge.exportCheckpoint(current.handle, maxPayloadBytes)
        }
        if (raw.nextSourceSequence != current.nextSourceSequence) {
            throw NativeTerminalContractException(
                "exportCheckpoint",
                "expected next source sequence ${current.nextSourceSequence}, " +
                    "received ${raw.nextSourceSequence}",
            )
        }
        if (raw.presentationRevision != current.presentationRevision) {
            throw NativeTerminalContractException(
                "exportCheckpoint",
                "expected presentation revision ${current.presentationRevision}, " +
                    "received ${raw.presentationRevision}",
            )
        }
        if (raw.payload.isEmpty()) {
            throw NativeTerminalContractException("exportCheckpoint", "checkpoint is empty")
        }
        if (raw.payload.size > maxPayloadBytes || raw.payload.size > limits.maxCheckpointBytes) {
            throw NativeTerminalContractException(
                "exportCheckpoint",
                "checkpoint exceeds the requested bound",
            )
        }
        NativeCheckpoint(
            nextSourceSequence = raw.nextSourceSequence,
            presentationRevision = raw.presentationRevision,
            payload = raw.payload,
        )
    }

    override fun close() {
        synchronized(lock) {
            val current = allocation ?: return
            // Mark closed before crossing the bridge. Even a failed destroy is never retried with
            // a pointer whose lifetime is now uncertain.
            allocation = null
            unusable = true
            invokeBridge("destroy") { bridge.destroy(current.handle) }
        }
    }

    private fun requireUsable(): NativeBridgeTerminal {
        val current = allocation ?: error("Native terminal handle is closed")
        check(!unusable) { "Native terminal handle is unusable after a failed mutating call" }
        return current
    }

    private fun validateApplyReceipt(
        operation: String,
        batch: NativeEventBatch,
        receipt: NativeBridgeApplyReceipt,
    ) {
        if (receipt.nextSourceSequence !in 0 until Long.MAX_VALUE) {
            failMalformed(operation, "next source sequence is outside the supported range")
        }
        if (receipt.nextSourceSequence != batch.nextSourceSequence) {
            failMalformed(
                operation,
                "expected next source sequence ${batch.nextSourceSequence}, " +
                    "received ${receipt.nextSourceSequence}",
            )
        }
        if (receipt.presentationRevision !in 0 until Long.MAX_VALUE) {
            failMalformed(operation, "presentation revision is outside the supported range")
        }
        if (receipt.presentationRevision != receipt.nextSourceSequence) {
            failMalformed(
                operation,
                "presentation revision ${receipt.presentationRevision} does not match next " +
                    "source sequence ${receipt.nextSourceSequence}",
            )
        }
    }

    private fun failMalformed(operation: String, message: String): Nothing {
        unusable = true
        throw NativeTerminalContractException(operation, message)
    }

    private fun <T : Any> invokeBridge(
        operation: String,
        poisonOnException: Boolean = false,
        poisonOnFailure: (NativeTerminalStatus) -> Boolean = { false },
        call: () -> NativeBridgeResult<T>,
    ): T {
        val result = try {
            call()
        } catch (error: Throwable) {
            if (poisonOnException) unusable = true
            throw NativeTerminalException(
                operation = operation,
                status = NativeTerminalStatus.BRIDGE_FAILURE,
                diagnostic = error.message,
                cause = error,
            )
        }
        return when (result) {
            is NativeBridgeResult.Success -> result.value
            is NativeBridgeResult.Failure -> {
                val status = NativeTerminalStatus.fromBridgeFailureCode(result.statusCode)
                if (poisonOnFailure(status)) {
                    unusable = true
                }
                throw NativeTerminalException(
                    operation = operation,
                    status = status,
                    rawStatusCode = result.statusCode,
                    diagnostic = result.diagnostic?.take(MAX_DIAGNOSTIC_CHARACTERS),
                )
            }
        }
    }

    companion object {
        private const val MAX_DIAGNOSTIC_CHARACTERS = 4_096
        private const val HOST_EFFECT_HEADER_BYTES = 64
        private val RETRYABLE_APPLY_FAILURES = setOf(
            NativeTerminalStatus.INVALID_ARGUMENT,
            NativeTerminalStatus.INVALID_DIMENSIONS,
            NativeTerminalStatus.LIMIT_EXCEEDED,
            NativeTerminalStatus.SEQUENCE_MISMATCH,
            NativeTerminalStatus.TIME_OVERFLOW,
            NativeTerminalStatus.HOST_EFFECT_VERSION_REQUIRED,
        )
        private val RETRYABLE_MOUSE_FAILURES = setOf(
            NativeTerminalStatus.INVALID_ARGUMENT,
            NativeTerminalStatus.INVALID_DIMENSIONS,
            NativeTerminalStatus.LIMIT_EXCEEDED,
        )
        private val RETRYABLE_INPUT_FAILURES = RETRYABLE_MOUSE_FAILURES +
            NativeTerminalStatus.UNSUPPORTED
        private const val UNSIGNED_SHORT_MAX = 0xffff
        private const val UNSIGNED_INT_MAX = 0xffff_ffffL

        private fun nativeModifiers(modifiers: TerminalInputModifiers): Int = with(modifiers) {
            (if (shift) NATIVE_INPUT_MODIFIER_SHIFT else 0) or
                (if (alt) NATIVE_INPUT_MODIFIER_ALT else 0) or
                (if (control) NATIVE_INPUT_MODIFIER_CONTROL else 0) or
                (if (meta) NATIVE_INPUT_MODIFIER_META else 0)
        }

        private fun nativeKey(key: TerminalKey): Int = when (key) {
            TerminalKey.ENTER -> 2
            TerminalKey.BACKSPACE -> 3
            TerminalKey.TAB -> 4
            TerminalKey.ESCAPE -> 5
            TerminalKey.DELETE -> 6
            TerminalKey.INSERT -> 7
            TerminalKey.HOME -> 8
            TerminalKey.END -> 9
            TerminalKey.PAGE_UP -> 10
            TerminalKey.PAGE_DOWN -> 11
            TerminalKey.ARROW_UP -> 12
            TerminalKey.ARROW_DOWN -> 13
            TerminalKey.ARROW_LEFT -> 14
            TerminalKey.ARROW_RIGHT -> 15
            TerminalKey.F1 -> 16
            TerminalKey.F2 -> 17
            TerminalKey.F3 -> 18
            TerminalKey.F4 -> 19
            TerminalKey.F5 -> 20
            TerminalKey.F6 -> 21
            TerminalKey.F7 -> 22
            TerminalKey.F8 -> 23
            TerminalKey.F9 -> 24
            TerminalKey.F10 -> 25
            TerminalKey.F11 -> 26
            TerminalKey.F12 -> 27
        }

        fun create(
            bridge: NativeBridge,
            size: TerminalSize,
            initialNextSourceSequence: Long,
            limits: NativeTerminalLimits = NativeTerminalLimits(),
        ): NativeTerminalHandle {
            limits.validateSize(size, "initial terminal")
            require(initialNextSourceSequence in 0 until Long.MAX_VALUE) {
                "initial next source sequence must be in 0..${Long.MAX_VALUE - 1L}"
            }
            requireAbi(bridge)
            val allocation = invokeOpeningBridge("create") {
                bridge.create(size, initialNextSourceSequence)
            }
            try {
                validateAllocation("create", allocation)
                if (
                    allocation.nextSourceSequence != initialNextSourceSequence ||
                    allocation.presentationRevision != initialNextSourceSequence
                ) {
                    throw NativeTerminalContractException(
                        "create",
                        "a new terminal must retain the requested next source sequence and use " +
                            "that sequence as its presentation revision",
                    )
                }
            } catch (error: NativeTerminalContractException) {
                discardMalformedAllocation(bridge, allocation)
                throw error
            }
            return NativeTerminalHandle(bridge, limits, allocation)
        }

        /**
         * Creates a terminal through the additive Config V3 contract.
         *
         * The legacy [create] overload continues to use [NativeBridge.create]. This overload is
         * [JniNativeBridge] maps this overload to the packaged `tl_terminal_create_v3` entry point.
         */
        fun create(
            bridge: NativeBridge,
            size: TerminalSize,
            initialNextSourceSequence: Long,
            outputProfile: TerminalOutputProfile,
            historyCapacityRows: Long = 0,
            limits: NativeTerminalLimits = NativeTerminalLimits(),
        ): NativeTerminalHandle {
            limits.validateSize(size, "initial terminal")
            require(initialNextSourceSequence in 0 until Long.MAX_VALUE) {
                "initial next source sequence must be in 0..${Long.MAX_VALUE - 1L}"
            }
            require(historyCapacityRows in 0..UNSIGNED_INT_MAX) {
                "history capacity rows must be in 0..$UNSIGNED_INT_MAX"
            }
            requireAbi(bridge)
            requireConfigV3Abi(bridge)
            val config = NativeTerminalConfigV3(
                columns = size.columns,
                rows = size.rows,
                initialSourceSequence = initialNextSourceSequence,
                historyCapacityRows = historyCapacityRows,
                outputProfile = outputProfile.toNativeOutputProfile(),
            )
            val allocation = invokeOpeningBridge("createV3") {
                bridge.createV3(config)
            }
            try {
                validateAllocation("createV3", allocation)
                if (
                    allocation.nextSourceSequence != initialNextSourceSequence ||
                    allocation.presentationRevision != initialNextSourceSequence
                ) {
                    throw NativeTerminalContractException(
                        "createV3",
                        "a new terminal must retain the requested next source sequence and use " +
                            "that sequence as its presentation revision",
                    )
                }
            } catch (error: NativeTerminalContractException) {
                discardMalformedAllocation(bridge, allocation)
                throw error
            }
            return NativeTerminalHandle(bridge, limits, allocation)
        }

        fun importCheckpoint(
            bridge: NativeBridge,
            checkpoint: ByteArray,
            limits: NativeTerminalLimits = NativeTerminalLimits(),
        ): NativeTerminalHandle {
            require(checkpoint.isNotEmpty()) { "checkpoint must not be empty" }
            require(checkpoint.size <= limits.maxCheckpointBytes) {
                "checkpoint exceeds ${limits.maxCheckpointBytes} bytes"
            }
            requireAbi(bridge)
            val ownedCheckpoint = checkpoint.copyOf()
            val allocation = invokeOpeningBridge("importCheckpoint") {
                bridge.importCheckpoint(ownedCheckpoint)
            }
            try {
                validateAllocation("importCheckpoint", allocation)
            } catch (error: NativeTerminalContractException) {
                discardMalformedAllocation(bridge, allocation)
                throw error
            }
            return NativeTerminalHandle(bridge, limits, allocation)
        }

        private fun requireAbi(bridge: NativeBridge) {
            val actualAbi = readDiscovery("abiVersion") { bridge.abiVersion }
            if (actualAbi != TERMLINK_TERMINAL_ABI_VERSION) {
                throw NativeTerminalException(
                    operation = "abiVersion",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic =
                        "expected $TERMLINK_TERMINAL_ABI_VERSION, received $actualAbi",
                )
            }
            val actualFormat = readDiscovery("renderFrameFormatVersion") {
                bridge.renderFrameFormatVersion
            }
            if (actualFormat != TERMLINK_RENDER_FRAME_V1_FORMAT_VERSION) {
                throw NativeTerminalException(
                    operation = "renderFrameFormatVersion",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_RENDER_FRAME_V1_FORMAT_VERSION, " +
                        "received $actualFormat",
                )
            }
            val actualMaximum = readDiscovery("maxRenderFrameBytes") {
                bridge.maxRenderFrameBytes
            }
            if (actualMaximum != TERMLINK_RENDER_FRAME_V1_MAX_BYTES) {
                throw NativeTerminalException(
                    operation = "maxRenderFrameBytes",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_RENDER_FRAME_V1_MAX_BYTES, " +
                        "received $actualMaximum",
                )
            }
            val actualV3Format = readDiscovery("renderFrameV3FormatVersion") {
                bridge.renderFrameV3FormatVersion
            }
            if (actualV3Format != TERMLINK_RENDER_FRAME_V3_FORMAT_VERSION) {
                throw NativeTerminalException(
                    operation = "renderFrameV3FormatVersion",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_RENDER_FRAME_V3_FORMAT_VERSION, " +
                        "received $actualV3Format",
                )
            }
            val actualV3Maximum = readDiscovery("maxRenderFrameV3Bytes") {
                bridge.maxRenderFrameV3Bytes
            }
            if (actualV3Maximum != TERMLINK_RENDER_FRAME_V3_MAX_BYTES) {
                throw NativeTerminalException(
                    operation = "maxRenderFrameV3Bytes",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_RENDER_FRAME_V3_MAX_BYTES, " +
                        "received $actualV3Maximum",
                )
            }
            val actualHostEffectV2Format = readDiscovery("hostEffectFrameV2FormatVersion") {
                bridge.hostEffectFrameV2FormatVersion
            }
            if (actualHostEffectV2Format != TERMLINK_HOST_EFFECT_FRAME_V2_FORMAT_VERSION) {
                throw NativeTerminalException(
                    operation = "hostEffectFrameV2FormatVersion",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic =
                        "expected $TERMLINK_HOST_EFFECT_FRAME_V2_FORMAT_VERSION, " +
                            "received $actualHostEffectV2Format",
                )
            }
            val actualHostEffectV2Maximum = readDiscovery("maxHostEffectFrameV2Bytes") {
                bridge.maxHostEffectFrameV2Bytes
            }
            if (actualHostEffectV2Maximum != TERMLINK_HOST_EFFECT_FRAME_V2_MAX_BYTES) {
                throw NativeTerminalException(
                    operation = "maxHostEffectFrameV2Bytes",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_HOST_EFFECT_FRAME_V2_MAX_BYTES, " +
                        "received $actualHostEffectV2Maximum",
                )
            }
            val actualMouseInputSize = readDiscovery("mouseInputV1StructSize") {
                bridge.mouseInputV1StructSize
            }
            if (actualMouseInputSize != TERMLINK_MOUSE_INPUT_V1_STRUCT_SIZE) {
                throw NativeTerminalException(
                    operation = "mouseInputV1StructSize",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_MOUSE_INPUT_V1_STRUCT_SIZE, " +
                        "received $actualMouseInputSize",
                    )
            }
            val actualInputV1Size = readDiscovery("inputV1StructSize") {
                bridge.inputV1StructSize
            }
            if (actualInputV1Size != TERMLINK_INPUT_V1_STRUCT_SIZE) {
                throw NativeTerminalException(
                    operation = "inputV1StructSize",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_INPUT_V1_STRUCT_SIZE, " +
                        "received $actualInputV1Size",
                )
            }
            val actualInputV2Size = readDiscovery("inputV2StructSize") {
                bridge.inputV2StructSize
            }
            if (actualInputV2Size != TERMLINK_INPUT_V2_STRUCT_SIZE) {
                throw NativeTerminalException(
                    operation = "inputV2StructSize",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_INPUT_V2_STRUCT_SIZE, " +
                        "received $actualInputV2Size",
                )
            }
            val actualInputV3Size = readDiscovery("inputV3StructSize") {
                bridge.inputV3StructSize
            }
            if (actualInputV3Size != TERMLINK_INPUT_V3_STRUCT_SIZE) {
                throw NativeTerminalException(
                    operation = "inputV3StructSize",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_INPUT_V3_STRUCT_SIZE, " +
                        "received $actualInputV3Size",
                )
            }
        }

        private fun requireConfigV3Abi(bridge: NativeBridge) {
            val actualConfigV3Size = readDiscovery("configV3StructSize") {
                bridge.configV3StructSize
            }
            if (actualConfigV3Size != TERMLINK_CONFIG_V3_STRUCT_SIZE) {
                throw NativeTerminalException(
                    operation = "configV3StructSize",
                    status = NativeTerminalStatus.ABI_MISMATCH,
                    diagnostic = "expected $TERMLINK_CONFIG_V3_STRUCT_SIZE, " +
                        "received $actualConfigV3Size",
                )
            }
        }

        private fun requireUnsignedShort(value: Int, field: String) {
            require(value in 0..UNSIGNED_SHORT_MAX) {
                "$field must be in 0..$UNSIGNED_SHORT_MAX"
            }
        }

        private fun readDiscovery(operation: String, value: () -> Int): Int = try {
            value()
        } catch (error: Throwable) {
            throw NativeTerminalException(
                operation = operation,
                status = NativeTerminalStatus.BRIDGE_FAILURE,
                diagnostic = error.message,
                cause = error,
            )
        }

        private fun validateAllocation(operation: String, allocation: NativeBridgeTerminal) {
            if (allocation.nextSourceSequence !in 0 until Long.MAX_VALUE) {
                throw NativeTerminalContractException(
                    operation,
                    "next source sequence is outside 0..${Long.MAX_VALUE - 1L}",
                )
            }
            if (allocation.presentationRevision !in 0 until Long.MAX_VALUE) {
                throw NativeTerminalContractException(
                    operation,
                    "presentation revision is outside 0..${Long.MAX_VALUE - 1L}",
                )
            }
            if (allocation.presentationRevision != allocation.nextSourceSequence) {
                throw NativeTerminalContractException(
                    operation,
                    "presentation revision must equal next source sequence",
                )
            }
        }

        private fun discardMalformedAllocation(
            bridge: NativeBridge,
            allocation: NativeBridgeTerminal,
        ) {
            // A malformed result is still an ownership transfer. Best-effort destruction prevents
            // leaking a real native allocation; the original contract failure remains primary.
            runCatching { bridge.destroy(allocation.handle) }
        }

        private fun <T : Any> invokeOpeningBridge(
            operation: String,
            call: () -> NativeBridgeResult<T>,
        ): T {
            val result = try {
                call()
            } catch (error: Throwable) {
                throw NativeTerminalException(
                    operation = operation,
                    status = NativeTerminalStatus.BRIDGE_FAILURE,
                    diagnostic = error.message,
                    cause = error,
                )
            }
            return when (result) {
                is NativeBridgeResult.Success -> result.value
                is NativeBridgeResult.Failure -> throw NativeTerminalException(
                    operation = operation,
                    status = NativeTerminalStatus.fromBridgeFailureCode(result.statusCode),
                    rawStatusCode = result.statusCode,
                    diagnostic = result.diagnostic?.take(MAX_DIAGNOSTIC_CHARACTERS),
                )
            }
        }
    }
}

private fun TerminalMouseInput.toNativeMouseInputV1(): NativeMouseInputV1 {
    val action = when (this) {
        is TerminalMouseInput.Press -> NATIVE_MOUSE_ACTION_PRESS
        is TerminalMouseInput.Release -> NATIVE_MOUSE_ACTION_RELEASE
        is TerminalMouseInput.Move -> NATIVE_MOUSE_ACTION_MOVE
        is TerminalMouseInput.Wheel -> NATIVE_MOUSE_ACTION_WHEEL
    }
    val button = when (this) {
        is TerminalMouseInput.Press -> button.toNativeCode()
        is TerminalMouseInput.Release -> button.toNativeCode()
        is TerminalMouseInput.Move,
        is TerminalMouseInput.Wheel,
        -> NATIVE_MOUSE_BUTTON_NONE
    }
    val wheel = when (this) {
        is TerminalMouseInput.Wheel -> direction.toNativeCode()
        is TerminalMouseInput.Press,
        is TerminalMouseInput.Release,
        is TerminalMouseInput.Move,
        -> NATIVE_MOUSE_WHEEL_NONE
    }
    val pixel = position.pixel
    return NativeMouseInputV1(
        action = action,
        button = button,
        wheel = wheel,
        buttonsHeld = buttonsHeld.toNativeBits(),
        positionFlags = if (pixel == null) 0 else NATIVE_MOUSE_POSITION_PIXEL,
        modifiers = modifiers.toNativeBits(),
        cellColumn = position.cell.column,
        cellRow = position.cell.row,
        pixelX = pixel?.x ?: 0,
        pixelY = pixel?.y ?: 0,
    )
}

private fun TerminalMouseButton.toNativeCode(): Int = when (this) {
    TerminalMouseButton.LEFT -> NATIVE_MOUSE_BUTTON_LEFT
    TerminalMouseButton.MIDDLE -> NATIVE_MOUSE_BUTTON_MIDDLE
    TerminalMouseButton.RIGHT -> NATIVE_MOUSE_BUTTON_RIGHT
}

private fun TerminalMouseWheelDirection.toNativeCode(): Int = when (this) {
    TerminalMouseWheelDirection.UP -> NATIVE_MOUSE_WHEEL_UP
    TerminalMouseWheelDirection.DOWN -> NATIVE_MOUSE_WHEEL_DOWN
    TerminalMouseWheelDirection.LEFT -> NATIVE_MOUSE_WHEEL_LEFT
    TerminalMouseWheelDirection.RIGHT -> NATIVE_MOUSE_WHEEL_RIGHT
}

private fun TerminalMouseButtons.toNativeBits(): Int =
    (if (left) NATIVE_MOUSE_BUTTON_HELD_LEFT else 0) or
        (if (middle) NATIVE_MOUSE_BUTTON_HELD_MIDDLE else 0) or
        (if (right) NATIVE_MOUSE_BUTTON_HELD_RIGHT else 0)

private fun TerminalInputModifiers.toNativeBits(): Int =
    (if (shift) NATIVE_INPUT_MODIFIER_SHIFT else 0) or
        (if (alt) NATIVE_INPUT_MODIFIER_ALT else 0) or
        (if (control) NATIVE_INPUT_MODIFIER_CONTROL else 0) or
        (if (meta) NATIVE_INPUT_MODIFIER_META else 0)
