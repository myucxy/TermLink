package dev.termlink.crypto

import com.google.protobuf.ByteString
import dev.termlink.protocol.realtime.v1.SnapshotCompressionV1
import dev.termlink.protocol.realtime.v1.SnapshotParserStateV1
import dev.termlink.protocol.realtime.v1.TerminalCellRunV1
import dev.termlink.protocol.realtime.v1.TerminalCharsetV1
import dev.termlink.protocol.realtime.v1.TerminalCursorStyleV1
import dev.termlink.protocol.realtime.v1.TerminalLifecycleV1
import dev.termlink.protocol.realtime.v1.TerminalRowV1
import dev.termlink.protocol.realtime.v1.TerminalScreenKindV1
import dev.termlink.protocol.realtime.v1.TerminalSnapshotCiphertextV1
import dev.termlink.protocol.realtime.v1.TerminalSnapshotV1
import dev.termlink.protocol.realtime.v1.TerminalTranscriptCiphertextV1
import dev.termlink.protocol.realtime.v1.TerminalTranscriptPageV1
import java.io.File
import java.util.UUID
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Test
import kotlin.test.assertContentEquals
import kotlin.test.assertEquals
import kotlin.test.assertFails
import kotlin.test.assertFailsWith
import kotlin.test.assertNotEquals

class TerminalSnapshotCryptoTest {
    @Test
    fun opensAndReproducesSharedCSharpFixedVectors() {
        val snapshotPlaintext = vectorBytes("snapshot_plaintext")
        val transcriptPlaintext = vectorBytes("transcript_plaintext")
        val snapshot = TerminalSnapshotV1.parseFrom(snapshotPlaintext)
        val transcript = TerminalTranscriptPageV1.parseFrom(transcriptPlaintext)

        assertEquals(vectorText("snapshot_key_id"), TerminalSnapshotCrypto.snapshotKeyId(RECIPIENT_ID, KEY_EPOCH))
        assertEquals(vectorText("transcript_key_id"), TerminalSnapshotCrypto.transcriptKeyId(RECIPIENT_ID, KEY_EPOCH))
        assertContentEquals(
            vectorBytes("snapshot_key"),
            TerminalSnapshotCrypto.deriveSnapshotKey(
                PAIRWISE_KEY,
                SESSION_ID,
                RECIPIENT_ID,
                KEY_EPOCH,
                vectorText("snapshot_key_id"),
            ),
        )
        assertContentEquals(
            vectorBytes("transcript_key"),
            TerminalSnapshotCrypto.deriveTranscriptKey(
                PAIRWISE_KEY,
                SESSION_ID,
                RECIPIENT_ID,
                KEY_EPOCH,
                vectorText("transcript_key_id"),
            ),
        )

        val snapshotSealed = TerminalSnapshotCrypto.sealSnapshot(
            PAIRWISE_KEY,
            SESSION_ID,
            PC_ID,
            RECIPIENT_ID,
            KEY_EPOCH,
            snapshot,
            vectorBytes("snapshot_nonce"),
        )
        val transcriptSealed = TerminalSnapshotCrypto.sealTranscript(
            PAIRWISE_KEY,
            SESSION_ID,
            PC_ID,
            RECIPIENT_ID,
            KEY_EPOCH,
            transcript,
            INITIAL_BEFORE_LINE,
            vectorBytes("transcript_nonce"),
        )
        assertContentEquals(vectorBytes("snapshot_sealed_object"), snapshotSealed)
        assertContentEquals(vectorBytes("transcript_sealed_object"), transcriptSealed)
        assertContentEquals(
            vectorBytes("snapshot_associated_data"),
            TerminalSnapshotCrypto.snapshotAssociatedData(TerminalSnapshotCiphertextV1.parseFrom(snapshotSealed)),
        )
        assertContentEquals(
            vectorBytes("transcript_associated_data"),
            TerminalSnapshotCrypto.transcriptAssociatedData(TerminalTranscriptCiphertextV1.parseFrom(transcriptSealed)),
        )
        val openedSnapshot = openSnapshot(snapshotSealed)
        assertEquals(listOf(0L, 0L), openedSnapshot.alternateScreen.visibleRowsList.map { it.lineId })
        assertContentEquals(
            snapshotPlaintext,
            openedSnapshot.toBuilder().setAlternateScreen(snapshot.alternateScreen).build().toByteArray(),
        )
        assertContentEquals(transcriptPlaintext, openTranscript(transcriptSealed).toByteArray())
    }

    @Test
    fun openSnapshotRejectsWrongSelectionMetadataAndKey() {
        val sealed = vectorBytes("snapshot_sealed_object")
        assertFails("wrong pairwise key") { openSnapshot(sealed, pairwiseKey = ByteArray(32) { 0x5a }) }
        assertFails("wrong session") { openSnapshot(sealed, sessionId = OTHER_ID) }
        assertFails("wrong PC") { openSnapshot(sealed, pcDeviceId = OTHER_ID) }
        assertFails("wrong recipient") { openSnapshot(sealed, recipientDeviceId = OTHER_ID) }
        assertFails("wrong epoch") { openSnapshot(sealed, keyEpoch = KEY_EPOCH + 1) }
        assertFails("wrong key ID") { openSnapshot(sealed, keyId = "snapshot-v1:wrong-key") }
        assertFails("wrong sequence") { openSnapshot(sealed, sequence = SNAPSHOT_SEQUENCE + 1u) }
        assertFails("wrong columns") { openSnapshot(sealed, columns = COLUMNS + 1) }
        assertFails("wrong rows") { openSnapshot(sealed, rows = ROWS + 1) }
    }

    @Test
    fun openSnapshotRejectsOuterMetadataAndCiphertextTampering() {
        assertTamperedSnapshotRejected { setContainerVersion(2) }
        assertTamperedSnapshotRejected { setSessionId(ByteString.copyFrom(SessionCrypto.uuidBytes(OTHER_ID))) }
        assertTamperedSnapshotRejected { setPcDeviceId(ByteString.copyFrom(SessionCrypto.uuidBytes(OTHER_ID))) }
        assertTamperedSnapshotRejected { setRecipientDeviceId(ByteString.copyFrom(SessionCrypto.uuidBytes(OTHER_ID))) }
        assertTamperedSnapshotRejected { setKeyEpoch(KEY_EPOCH + 1) }
        assertTamperedSnapshotRejected { setKeyId("snapshot-v1:wrong-key") }
        assertTamperedSnapshotRejected { setSnapshotSeq((SNAPSHOT_SEQUENCE + 1u).toLong()) }
        assertTamperedSnapshotRejected { setColumns(COLUMNS + 1) }
        assertTamperedSnapshotRejected { setRows(ROWS + 1) }
        assertTamperedSnapshotRejected { setFormatName("TERMLINK_TERMINAL_SNAPSHOT_V2") }
        assertTamperedSnapshotRejected { setFormatVersion(2) }
        assertTamperedSnapshotRejected { setCompression(SnapshotCompressionV1.SNAPSHOT_COMPRESSION_V1_ZSTD) }
        assertTamperedSnapshotRejected { setUncompressedBytes(uncompressedBytes + 1) }
        assertTamperedSnapshotRejected { setNonce(ByteString.copyFrom(ByteArray(12) { 0x44 })) }
        assertTamperedSnapshotRejected {
            val changed = ciphertext.toByteArray()
            changed[changed.lastIndex] = (changed.last().toInt() xor 1).toByte()
            setCiphertext(ByteString.copyFrom(changed))
        }
    }

    @Test
    fun openPathsEnforceBoundsBeforeParsingAndValidateAuthenticatedPlaintext() {
        val snapshotError = assertFailsWith<IllegalArgumentException> {
            openSnapshot(ByteArray(TerminalSnapshotCrypto.MAX_SNAPSHOT_OBJECT_BYTES + 1))
        }
        assertEquals("Sealed snapshot size is invalid", snapshotError.message)
        val transcriptError = assertFailsWith<IllegalArgumentException> {
            openTranscript(ByteArray(TerminalSnapshotCrypto.MAX_TRANSCRIPT_OBJECT_BYTES + 1))
        }
        assertEquals("Sealed transcript size is invalid", transcriptError.message)

        assertFails("invalid authenticated snapshot protobuf") {
            openSnapshot(sealUncheckedSnapshot(byteArrayOf(0x80.toByte())))
        }
        assertFails("authenticated plaintext size mismatch") {
            val plaintext = vectorBytes("snapshot_plaintext")
            openSnapshot(sealUncheckedSnapshot(plaintext) { setUncompressedBytes(plaintext.size.toLong() + 1) })
        }
        assertFails("authenticated inner and outer sequence mismatch") {
            val inner = baseSnapshot.toBuilder().setSnapshotSeq((SNAPSHOT_SEQUENCE + 1u).toLong()).build()
            openSnapshot(sealUncheckedSnapshot(inner.toByteArray()))
        }
        assertFails("authenticated non-ground parser state") {
            val inner = baseSnapshot.toBuilder()
                .setParserState(SnapshotParserStateV1.SNAPSHOT_PARSER_STATE_V1_UNSPECIFIED)
                .build()
            openSnapshot(sealUncheckedSnapshot(inner.toByteArray()))
        }
    }

    @Test
    fun snapshotValidatorRejectsMissingAndOutOfBoundStructure() {
        assertInvalidSnapshot("zero sequence") { it.toBuilder().setSnapshotSeq(0).build() }
        assertInvalidSnapshot("invalid columns") { it.toBuilder().setColumns(0).build() }
        assertInvalidSnapshot("invalid rows") { it.toBuilder().setRows(257).build() }
        assertInvalidSnapshot("unspecified active screen") {
            it.toBuilder().setActiveScreen(TerminalScreenKindV1.TERMINAL_SCREEN_KIND_V1_UNSPECIFIED).build()
        }
        assertInvalidSnapshot("unknown active screen") { it.toBuilder().setActiveScreenValue(99).build() }
        assertInvalidSnapshot("non-ground parser") {
            it.toBuilder().setParserState(SnapshotParserStateV1.SNAPSHOT_PARSER_STATE_V1_UNSPECIFIED).build()
        }
        assertInvalidSnapshot("missing primary screen") { it.toBuilder().clearPrimaryScreen().build() }
        assertInvalidSnapshot("scrollback row") {
            it.toBuilder().addScrollbackRows(it.primaryScreen.visibleRowsList.first()).build()
        }
        assertInvalidSnapshot("missing style table") { it.toBuilder().clearStyleTable().build() }
        assertInvalidSnapshot("short palette") {
            val palette = it.palette.toBuilder()
                .clearColorsRgb()
                .addAllColorsRgb(it.palette.colorsRgbList.dropLast(1))
                .build()
            it.toBuilder().setPalette(palette).build()
        }
        assertInvalidSnapshot("duplicate tab stop") { it.toBuilder().addTabStops(4).build() }
        assertInvalidSnapshot("missing visible row") {
            val screen = it.primaryScreen.toBuilder().removeVisibleRows(1).build()
            it.toBuilder().setPrimaryScreen(screen).build()
        }
        assertInvalidSnapshot("screen pending-wrap mismatch") {
            val screen = it.primaryScreen.toBuilder().setPendingWrap(!it.primaryScreen.cursor.pendingWrap).build()
            it.toBuilder().setPrimaryScreen(screen).build()
        }
    }

    @Test
    fun snapshotValidatorRejectsUnsignedOverflowsAndUnknownEnums() {
        assertInvalidSnapshot("unsigned scroll top") { mutatePrimaryScreen(it) { setScrollTop(-1) } }
        assertInvalidSnapshot("unsigned margin left") { mutatePrimaryScreen(it) { setMarginLeft(-1) } }
        assertInvalidSnapshot("unsigned current style") { mutatePrimaryScreen(it) { setCurrentStyleIndex(-1) } }
        assertInvalidSnapshot("unsigned cursor column") { mutatePrimaryCursor(it) { setColumn(-1) } }
        assertInvalidSnapshot("unsigned cursor style index") { mutatePrimaryCursor(it) { setRenditionStyleIndex(-1) } }
        assertInvalidSnapshot("unspecified cursor style") {
            mutatePrimaryCursor(it) { setStyle(TerminalCursorStyleV1.TERMINAL_CURSOR_STYLE_V1_UNSPECIFIED) }
        }
        assertInvalidSnapshot("unknown cursor style") { mutatePrimaryCursor(it) { setStyleValue(99) } }
        assertInvalidSnapshot("unspecified charset") {
            mutatePrimaryCursorCharset(it) { setG0(TerminalCharsetV1.TERMINAL_CHARSET_V1_UNSPECIFIED) }
        }
        assertInvalidSnapshot("unknown charset") { mutatePrimaryCursorCharset(it) { setG0Value(99) } }
        assertInvalidSnapshot("unknown mouse tracking") {
            it.toBuilder().setModes(it.modes.toBuilder().setMouseTrackingValue(99)).build()
        }
        assertInvalidSnapshot("unknown mouse encoding") {
            it.toBuilder().setModes(it.modes.toBuilder().setMouseEncodingValue(99)).build()
        }
        assertInvalidSnapshot("unsigned run style index") { mutatePrimaryRun(it, 0) { setStyleIndex(-1) } }
        assertInvalidSnapshot("unsigned hyperlink index") {
            val style = it.getStyleTable(0).toBuilder().setHyperlinkIndexPlusOne(-1)
            it.toBuilder().setStyleTable(0, style).build()
        }
        assertInvalidSnapshot("unsigned palette color index") {
            val style = it.getStyleTable(0).toBuilder()
                .setForeground(it.getStyleTable(0).foreground.toBuilder().setPaletteIndex(-1))
            it.toBuilder().setStyleTable(0, style).build()
        }
        assertInvalidSnapshot("unknown underline style") {
            val style = it.getStyleTable(0).toBuilder().setUnderlineStyleValue(99)
            it.toBuilder().setStyleTable(0, style).build()
        }
    }

    @Test
    fun snapshotValidatorRejectsInvalidRowsWideCellsAndUtf8Bounds() {
        assertInvalidSnapshot("row wider than columns") { mutatePrimaryRun(it, 0, rowIndex = 1) { setRepeat(7) } }
        assertInvalidSnapshot("zero repeat") { mutatePrimaryRun(it, 0) { setRepeat(0) } }
        assertInvalidSnapshot("repeated non-empty grapheme") { mutatePrimaryRun(it, 0) { setRepeat(2) } }
        assertInvalidSnapshot("empty width-two lead") { mutatePrimaryRun(it, 1) { clearGrapheme() } }
        assertInvalidSnapshot("wide continuation style mismatch") { mutatePrimaryRun(it, 2) { setStyleIndex(0) } }
        assertInvalidSnapshot("stray wide continuation") {
            mutatePrimaryRun(it, 0) {
                clearGrapheme()
                setDisplayWidth(0)
                setWideContinuation(true)
            }
        }
        assertInvalidSnapshot("grapheme exceeds UTF-8 bound") {
            mutatePrimaryRun(it, 0) { setGrapheme("x".repeat(65)) }
        }
        assertInvalidSnapshot("title exceeds UTF-8 bound") {
            it.toBuilder().setTitle("\u754c".repeat(1_366)).build()
        }
        assertInvalidSnapshot("malformed Unicode title") {
            it.toBuilder().setTitle("\ud800").build()
        }
    }

    @Test
    fun snapshotValidatorEnforcesLifecycleExitCodePresence() {
        assertInvalidSnapshot("ended without exit code") {
            val projection = it.projection.toBuilder()
                .setLifecycle(TerminalLifecycleV1.TERMINAL_LIFECYCLE_V1_ENDED)
                .clearExitCode()
            it.toBuilder().setProjection(projection).build()
        }
        assertInvalidSnapshot("running with exit code") {
            val projection = it.projection.toBuilder()
                .setLifecycle(TerminalLifecycleV1.TERMINAL_LIFECYCLE_V1_RUNNING)
                .setExitCode(0)
            it.toBuilder().setProjection(projection).build()
        }
        assertInvalidSnapshot("unknown lifecycle") {
            it.toBuilder().setProjection(it.projection.toBuilder().setLifecycleValue(99)).build()
        }

        val ended = baseSnapshot.toBuilder().setProjection(
            baseSnapshot.projection.toBuilder()
                .setLifecycle(TerminalLifecycleV1.TERMINAL_LIFECYCLE_V1_ENDED)
                .setExitCode(0),
        ).build()
        TerminalSnapshotCrypto.validateSnapshot(ended)
        assertEquals(0, ended.projection.exitCode)
    }

    @Test
    fun snapshotValidatorFreezesPrimaryNamespaceAndSignedRange() {
        assertEquals(INITIAL_BEFORE_LINE, TerminalSnapshotCrypto.initialTranscriptBeforeLineId(baseSnapshot))
        TerminalSnapshotCrypto.validateInitialTranscriptPage(baseSnapshot, baseTranscript)
        TerminalSnapshotCrypto.validateSnapshot(baseSnapshot)
        assertFailsWith<IllegalArgumentException>("legacy alternate IDs are not producer-canonical") {
            TerminalSnapshotCrypto.validateSnapshotForProducer(baseSnapshot)
        }

        val canonical = baseSnapshot.toBuilder().setAlternateScreen(
            baseSnapshot.alternateScreen.toBuilder()
                .setVisibleRows(0, baseSnapshot.alternateScreen.getVisibleRows(0).toBuilder().setLineId(0))
                .setVisibleRows(1, baseSnapshot.alternateScreen.getVisibleRows(1).toBuilder().setLineId(0)),
        ).build()
        TerminalSnapshotCrypto.validateSnapshotForProducer(canonical)

        assertInvalidSnapshot("zero primary line ID") {
            mutatePrimaryScreen(it) { setVisibleRows(0, getVisibleRows(0).toBuilder().setLineId(0)) }
        }
        assertInvalidSnapshot("duplicate primary line ID") {
            mutatePrimaryScreen(it) { setVisibleRows(1, getVisibleRows(1).toBuilder().setLineId(101)) }
        }
        assertInvalidSnapshot("wrong next transcript line ID") { it.toBuilder().setNextTranscriptLineId(104).build() }
        assertInvalidSnapshot("primary line ID leaves no signed successor") {
            mutatePrimaryScreen(it) {
                setVisibleRows(1, getVisibleRows(1).toBuilder().setLineId(Long.MAX_VALUE))
            }
        }
        assertInvalidSnapshot("snapshot sequence exceeds signed range") {
            it.toBuilder().setSnapshotSeq(Long.MIN_VALUE).build()
        }
        assertInvalidSnapshot("alternate line ID exceeds signed range") {
            it.toBuilder().setAlternateScreen(
                it.alternateScreen.toBuilder()
                    .setVisibleRows(0, it.alternateScreen.getVisibleRows(0).toBuilder().setLineId(Long.MIN_VALUE)),
            ).build()
        }
        assertInvalidSnapshot("row sequence exceeds signed range") {
            mutatePrimaryScreen(it) {
                setVisibleRows(0, getVisibleRows(0).toBuilder().setSealedAtSeq(Long.MIN_VALUE))
            }
        }
        assertInvalidSnapshot("non-zero empty-scrollback sentinel") {
            it.toBuilder().setScrollbackFirstLineId(1).build()
        }
        assertInvalidSnapshot("local input epoch exceeds signed range") {
            it.toBuilder().setProjection(
                it.projection.toBuilder().setObservedLocalInputEpoch(Long.MIN_VALUE),
            ).build()
        }
    }

    @Test
    fun transcriptOpenAndValidatorEnforceCursorSequenceAndBounds() {
        val sealed = vectorBytes("transcript_sealed_object")
        assertFails("wrong as-of sequence") { openTranscript(sealed, asOf = SNAPSHOT_SEQUENCE + 1u) }
        assertFails("wrong first line") { openTranscript(sealed, first = FIRST_LINE + 1u) }
        assertFails("wrong last line") { openTranscript(sealed, last = LAST_LINE + 1u) }
        assertFails("row is not below exclusive cursor") { openTranscript(sealed, before = LAST_LINE) }
        assertFails("tampered transcript ciphertext") {
            val container = TerminalTranscriptCiphertextV1.parseFrom(sealed).toBuilder()
            val ciphertext = container.ciphertext.toByteArray()
            ciphertext[ciphertext.lastIndex] = (ciphertext.last().toInt() xor 1).toByte()
            openTranscript(container.setCiphertext(ByteString.copyFrom(ciphertext)).build().toByteArray())
        }

        assertInvalidTranscript("empty page") { it.toBuilder().clearRows().build() }
        assertInvalidTranscript("zero first line") { it.toBuilder().setFirstLineId(0).build() }
        assertInvalidTranscript("wrong exclusive cursor") { it.toBuilder().setNextBeforeLineId(FIRST_LINE.toLong() - 1).build() }
        assertInvalidTranscript("cursor present without earlier page") {
            it.toBuilder().setHasMoreBefore(false).setNextBeforeLineId(FIRST_LINE.toLong()).build()
        }
        assertInvalidTranscript("duplicate line ID") {
            val second = it.getRows(1).toBuilder().setLineId(FIRST_LINE.toLong())
            it.toBuilder().setRows(1, second).build()
        }
        assertInvalidTranscript("zero sealed sequence") {
            val first = it.getRows(0).toBuilder().setSealedAtSeq(0)
            it.toBuilder().setRows(0, first).build()
        }
        assertInvalidTranscript("row newer than snapshot") {
            val first = it.getRows(0).toBuilder().setSealedAtSeq(SNAPSHOT_SEQUENCE.toLong() + 1)
            it.toBuilder().setRows(0, first).build()
        }
        assertInvalidTranscript("snapshot anchor exceeds signed range") {
            it.toBuilder().setAsOfSnapshotSeq(Long.MIN_VALUE).build()
        }
        assertInvalidTranscript("line ID exceeds signed range") {
            val second = it.getRows(1).toBuilder().setLineId(Long.MIN_VALUE)
            it.toBuilder().setRows(1, second).setLastLineId(Long.MIN_VALUE).build()
        }
        assertInvalidTranscript("sealed sequence exceeds signed range") {
            val first = it.getRows(0).toBuilder().setSealedAtSeq(Long.MIN_VALUE)
            it.toBuilder().setRows(0, first).build()
        }
        assertInvalidTranscript("row wider than maximum") {
            val row = it.getRows(0).toBuilder().clearRuns().addRuns(
                TerminalCellRunV1.newBuilder().setRepeat(513).setDisplayWidth(1),
            )
            it.toBuilder().setRows(0, row).build()
        }
        assertInvalidTranscript("more than 200 rows") { transcriptWithRows(it, 201) }
        assertInvalidTranscript("unknown shared style enum") {
            val style = it.getStyleTable(0).toBuilder().setUnderlineStyleValue(99)
            it.toBuilder().setStyleTable(0, style).build()
        }
        assertInvalidTranscript("unsigned shared style index") {
            val row = it.getRows(0).toBuilder()
            val run = row.getRuns(0).toBuilder().setStyleIndex(-1)
            row.setRuns(0, run)
            it.toBuilder().setRows(0, row).build()
        }
        assertFailsWith<IllegalArgumentException>("page anchor changes between requests") {
            TerminalSnapshotCrypto.validateTranscriptForRequest(
                baseTranscript,
                SNAPSHOT_SEQUENCE + 1u,
                INITIAL_BEFORE_LINE,
            )
        }
    }

    @Test
    fun snapshotAndTranscriptKeysAreDomainSeparated() {
        val snapshot = TerminalSnapshotCrypto.deriveSnapshotKey(
            PAIRWISE_KEY,
            SESSION_ID,
            RECIPIENT_ID,
            KEY_EPOCH,
            TerminalSnapshotCrypto.snapshotKeyId(RECIPIENT_ID, KEY_EPOCH),
        )
        val transcript = TerminalSnapshotCrypto.deriveTranscriptKey(
            PAIRWISE_KEY,
            SESSION_ID,
            RECIPIENT_ID,
            KEY_EPOCH,
            TerminalSnapshotCrypto.transcriptKeyId(RECIPIENT_ID, KEY_EPOCH),
        )
        assertNotEquals(hex(snapshot), hex(transcript))
    }

    private fun openSnapshot(
        sealedObject: ByteArray = vectorBytes("snapshot_sealed_object"),
        pairwiseKey: ByteArray = PAIRWISE_KEY,
        sessionId: UUID = SESSION_ID,
        pcDeviceId: UUID = PC_ID,
        recipientDeviceId: UUID = RECIPIENT_ID,
        keyEpoch: Long = KEY_EPOCH,
        keyId: String = TerminalSnapshotCrypto.snapshotKeyId(recipientDeviceId, keyEpoch),
        sequence: ULong = SNAPSHOT_SEQUENCE,
        columns: Int = COLUMNS,
        rows: Int = ROWS,
    ): TerminalSnapshotV1 = TerminalSnapshotCrypto.openSnapshot(
        pairwiseKey,
        sealedObject,
        sessionId,
        pcDeviceId,
        recipientDeviceId,
        keyEpoch,
        keyId,
        sequence,
        columns,
        rows,
    )

    private fun openTranscript(
        sealedObject: ByteArray = vectorBytes("transcript_sealed_object"),
        asOf: ULong = SNAPSHOT_SEQUENCE,
        first: ULong = FIRST_LINE,
        last: ULong = LAST_LINE,
        before: ULong = INITIAL_BEFORE_LINE,
    ): TerminalTranscriptPageV1 = TerminalSnapshotCrypto.openTranscript(
        PAIRWISE_KEY,
        sealedObject,
        SESSION_ID,
        PC_ID,
        RECIPIENT_ID,
        KEY_EPOCH,
        TerminalSnapshotCrypto.transcriptKeyId(RECIPIENT_ID, KEY_EPOCH),
        asOf,
        first,
        last,
        before,
    )

    private fun assertTamperedSnapshotRejected(mutate: TerminalSnapshotCiphertextV1.Builder.() -> Unit) {
        val container = TerminalSnapshotCiphertextV1.parseFrom(vectorBytes("snapshot_sealed_object"))
            .toBuilder()
            .apply(mutate)
            .build()
        assertFails { openSnapshot(container.toByteArray()) }
    }

    private fun sealUncheckedSnapshot(
        plaintext: ByteArray,
        mutate: TerminalSnapshotCiphertextV1.Builder.() -> Unit = {},
    ): ByteArray {
        val builder = TerminalSnapshotCiphertextV1.parseFrom(vectorBytes("snapshot_sealed_object"))
            .toBuilder()
            .clearCiphertext()
            .setUncompressedBytes(plaintext.size.toLong())
            .apply(mutate)
        val metadata = builder.build()
        val key = TerminalSnapshotCrypto.deriveSnapshotKey(
            PAIRWISE_KEY,
            SESSION_ID,
            RECIPIENT_ID,
            KEY_EPOCH,
            TerminalSnapshotCrypto.snapshotKeyId(RECIPIENT_ID, KEY_EPOCH),
        )
        val aad = TerminalSnapshotCrypto.snapshotAssociatedData(metadata)
        return try {
            builder.setCiphertext(
                ByteString.copyFrom(SessionCrypto.encrypt(key, metadata.nonce.toByteArray(), aad, plaintext)),
            ).build().toByteArray()
        } finally {
            key.fill(0)
            aad.fill(0)
        }
    }

    private fun assertInvalidSnapshot(label: String, mutate: (TerminalSnapshotV1) -> TerminalSnapshotV1) {
        assertFailsWith<IllegalArgumentException>(label) {
            TerminalSnapshotCrypto.validateSnapshot(mutate(baseSnapshot))
        }
    }

    private fun assertInvalidTranscript(
        label: String,
        mutate: (TerminalTranscriptPageV1) -> TerminalTranscriptPageV1,
    ) {
        assertFailsWith<IllegalArgumentException>(label) {
            TerminalSnapshotCrypto.validateTranscript(mutate(baseTranscript))
        }
    }

    private fun mutatePrimaryScreen(
        snapshot: TerminalSnapshotV1,
        mutate: dev.termlink.protocol.realtime.v1.TerminalScreenV1.Builder.() -> Unit,
    ): TerminalSnapshotV1 {
        val screen = snapshot.primaryScreen.toBuilder().apply(mutate)
        return snapshot.toBuilder().setPrimaryScreen(screen).build()
    }

    private fun mutatePrimaryCursor(
        snapshot: TerminalSnapshotV1,
        mutate: dev.termlink.protocol.realtime.v1.TerminalCursorStateV1.Builder.() -> Unit,
    ): TerminalSnapshotV1 = mutatePrimaryScreen(snapshot) {
        setCursor(cursor.toBuilder().apply(mutate))
    }

    private fun mutatePrimaryCursorCharset(
        snapshot: TerminalSnapshotV1,
        mutate: dev.termlink.protocol.realtime.v1.TerminalCharsetStateV1.Builder.() -> Unit,
    ): TerminalSnapshotV1 = mutatePrimaryCursor(snapshot) {
        setCharset(charset.toBuilder().apply(mutate))
    }

    private fun mutatePrimaryRun(
        snapshot: TerminalSnapshotV1,
        runIndex: Int,
        rowIndex: Int = 0,
        mutate: TerminalCellRunV1.Builder.() -> Unit,
    ): TerminalSnapshotV1 = mutatePrimaryScreen(snapshot) {
        val row = getVisibleRows(rowIndex).toBuilder()
        row.setRuns(runIndex, row.getRuns(runIndex).toBuilder().apply(mutate))
        setVisibleRows(rowIndex, row)
    }

    private fun transcriptWithRows(page: TerminalTranscriptPageV1, count: Int): TerminalTranscriptPageV1 {
        val builder = page.toBuilder().clearRows()
        repeat(count) { index ->
            builder.addRows(
                page.getRows(0).toBuilder()
                    .setLineId((index + 1).toLong())
                    .setSealedAtSeq(1)
                    .build(),
            )
        }
        return builder
            .setFirstLineId(1)
            .setLastLineId(count.toLong())
            .setNextBeforeLineId(1)
            .build()
    }

    private val baseSnapshot: TerminalSnapshotV1
        get() = TerminalSnapshotV1.parseFrom(vectorBytes("snapshot_plaintext"))

    private val baseTranscript: TerminalTranscriptPageV1
        get() = TerminalTranscriptPageV1.parseFrom(vectorBytes("transcript_plaintext"))

    private val vector by lazy {
        Json.parseToJsonElement(resolveVectorFile().readText(Charsets.UTF_8)).jsonObject
    }

    private fun vectorText(name: String): String = vector.getValue(name).jsonPrimitive.content
    private fun vectorBytes(name: String): ByteArray = decodeHex(vectorText(name))

    private fun resolveVectorFile(): File {
        val candidates = generateSequence(File(System.getProperty("user.dir") ?: ".").absoluteFile) { it.parentFile }
            .map { File(it, "protocol/test-vectors/terminal-snapshot-v1.json") }
        return candidates.firstOrNull(File::isFile)
            ?: error("Unable to locate protocol/test-vectors/terminal-snapshot-v1.json")
    }

    private fun decodeHex(value: String): ByteArray {
        require(value.length % 2 == 0)
        return ByteArray(value.length / 2) { index ->
            value.substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
    }

    private fun hex(value: ByteArray): String = value.joinToString("") { byte -> "%02x".format(byte.toInt() and 0xff) }

    private companion object {
        val PC_ID: UUID = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
        val RECIPIENT_ID: UUID = UUID.fromString("10213243-5465-7687-98a9-bacbdcedfe0f")
        val SESSION_ID: UUID = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee")
        val OTHER_ID: UUID = UUID.fromString("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")
        val PAIRWISE_KEY: ByteArray = decodeHexStatic("b1632a5a77cafec55f885fc4c3588f303ed3ca9856d5c464636e0b5feaefa773")
        const val KEY_EPOCH: Long = 7
        val SNAPSHOT_SEQUENCE: ULong = 42u
        const val COLUMNS: Int = 6
        const val ROWS: Int = 2
        val FIRST_LINE: ULong = 90u
        val LAST_LINE: ULong = 91u
        val INITIAL_BEFORE_LINE: ULong = 101u

        private fun decodeHexStatic(value: String): ByteArray = ByteArray(value.length / 2) { index ->
            value.substring(index * 2, index * 2 + 2).toInt(16).toByte()
        }
    }
}
