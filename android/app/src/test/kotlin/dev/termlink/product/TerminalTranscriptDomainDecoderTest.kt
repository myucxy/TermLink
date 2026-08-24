package dev.termlink.product

import dev.termlink.protocol.realtime.v1.TerminalTranscriptPageV1
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class TerminalTranscriptDomainDecoderTest {
    @Test
    fun sharedVectorConvertsCursorRleWideCellsAndResourcesExactly() {
        val wire = sharedVectorTranscript()

        val decoded = TerminalTranscriptDomainDecoder.decode(wire)

        assertEquals(wire.asOfSnapshotSeq, decoded.snapshotAnchorSequence)
        assertEquals(wire.firstLineId, decoded.firstLineId)
        assertEquals(wire.lastLineId, decoded.lastLineId)
        assertEquals(wire.hasMoreBefore, decoded.hasMoreBefore)
        assertEquals(wire.nextBeforeLineId, decoded.nextBeforeLineId)
        assertEquals(wire.rowsCount, decoded.rows.size)
        assertEquals(6, decoded.columns)
        assertEquals(wire.styleTableCount, decoded.styleTable.size)
        assertEquals(wire.hyperlinkTableCount, decoded.hyperlinks.size)
        assertEquals(wire.palette.colorsRgbList, decoded.palette.colorsRgb)

        val firstRow = decoded.rows.first()
        assertEquals(6, firstRow.cells.size)
        assertEquals(listOf("o", "l", "d"), firstRow.cells.take(3).map { it.grapheme })
        assertTrue(firstRow.cells.drop(3).all { it.grapheme == " " })

        val secondRow = decoded.rows.last()
        val wideLead = secondRow.cells.indexOfFirst { it.displayWidth == 2 }
        assertTrue(wideLead >= 0)
        assertTrue(secondRow.cells[wideLead + 1].wideContinuation)
        assertEquals(secondRow.cells[wideLead].styleIndex, secondRow.cells[wideLead + 1].styleIndex)
        assertEquals(wire.rowsList.map { it.lineId }, decoded.rows.map { it.lineId })
        assertEquals(wire.rowsList.map { it.sealedAtSeq }, decoded.rows.map { it.sealedAtSequence })
    }

    @Test
    fun unsignedSnapshotAnchorOutsideDomainIsRejected() {
        val unsupported = sharedVectorTranscript().toBuilder()
            .setAsOfSnapshotSeq(-1L)
            .build()

        assertThrows(IllegalArgumentException::class.java) {
            TerminalTranscriptDomainDecoder.decode(unsupported)
        }
    }

    private fun sharedVectorTranscript(): TerminalTranscriptPageV1 {
        val resource = requireNotNull(javaClass.classLoader?.getResourceAsStream(VECTOR_RESOURCE)) {
            "Missing shared terminal snapshot vector"
        }.use { it.reader().readText() }
        val encoded = Json.parseToJsonElement(resource).jsonObject
            .getValue("transcript_plaintext").jsonPrimitive.content
        require(encoded.length % 2 == 0) { "Transcript vector must use even-length hexadecimal" }
        val bytes = ByteArray(encoded.length / 2) { index ->
            encoded.substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
        return TerminalTranscriptPageV1.parseFrom(bytes)
    }

    private companion object {
        const val VECTOR_RESOURCE = "terminal-snapshot-v1.json"
    }
}
