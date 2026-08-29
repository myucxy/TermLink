package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalKittyFunctionalKey
import dev.termlink.terminal.api.TerminalInput
import dev.termlink.terminal.api.TerminalInputModifiers
import dev.termlink.terminal.api.TerminalKey
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.VtTerminalInputEncoder
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.nio.charset.StandardCharsets
import java.util.zip.CRC32C

/**
 * Pure Kotlin fixture. It models ownership and sequencing plus the narrow input-mode surface used
 * by this bridge contract; it is not a general VT parser.
 */
internal class FakeNativeBridge : NativeBridge {
    override var abiVersion: Int = TERMLINK_TERMINAL_ABI_VERSION
    override var renderFrameFormatVersion: Int = TERMLINK_RENDER_FRAME_V1_FORMAT_VERSION
    override var maxRenderFrameBytes: Int = TERMLINK_RENDER_FRAME_V1_MAX_BYTES
    override var renderFrameV3FormatVersion: Int = TERMLINK_RENDER_FRAME_V3_FORMAT_VERSION
    override var maxRenderFrameV3Bytes: Int = TERMLINK_RENDER_FRAME_V3_MAX_BYTES
    override var hostEffectFrameV2FormatVersion: Int =
        TERMLINK_HOST_EFFECT_FRAME_V2_FORMAT_VERSION
    override var maxHostEffectFrameV2Bytes: Int = TERMLINK_HOST_EFFECT_FRAME_V2_MAX_BYTES
    override var mouseInputV1StructSize: Int = TERMLINK_MOUSE_INPUT_V1_STRUCT_SIZE
    override var inputV1StructSize: Int = TERMLINK_INPUT_V1_STRUCT_SIZE
    override var inputV2StructSize: Int = TERMLINK_INPUT_V2_STRUCT_SIZE
    override var inputV3StructSize: Int = TERMLINK_INPUT_V3_STRUCT_SIZE
    override var configV3StructSize: Int = TERMLINK_CONFIG_V3_STRUCT_SIZE

    var forcedCreateFailure: NativeBridgeResult.Failure? = null
    var forcedCreateAllocation: NativeBridgeTerminal? = null
    var forcedImportFailure: NativeBridgeResult.Failure? = null
    var forcedApplyFailure: NativeBridgeResult.Failure? = null
    var forcedHostEffectV2Failure: NativeBridgeResult.Failure? = null
    var forcedRenderFailure: NativeBridgeResult.Failure? = null
    var forcedMouseFailure: NativeBridgeResult.Failure? = null
    var forcedInputV2Failure: NativeBridgeResult.Failure? = null
    var forcedInputV3Failure: NativeBridgeResult.Failure? = null
    var forcedCheckpointFailure: NativeBridgeResult.Failure? = null
    var forcedRenderFrame: NativeBridgeRenderFrame? = null
    var forcedRenderFrameV3: NativeBridgeRenderFrame? = null
    var forcedInputV1Failure: NativeBridgeResult.Failure? = null
    var forcedInputV1EncodedInput: NativeBridgeEncodedInput? = null
    var forcedMouseEncodedInput: NativeBridgeEncodedInput? = null
    var forcedInputV2EncodedInput: NativeBridgeEncodedInput? = null
    var forcedInputV3EncodedInput: NativeBridgeEncodedInput? = null
    var forcedHostEffectV2Result: NativeBridgeApplyWithHostEffectsV2? = null
    var forcedCheckpoint: NativeBridgeCheckpoint? = null
    var throwOnApply: Throwable? = null
    var throwOnHostEffectV2: RuntimeException? = null
    var throwOnMouse: RuntimeException? = null
    var throwOnInputV2: RuntimeException? = null
    var throwOnInputV3: RuntimeException? = null
    var destroyCalls: Int = 0
        private set
    var createCalls: Int = 0
        private set
    var createV3Calls: Int = 0
        private set
    var applyCalls: Int = 0
        private set
    var hostEffectV2Calls: Int = 0
        private set
    var mouseCalls: Int = 0
        private set
    var inputV1Calls: Int = 0
        private set
    var inputV2Calls: Int = 0
        private set
    var inputV3Calls: Int = 0
        private set
    var lastMouseInput: NativeMouseInputV1? = null
        private set
    var lastInputV1: NativeTerminalInputV1? = null
        private set
    var lastInputV2: NativeWin32KeyInputV2? = null
        private set
    var lastInputV3: NativeKittyKeyInputV3? = null
        private set
    var lastConfigV3: NativeTerminalConfigV3? = null
        private set
    var lastHostEffectV2Batch: NativeEventBatch? = null
        private set
    val lastOutputBytes: ByteArray?
        get() = synchronized(this) { lastCommittedOutputBytes?.copyOf() }

    private var nextHandle = 1L
    private val states = mutableMapOf<NativeBridgeHandle, State>()
    private var lastCommittedOutputBytes: ByteArray? = null

    override fun create(
        size: TerminalSize,
        initialNextSourceSequence: Long,
    ): NativeBridgeResult<NativeBridgeTerminal> = synchronized(this) {
        createCalls += 1
        forcedCreateFailure?.let { return it }
        forcedCreateAllocation?.let { return NativeBridgeResult.Success(it) }
        if (initialNextSourceSequence !in 0 until Long.MAX_VALUE) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
        }
        allocate(
            State(
                nextSourceSequence = initialNextSourceSequence,
                presentationRevision = initialNextSourceSequence,
                size = size,
            ),
        )
    }

    override fun createV3(
        config: NativeTerminalConfigV3,
    ): NativeBridgeResult<NativeBridgeTerminal> = synchronized(this) {
        createV3Calls += 1
        lastConfigV3 = config.copy()
        forcedCreateFailure?.let { return it }
        forcedCreateAllocation?.let { return NativeBridgeResult.Success(it) }
        validateConfigV3(config)?.let { return it }

        val columns = config.columns.toUnsignedLong().toInt()
        val rows = config.rows.toUnsignedLong().toInt()
        allocate(
            State(
                nextSourceSequence = config.initialSourceSequence,
                presentationRevision = config.initialSourceSequence,
                size = TerminalSize(columns, rows),
                historyCapacityRows = config.historyCapacityRows,
                outputProfile = config.outputProfile,
            ),
        )
    }

    private fun validateConfigV3(config: NativeTerminalConfigV3): NativeBridgeResult.Failure? {
        if (
            config.abiVersion != TERMLINK_TERMINAL_ABI_VERSION ||
            config.structSize != TERMLINK_CONFIG_V3_STRUCT_SIZE ||
            config.initialSourceSequence !in 0 until Long.MAX_VALUE ||
            config.reservedV1Compat0 != 0L ||
            config.reservedV1Compat1 != 0L ||
            config.historyCapacityRows !in 0..UNSIGNED_INT_MAX ||
            config.reservedV2Compat0 != 0L ||
            config.reservedV2Compat1 != 0L ||
            config.outputProfile !in NATIVE_OUTPUT_PROFILE_UTF8..
                NATIVE_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1 ||
            config.reserved0 != 0 ||
            config.reserved1 != 0L ||
            config.reserved2 != 0L
        ) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
        }

        val columns = config.columns.toUnsignedLong()
        val rows = config.rows.toUnsignedLong()
        if (columns == 0L || rows == 0L) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_DIMENSIONS.code)
        }
        if (
            columns > MAX_CONFIG_COLUMNS ||
            rows > MAX_CONFIG_ROWS ||
            columns * rows > MAX_CONFIG_CELLS
        ) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.LIMIT_EXCEEDED.code)
        }
        return null
    }

    override fun importCheckpoint(
        checkpoint: ByteArray,
    ): NativeBridgeResult<NativeBridgeTerminal> = synchronized(this) {
        forcedImportFailure?.let { return it }
        if (checkpoint.size !in setOf(CHECKPOINT_V1_BYTES, CHECKPOINT_V2_BYTES)) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.MALFORMED_SNAPSHOT.code)
        }
        val buffer = ByteBuffer.wrap(checkpoint)
        if (buffer.int != CHECKPOINT_MAGIC) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT.code)
        }
        val version = buffer.int
        if (
            version !in CHECKPOINT_V1_VERSION..CHECKPOINT_V2_VERSION ||
            version == CHECKPOINT_V1_VERSION && checkpoint.size != CHECKPOINT_V1_BYTES ||
            version == CHECKPOINT_V2_VERSION && checkpoint.size != CHECKPOINT_V2_BYTES
        ) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.INCOMPATIBLE_SNAPSHOT.code)
        }
        val nextSourceSequence = buffer.long
        val revision = buffer.long
        val columns = buffer.int
        val rows = buffer.int
        val logicalMicros = buffer.long
        val mouseTracking = FakeMouseTracking.fromCode(buffer.int)
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.MALFORMED_SNAPSHOT.code)
        val mouseEncoding = FakeMouseEncoding.fromCode(buffer.int)
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.MALFORMED_SNAPSHOT.code)
        val win32InputMode = when (buffer.int) {
            0 -> false
            1 -> true
            else -> return NativeBridgeResult.Failure(NativeTerminalStatus.MALFORMED_SNAPSHOT.code)
        }
        val historyCapacityRows = if (version >= CHECKPOINT_V2_VERSION) buffer.long else 0L
        val outputProfile = if (version >= CHECKPOINT_V2_VERSION) buffer.int else {
            NATIVE_OUTPUT_PROFILE_UTF8
        }
        if (
            nextSourceSequence !in 0 until Long.MAX_VALUE ||
            revision !in 0 until Long.MAX_VALUE ||
            revision != nextSourceSequence ||
            columns <= 0 ||
            rows <= 0 ||
            logicalMicros < 0 ||
            historyCapacityRows !in 0..UNSIGNED_INT_MAX ||
            outputProfile !in NATIVE_OUTPUT_PROFILE_UTF8..
                NATIVE_OUTPUT_PROFILE_UTF8_WITH_STANDALONE_C1
        ) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.MALFORMED_SNAPSHOT.code)
        }
        allocate(
            State(
                nextSourceSequence = nextSourceSequence,
                presentationRevision = revision,
                size = TerminalSize(columns, rows),
                logicalMicros = logicalMicros,
                mouseTracking = mouseTracking,
                mouseEncoding = mouseEncoding,
                win32InputMode = win32InputMode,
                historyCapacityRows = historyCapacityRows,
                outputProfile = outputProfile,
            ),
        )
    }

    override fun destroy(handle: NativeBridgeHandle): NativeBridgeResult<Unit> = synchronized(this) {
        destroyCalls += 1
        if (states.remove(handle) == null) {
            NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        } else {
            NativeBridgeResult.Success(Unit)
        }
    }

    override fun applyBatch(
        handle: NativeBridgeHandle,
        batch: NativeEventBatch,
    ): NativeBridgeResult<NativeBridgeApplyReceipt> = synchronized(this) {
        applyCalls += 1
        throwOnApply?.let { throw it }
        forcedApplyFailure?.let { return it }
        applyBatchCore(handle, batch)
    }

    override fun applyBatchWithHostEffectsV2(
        handle: NativeBridgeHandle,
        batch: NativeEventBatch,
    ): NativeBridgeResult<NativeBridgeApplyWithHostEffectsV2> = synchronized(this) {
        hostEffectV2Calls += 1
        lastHostEffectV2Batch = batch.copyForBridge()
        throwOnHostEffectV2?.let { throw it }
        forcedHostEffectV2Failure?.let { return it }
        when (val applied = applyBatchCore(handle, batch)) {
            is NativeBridgeResult.Failure -> applied
            is NativeBridgeResult.Success -> NativeBridgeResult.Success(
                forcedHostEffectV2Result ?: NativeBridgeApplyWithHostEffectsV2(
                    receipt = applied.value,
                    hostEffectFrame = fakeHostEffectFrameV2(),
                ),
            )
        }
    }

    private fun applyBatchCore(
        handle: NativeBridgeHandle,
        batch: NativeEventBatch,
    ): NativeBridgeResult<NativeBridgeApplyReceipt> {
        val state = states[handle]
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        if (state.failedClosed) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.FAILED_CLOSED.code)
        }
        if (batch.firstSourceSequence != state.nextSourceSequence) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.SEQUENCE_MISMATCH.code)
        }
        val candidate = state.copy()
        var candidateOutput = lastCommittedOutputBytes?.copyOf()
        batch.events.forEach { event ->
            when (event) {
                is NativeSourceEvent.Output -> {
                    val bytes = event.copyBytes()
                    candidate.consumeOutput(bytes)
                    candidateOutput = bytes
                }
                is NativeSourceEvent.Resize -> candidate.size = event.size
                is NativeSourceEvent.Advance -> {
                    candidate.logicalMicros = try {
                        Math.addExact(candidate.logicalMicros, event.micros)
                    } catch (_: ArithmeticException) {
                        return NativeBridgeResult.Failure(NativeTerminalStatus.TIME_OVERFLOW.code)
                    }
                }
                is NativeSourceEvent.Gap -> {
                    // GAP alone may latch fail-closed. No preceding candidate state is committed.
                    state.failedClosed = true
                    return NativeBridgeResult.Failure(NativeTerminalStatus.GAP.code)
                }
            }
        }
        candidate.nextSourceSequence = batch.nextSourceSequence
        candidate.presentationRevision = candidate.nextSourceSequence
        if (candidate.checkpointEligibleAfterNextSuccessfulBatch) {
            candidate.checkpointEligible = true
            candidate.checkpointEligibleAfterNextSuccessfulBatch = false
        }
        states[handle] = candidate
        lastCommittedOutputBytes = candidateOutput
        return NativeBridgeResult.Success(
            NativeBridgeApplyReceipt(
                nextSourceSequence = candidate.nextSourceSequence,
                presentationRevision = candidate.presentationRevision,
            ),
        )
    }

    override fun render(
        handle: NativeBridgeHandle,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeRenderFrame> = synchronized(this) {
        forcedRenderFailure?.let { return it }
        forcedRenderFrame?.let { return NativeBridgeResult.Success(it) }
        val state = states[handle]
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        val payload = fakeRenderFrameV1(
            nextSourceSequence = state.nextSourceSequence,
            presentationRevision = state.presentationRevision,
            size = state.size,
        )
        if (payload.size > request.maxPayloadBytes) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.LIMIT_EXCEEDED.code)
        }
        NativeBridgeResult.Success(
            NativeBridgeRenderFrame(payload = payload),
        )
    }

    override fun renderV3(
        handle: NativeBridgeHandle,
        request: NativeRenderRequest,
    ): NativeBridgeResult<NativeBridgeRenderFrame> = synchronized(this) {
        forcedRenderFailure?.let { return it }
        val state = states[handle]
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        val payload = forcedRenderFrameV3?.payload?.copyOf() ?: fakeRenderFrameV3(
            nextSourceSequence = state.nextSourceSequence,
            presentationRevision = state.presentationRevision,
            size = state.size,
        )
        if (payload.size > request.maxPayloadBytes) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.LIMIT_EXCEEDED.code)
        }
        NativeBridgeResult.Success(NativeBridgeRenderFrame(payload))
    }

    override fun encodeInputV1(
        handle: NativeBridgeHandle,
        input: NativeTerminalInputV1,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = synchronized(this) {
        inputV1Calls += 1
        lastInputV1 = input.copyForBridge()
        forcedInputV1Failure?.let { return it }
        forcedInputV1EncodedInput?.let {
            return NativeBridgeResult.Success(NativeBridgeEncodedInput(it.payload.copyOf()))
        }
        val state = states[handle]
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        if (state.failedClosed) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.FAILED_CLOSED.code)
        }
        validateInputV1(input)?.let { return it }
        val modifiers = TerminalInputModifiers(
            control = input.modifiers and NATIVE_INPUT_MODIFIER_CONTROL != 0,
            alt = input.modifiers and NATIVE_INPUT_MODIFIER_ALT != 0,
            shift = input.modifiers and NATIVE_INPUT_MODIFIER_SHIFT != 0,
            meta = input.modifiers and NATIVE_INPUT_MODIFIER_META != 0,
        )
        val semantic = when (input.kind) {
            NATIVE_INPUT_KIND_KEY -> if (input.key == NATIVE_INPUT_KEY_CHARACTER.toInt()) {
                TerminalInput.Text(String(Character.toChars(input.character)), modifiers)
            } else {
                TerminalInput.Key(fakeTerminalKey(input.key)!!, modifiers)
            }
            NATIVE_INPUT_KIND_PASTE -> TerminalInput.Paste(input.copyData())
            NATIVE_INPUT_KIND_FOCUS_GAINED,
            NATIVE_INPUT_KIND_FOCUS_LOST,
            -> return NativeBridgeResult.Success(NativeBridgeEncodedInput(byteArrayOf()))
            else -> error("validated input kind is outside Input V1")
        }
        NativeBridgeResult.Success(NativeBridgeEncodedInput(VtTerminalInputEncoder.encode(semantic)))
    }

    override fun encodeMouse(
        handle: NativeBridgeHandle,
        input: NativeMouseInputV1,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = synchronized(this) {
        mouseCalls += 1
        lastMouseInput = input.copy()
        throwOnMouse?.let { throw it }
        forcedMouseFailure?.let { return it }
        forcedMouseEncodedInput?.let { return NativeBridgeResult.Success(it) }
        val state = states[handle]
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        if (state.failedClosed) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.FAILED_CLOSED.code)
        }
        validateMouseInput(input, state.size)?.let { return it }
        if (!state.mouseTracking.reports(input)) {
            return NativeBridgeResult.Success(NativeBridgeEncodedInput(byteArrayOf()))
        }
        val payload = when (state.mouseEncoding) {
            FakeMouseEncoding.DEFAULT -> encodeDefaultMouse(input)
            FakeMouseEncoding.SGR -> encodeSgrMouse(input, input.cellColumn, input.cellRow)
            FakeMouseEncoding.SGR_PIXELS -> {
                if (input.positionFlags and NATIVE_MOUSE_POSITION_PIXEL == 0) {
                    return NativeBridgeResult.Failure(
                        NativeTerminalStatus.INVALID_ARGUMENT.code,
                        "mode 1016 requires a pixel position",
                    )
                }
                encodeSgrMouse(input, input.pixelX, input.pixelY)
            }
        }
        payload
    }

    override fun encodeInputV2(
        handle: NativeBridgeHandle,
        input: NativeWin32KeyInputV2,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = synchronized(this) {
        inputV2Calls += 1
        lastInputV2 = input.copy()
        throwOnInputV2?.let { throw it }
        forcedInputV2Failure?.let { return it }
        forcedInputV2EncodedInput?.let { return NativeBridgeResult.Success(it) }
        val state = states[handle]
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        if (state.failedClosed) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.FAILED_CLOSED.code)
        }
        validateWin32KeyInputV2(input)?.let { return it }
        if (!state.win32InputMode) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.UNSUPPORTED.code)
        }
        NativeBridgeResult.Success(
            NativeBridgeEncodedInput(
                (
                    "\u001b[${input.win32VirtualKey};${input.win32ScanCode};" +
                        "${input.win32UnicodeChar};${input.win32KeyDown};" +
                        "${input.win32ControlKeyState};${input.win32RepeatCount}_"
                    ).encodeToByteArray(),
            ),
        )
    }

    override fun encodeInputV3(
        handle: NativeBridgeHandle,
        input: NativeKittyKeyInputV3,
    ): NativeBridgeResult<NativeBridgeEncodedInput> = synchronized(this) {
        inputV3Calls += 1
        lastInputV3 = input.copyForBridge()
        throwOnInputV3?.let { throw it }
        forcedInputV3Failure?.let { return it }
        forcedInputV3EncodedInput?.let {
            return NativeBridgeResult.Success(NativeBridgeEncodedInput(it.payload.copyOf()))
        }
        val state = states[handle]
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        if (state.failedClosed) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.FAILED_CLOSED.code)
        }
        validateKittyKeyInputV3(input)?.let { return it }
        val associatedText = input.copyAssociatedText()
        val payload = if (associatedText.isNotEmpty()) {
            associatedText
        } else {
            (
                "kitty:${input.key}:${input.character}:${input.kittyEventType}:" +
                    input.kittyModifiers
                ).encodeToByteArray()
        }
        NativeBridgeResult.Success(NativeBridgeEncodedInput(payload.copyOf()))
    }

    override fun exportCheckpoint(
        handle: NativeBridgeHandle,
        maxPayloadBytes: Int,
    ): NativeBridgeResult<NativeBridgeCheckpoint> = synchronized(this) {
        forcedCheckpointFailure?.let { return it }
        forcedCheckpoint?.let { return NativeBridgeResult.Success(it) }
        val state = states[handle]
            ?: return NativeBridgeResult.Failure(NativeTerminalStatus.NULL_POINTER.code)
        if (!state.checkpointEligible) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.SNAPSHOT_NOT_ELIGIBLE.code)
        }
        val payload = encodeCheckpoint(state)
        if (payload.size > maxPayloadBytes) {
            return NativeBridgeResult.Failure(NativeTerminalStatus.LIMIT_EXCEEDED.code)
        }
        NativeBridgeResult.Success(
            NativeBridgeCheckpoint(
                nextSourceSequence = state.nextSourceSequence,
                presentationRevision = state.presentationRevision,
                payload = payload,
            ),
        )
    }

    private fun allocate(state: State): NativeBridgeResult<NativeBridgeTerminal> {
        val handle = NativeBridgeHandle(nextHandle++)
        states[handle] = state
        return NativeBridgeResult.Success(
            NativeBridgeTerminal(
                handle = handle,
                nextSourceSequence = state.nextSourceSequence,
                presentationRevision = state.presentationRevision,
            ),
        )
    }

    private fun encodeCheckpoint(state: State): ByteArray = ByteBuffer.allocate(CHECKPOINT_V2_BYTES)
        .putInt(CHECKPOINT_MAGIC)
        .putInt(CHECKPOINT_V2_VERSION)
        .putLong(state.nextSourceSequence)
        .putLong(state.presentationRevision)
        .putInt(state.size.columns)
        .putInt(state.size.rows)
        .putLong(state.logicalMicros)
        .putInt(state.mouseTracking.code)
        .putInt(state.mouseEncoding.code)
        .putInt(if (state.win32InputMode) 1 else 0)
        .putLong(state.historyCapacityRows)
        .putInt(state.outputProfile)
        .array()

    fun markCheckpointIneligibleUntilNextSuccessfulBatch() = synchronized(this) {
        val state = checkNotNull(states.values.singleOrNull()) {
            "fixture helper requires exactly one live terminal"
        }
        state.checkpointEligible = false
        state.checkpointEligibleAfterNextSuccessfulBatch = true
    }

    fun onlyStateForTest(): FakeNativeState = synchronized(this) {
        val state = checkNotNull(states.values.singleOrNull()) {
            "fixture helper requires exactly one live terminal"
        }
        FakeNativeState(
            nextSourceSequence = state.nextSourceSequence,
            presentationRevision = state.presentationRevision,
            size = state.size,
            logicalMicros = state.logicalMicros,
            failedClosed = state.failedClosed,
            checkpointEligible = state.checkpointEligible,
            mouseTracking = state.mouseTracking.code,
            mouseEncoding = state.mouseEncoding.code,
            win32InputMode = state.win32InputMode,
            historyCapacityRows = state.historyCapacityRows,
            outputProfile = state.outputProfile,
        )
    }

    private data class State(
        var nextSourceSequence: Long = 0,
        var presentationRevision: Long,
        var size: TerminalSize,
        var logicalMicros: Long = 0,
        var failedClosed: Boolean = false,
        var checkpointEligible: Boolean = true,
        var checkpointEligibleAfterNextSuccessfulBatch: Boolean = false,
        var mouseTracking: FakeMouseTracking = FakeMouseTracking.NONE,
        var mouseEncoding: FakeMouseEncoding = FakeMouseEncoding.DEFAULT,
        var win32InputMode: Boolean = false,
        var historyCapacityRows: Long = 0,
        var outputProfile: Int = NATIVE_OUTPUT_PROFILE_UTF8,
        var modeParserState: FakePrivateModeParserState = FakePrivateModeParserState.TEXT,
        var privateModeParameters: String = "",
    ) {
        fun consumeOutput(bytes: ByteArray) {
            bytes.forEach { byte ->
                val value = byte.toInt() and 0xff
                modeParserState = when (modeParserState) {
                    FakePrivateModeParserState.TEXT -> if (value == ESCAPE) {
                        FakePrivateModeParserState.ESCAPE
                    } else {
                        FakePrivateModeParserState.TEXT
                    }
                    FakePrivateModeParserState.ESCAPE -> when (value) {
                        ESCAPE -> FakePrivateModeParserState.ESCAPE
                        '['.code -> FakePrivateModeParserState.CSI
                        else -> FakePrivateModeParserState.TEXT
                    }
                    FakePrivateModeParserState.CSI -> when (value) {
                        ESCAPE -> FakePrivateModeParserState.ESCAPE
                        '?'.code -> {
                            privateModeParameters = ""
                            FakePrivateModeParserState.PRIVATE_PARAMETERS
                        }
                        else -> FakePrivateModeParserState.TEXT
                    }
                    FakePrivateModeParserState.PRIVATE_PARAMETERS -> when {
                        value in '0'.code..'9'.code || value == ';'.code -> {
                            if (privateModeParameters.length >= MAX_MODE_PARAMETERS) {
                                privateModeParameters = ""
                                FakePrivateModeParserState.TEXT
                            } else {
                                privateModeParameters += value.toChar()
                                FakePrivateModeParserState.PRIVATE_PARAMETERS
                            }
                        }
                        value == 'h'.code || value == 'l'.code -> {
                            applyPrivateModes(enabled = value == 'h'.code)
                            privateModeParameters = ""
                            FakePrivateModeParserState.TEXT
                        }
                        value == ESCAPE -> {
                            privateModeParameters = ""
                            FakePrivateModeParserState.ESCAPE
                        }
                        else -> {
                            privateModeParameters = ""
                            FakePrivateModeParserState.TEXT
                        }
                    }
                }
            }
        }

        private fun applyPrivateModes(enabled: Boolean) {
            privateModeParameters.split(';').mapNotNull(String::toIntOrNull).forEach { parameter ->
                FakeMouseTracking.fromParameter(parameter)?.let { selected ->
                    if (enabled) {
                        mouseTracking = selected
                    } else if (mouseTracking == selected) {
                        mouseTracking = FakeMouseTracking.NONE
                    }
                }
                FakeMouseEncoding.fromParameter(parameter)?.let { selected ->
                    if (enabled) {
                        mouseEncoding = selected
                    } else if (mouseEncoding == selected) {
                        mouseEncoding = FakeMouseEncoding.DEFAULT
                    }
                }
                if (parameter == WIN32_INPUT_MODE) {
                    win32InputMode = enabled
                }
            }
        }
    }

    private companion object {
        const val CHECKPOINT_MAGIC = 0x544C464B
        const val CHECKPOINT_V1_VERSION = 1
        const val CHECKPOINT_V2_VERSION = 2
        const val CHECKPOINT_V1_BYTES = 52
        const val CHECKPOINT_V2_BYTES = 64
        const val ESCAPE = 0x1b
        const val MAX_MODE_PARAMETERS = 64
        const val WIN32_INPUT_MODE = 9001
        const val MAX_CONFIG_COLUMNS = 512L
        const val MAX_CONFIG_ROWS = 512L
        const val MAX_CONFIG_CELLS = 262_144L
    }
}

internal enum class FakeMouseTracking(val code: Int) {
    NONE(0),
    X10(9),
    NORMAL(1000),
    BUTTON_EVENT(1002),
    ANY_EVENT(1003),
    ;

    fun reports(input: NativeMouseInputV1): Boolean = when (this) {
        NONE -> false
        X10 -> input.action == NATIVE_MOUSE_ACTION_PRESS ||
            input.action == NATIVE_MOUSE_ACTION_WHEEL
        NORMAL -> input.action != NATIVE_MOUSE_ACTION_MOVE
        BUTTON_EVENT -> input.action != NATIVE_MOUSE_ACTION_MOVE || input.buttonsHeld != 0
        ANY_EVENT -> true
    }

    companion object {
        fun fromCode(code: Int): FakeMouseTracking? = entries.firstOrNull { it.code == code }

        fun fromParameter(parameter: Int): FakeMouseTracking? = entries.firstOrNull {
            it !== NONE && it.code == parameter
        }
    }
}

internal enum class FakeMouseEncoding(val code: Int) {
    DEFAULT(0),
    SGR(1006),
    SGR_PIXELS(1016),
    ;

    companion object {
        fun fromCode(code: Int): FakeMouseEncoding? = entries.firstOrNull { it.code == code }

        fun fromParameter(parameter: Int): FakeMouseEncoding? = entries.firstOrNull {
            it !== DEFAULT && it.code == parameter
        }
    }
}

private enum class FakePrivateModeParserState { TEXT, ESCAPE, CSI, PRIVATE_PARAMETERS }

private fun validateKittyKeyInputV3(
    input: NativeKittyKeyInputV3,
): NativeBridgeResult.Failure? {
    if (
        input.abiVersion != TERMLINK_TERMINAL_ABI_VERSION ||
        input.structSize.toUnsignedLong() < TERMLINK_INPUT_V3_STRUCT_SIZE.toLong() ||
        input.kind != NATIVE_INPUT_KIND_KITTY_KEY ||
        input.key !in 0..UNSIGNED_INT_MAX ||
        input.modifiers != 0 ||
        input.data != 0L ||
        input.dataLength != 0L ||
        input.reservedV1Compat0 != 0L ||
        input.reservedV1Compat1 != 0L ||
        input.win32VirtualKey != 0 ||
        input.win32ScanCode != 0 ||
        input.win32UnicodeChar != 0 ||
        input.win32KeyDown != 0 ||
        input.win32ControlKeyState != 0L ||
        input.win32RepeatCount != 0 ||
        input.reservedV2Prefix0 != 0L ||
        input.reservedV2Prefix1 != 0L ||
        input.kittyEventType !in NATIVE_KITTY_EVENT_PRESS..NATIVE_KITTY_EVENT_RELEASE ||
        input.kittyModifiers and NATIVE_KITTY_MODIFIERS_ALL.inv() != 0 ||
        input.associatedTextLength != input.associatedTextByteCount.toLong() ||
        input.associatedTextLength !in 0..MAX_KITTY_ASSOCIATED_TEXT_BYTES.toLong() ||
        input.reservedV3Tail0 != 0L ||
        input.reservedV3Tail1 != 0L ||
        input.reservedV3Tail2 != 0L ||
        input.reservedV3Tail3 != 0L
    ) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }

    val associatedText = decodeKittyUtf8(input.copyAssociatedText())
        ?: return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    val associatedScalars = associatedText.codePoints().toArray()
    if (
        associatedScalars.size > MAX_KITTY_ASSOCIATED_TEXT_SCALARS ||
        associatedScalars.any { !isKittyTextScalar(it) } ||
        input.kittyShiftedKey != 0 && !isKittyTextScalar(input.kittyShiftedKey) ||
        input.kittyBaseLayoutKey != 0 && !isKittyTextScalar(input.kittyBaseLayoutKey) ||
        input.kittyShiftedKey != 0 &&
        input.kittyModifiers and NATIVE_KITTY_MODIFIER_SHIFT == 0 ||
        input.kittyEventType == NATIVE_KITTY_EVENT_RELEASE && associatedText.isNotEmpty()
    ) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }

    val characterIdentity = input.key == NATIVE_INPUT_KEY_CHARACTER
    val unidentifiedIdentity = input.key == NATIVE_INPUT_KITTY_KEY_UNIDENTIFIED_TEXT
    val functionalIdentity = TerminalKittyFunctionalKey.entries.any {
        nativeKittyFunctionalKey(it) == input.key
    }
    if (
        !characterIdentity && !unidentifiedIdentity && !functionalIdentity ||
        characterIdentity && !isKittyTextScalar(input.character) ||
        !characterIdentity && input.character != 0 ||
        !characterIdentity && (input.kittyShiftedKey != 0 || input.kittyBaseLayoutKey != 0) ||
        unidentifiedIdentity && input.kittyEventType != NATIVE_KITTY_EVENT_PRESS ||
        unidentifiedIdentity && associatedText.isEmpty()
    ) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }
    return null
}

private fun decodeKittyUtf8(bytes: ByteArray): String? = try {
    StandardCharsets.UTF_8.newDecoder()
        .onMalformedInput(CodingErrorAction.REPORT)
        .onUnmappableCharacter(CodingErrorAction.REPORT)
        .decode(ByteBuffer.wrap(bytes))
        .toString()
} catch (_: CharacterCodingException) {
    null
}

private fun isKittyTextScalar(value: Int): Boolean =
    value in 0..0x10ffff && value !in 0xd800..0xdfff &&
        value !in 0x00..0x1f && value !in 0x7f..0x9f

private fun validateWin32KeyInputV2(
    input: NativeWin32KeyInputV2,
): NativeBridgeResult.Failure? {
    if (
        input.abiVersion != TERMLINK_TERMINAL_ABI_VERSION ||
        input.structSize.toUnsignedLong() < TERMLINK_INPUT_V2_STRUCT_SIZE.toLong() ||
        input.kind != NATIVE_INPUT_KIND_WIN32_KEY ||
        input.key != 0 ||
        input.modifiers != 0 ||
        input.character != 0 ||
        input.data != 0L ||
        input.dataLength != 0L ||
        input.reservedV1Compat0 != 0L ||
        input.reservedV1Compat1 != 0L ||
        input.win32VirtualKey !in 0..UNSIGNED_SHORT_MAX ||
        input.win32ScanCode !in 0..UNSIGNED_SHORT_MAX ||
        input.win32UnicodeChar !in 0..UNSIGNED_SHORT_MAX ||
        input.win32KeyDown !in 0..1 ||
        input.win32ControlKeyState !in 0..UNSIGNED_INT_MAX ||
        input.win32RepeatCount !in 0..UNSIGNED_SHORT_MAX ||
        input.reserved0 != 0L ||
        input.reserved1 != 0L
    ) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }
    return null
}

private fun validateMouseInput(
    input: NativeMouseInputV1,
    size: TerminalSize,
): NativeBridgeResult.Failure? {
    if (
        input.abiVersion != TERMLINK_TERMINAL_ABI_VERSION ||
        input.structSize.toUnsignedLong() < TERMLINK_MOUSE_INPUT_V1_STRUCT_SIZE.toLong() ||
        input.reserved0 != 0L ||
        input.reserved1 != 0L ||
        input.modifiers and NATIVE_INPUT_MODIFIERS_ALL.inv() != 0 ||
        input.buttonsHeld and NATIVE_MOUSE_BUTTONS_HELD_ALL.inv() != 0 ||
        input.positionFlags and NATIVE_MOUSE_POSITION_FLAGS_ALL.inv() != 0
    ) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }

    val actedButton = when (input.action) {
        NATIVE_MOUSE_ACTION_PRESS,
        NATIVE_MOUSE_ACTION_RELEASE,
        -> {
            if (input.wheel != NATIVE_MOUSE_WHEEL_NONE || !isNativeMouseButton(input.button)) {
                return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
            }
            input.button
        }
        NATIVE_MOUSE_ACTION_MOVE -> {
            if (
                input.button != NATIVE_MOUSE_BUTTON_NONE ||
                input.wheel != NATIVE_MOUSE_WHEEL_NONE
            ) {
                return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
            }
            NATIVE_MOUSE_BUTTON_NONE
        }
        NATIVE_MOUSE_ACTION_WHEEL -> {
            if (
                input.button != NATIVE_MOUSE_BUTTON_NONE ||
                input.wheel !in NATIVE_MOUSE_WHEEL_UP..NATIVE_MOUSE_WHEEL_RIGHT
            ) {
                return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
            }
            NATIVE_MOUSE_BUTTON_NONE
        }
        else -> return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }

    if (
        input.action == NATIVE_MOUSE_ACTION_PRESS && !input.isHeld(actedButton) ||
        input.action == NATIVE_MOUSE_ACTION_RELEASE && input.isHeld(actedButton)
    ) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }
    if (
        input.cellColumn.toUnsignedLong() >= size.columns.toLong() ||
        input.cellRow.toUnsignedLong() >= size.rows.toLong()
    ) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }

    val hasPixel = input.positionFlags and NATIVE_MOUSE_POSITION_PIXEL != 0
    if (!hasPixel && (input.pixelX != 0 || input.pixelY != 0)) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }
    if (hasPixel && (input.pixelX == -1 || input.pixelY == -1)) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.LIMIT_EXCEEDED.code)
    }
    return null
}

private fun encodeDefaultMouse(
    input: NativeMouseInputV1,
): NativeBridgeResult<NativeBridgeEncodedInput> {
    val column = input.cellColumn.toUnsignedLong()
    val row = input.cellRow.toUnsignedLong()
    if (column > MAX_DEFAULT_MOUSE_CELL || row > MAX_DEFAULT_MOUSE_CELL) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.LIMIT_EXCEEDED.code)
    }
    return NativeBridgeResult.Success(
        NativeBridgeEncodedInput(
            byteArrayOf(
                0x1b,
                '['.code.toByte(),
                'M'.code.toByte(),
                (32 + mouseReportCode(input, sgrRelease = false)).toByte(),
                (column + 33).toByte(),
                (row + 33).toByte(),
            ),
        ),
    )
}

private fun encodeSgrMouse(
    input: NativeMouseInputV1,
    rawColumn: Int,
    rawRow: Int,
): NativeBridgeResult<NativeBridgeEncodedInput> {
    val column = rawColumn.toUnsignedLong()
    val row = rawRow.toUnsignedLong()
    if (column == UNSIGNED_INT_MAX || row == UNSIGNED_INT_MAX) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.LIMIT_EXCEEDED.code)
    }
    val final = if (input.action == NATIVE_MOUSE_ACTION_RELEASE) 'm' else 'M'
    val sequence = "\u001b[<${mouseReportCode(input, sgrRelease = true)};" +
        "${column + 1};${row + 1}$final"
    return NativeBridgeResult.Success(
        NativeBridgeEncodedInput(sequence.encodeToByteArray()),
    )
}

private fun mouseReportCode(input: NativeMouseInputV1, sgrRelease: Boolean): Int {
    val base = when (input.action) {
        NATIVE_MOUSE_ACTION_PRESS -> input.button - 1
        NATIVE_MOUSE_ACTION_RELEASE -> if (sgrRelease) input.button - 1 else 3
        NATIVE_MOUSE_ACTION_MOVE -> 32 + when {
            input.buttonsHeld and NATIVE_MOUSE_BUTTON_HELD_LEFT != 0 -> 0
            input.buttonsHeld and NATIVE_MOUSE_BUTTON_HELD_MIDDLE != 0 -> 1
            input.buttonsHeld and NATIVE_MOUSE_BUTTON_HELD_RIGHT != 0 -> 2
            else -> 3
        }
        NATIVE_MOUSE_ACTION_WHEEL -> 63 + input.wheel
        else -> error("validated mouse action is outside the ABI")
    }
    val modifier =
        (if (input.modifiers and NATIVE_INPUT_MODIFIER_SHIFT != 0) 4 else 0) +
            (if (
                input.modifiers and
                    (NATIVE_INPUT_MODIFIER_ALT or NATIVE_INPUT_MODIFIER_META) != 0
            ) 8 else 0) +
            (if (input.modifiers and NATIVE_INPUT_MODIFIER_CONTROL != 0) 16 else 0)
    return base + modifier
}

private fun NativeMouseInputV1.isHeld(button: Int): Boolean = when (button) {
    NATIVE_MOUSE_BUTTON_LEFT -> buttonsHeld and NATIVE_MOUSE_BUTTON_HELD_LEFT != 0
    NATIVE_MOUSE_BUTTON_MIDDLE -> buttonsHeld and NATIVE_MOUSE_BUTTON_HELD_MIDDLE != 0
    NATIVE_MOUSE_BUTTON_RIGHT -> buttonsHeld and NATIVE_MOUSE_BUTTON_HELD_RIGHT != 0
    else -> false
}

private fun isNativeMouseButton(button: Int): Boolean =
    button in NATIVE_MOUSE_BUTTON_LEFT..NATIVE_MOUSE_BUTTON_RIGHT

private fun validateInputV1(input: NativeTerminalInputV1): NativeBridgeResult.Failure? {
    if (
        input.abiVersion != TERMLINK_TERMINAL_ABI_VERSION ||
        input.structSize != TERMLINK_INPUT_V1_STRUCT_SIZE ||
        input.modifiers and NATIVE_INPUT_MODIFIERS_ALL.inv() != 0 ||
        input.dataLength != input.dataByteCount.toLong() ||
        input.reserved0 != 0L ||
        input.reserved1 != 0L
    ) {
        return NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }
    val valid = when (input.kind) {
        NATIVE_INPUT_KIND_KEY -> {
            input.dataByteCount == 0 &&
                if (input.key == NATIVE_INPUT_KEY_CHARACTER.toInt()) {
                    Character.isValidCodePoint(input.character) &&
                        input.character !in
                        Character.MIN_SURROGATE.code..Character.MAX_SURROGATE.code
                } else {
                    input.character == 0 && fakeTerminalKey(input.key) != null
                }
        }
        NATIVE_INPUT_KIND_PASTE -> input.key == 0 && input.modifiers == 0 &&
            input.character == 0 && input.dataByteCount > 0
        NATIVE_INPUT_KIND_FOCUS_GAINED,
        NATIVE_INPUT_KIND_FOCUS_LOST,
        -> input.key == 0 && input.modifiers == 0 && input.character == 0 &&
            input.dataByteCount == 0
        else -> false
    }
    return if (valid) null else {
        NativeBridgeResult.Failure(NativeTerminalStatus.INVALID_ARGUMENT.code)
    }
}

private fun fakeTerminalKey(key: Int): TerminalKey? = when (key) {
    2 -> TerminalKey.ENTER
    3 -> TerminalKey.BACKSPACE
    4 -> TerminalKey.TAB
    5 -> TerminalKey.ESCAPE
    6 -> TerminalKey.DELETE
    7 -> TerminalKey.INSERT
    8 -> TerminalKey.HOME
    9 -> TerminalKey.END
    10 -> TerminalKey.PAGE_UP
    11 -> TerminalKey.PAGE_DOWN
    12 -> TerminalKey.ARROW_UP
    13 -> TerminalKey.ARROW_DOWN
    14 -> TerminalKey.ARROW_LEFT
    15 -> TerminalKey.ARROW_RIGHT
    16 -> TerminalKey.F1
    17 -> TerminalKey.F2
    18 -> TerminalKey.F3
    19 -> TerminalKey.F4
    20 -> TerminalKey.F5
    21 -> TerminalKey.F6
    22 -> TerminalKey.F7
    23 -> TerminalKey.F8
    24 -> TerminalKey.F9
    25 -> TerminalKey.F10
    26 -> TerminalKey.F11
    27 -> TerminalKey.F12
    else -> null
}

private fun Int.toUnsignedLong(): Long = Integer.toUnsignedLong(this)

private const val NATIVE_INPUT_MODIFIERS_ALL =
    NATIVE_INPUT_MODIFIER_SHIFT or NATIVE_INPUT_MODIFIER_ALT or
        NATIVE_INPUT_MODIFIER_CONTROL or NATIVE_INPUT_MODIFIER_META
private const val NATIVE_KITTY_MODIFIERS_ALL =
    NATIVE_KITTY_MODIFIER_SHIFT or NATIVE_KITTY_MODIFIER_ALT or
        NATIVE_KITTY_MODIFIER_CONTROL or NATIVE_KITTY_MODIFIER_SUPER or
        NATIVE_KITTY_MODIFIER_HYPER or NATIVE_KITTY_MODIFIER_META or
        NATIVE_KITTY_MODIFIER_CAPS_LOCK or NATIVE_KITTY_MODIFIER_NUM_LOCK
private const val MAX_KITTY_ASSOCIATED_TEXT_BYTES = 4_096
private const val MAX_KITTY_ASSOCIATED_TEXT_SCALARS = 1_024
private const val NATIVE_MOUSE_BUTTONS_HELD_ALL =
    NATIVE_MOUSE_BUTTON_HELD_LEFT or NATIVE_MOUSE_BUTTON_HELD_MIDDLE or
        NATIVE_MOUSE_BUTTON_HELD_RIGHT
private const val NATIVE_MOUSE_POSITION_FLAGS_ALL = NATIVE_MOUSE_POSITION_PIXEL
private const val UNSIGNED_SHORT_MAX = 0xffff
private const val MAX_DEFAULT_MOUSE_CELL = 222L
private const val UNSIGNED_INT_MAX = 0xffff_ffffL

internal data class FakeNativeState(
    val nextSourceSequence: Long,
    val presentationRevision: Long,
    val size: TerminalSize,
    val logicalMicros: Long,
    val failedClosed: Boolean,
    val checkpointEligible: Boolean,
    val mouseTracking: Int = FakeMouseTracking.NONE.code,
    val mouseEncoding: Int = FakeMouseEncoding.DEFAULT.code,
    val win32InputMode: Boolean = false,
    val historyCapacityRows: Long = 0,
    val outputProfile: Int = NATIVE_OUTPUT_PROFILE_UTF8,
)

internal data class FakeHostEffectV2Record(
    val sourceSequence: Long,
    val sourceOffsetEnd: Int,
    val ordinal: Int,
    val payload: ByteArray,
)

internal fun fakeHostEffectFrameV2(
    records: List<FakeHostEffectV2Record> = emptyList(),
): ByteArray {
    val payloadOffset = FAKE_HOST_EFFECT_HEADER_BYTES + records.size * FAKE_HOST_EFFECT_RECORD_BYTES
    val totalLength = payloadOffset + records.sumOf { it.payload.size }
    val frame = ByteArray(totalLength)
    val writer = ByteBuffer.wrap(frame).order(ByteOrder.LITTLE_ENDIAN)
    FAKE_HOST_EFFECT_V2_MAGIC.copyInto(frame)
    writer.putShort(8, 2.toShort())
    writer.putShort(10, 0.toShort())
    writer.putShort(12, FAKE_HOST_EFFECT_HEADER_BYTES.toShort())
    writer.putShort(14, FAKE_HOST_EFFECT_RECORD_BYTES.toShort())
    writer.putInt(16, records.size)
    writer.putInt(20, frame.size)
    writer.putLong(24, FAKE_HOST_EFFECT_HEADER_BYTES.toLong())
    writer.putLong(32, payloadOffset.toLong())
    writer.putLong(40, 1)
    writer.putLong(48, 0)
    var nextPayloadOffset = payloadOffset
    records.forEachIndexed { index, record ->
        val recordOffset = FAKE_HOST_EFFECT_HEADER_BYTES + index * FAKE_HOST_EFFECT_RECORD_BYTES
        writer.putInt(recordOffset, 1)
        writer.putInt(recordOffset + 4, 0)
        writer.putLong(recordOffset + 8, record.sourceSequence)
        writer.putInt(recordOffset + 16, record.sourceOffsetEnd)
        writer.putInt(recordOffset + 20, record.ordinal)
        writer.putLong(recordOffset + 24, nextPayloadOffset.toLong())
        writer.putInt(recordOffset + 32, record.payload.size)
        writer.putInt(recordOffset + 36, 0)
        record.payload.copyInto(frame, nextPayloadOffset)
        nextPayloadOffset += record.payload.size
    }
    writer.putInt(56, fakeCrc32c(frame, FAKE_HOST_EFFECT_HEADER_BYTES, frame.size))
    writer.putInt(60, fakeCrc32c(frame, 0, 60))
    return frame
}

internal fun fakeRenderFrameV1(
    nextSourceSequence: Long,
    presentationRevision: Long = nextSourceSequence,
    size: TerminalSize,
    reverseVideo: Boolean = false,
    conceal: Boolean = false,
    strikethrough: Boolean = false,
): ByteArray {
    require(nextSourceSequence >= 0)
    require(presentationRevision >= 0)
    val cellCount = Math.multiplyExact(size.columns, size.rows)
    val grapheme = ByteBuffer.allocate(9).order(ByteOrder.LITTLE_ENDIAN)
        .putInt(8)
        .putShort(1.toShort())
        .put(1.toByte())
        .put(0.toByte())
        .put(' '.code.toByte())
        .array()
    val renditionFlags =
        (if (conceal) FAKE_RENDITION_CONCEAL else 0) or
            (if (strikethrough) FAKE_RENDITION_STRIKETHROUGH else 0)
    val rendition = ByteBuffer.allocate(16).order(ByteOrder.LITTLE_ENDIAN)
        .putInt(renditionFlags)
        .putInt(0)
        .putInt(0)
        .putInt(0)
        .array()
    val palette = ByteBuffer.allocate(259 * 4).order(ByteOrder.LITTLE_ENDIAN).apply {
        repeat(259) { putInt(-1) }
    }.array()
    val rows = ByteBuffer.allocate(8 + size.rows * 8 + cellCount * 12)
        .order(ByteOrder.LITTLE_ENDIAN)
    rows.putInt(8).putInt(12)
    repeat(size.rows) { rows.putInt(0).putInt(0) }
    repeat(cellCount) { rows.putInt(1).putInt(0).putInt(0) }

    val sections = listOf(
        FakeRenderSection(1, 8, grapheme),
        FakeRenderSection(1, 16, rendition),
        FakeRenderSection(259, 4, palette),
        FakeRenderSection(0, 16, byteArrayOf()),
        FakeRenderSection(cellCount, 0, rows.array()),
        FakeRenderSection(0, 0, byteArrayOf()),
    )
    var total = FAKE_RENDER_ENVELOPE_BYTES
    val offsets = sections.map { section ->
        total = fakeAlign8(total)
        total.also { total = fakeAlign8(Math.addExact(total, section.bytes.size)) }
    }
    val frame = ByteArray(total)
    val writer = ByteBuffer.wrap(frame).order(ByteOrder.LITTLE_ENDIAN)
    writer.put(FAKE_RENDER_MAGIC)
    writer.putShort(8, 1.toShort())
    writer.putShort(10, 0.toShort())
    writer.putShort(12, FAKE_RENDER_HEADER_BYTES.toShort())
    writer.putShort(14, FAKE_RENDER_DIRECTORY_ENTRY_BYTES.toShort())
    writer.putShort(16, FAKE_RENDER_SECTION_COUNT.toShort())
    writer.putShort(
        18,
        (if (reverseVideo) FAKE_FRAME_REVERSE_VIDEO else 0).toShort(),
    )
    writer.putInt(20, frame.size)
    writer.putLong(24, presentationRevision)
    writer.putLong(32, nextSourceSequence)
    writer.putInt(40, size.columns)
    writer.putInt(44, size.rows)
    writer.putInt(48, 0)
    writer.putInt(52, 0)
    writer.putInt(56, 1)
    writer.putInt(60, 1)
    writer.putInt(64, FAKE_RENDER_HEADER_BYTES)
    writer.putLong(72, 0x1f)
    sections.forEachIndexed { index, section ->
        val directory = FAKE_RENDER_HEADER_BYTES + index * FAKE_RENDER_DIRECTORY_ENTRY_BYTES
        writer.putInt(directory, index + 1)
        writer.putShort(directory + 4, 1.toShort())
        writer.putShort(directory + 6, 1.toShort())
        writer.putInt(directory + 8, offsets[index])
        writer.putInt(directory + 12, section.bytes.size)
        writer.putInt(directory + 16, section.count)
        writer.putInt(directory + 20, section.recordSize)
        section.bytes.copyInto(frame, offsets[index])
    }
    writer.putInt(88, fakeCrc32c(frame, FAKE_RENDER_HEADER_BYTES, frame.size))
    writer.putInt(92, fakeCrc32c(frame, 0, 92))
    return frame
}

internal fun fakeRenderFrameV3(
    nextSourceSequence: Long,
    presentationRevision: Long = nextSourceSequence,
    size: TerminalSize,
): ByteArray = fakeRenderFrameV1(
    nextSourceSequence = nextSourceSequence,
    presentationRevision = presentationRevision,
    size = size,
).also { frame ->
    frame[6] = '3'.code.toByte()
    val writer = ByteBuffer.wrap(frame).order(ByteOrder.LITTLE_ENDIAN)
    writer.putShort(8, 3.toShort())
    writer.putLong(72, 0x7f)
    val renditionDirectory = FAKE_RENDER_HEADER_BYTES + FAKE_RENDER_DIRECTORY_ENTRY_BYTES
    writer.putShort(renditionDirectory + 4, 2.toShort())
    writer.putInt(88, fakeCrc32c(frame, FAKE_RENDER_HEADER_BYTES, frame.size))
    writer.putInt(92, fakeCrc32c(frame, 0, 92))
}

private data class FakeRenderSection(
    val count: Int,
    val recordSize: Int,
    val bytes: ByteArray,
)

private fun fakeAlign8(value: Int): Int = Math.addExact(value, 7) and -8

private fun fakeCrc32c(bytes: ByteArray, start: Int, end: Int): Int = CRC32C().run {
    update(bytes, start, end - start)
    value.toInt()
}

private const val FAKE_RENDER_HEADER_BYTES = 96
private const val FAKE_RENDER_DIRECTORY_ENTRY_BYTES = 24
private const val FAKE_RENDER_SECTION_COUNT = 6
private const val FAKE_RENDER_ENVELOPE_BYTES =
    FAKE_RENDER_HEADER_BYTES + FAKE_RENDER_SECTION_COUNT * FAKE_RENDER_DIRECTORY_ENTRY_BYTES
private const val FAKE_FRAME_REVERSE_VIDEO = 1 shl 1
private const val FAKE_RENDITION_CONCEAL = 1 shl 5
private const val FAKE_RENDITION_STRIKETHROUGH = 1 shl 6
private const val FAKE_HOST_EFFECT_HEADER_BYTES = 64
private const val FAKE_HOST_EFFECT_RECORD_BYTES = 40
private val FAKE_HOST_EFFECT_V2_MAGIC = byteArrayOf(
    'T'.code.toByte(),
    'L'.code.toByte(),
    'H'.code.toByte(),
    'E'.code.toByte(),
    'F'.code.toByte(),
    '2'.code.toByte(),
    0,
    0,
)
private val FAKE_RENDER_MAGIC = byteArrayOf(
    'T'.code.toByte(),
    'L'.code.toByte(),
    'R'.code.toByte(),
    'F'.code.toByte(),
    'R'.code.toByte(),
    'M'.code.toByte(),
    '1'.code.toByte(),
    0,
)
