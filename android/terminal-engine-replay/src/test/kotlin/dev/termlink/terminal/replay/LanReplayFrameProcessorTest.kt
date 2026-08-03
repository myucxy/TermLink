package dev.termlink.terminal.replay

import dev.termlink.terminal.api.TerminalEngine
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalThemeV1
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.runBlocking
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest

class LanReplayFrameProcessorTest {
    @Test
    fun verifiesBinaryReplayAndPreservesOutputChunks() = runBlocking {
        val outputChunks = listOf(byteArrayOf(0x1b, 0x5b), "hello".encodeToByteArray())
        val output = outputChunks.fold(byteArrayOf()) { result, chunk -> result + chunk }
        val engine = RecordingEngine()
        val processor = LanReplayFrameProcessor(engine)

        processor.accept(frame(1, 1, 10, startedPayload(output, eventCount = 5)))
        processor.accept(frame(1, 2, 20, resizePayload(100, 30)))
        processor.accept(frame(1, 3, 30, outputChunks[0]))
        processor.accept(frame(1, 3, 40, outputChunks[1]))
        val progress = processor.accept(frame(1, 4, 50, endedPayload(17)))

        val complete = progress as LanReplayProgress.Complete
        assertEquals(5, complete.report.eventCount)
        assertEquals(2, complete.report.outputEventCount)
        assertEquals(output.size, complete.report.outputBytes)
        assertEquals(TerminalSize(100, 30), complete.report.finalSize)
        assertEquals(17, complete.report.exitCode)
        assertArrayEquals(outputChunks[0], engine.feeds[0])
        assertArrayEquals(outputChunks[1], engine.feeds[1])
    }

    @Test
    fun rejectsSequenceGap() {
        val output = "hello".encodeToByteArray()
        val processor = LanReplayFrameProcessor(RecordingEngine())

        assertThrows(LanReplayFormatException::class.java) {
            runBlocking {
                processor.accept(frame(1, 1, 10, startedPayload(output, eventCount = 4)))
                processor.accept(frame(1, 2, 20, resizePayload(80, 25)))
                processor.accept(frame(1, 3, 30, output))
                processor.accept(frame(1, 4, 40, endedPayload(0), sequence = 5))
            }
        }
    }

    private fun startedPayload(output: ByteArray, eventCount: Int): ByteArray {
        val hash = MessageDigest.getInstance("SHA-256")
            .digest(output)
            .joinToString(separator = "") { byte -> "%02x".format(byte) }
        return """
            {
              "case_name":"lan-test-v1",
              "event_count":$eventCount,
              "output_event_count":${if (eventCount == 5) 2 else 1},
              "output_bytes":${output.size},
              "output_sha256":"$hash"
            }
        """.trimIndent().encodeToByteArray()
    }

    private fun resizePayload(columns: Int, rows: Int): ByteArray = ByteBuffer
        .allocate(4)
        .order(ByteOrder.BIG_ENDIAN)
        .putShort(columns.toShort())
        .putShort(rows.toShort())
        .array()

    private fun endedPayload(exitCode: Int): ByteArray = ByteBuffer
        .allocate(4)
        .order(ByteOrder.BIG_ENDIAN)
        .putInt(exitCode)
        .array()

    private fun frame(
        version: Int,
        type: Int,
        elapsed: Long,
        payload: ByteArray,
        sequence: Long = elapsed / 10,
    ): ByteArray = ByteBuffer
        .allocate(18 + payload.size)
        .order(ByteOrder.BIG_ENDIAN)
        .put(version.toByte())
        .put(type.toByte())
        .putLong(sequence)
        .putLong(elapsed)
        .put(payload)
        .array()

    private class RecordingEngine : TerminalEngine {
        override val inputEvents: Flow<ByteArray> = emptyFlow()
        override val sizeEvents: Flow<TerminalSize> = emptyFlow()
        val feeds = mutableListOf<ByteArray>()

        override suspend fun feed(seq: Long, bytes: ByteBuffer) {
            feeds += ByteArray(bytes.remaining()).also(bytes::get)
        }

        override fun resize(columns: Int, rows: Int) = Unit

        override fun applyTheme(theme: TerminalThemeV1) = Unit

        override fun setReadOnly(readOnly: Boolean) = Unit

        override fun clear() {
            feeds.clear()
        }

        override fun dispose() = Unit
    }
}
