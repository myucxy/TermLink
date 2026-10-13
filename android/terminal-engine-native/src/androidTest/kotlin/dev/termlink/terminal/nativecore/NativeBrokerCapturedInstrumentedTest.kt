package dev.termlink.terminal.nativecore

import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.terminal.api.TerminalHostGeometryV1
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalWidthProfile
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class NativeBrokerCapturedInstrumentedTest {
    @Test
    fun graphicsEngineConsumesCanonicalCaptureThroughItsOrderedApply() = runBlocking {
        val engine = NativeTerminalEngine(TerminalSize(40, 12), graphicsProfile = true,
            widthProfile = TerminalWidthProfile.UNICODE_16)
        val capture = NativeBrokerCaptureV1(FIRST_FILE_IDENTITY, NativeBrokerOutcomeV1.BYTES,
            byteArrayOf(1, 2, 3, 4))
        try {
            engine.applyHostGeometry(1, TerminalHostGeometryV1(9, 18, 360, 216, 360, 216))
            val canonical = NativeBrokerCaptureCodecV1.encode(2, listOf(capture))
            try {
                engine.applyBrokerCapturedOutput(2, FIRST_FILE.encodeToByteArray(), canonical)
                assertEquals(3L, engine.frames.value.nextSourceSequence)
            } finally { canonical.fill(0) }
        } finally { capture.close(); engine.dispose() }
    }

    @Test
    fun redactedCaptureMismatchRollsBackAndValidRetryCommitsOnce() {
        val terminal = NativeTerminalHandle.createWithProfiles(JniNativeBridge, TerminalSize(40, 12), 1,
            TerminalOutputProfile.UTF8, TerminalWidthProfile.UNICODE_16, graphicsProfile = true)
        val output = FIRST_FILE.encodeToByteArray()
        val valid = NativeBrokerCaptureV1(FIRST_FILE_IDENTITY, NativeBrokerOutcomeV1.BYTES, byteArrayOf(1, 2, 3, 4))
        val extraIdentity = FIRST_FILE_IDENTITY.copyOf().also { it[28] = 1 }
        val extra = NativeBrokerCaptureV1(extraIdentity, NativeBrokerOutcomeV1.BYTES, byteArrayOf(1, 2, 3, 4))
        val wrongDigest = FIRST_FILE_IDENTITY.copyOf().also { it[32] = (it[32].toInt() xor 1).toByte() }
        try {
            terminal.applyWithHostEffectsV2(listOf(NativeSourceEvent.HostGeometry(1,
                TerminalHostGeometryV1(9, 18, 360, 216, 360, 216))))
            val original = terminal.exportCheckpoint().copyPayload()
            val failures = listOf(
                NativeBrokerOutputV1(2, output, emptyList()),
                NativeBrokerOutputV1(2, output, listOf(valid, extra)),
                NativeBrokerOutputV1(2, output, listOf(
                    NativeBrokerCaptureV1(wrongDigest, NativeBrokerOutcomeV1.BYTES, byteArrayOf(1, 2, 3, 4)))),
            )
            failures.forEach { replay ->
                val failure = runCatching { terminal.applyBrokerCapturedV1(replay) }.exceptionOrNull()
                assertTrue("Expected native broker rejection: $failure", failure is NativeTerminalException)
                assertEquals(NativeTerminalStatus.INVALID_ARGUMENT, (failure as NativeTerminalException).status)
                assertEquals(2L, terminal.nextSourceSequence)
                assertArrayEquals(original, terminal.exportCheckpoint().copyPayload())
            }

            val effects = terminal.applyBrokerCapturedV1(NativeBrokerOutputV1(2, output, listOf(valid)))
            assertEquals(2, effects.formatVersion)
            assertEquals(3L, terminal.nextSourceSequence)
            assertEquals(3L, terminal.renderV5().nextSourceSequence)
        } finally { terminal.close() }
        assertTrue(terminal.isClosed)
    }

    @Test
    fun jniRejectsMismatchedArraysAndStaleHandleWithoutPublishingEffects() {
        val config = NativeTerminalConfigV4(columns = 40, rows = 12, initialSourceSequence = 1,
            historyCapacityRows = 0, outputProfile = NATIVE_OUTPUT_PROFILE_UTF8,
            widthProfile = TerminalWidthProfile.UNICODE_16.wireValue)
        val allocation = JniNativeBridge.createGraphicsV1(config)
        assertTrue(allocation is NativeBridgeResult.Success)
        val handle = (allocation as NativeBridgeResult.Success).value.handle
        try {
            val effects = arrayOfNulls<ByteArray>(1)
            val diagnostic = arrayOfNulls<ByteArray>(1)
            assertEquals(NativeTerminalStatus.INVALID_ARGUMENT.code,
                JniNativeMethods.applyBrokerCapturedV1(handle.value, 1, FIRST_FILE.encodeToByteArray(),
                    arrayOf(FIRST_FILE_IDENTITY), intArrayOf(), arrayOf(byteArrayOf(1, 2, 3, 4)),
                    effects, diagnostic))
            assertNull(effects[0])
            assertEquals(1L, (JniNativeBridge.exportCheckpointGraphicsV1(handle,
                TERMLINK_GRAPHICS_SNAPSHOT_MAX_BYTES) as NativeBridgeResult.Success).value.nextSourceSequence)
        } finally {
            assertTrue(JniNativeBridge.destroy(handle) is NativeBridgeResult.Success)
        }
        val effects = arrayOfNulls<ByteArray>(1)
        val diagnostic = arrayOfNulls<ByteArray>(1)
        assertEquals(NativeTerminalStatus.INVALID_ARGUMENT.code,
            JniNativeMethods.applyBrokerCapturedV1(handle.value, 1, byteArrayOf(65),
                emptyArray(), intArrayOf(), emptyArray(), effects, diagnostic))
        assertNull(effects[0])
        assertNull(diagnostic[0])
    }

    private companion object {
        const val FIRST_FILE = "\u001b_Ga=T,t=f,f=32,s=1,v=1,S=4,i=31;QzovYS5yZ2Jh\u001b\\"
        // Generated from the native redaction API for FIRST_FILE; sequence is 2 after HostGeometry.
        val FIRST_FILE_IDENTITY = (
            "0100000060000000010000006600000002000000000000002f00000000000000" +
            "5fdcacd9ac4c7ebdc9a0ecf35d9dad21627a91f7d66e1a7c2f1f4a9e6aea45e8" +
            "0000000000000000040000000100000000000000000000000000000000000000"
        ).chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    }
}
