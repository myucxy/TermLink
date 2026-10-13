package dev.termlink.terminal.nativecore

import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalWidthProfile
import java.nio.ByteBuffer
import java.nio.ByteOrder
import kotlin.test.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertTrue
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import kotlinx.coroutines.runBlocking

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [26])
class NativeBrokerOutputTest {
    @Test
    fun nonGraphicsEngineRejectsBrokerReplayBeforeNativeMutation() = runBlocking {
        val bridge = FakeNativeBridge()
        val engine = NativeTerminalEngine(TerminalSize(2, 1), bridge)
        try {
            assertFailsWith<IllegalArgumentException> {
                engine.applyBrokerCapturedOutput(1, byteArrayOf(65), byteArrayOf(1))
            }
            assertEquals(1L, bridge.onlyStateForTest().nextSourceSequence)
        } finally { engine.dispose() }
    }

    @Test
    fun jniSignatureMatchesCapturedBrokerArrayContract() {
        val method = JniNativeMethods::class.java.declaredMethods.single { it.name == "applyBrokerCapturedV1" }
        assertEquals(Int::class.javaPrimitiveType, method.returnType)
        assertEquals(listOf(Long::class.javaPrimitiveType, Long::class.javaPrimitiveType,
            ByteArray::class.java, Array<ByteArray>::class.java, IntArray::class.java,
            Array<ByteArray>::class.java, Array<ByteArray?>::class.java,
            Array<ByteArray?>::class.java), method.parameterTypes.toList())
    }

    @Test
    fun captureAndOutputDetachCallerBuffersAndKeepParserOrdinals() {
        val identity = brokerIdentity(1, 1, 0, readLength = 17 * 1024 * 1024)
        val bytes = byteArrayOf(1, 2, 3, 4)
        val rawOutput = byteArrayOf(65, 66)
        val first = NativeBrokerCaptureV1(identity, NativeBrokerOutcomeV1.BYTES, bytes)
        val second = NativeBrokerCaptureV1(brokerIdentity(1, 2, 1), NativeBrokerOutcomeV1.OVER_LIMIT)
        val captures = mutableListOf(first, second)
        val output = NativeBrokerOutputV1(1, rawOutput, captures)

        identity.fill(0)
        bytes.fill(0)
        rawOutput.fill(0)
        captures.clear()
        assertEquals(2, output.captures.size)
        assertContentEquals(byteArrayOf(1, 2, 3, 4), first.copyBytes())
        assertContentEquals(byteArrayOf(65, 66), output.copyOutput())
        first.copyIdentity().fill(0)
        first.copyBytes().fill(0)
        output.copyOutput().fill(0)
        assertEquals(1, ByteBuffer.wrap(first.copyIdentity()).order(ByteOrder.LITTLE_ENDIAN).getInt(0))
        assertContentEquals(byteArrayOf(1, 2, 3, 4), first.copyBytes())
        assertContentEquals(byteArrayOf(65, 66), output.copyOutput())
        assertFailsWith<UnsupportedOperationException> { (output.captures as MutableList).clear() }
        output.close()
        output.close()
        assertFailsWith<IllegalStateException> { output.copyOutput() }
        assertFailsWith<IllegalStateException> { first.copyBytes() }
    }

    @Test
    fun malformedIdentitySequenceAndBoundsStopBeforeBridge() {
        assertFailsWith<IllegalArgumentException> {
            NativeBrokerCaptureV1(ByteArray(95), NativeBrokerOutcomeV1.DENIED)
        }
        assertFailsWith<IllegalArgumentException> {
            NativeBrokerCaptureV1(brokerIdentity(1, 1, 0), NativeBrokerOutcomeV1.DENIED, byteArrayOf(1))
        }
        assertFailsWith<IllegalArgumentException> { NativeBrokerOutputV1(1, byteArrayOf(), emptyList()) }
        assertFailsWith<IllegalArgumentException> {
            NativeBrokerOutputV1(1, byteArrayOf(1), listOf(NativeBrokerCaptureV1(
                brokerIdentity(2, 1, 0), NativeBrokerOutcomeV1.UNAVAILABLE)))
        }
        assertFailsWith<IllegalArgumentException> {
            NativeBrokerOutputV1(1, byteArrayOf(1), listOf(NativeBrokerCaptureV1(
                brokerIdentity(1, 2, 0), NativeBrokerOutcomeV1.UNAVAILABLE)))
        }
        val one = NativeBrokerCaptureV1(brokerIdentity(1, 1, 0), NativeBrokerOutcomeV1.DENIED)
        assertFailsWith<IllegalArgumentException> { NativeBrokerOutputV1(1, byteArrayOf(1), List(65) { one }) }
    }

    @Test
    fun captureBudgetsAreEnforcedWithoutAllocatingAnUnboundedBatch() {
        assertFailsWith<IllegalArgumentException> {
            NativeBrokerCaptureV1(brokerIdentity(1, 1, 0), NativeBrokerOutcomeV1.BYTES,
                ByteArray(NativeBrokerCaptureV1.MAX_CAPTURE_BYTES + 1))
        }
        val large = ByteArray(NativeBrokerCaptureV1.MAX_CAPTURE_BYTES)
        val first = NativeBrokerCaptureV1(brokerIdentity(1, 1, 0), NativeBrokerOutcomeV1.BYTES, large)
        val second = NativeBrokerCaptureV1(brokerIdentity(1, 1, 1), NativeBrokerOutcomeV1.BYTES, large)
        val extra = NativeBrokerCaptureV1(brokerIdentity(1, 1, 2), NativeBrokerOutcomeV1.BYTES, byteArrayOf(1))
        assertFailsWith<IllegalArgumentException> {
            NativeBrokerOutputV1(1, byteArrayOf(1), listOf(first, second, extra))
        }
        first.close(); second.close(); extra.close()
    }

    @Test
    fun graphicsHandleRetriesAtomicFailureAndDecodesCommittedEffects() {
        val graphics = GraphicsTestBridge()
        var failure = true
        var calls = 0
        val bridge = object : NativeBridge by graphics {
            override fun applyBrokerCapturedV1(handle: NativeBridgeHandle, output: NativeBrokerOutputV1):
                NativeBridgeResult<NativeBridgeApplyWithHostEffectsV2> {
                calls++
                if (failure) return NativeBridgeResult.Failure(NativeTerminalStatus.UNSUPPORTED.code)
                return NativeBridgeResult.Success(NativeBridgeApplyWithHostEffectsV2(
                    NativeBridgeApplyReceipt(2, 2), fakeHostEffectFrameV2()))
            }
        }
        val handle = NativeTerminalHandle.createWithProfiles(bridge, TerminalSize(2, 1), 1,
            TerminalOutputProfile.UTF8, TerminalWidthProfile.LEGACY_V1, graphicsProfile = true)
        val output = NativeBrokerOutputV1(1, byteArrayOf(65), emptyList())
        try {
            assertEquals(NativeTerminalStatus.UNSUPPORTED,
                assertFailsWith<NativeTerminalException> { handle.applyBrokerCapturedV1(output) }.status)
            assertEquals(1, handle.nextSourceSequence)
            failure = false
            assertTrue(handle.applyBrokerCapturedV1(output).effects.isEmpty())
            assertEquals(2, handle.nextSourceSequence)
            assertEquals(2, calls)
            assertFailsWith<IllegalArgumentException> { handle.applyBrokerCapturedV1(output) }
            assertEquals(2, calls)
        } finally { handle.close() }
        assertFailsWith<IllegalStateException> { handle.applyBrokerCapturedV1(output) }
    }

    @Test
    fun malformedCommittedReceiptPoisonsHandle() {
        val graphics = GraphicsTestBridge()
        val bridge = object : NativeBridge by graphics {
            override fun applyBrokerCapturedV1(handle: NativeBridgeHandle, output: NativeBrokerOutputV1):
                NativeBridgeResult<NativeBridgeApplyWithHostEffectsV2> = NativeBridgeResult.Success(
                    NativeBridgeApplyWithHostEffectsV2(NativeBridgeApplyReceipt(99, 99), fakeHostEffectFrameV2()))
        }
        val handle = NativeTerminalHandle.createWithProfiles(bridge, TerminalSize(2, 1), 1,
            TerminalOutputProfile.UTF8, TerminalWidthProfile.LEGACY_V1, graphicsProfile = true)
        val output = NativeBrokerOutputV1(1, byteArrayOf(65), emptyList())
        try {
            assertFailsWith<NativeTerminalContractException> { handle.applyBrokerCapturedV1(output) }
            assertFailsWith<IllegalStateException> { handle.applyBrokerCapturedV1(output) }
        } finally { handle.close() }
    }
}

private fun brokerIdentity(sequence: Long, offset: Int, ordinal: Int, readLength: Int = 0): ByteArray =
    ByteArray(96).also { bytes ->
        ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN).apply {
            putInt(0, TERMLINK_TERMINAL_ABI_VERSION)
            putInt(4, 96)
            putInt(8, 1)
            putInt(12, 'f'.code)
            putLong(16, sequence)
            putInt(24, offset)
            putInt(28, ordinal)
            putInt(72, readLength)
            putInt(76, if (readLength == 0) 0 else 1)
        }
    }
