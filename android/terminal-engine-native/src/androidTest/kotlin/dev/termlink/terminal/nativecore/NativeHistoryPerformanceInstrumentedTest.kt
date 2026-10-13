package dev.termlink.terminal.nativecore

import android.util.Log
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import dev.termlink.terminal.api.TerminalOutputProfile
import dev.termlink.terminal.api.TerminalSize
import dev.termlink.terminal.api.TerminalWidthProfile
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import org.junit.runner.RunWith
import java.util.Locale

/** Opt-in real JNI timing probe. Run with `-e termlink_perf true`; no device speed threshold. */
@RunWith(AndroidJUnit4::class)
class NativeHistoryPerformanceInstrumentedTest {
    @Test
    fun graphicsHistory5200LinesSeparatesFeedAndRenderCost() {
        assumeTrue(InstrumentationRegistry.getArguments().getString("termlink_perf") == "true")
        val chunks = outputChunks()
        assertEquals(5_200, chunks.last().lastLine)
        NativeTerminalHandle.createWithProfiles(
            bridge = JniNativeBridge,
            size = TerminalSize(80, 24),
            initialNextSourceSequence = 1,
            outputProfile = TerminalOutputProfile.UTF8,
            widthProfile = TerminalWidthProfile.LEGACY_V1,
            historyCapacityRows = 2_000,
            graphicsProfile = true,
        ).use { terminal ->
            var started = System.nanoTime()
            val baseline = terminal.renderV5AtScrollback(0)
            val baselineNs = System.nanoTime() - started
            assertEquals(1L, baseline.renderFrame.nextSourceSequence)
            assertEquals(0L, baseline.availableRows)
            Log.i(TAG, "rows=0 feed_ms=0 render_ms=${millis(baselineNs)}")

            var sequence = 1L
            var feedNs = 0L
            var renderNs = 0L
            var chunkCount = 0
            var priorHistoryRows = 0L
            for (chunk in chunks) {
                started = System.nanoTime()
                val effects = terminal.feedWithHostEffectsV2(sequence, chunk.bytes)
                feedNs += System.nanoTime() - started
                assertTrue(effects.effects.isEmpty())
                sequence++

                started = System.nanoTime()
                val visible = terminal.renderV5AtScrollback(0)
                renderNs += System.nanoTime() - started
                assertEquals(sequence, visible.renderFrame.nextSourceSequence)
                assertEquals(sequence, visible.renderFrame.presentationRevision)
                assertTrue(visible.availableRows >= priorHistoryRows)
                priorHistoryRows = visible.availableRows
                chunkCount++

                if (chunk.lastLine == chunk.bucketEnd) {
                    val marker = "TL-PERF-" + chunk.lastLine.toString().padStart(5, '0')
                    assertTrue(frameText(visible.renderFrame).contains(marker))
                    Log.i(TAG, "rows=${chunk.lastLine} chunks=$chunkCount " +
                        "feed_ms=${millis(feedNs)} render_ms=${millis(renderNs)} " +
                        "history_rows=${visible.availableRows}")
                    feedNs = 0
                    renderNs = 0
                    chunkCount = 0
                }
            }
            assertEquals(334L, sequence)
            assertTrue(priorHistoryRows > 0)
        }
    }

    private data class OutputChunk(val lastLine: Int, val bucketEnd: Int, val bytes: ByteArray)

    private fun outputChunks(): List<OutputChunk> = buildList {
        var line = 1
        while (line <= 5_200) {
            val bucketEnd = minOf(((line - 1) / 500 + 1) * 500, 5_200)
            val count = minOf(16, bucketEnd - line + 1)
            val bytes = buildString(count * 64) {
                repeat(count) { offset ->
                    val prefix = "TL-PERF-" + (line + offset).toString().padStart(5, '0')
                    append(prefix).append("x".repeat(62 - prefix.length)).append("\r\n")
                }
            }.encodeToByteArray()
            check(bytes.size == count * 64)
            add(OutputChunk(line + count - 1, bucketEnd, bytes))
            line += count
        }
    }

    private fun millis(nanos: Long): String = String.format(Locale.US, "%.2f", nanos / 1_000_000.0)

    private companion object { const val TAG = "TermLinkHistoryPerf" }
}
