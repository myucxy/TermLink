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
import java.security.MessageDigest

class GoldenStreamReplayerTest {
    @Test
    fun replaysRepositoryPowerShellCapture() = runBlocking {
        val casePath = "terminal-golden-streams/smoke-powershell-v1"
        val bundle = GoldenStreamBundle(
            manifestBytes = resourceBytes("$casePath/manifest.json"),
            eventsBytes = resourceBytes("$casePath/events.jsonl"),
            outputBytes = resourceBytes("$casePath/output.bin"),
            expectationsBytes = resourceBytes("$casePath/expectations.json"),
        )

        val report = GoldenStreamReplayer().replay(bundle, RecordingEngine())

        assertEquals("9a69e46f-0071-40a8-82a3-510b9499ed14", report.captureId)
        assertEquals("smoke-powershell-v1", report.caseName)
        assertEquals(14, report.eventCount)
        assertEquals(10, report.outputEventCount)
        assertEquals(702, report.outputBytes)
        assertEquals(TerminalSize(100, 30), report.finalSize)
        assertEquals(17, report.exitCode)
    }

    @Test
    fun replaysExactOutputChunksAndResizeEvents() = runBlocking {
        val output = byteArrayOf(
            0x1b,
            0x5b,
            0xE4.toByte(),
            0xB8.toByte(),
            0xAD.toByte(),
            0x0d,
            0x0a,
        )
        val bundle = testBundle(output)
        val engine = RecordingEngine()

        val report = GoldenStreamReplayer().replay(bundle, engine)

        assertEquals("split-utf8-v1", report.caseName)
        assertEquals(2, report.outputEventCount)
        assertEquals(output.size, report.outputBytes)
        assertEquals(TerminalSize(100, 30), report.finalSize)
        assertEquals(17, report.exitCode)
        assertArrayEquals(output.copyOfRange(0, 4), engine.feeds[0].second)
        assertArrayEquals(output.copyOfRange(4, output.size), engine.feeds[1].second)
        assertEquals(listOf(TerminalSize(80, 25), TerminalSize(100, 30)), engine.sizes)
    }

    @Test
    fun rejectsTamperedOutputBeforeReplay() {
        val output = "safe".encodeToByteArray()
        val bundle = testBundle(output).copy(outputBytes = "tampered".encodeToByteArray())

        assertThrows(GoldenStreamFormatException::class.java) {
            runBlocking {
                GoldenStreamReplayer().replay(bundle, RecordingEngine())
            }
        }
    }

    private fun testBundle(output: ByteArray): GoldenStreamBundle {
        val events = listOf(
            """{"seq":1,"elapsed_us":1,"type":"lifecycle","state":"started"}""",
            """{"seq":2,"elapsed_us":2,"type":"resize","columns":80,"rows":25,"reason":"initial"}""",
            """{"seq":3,"elapsed_us":3,"type":"output","data_offset":0,"data_length":4}""",
            """{"seq":4,"elapsed_us":4,"type":"resize","columns":100,"rows":30,"reason":"probe"}""",
            """{"seq":5,"elapsed_us":5,"type":"output","data_offset":4,"data_length":${output.size - 4}}""",
            """{"seq":6,"elapsed_us":6,"type":"lifecycle","state":"ended","exit_code":17}""",
        ).joinToString(separator = "\n", postfix = "\n").encodeToByteArray()
        val expectations = """
            {
              "exit_code": 17,
              "final_size": {"columns": 100, "rows": 30},
              "output_contains_utf8": [],
              "normalized_grid": null,
              "reference_screenshot": null
            }
        """.trimIndent().encodeToByteArray()
        val manifest = """
            {
              "format": "termlink.golden-stream",
              "format_version": 1,
              "capture_id": "00000000-0000-0000-0000-000000000001",
              "case_name": "split-utf8-v1",
              "created_at": "2026-08-02T00:00:00Z",
              "clock": "monotonic-microseconds",
              "source": {
                "platform": "test",
                "shell": "synthetic",
                "command_line": "synthetic"
              },
              "initial_size": {"columns": 80, "rows": 25},
              "files": {
                "events": "events.jsonl",
                "output": "output.bin",
                "expectations": "expectations.json"
              },
              "statistics": {
                "event_count": 6,
                "output_event_count": 2,
                "output_bytes": ${output.size}
              },
              "sha256": {
                "events": "${sha256(events)}",
                "output": "${sha256(output)}",
                "expectations": "${sha256(expectations)}"
              }
            }
        """.trimIndent().encodeToByteArray()

        return GoldenStreamBundle(manifest, events, output, expectations)
    }

    private fun sha256(bytes: ByteArray): String = MessageDigest.getInstance("SHA-256")
        .digest(bytes)
        .joinToString(separator = "") { byte -> "%02x".format(byte) }

    private fun resourceBytes(path: String): ByteArray = requireNotNull(
        javaClass.classLoader?.getResourceAsStream(path),
    ) { "Test resource not found: $path" }.use { it.readBytes() }

    private class RecordingEngine : TerminalEngine {
        override val inputEvents: Flow<ByteArray> = emptyFlow()
        override val sizeEvents: Flow<TerminalSize> = emptyFlow()
        val feeds = mutableListOf<Pair<Long, ByteArray>>()
        val sizes = mutableListOf<TerminalSize>()

        override suspend fun feed(seq: Long, bytes: ByteBuffer) {
            val copy = ByteArray(bytes.remaining())
            bytes.get(copy)
            feeds += seq to copy
        }

        override fun resize(columns: Int, rows: Int) {
            sizes += TerminalSize(columns, rows)
        }

        override fun applyTheme(theme: TerminalThemeV1) = Unit

        override fun setReadOnly(readOnly: Boolean) = Unit

        override fun clear() {
            feeds.clear()
            sizes.clear()
        }

        override fun dispose() = Unit
    }
}
