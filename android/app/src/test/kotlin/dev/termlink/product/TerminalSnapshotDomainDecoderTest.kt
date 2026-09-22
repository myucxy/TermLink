package dev.termlink.product

import dev.termlink.crypto.TerminalSnapshotCrypto
import dev.termlink.protocol.realtime.v1.TerminalSnapshotV1
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalSnapshotDomainDecoderTest {
    @Test
    fun sharedVectorConvertsMarginsRleAndWideCellsExactly() {
        val wire = sharedVectorSnapshot()

        val decoded = TerminalSnapshotDomainDecoder.decode(wire)

        assertEquals(wire.snapshotSeq, decoded.sequence)
        assertEquals(wire.columns, decoded.size.columns)
        assertEquals(wire.rows, decoded.size.rows)
        assertEquals(wire.primaryScreen.scrollBottom + 1, decoded.primaryScreen.scrollBottomExclusive)
        assertEquals(wire.primaryScreen.marginRight + 1, decoded.primaryScreen.marginRightExclusive)
        val decodedRow = decoded.primaryScreen.visibleRows.first()
        assertEquals(wire.columns, decodedRow.cells.size)
        assertTrue(
            decodedRow.cells.any { cell ->
                cell.grapheme == " " && cell.displayWidth == 1 && !cell.wideContinuation
            },
        )
        val wideLead = decodedRow.cells.indexOfFirst { it.displayWidth == 2 }
        assertTrue(wideLead >= 0)
        assertTrue(decodedRow.cells[wideLead + 1].wideContinuation)
        assertEquals(
            decodedRow.cells[wideLead].styleIndex,
            decodedRow.cells[wideLead + 1].styleIndex,
        )
    }

    @Test
    fun unsignedValuesOutsideRendererDomainAreRejected() {
        val wire = sharedVectorSnapshot()
        val unsupported = wire.toBuilder()
            .setProjection(
                wire.projection.toBuilder()
                    .setObservedLocalInputEpoch(-1L),
            )
            .build()

        assertThrows(IllegalArgumentException::class.java) {
            TerminalSnapshotDomainDecoder.decode(unsupported)
        }
    }

    @Test
    fun firstTranscriptCursorStopsBeforeTheFirstVisiblePrimaryLine() {
        val wire = sharedVectorSnapshot()
        val beforeLineId = TerminalSnapshotCrypto.initialTranscriptBeforeLineId(wire)

        assertEquals(wire.primaryScreen.visibleRowsList.first().lineId.toULong(), beforeLineId)
        assertTrue(wire.nextTranscriptLineId.toULong() != beforeLineId)
    }

    private fun sharedVectorSnapshot(): TerminalSnapshotV1 {
        val resource = requireNotNull(javaClass.classLoader?.getResourceAsStream(VECTOR_RESOURCE)) {
            "Missing shared terminal snapshot vector"
        }.use { it.reader().readText() }
        val encoded = Json.parseToJsonElement(resource).jsonObject
            .getValue("snapshot_plaintext").jsonPrimitive.content
        require(encoded.length % 2 == 0) { "Snapshot vector must use even-length hexadecimal" }
        val bytes = ByteArray(encoded.length / 2) { index ->
            encoded.substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
        return TerminalSnapshotV1.parseFrom(bytes)
    }

    private companion object {
        const val VECTOR_RESOURCE = "terminal-snapshot-v1.json"
    }
}
