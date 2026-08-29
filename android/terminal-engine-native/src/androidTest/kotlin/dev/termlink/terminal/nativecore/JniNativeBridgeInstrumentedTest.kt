package dev.termlink.terminal.nativecore

import androidx.test.ext.junit.runners.AndroidJUnit4
import dev.termlink.terminal.api.TerminalKittyKey
import dev.termlink.terminal.api.TerminalKittyKeyInput
import dev.termlink.terminal.api.TerminalCellPosition
import dev.termlink.terminal.api.TerminalMouseInput
import dev.termlink.terminal.api.TerminalMousePosition
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.Collections
import java.util.concurrent.CountDownLatch

@RunWith(AndroidJUnit4::class)
class JniNativeBridgeInstrumentedTest {
    @Test
    fun nativeCreateApplyRenderCheckpointImportAndDestroyRoundTrip() {
        assertEquals(TERMLINK_TERMINAL_ABI_VERSION, JniNativeBridge.abiVersion)
        val terminal = NativeTerminalHandle.create(
            bridge = JniNativeBridge,
            size = TerminalSize(8, 2),
            initialNextSourceSequence = 1,
        )
        terminal.use {
            val receipt = it.feed(1, "hello".encodeToByteArray())
            assertEquals(2, receipt.nextSourceSequence)
            assertEquals(2, it.render().nextSourceSequence)
            val checkpoint = it.exportCheckpoint()
            assertTrue(checkpoint.payloadByteCount > 0)

            NativeTerminalHandle.importCheckpoint(JniNativeBridge, checkpoint.copyPayload()).use { restored ->
                assertEquals(2, restored.nextSourceSequence)
                assertEquals(2, restored.render().nextSourceSequence)
            }
        }
    }

    @Test
    fun opaqueTokensRejectForgeryStaleUseAndDoubleDestroy() {
        val allocation = LongArray(3)
        assertEquals(
            NativeTerminalStatus.OK.code.toLong(),
            JniNativeMethods.create(8, 2, 1, allocation).toLong(),
        )
        val token = allocation[0]
        assertTrue(token > 0)
        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT.code.toLong(),
            JniNativeMethods.destroy(Long.MAX_VALUE).toLong(),
        )
        assertEquals(NativeTerminalStatus.OK.code.toLong(), JniNativeMethods.destroy(token).toLong())
        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT.code.toLong(),
            JniNativeMethods.destroy(token).toLong(),
        )
        assertEquals(
            NativeTerminalStatus.INVALID_ARGUMENT.code.toLong(),
            JniNativeMethods.applyBatch(
                token,
                longArrayOf(1, 3, 0, 0, 1),
                arrayOfNulls(1),
            ).toLong(),
        )
    }

    @Test
    fun configV3HostEffectsAndSemanticInputUseTheSameOpaqueTerminal() {
        NativeTerminalHandle.create(
            bridge = JniNativeBridge,
            size = TerminalSize(8, 2),
            initialNextSourceSequence = 1,
            outputProfile = TerminalOutputProfile.UTF8_WITH_STANDALONE_C1,
            historyCapacityRows = 16,
        ).use { terminal ->
            val kitty = terminal.encodeKittyKey(
                TerminalKittyKeyInput(TerminalKittyKey.Character('x'.code)),
            )
            assertTrue(kitty.isNotEmpty())
            val mouse = terminal.encodeMouse(
                TerminalMouseInput.Move(
                    position = TerminalMousePosition(TerminalCellPosition(0, 0)),
                ),
            )
            assertTrue("mouse reporting is disabled by default", mouse.isEmpty())

            val unsupported = runCatching {
                terminal.encodeWin32Key(0x41, 0x1e, 'a'.code, true, 0, 1)
            }.exceptionOrNull() as NativeTerminalException
            assertEquals(NativeTerminalStatus.UNSUPPORTED, unsupported.status)

            val hostEffects = terminal.feedWithHostEffectsV2(1, "hello".encodeToByteArray())
            assertEquals(2, hostEffects.formatVersion)
            assertTrue(hostEffects.effects.isEmpty())
            assertEquals(2, terminal.render().nextSourceSequence)
        }
    }

    @Test
    fun concurrentDestroyPublishesExactlyOneSuccess() {
        val allocation = LongArray(3)
        assertEquals(
            NativeTerminalStatus.OK.code.toLong(),
            JniNativeMethods.create(8, 2, 1, allocation).toLong(),
        )
        val ready = CountDownLatch(2)
        val start = CountDownLatch(1)
        val statuses = Collections.synchronizedList(mutableListOf<Int>())
        val workers = List(2) {
            Thread {
                ready.countDown()
                start.await()
                statuses += JniNativeMethods.destroy(allocation[0])
            }.apply { start() }
        }
        ready.await()
        start.countDown()
        workers.forEach(Thread::join)

        assertEquals(
            listOf(NativeTerminalStatus.OK.code, NativeTerminalStatus.INVALID_ARGUMENT.code),
            statuses.sorted(),
        )
    }
}
