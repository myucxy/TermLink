package dev.termlink.terminal.nativecore

import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalWidthProfile
import java.security.MessageDigest
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class NativeStateDeltaInstrumentedTest {
    @Test
    fun graphicsSnapshotsCrossRealJniWithIdempotentApply() {
        val codec = NativeStateDelta()
        for (width in TerminalWidthProfile.entries) for (outputProfile in TerminalOutputProfile.entries) {
            val profile = width.wireValue + (if (outputProfile == TerminalOutputProfile.UTF8) 0 else 2)
            val context = ByteArray(32).also { it[0] = profile.toByte() }
            NativeTerminalHandle.createWithProfiles(
                JniNativeBridge, TerminalSize(80, 24), 1, outputProfile,
                width, historyCapacityRows = 100, graphicsProfile = true,
            ).use { terminal ->
                terminal.feedWithHostEffectsV2(1, (0..80).joinToString("\r\n") { "line $it with retained content" }.encodeToByteArray())
                val base = terminal.exportCheckpoint().copyPayload()
                terminal.feedWithHostEffectsV2(2, "target".encodeToByteArray())
                val target = terminal.exportCheckpoint().copyPayload()
                val delta = codec.export(base, target, context, widthProfile = width, outputProfile = outputProfile)
                assertTrue(delta.isNotEmpty() && delta.size < target.size)
                val applied = codec.apply(base, context, delta, widthProfile = width, outputProfile = outputProfile)
                assertEquals(NativeDeltaOutcome.APPLIED, applied.outcome)
                assertEquals(3, applied.nextSourceSequence)
                assertArrayEquals(target, applied.copySnapshot())
                assertArrayEquals(MessageDigest.getInstance("SHA-256").digest(target), applied.copySha256())
                val alreadyApplied = codec.apply(target, context, delta, widthProfile = width, outputProfile = outputProfile)
                assertEquals(NativeDeltaOutcome.ALREADY_APPLIED, alreadyApplied.outcome)
                assertArrayEquals(applied.copySha256(), alreadyApplied.copySha256())
                val detached = alreadyApplied.copySnapshot()
                detached[0] = 0
                assertArrayEquals(target, alreadyApplied.copySnapshot())
                val wrongContext = context.copyOf().also { it[1] = 1 }
                assertEquals(NativeTerminalStatus.NEEDS_FULL_SNAPSHOT,
                    org.junit.Assert.assertThrows(NativeTerminalException::class.java) {
                        codec.apply(base, wrongContext, delta, widthProfile = width, outputProfile = outputProfile)
                    }.status)
                val corrupted = delta.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }
                assertEquals(NativeTerminalStatus.MALFORMED_DELTA,
                    org.junit.Assert.assertThrows(NativeTerminalException::class.java) {
                        codec.apply(base, context, corrupted, widthProfile = width, outputProfile = outputProfile)
                    }.status)
                if (width == TerminalWidthProfile.UNICODE_16) {
                    val wrongOutput = if (outputProfile == TerminalOutputProfile.UTF8)
                        TerminalOutputProfile.UTF8_WITH_STANDALONE_C1 else TerminalOutputProfile.UTF8
                    assertEquals(NativeTerminalStatus.INCOMPATIBLE_DELTA,
                        org.junit.Assert.assertThrows(NativeTerminalException::class.java) {
                            codec.export(base, target, context, widthProfile = width, outputProfile = wrongOutput)
                        }.status)
                }
            }
        }
    }
}
