package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalWidthProfile
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith

class NativeStateDeltaTest {
    private val context = ByteArray(32).also { it[0] = 1 }

    @Test
    fun jniMethodsMatchTheVersionedByteArrayContract() {
        val methods = JniNativeMethods::class.java.declaredMethods.associateBy { it.name }
        assertEquals(listOf(ByteArray::class.java, ByteArray::class.java, ByteArray::class.java,
            Int::class.javaPrimitiveType, Long::class.javaPrimitiveType, Array<ByteArray?>::class.java),
            methods.getValue("deltaExportV1").parameterTypes.toList())
        assertEquals(listOf(ByteArray::class.java, ByteArray::class.java, Int::class.javaPrimitiveType,
            ByteArray::class.java, Long::class.javaPrimitiveType, LongArray::class.java,
            Array<ByteArray?>::class.java, ByteArray::class.java),
            methods.getValue("deltaApplyV1").parameterTypes.toList())
    }

    @Test
    fun rejectsWrongProfileAndBoundsBeforeEnteringNative() {
        val bridge = RecordingBridge()
        val codec = NativeStateDelta(bridge)
        val snapshot = graphicsSnapshot()
        val legacy = snapshot.copyOf().also { it[10] = 21 }
        assertEquals(NativeTerminalStatus.INCOMPATIBLE_DELTA,
            assertFailsWith<NativeTerminalException> { codec.export(legacy, snapshot, context) }.status)
        assertFailsWith<IllegalArgumentException> { codec.export(snapshot, snapshot, ByteArray(32)) }
        assertFailsWith<IllegalArgumentException> { codec.export(snapshot, snapshot, context, 0) }
        assertFailsWith<IllegalArgumentException> { codec.apply(snapshot, context, ByteArray(4), 3) }
        assertEquals(0, bridge.calls)
    }

    @Test
    fun classifiesFullResyncAndOldNativeWithoutChangingInput() {
        val snapshot = graphicsSnapshot()
        val bridge = RecordingBridge().also { it.result = NativeBridgeResult.Failure(NativeTerminalStatus.NEEDS_FULL_SNAPSHOT.code) }
        val codec = NativeStateDelta(bridge)
        assertEquals(NativeTerminalStatus.NEEDS_FULL_SNAPSHOT,
            assertFailsWith<NativeTerminalException> { codec.export(snapshot, snapshot, context) }.status)
        assertEquals(NativeTerminalStatus.UNSUPPORTED,
            assertFailsWith<NativeTerminalException> { NativeStateDelta(FakeNativeBridge()).export(snapshot, snapshot, context) }.status)
        assertEquals(1, bridge.calls)
        assertEquals(1.toByte(), context[0])
        assertEquals(1, bridge.lastProfile)
        assertFailsWith<NativeTerminalException> {
            codec.export(snapshot, snapshot, context, widthProfile = TerminalWidthProfile.UNICODE_16)
        }
        assertEquals(2, bridge.lastProfile)
        assertFailsWith<NativeTerminalException> {
            codec.export(snapshot, snapshot, context, outputProfile = TerminalOutputProfile.UTF8_WITH_STANDALONE_C1)
        }
        assertEquals(3, bridge.lastProfile)
        assertFailsWith<NativeTerminalException> {
            codec.export(snapshot, snapshot, context, widthProfile = TerminalWidthProfile.UNICODE_16,
                outputProfile = TerminalOutputProfile.UTF8_WITH_STANDALONE_C1)
        }
        assertEquals(4, bridge.lastProfile)
    }

    @Test
    fun appliedAndIdempotentResultsKeepDetachedBytesAndRejectMalformedBridgeData() {
        val snapshot = graphicsSnapshot()
        val bridge = RecordingBridge()
        val codec = NativeStateDelta(bridge)
        bridge.applyResult = NativeBridgeResult.Success(NativeBridgeDeltaApply(1, 8, snapshot.size.toLong(), snapshot.copyOf(), ByteArray(32) { 7 }))
        val applied = codec.apply(snapshot, context, byteArrayOf(1))
        assertEquals(NativeDeltaOutcome.APPLIED, applied.outcome)
        assertEquals(8, applied.nextSourceSequence)
        applied.copySnapshot()[0] = 0
        assertContentEquals(snapshot, applied.copySnapshot())
        applied.copySha256()[0] = 0
        assertContentEquals(ByteArray(32) { 7 }, applied.copySha256())

        bridge.applyResult = NativeBridgeResult.Success(NativeBridgeDeltaApply(2, 8, snapshot.size.toLong(), snapshot.copyOf(), ByteArray(32)))
        assertEquals(NativeDeltaOutcome.ALREADY_APPLIED, codec.apply(snapshot, context, byteArrayOf(1)).outcome)
        bridge.applyResult = NativeBridgeResult.Success(NativeBridgeDeltaApply(2, 8, snapshot.size.toLong(), snapshot.copyOf().also { it[16] = 1 }, ByteArray(32)))
        assertFailsWith<NativeTerminalContractException> { codec.apply(snapshot, context, byteArrayOf(1)) }
        bridge.applyResult = NativeBridgeResult.Success(NativeBridgeDeltaApply(3, 8, snapshot.size.toLong(), snapshot.copyOf(), ByteArray(32)))
        assertFailsWith<NativeTerminalContractException> { codec.apply(snapshot, context, byteArrayOf(1)) }
        bridge.applyResult = NativeBridgeResult.Success(NativeBridgeDeltaApply(1, 8, snapshot.size.toLong(), snapshot.copyOf(), ByteArray(31)))
        assertFailsWith<NativeTerminalContractException> { codec.apply(snapshot, context, byteArrayOf(1)) }
    }

    private class RecordingBridge : NativeBridge by FakeNativeBridge() {
        var calls = 0
        var lastProfile = 0
        var result: NativeBridgeResult<ByteArray> = NativeBridgeResult.Success(byteArrayOf(1))
        var applyResult: NativeBridgeResult<NativeBridgeDeltaApply> = NativeBridgeResult.Failure(NativeTerminalStatus.UNSUPPORTED.code)

        override fun deltaExportV1(base: ByteArray, target: ByteArray, contextId: ByteArray, profile: Int, maxBytes: Int): NativeBridgeResult<ByteArray> {
            calls++
            lastProfile = profile
            contextId[0] = 2
            return result
        }

        override fun deltaApplyV1(current: ByteArray, contextId: ByteArray, profile: Int, delta: ByteArray, maxBytes: Int): NativeBridgeResult<NativeBridgeDeltaApply> {
            calls++
            lastProfile = profile
            return applyResult
        }
    }
}
