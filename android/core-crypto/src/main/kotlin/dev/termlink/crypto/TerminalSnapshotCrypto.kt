package dev.termlink.crypto

import com.google.protobuf.ByteString
import com.google.protobuf.InvalidProtocolBufferException
import dev.termlink.protocol.realtime.v1.SnapshotCompressionV1
import dev.termlink.protocol.realtime.v1.SnapshotParserStateV1
import dev.termlink.protocol.realtime.v1.TerminalCharsetStateV1
import dev.termlink.protocol.realtime.v1.TerminalCharsetV1
import dev.termlink.protocol.realtime.v1.TerminalColorRefV1
import dev.termlink.protocol.realtime.v1.TerminalCoreSnapshotCiphertextV2
import dev.termlink.protocol.realtime.v1.TerminalCursorStateV1
import dev.termlink.protocol.realtime.v1.TerminalCursorStyleV1
import dev.termlink.protocol.realtime.v1.TerminalHyperlinkV1
import dev.termlink.protocol.realtime.v1.TerminalLifecycleV1
import dev.termlink.protocol.realtime.v1.TerminalModesV1
import dev.termlink.protocol.realtime.v1.TerminalMouseEncodingV1
import dev.termlink.protocol.realtime.v1.TerminalMouseTrackingV1
import dev.termlink.protocol.realtime.v1.TerminalPaletteV1
import dev.termlink.protocol.realtime.v1.TerminalRowV1
import dev.termlink.protocol.realtime.v1.TerminalScreenKindV1
import dev.termlink.protocol.realtime.v1.TerminalScreenV1
import dev.termlink.protocol.realtime.v1.TerminalSessionProjectionV1
import dev.termlink.protocol.realtime.v1.TerminalSnapshotCiphertextV1
import dev.termlink.protocol.realtime.v1.TerminalSnapshotV1
import dev.termlink.protocol.realtime.v1.TerminalStyleV1
import dev.termlink.protocol.realtime.v1.TerminalTranscriptCiphertextV1
import dev.termlink.protocol.realtime.v1.TerminalTranscriptPageV1
import dev.termlink.protocol.realtime.v1.TerminalUnderlineStyleV1
import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.CharBuffer
import java.nio.charset.CodingErrorAction
import java.security.SecureRandom
import java.util.UUID
import org.bouncycastle.crypto.digests.SHA256Digest
import org.bouncycastle.crypto.generators.HKDFBytesGenerator
import org.bouncycastle.crypto.params.HKDFParameters

object TerminalSnapshotCrypto {
    const val CONTAINER_VERSION: Int = 1
    const val FORMAT_VERSION: Int = 1
    const val MAX_SNAPSHOT_OBJECT_BYTES: Int = 2 * 1024 * 1024
    const val MAX_SNAPSHOT_PLAINTEXT_BYTES: Int = 8 * 1024 * 1024
    const val MAX_TRANSCRIPT_OBJECT_BYTES: Int = 512 * 1024
    const val MAX_TRANSCRIPT_PLAINTEXT_BYTES: Int = 2 * 1024 * 1024
    const val SNAPSHOT_FORMAT_NAME: String = "TERMLINK_TERMINAL_SNAPSHOT"
    const val TRANSCRIPT_FORMAT_NAME: String = "TERMLINK_TERMINAL_TRANSCRIPT"
    const val TERMINAL_CORE_SNAPSHOT_CONTAINER_VERSION: Int = 2
    const val TERMINAL_CORE_SNAPSHOT_FORMAT_VERSION: Int = 2
    const val MAX_TERMINAL_CORE_SNAPSHOT_OBJECT_BYTES: Int = 64 * 1024 * 1024
    const val MAX_TERMINAL_CORE_SNAPSHOT_PLAINTEXT_BYTES: Int =
        MAX_TERMINAL_CORE_SNAPSHOT_OBJECT_BYTES - 4 * 1024
    const val TERMINAL_CORE_SNAPSHOT_FORMAT_NAME: String = "TERMLINK_TERMINAL_CORE_SNAPSHOT"

    private const val MAX_COLUMNS = 512
    private const val MAX_ROWS = 256
    private const val MAX_EXPANDED_SNAPSHOT_CELLS = 262_144
    private const val MAX_TRANSCRIPT_ROWS = 200
    private const val MAX_STYLES = 1_024
    private const val MAX_HYPERLINKS = 1_024
    private const val MAX_TITLE_BYTES = 4_096
    private const val MAX_GRAPHEME_BYTES = 64
    private const val MAX_HYPERLINK_ID_BYTES = 256
    private const val MAX_HYPERLINK_URI_BYTES = 4_096
    private const val MAX_HYPERLINK_PARAMS_BYTES = 1_024
    private const val SUPPORTED_STYLE_ATTRIBUTES = 0x1ff
    private val MAX_SUPPORTED_UINT64 = Long.MAX_VALUE.toULong()

    private val snapshotKeyPrefix = "TERMLINK-TERMINAL-SNAPSHOT-KEY-V1\u0000".encodeToByteArray()
    private val transcriptKeyPrefix = "TERMLINK-TERMINAL-TRANSCRIPT-KEY-V1\u0000".encodeToByteArray()
    private val snapshotAadPrefix = "TERMLINK-TERMINAL-SNAPSHOT-OBJECT-V1\u0000".encodeToByteArray()
    private val transcriptAadPrefix = "TERMLINK-TERMINAL-TRANSCRIPT-OBJECT-V1\u0000".encodeToByteArray()
    private val terminalCoreSnapshotKeyPrefix =
        "TERMLINK-TERMINAL-CORE-SNAPSHOT-KEY-V2\u0000".encodeToByteArray()
    private val terminalCoreSnapshotAadPrefix =
        "TERMLINK-TERMINAL-CORE-SNAPSHOT-OBJECT-V2\u0000".encodeToByteArray()

    data class OpenedTerminalCoreSnapshot(
        val checkpoint: ByteArray,
        val initialTranscriptBeforeLineId: ULong,
        val observedLocalInputEpoch: ULong,
    )

    fun snapshotKeyId(recipientDeviceId: UUID, keyEpoch: Long): String =
        keyId("snapshot-v1:", recipientDeviceId, keyEpoch)

    fun transcriptKeyId(recipientDeviceId: UUID, keyEpoch: Long): String =
        keyId("transcript-v1:", recipientDeviceId, keyEpoch)

    fun terminalCoreSnapshotKeyId(recipientDeviceId: UUID, keyEpoch: Long): String =
        keyId("terminal-core-snapshot-v2:", recipientDeviceId, keyEpoch)

    fun sealSnapshot(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        pcDeviceId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        snapshot: TerminalSnapshotV1,
        nonce: ByteArray = randomNonce(),
    ): ByteArray {
        validatePairwiseAndIdentity(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch)
        validateSnapshot(snapshot)
        val plaintext = snapshot.toByteArray()
        val nonceBytes = nonce.copyOf()
        try {
            require(plaintext.size <= MAX_SNAPSHOT_PLAINTEXT_BYTES) { "Snapshot plaintext exceeds the Phase 1 bound" }
            require(nonceBytes.size == 12) { "Snapshot nonce must be 12 bytes" }
            val keyId = snapshotKeyId(recipientDeviceId, keyEpoch)
            val container = TerminalSnapshotCiphertextV1.newBuilder()
                .setContainerVersion(CONTAINER_VERSION)
                .setSessionId(ByteString.copyFrom(SessionCrypto.uuidBytes(sessionId)))
                .setPcDeviceId(ByteString.copyFrom(SessionCrypto.uuidBytes(pcDeviceId)))
                .setRecipientDeviceId(ByteString.copyFrom(SessionCrypto.uuidBytes(recipientDeviceId)))
                .setKeyEpoch(keyEpoch)
                .setKeyId(keyId)
                .setSnapshotSeq(snapshot.snapshotSeq)
                .setColumns(snapshot.columns)
                .setRows(snapshot.rows)
                .setFormatName(SNAPSHOT_FORMAT_NAME)
                .setFormatVersion(FORMAT_VERSION)
                .setCompression(SnapshotCompressionV1.SNAPSHOT_COMPRESSION_V1_NONE)
                .setUncompressedBytes(plaintext.size.toLong())
                .setNonce(ByteString.copyFrom(nonceBytes))
                .build()
            val key = deriveSnapshotKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, keyId)
            val aad = snapshotAssociatedData(container)
            return try {
                val ciphertext = SessionCrypto.encrypt(key, nonceBytes, aad, plaintext)
                try {
                    val result = container.toBuilder().setCiphertext(ByteString.copyFrom(ciphertext)).build().toByteArray()
                    require(result.size <= MAX_SNAPSHOT_OBJECT_BYTES) { "Sealed snapshot exceeds the Phase 1 bound" }
                    result
                } finally {
                    ciphertext.fill(0)
                }
            } finally {
                key.fill(0)
                aad.fill(0)
            }
        } finally {
            plaintext.fill(0)
            nonceBytes.fill(0)
        }
    }

    fun openSnapshot(
        pairwiseKey: ByteArray,
        sealedObject: ByteArray,
        expectedSessionId: UUID,
        expectedPcDeviceId: UUID,
        expectedRecipientDeviceId: UUID,
        expectedKeyEpoch: Long,
        expectedKeyId: String,
        expectedSnapshotSequence: ULong,
        expectedColumns: Int,
        expectedRows: Int,
    ): TerminalSnapshotV1 {
        validatePairwiseAndIdentity(pairwiseKey, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId, expectedKeyEpoch)
        require(sealedObject.isNotEmpty() && sealedObject.size <= MAX_SNAPSHOT_OBJECT_BYTES) { "Sealed snapshot size is invalid" }
        val container = parseSnapshotContainer(sealedObject)
        validateSnapshotContainer(
            container,
            expectedSessionId,
            expectedPcDeviceId,
            expectedRecipientDeviceId,
            expectedKeyEpoch,
            expectedKeyId,
            expectedSnapshotSequence,
            expectedColumns,
            expectedRows,
        )
        val key = deriveSnapshotKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, expectedKeyId)
        val aad = snapshotAssociatedData(container)
        val plaintext = try {
            SessionCrypto.decrypt(key, container.nonce.toByteArray(), aad, container.ciphertext.toByteArray())
        } finally {
            key.fill(0)
            aad.fill(0)
        }
        return try {
            require(plaintext.size.toULong() == container.uncompressedBytes.toULong()) {
                "Snapshot plaintext size does not match authenticated metadata"
            }
            val snapshot = try {
                TerminalSnapshotV1.parseFrom(plaintext)
            } catch (error: InvalidProtocolBufferException) {
                throw IllegalArgumentException("Snapshot plaintext is invalid protobuf", error)
            }
            validateSnapshot(snapshot)
            require(
                snapshot.snapshotSeq == container.snapshotSeq &&
                    snapshot.columns == container.columns && snapshot.rows == container.rows,
            ) { "Snapshot plaintext does not match authenticated metadata" }
            normalizeSnapshotForImport(snapshot)
        } finally {
            plaintext.fill(0)
        }
    }

    fun sealTerminalCoreSnapshot(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        pcDeviceId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        snapshotSequence: ULong,
        columns: Int,
        rows: Int,
        initialTranscriptBeforeLineId: ULong,
        observedLocalInputEpoch: ULong,
        checkpoint: ByteArray,
        nonce: ByteArray = randomNonce(),
    ): ByteArray {
        validatePairwiseAndIdentity(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch)
        validateTerminalCoreSnapshotMetadata(
            snapshotSequence,
            columns,
            rows,
            initialTranscriptBeforeLineId,
            observedLocalInputEpoch,
            checkpoint,
        )
        val nonceBytes = nonce.copyOf()
        try {
            require(nonceBytes.size == 12) { "Terminal-core snapshot nonce must be 12 bytes" }
            val keyId = terminalCoreSnapshotKeyId(recipientDeviceId, keyEpoch)
            val container = TerminalCoreSnapshotCiphertextV2.newBuilder()
                .setContainerVersion(TERMINAL_CORE_SNAPSHOT_CONTAINER_VERSION)
                .setSessionId(ByteString.copyFrom(SessionCrypto.uuidBytes(sessionId)))
                .setPcDeviceId(ByteString.copyFrom(SessionCrypto.uuidBytes(pcDeviceId)))
                .setRecipientDeviceId(ByteString.copyFrom(SessionCrypto.uuidBytes(recipientDeviceId)))
                .setKeyEpoch(keyEpoch)
                .setKeyId(keyId)
                .setSnapshotSeq(snapshotSequence.toLong())
                .setColumns(columns)
                .setRows(rows)
                .setFormatName(TERMINAL_CORE_SNAPSHOT_FORMAT_NAME)
                .setFormatVersion(TERMINAL_CORE_SNAPSHOT_FORMAT_VERSION)
                .setCompression(SnapshotCompressionV1.SNAPSHOT_COMPRESSION_V1_NONE)
                .setUncompressedBytes(checkpoint.size.toLong())
                .setNonce(ByteString.copyFrom(nonceBytes))
                .setInitialTranscriptBeforeLineId(initialTranscriptBeforeLineId.toLong())
                .setObservedLocalInputEpoch(observedLocalInputEpoch.toLong())
                .build()
            val key = deriveTerminalCoreSnapshotKey(
                pairwiseKey,
                sessionId,
                recipientDeviceId,
                keyEpoch,
                keyId,
            )
            val aad = terminalCoreSnapshotAssociatedData(container)
            return try {
                val ciphertext = SessionCrypto.encrypt(key, nonceBytes, aad, checkpoint)
                try {
                    container.toBuilder()
                        .setCiphertext(ByteString.copyFrom(ciphertext))
                        .build()
                        .toByteArray()
                        .also {
                            require(it.size <= MAX_TERMINAL_CORE_SNAPSHOT_OBJECT_BYTES) {
                                "Sealed terminal-core snapshot exceeds the production bound"
                            }
                        }
                } finally {
                    ciphertext.fill(0)
                }
            } finally {
                key.fill(0)
                aad.fill(0)
            }
        } finally {
            nonceBytes.fill(0)
        }
    }

    fun openTerminalCoreSnapshot(
        pairwiseKey: ByteArray,
        sealedObject: ByteArray,
        expectedSessionId: UUID,
        expectedPcDeviceId: UUID,
        expectedRecipientDeviceId: UUID,
        expectedKeyEpoch: Long,
        expectedKeyId: String,
        expectedSnapshotSequence: ULong,
        expectedColumns: Int,
        expectedRows: Int,
    ): OpenedTerminalCoreSnapshot {
        validatePairwiseAndIdentity(
            pairwiseKey,
            expectedSessionId,
            expectedPcDeviceId,
            expectedRecipientDeviceId,
            expectedKeyEpoch,
        )
        require(
            sealedObject.isNotEmpty() &&
                sealedObject.size <= MAX_TERMINAL_CORE_SNAPSHOT_OBJECT_BYTES,
        ) { "Sealed terminal-core snapshot size is invalid" }
        val container = try {
            TerminalCoreSnapshotCiphertextV2.parseFrom(sealedObject)
        } catch (error: InvalidProtocolBufferException) {
            throw IllegalArgumentException("Terminal-core snapshot container is invalid protobuf", error)
        }
        validateTerminalCoreSnapshotContainer(
            container,
            expectedSessionId,
            expectedPcDeviceId,
            expectedRecipientDeviceId,
            expectedKeyEpoch,
            expectedKeyId,
            expectedSnapshotSequence,
            expectedColumns,
            expectedRows,
        )
        val key = deriveTerminalCoreSnapshotKey(
            pairwiseKey,
            expectedSessionId,
            expectedRecipientDeviceId,
            expectedKeyEpoch,
            expectedKeyId,
        )
        val aad = terminalCoreSnapshotAssociatedData(container)
        val plaintext = try {
            SessionCrypto.decrypt(
                key,
                container.nonce.toByteArray(),
                aad,
                container.ciphertext.toByteArray(),
            )
        } finally {
            key.fill(0)
            aad.fill(0)
        }
        try {
            require(plaintext.size.toULong() == container.uncompressedBytes.toULong()) {
                "Terminal-core snapshot plaintext size does not match authenticated metadata"
            }
            validateTerminalCoreSnapshotMetadata(
                container.snapshotSeq.toULong(),
                container.columns,
                container.rows,
                container.initialTranscriptBeforeLineId.toULong(),
                container.observedLocalInputEpoch.toULong(),
                plaintext,
            )
            return OpenedTerminalCoreSnapshot(
                checkpoint = plaintext.copyOf(),
                initialTranscriptBeforeLineId = container.initialTranscriptBeforeLineId.toULong(),
                observedLocalInputEpoch = container.observedLocalInputEpoch.toULong(),
            )
        } finally {
            plaintext.fill(0)
        }
    }

    fun sealTranscript(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        pcDeviceId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        page: TerminalTranscriptPageV1,
        exclusiveBeforeLineId: ULong,
        nonce: ByteArray = randomNonce(),
    ): ByteArray {
        validatePairwiseAndIdentity(pairwiseKey, sessionId, pcDeviceId, recipientDeviceId, keyEpoch)
        validateTranscriptForRequest(page, page.asOfSnapshotSeq.toULong(), exclusiveBeforeLineId)
        val plaintext = page.toByteArray()
        val nonceBytes = nonce.copyOf()
        try {
            require(plaintext.size <= MAX_TRANSCRIPT_PLAINTEXT_BYTES) { "Transcript plaintext exceeds the Phase 1 bound" }
            require(nonceBytes.size == 12) { "Transcript nonce must be 12 bytes" }
            val keyId = transcriptKeyId(recipientDeviceId, keyEpoch)
            val container = TerminalTranscriptCiphertextV1.newBuilder()
                .setContainerVersion(CONTAINER_VERSION)
                .setSessionId(ByteString.copyFrom(SessionCrypto.uuidBytes(sessionId)))
                .setPcDeviceId(ByteString.copyFrom(SessionCrypto.uuidBytes(pcDeviceId)))
                .setRecipientDeviceId(ByteString.copyFrom(SessionCrypto.uuidBytes(recipientDeviceId)))
                .setKeyEpoch(keyEpoch)
                .setKeyId(keyId)
                .setAsOfSnapshotSeq(page.asOfSnapshotSeq)
                .setFirstLineId(page.firstLineId)
                .setLastLineId(page.lastLineId)
                .setFormatName(TRANSCRIPT_FORMAT_NAME)
                .setFormatVersion(FORMAT_VERSION)
                .setCompression(SnapshotCompressionV1.SNAPSHOT_COMPRESSION_V1_NONE)
                .setUncompressedBytes(plaintext.size.toLong())
                .setNonce(ByteString.copyFrom(nonceBytes))
                .build()
            val key = deriveTranscriptKey(pairwiseKey, sessionId, recipientDeviceId, keyEpoch, keyId)
            val aad = transcriptAssociatedData(container)
            return try {
                val ciphertext = SessionCrypto.encrypt(key, nonceBytes, aad, plaintext)
                try {
                    val result = container.toBuilder().setCiphertext(ByteString.copyFrom(ciphertext)).build().toByteArray()
                    require(result.size <= MAX_TRANSCRIPT_OBJECT_BYTES) { "Sealed transcript exceeds the Phase 1 bound" }
                    result
                } finally {
                    ciphertext.fill(0)
                }
            } finally {
                key.fill(0)
                aad.fill(0)
            }
        } finally {
            plaintext.fill(0)
            nonceBytes.fill(0)
        }
    }

    fun openTranscript(
        pairwiseKey: ByteArray,
        sealedObject: ByteArray,
        expectedSessionId: UUID,
        expectedPcDeviceId: UUID,
        expectedRecipientDeviceId: UUID,
        expectedKeyEpoch: Long,
        expectedKeyId: String,
        expectedAsOfSnapshotSequence: ULong,
        expectedFirstLineId: ULong,
        expectedLastLineId: ULong,
        expectedExclusiveBeforeLineId: ULong = MAX_SUPPORTED_UINT64,
    ): TerminalTranscriptPageV1 {
        validatePairwiseAndIdentity(pairwiseKey, expectedSessionId, expectedPcDeviceId, expectedRecipientDeviceId, expectedKeyEpoch)
        require(sealedObject.isNotEmpty() && sealedObject.size <= MAX_TRANSCRIPT_OBJECT_BYTES) { "Sealed transcript size is invalid" }
        val container = parseTranscriptContainer(sealedObject)
        validateTranscriptContainer(
            container,
            expectedSessionId,
            expectedPcDeviceId,
            expectedRecipientDeviceId,
            expectedKeyEpoch,
            expectedKeyId,
            expectedAsOfSnapshotSequence,
            expectedFirstLineId,
            expectedLastLineId,
        )
        val key = deriveTranscriptKey(pairwiseKey, expectedSessionId, expectedRecipientDeviceId, expectedKeyEpoch, expectedKeyId)
        val aad = transcriptAssociatedData(container)
        val plaintext = try {
            SessionCrypto.decrypt(key, container.nonce.toByteArray(), aad, container.ciphertext.toByteArray())
        } finally {
            key.fill(0)
            aad.fill(0)
        }
        return try {
            require(plaintext.size.toULong() == container.uncompressedBytes.toULong()) {
                "Transcript plaintext size does not match authenticated metadata"
            }
            val page = try {
                TerminalTranscriptPageV1.parseFrom(plaintext)
            } catch (error: InvalidProtocolBufferException) {
                throw IllegalArgumentException("Transcript plaintext is invalid protobuf", error)
            }
            validateTranscriptForRequest(page, expectedAsOfSnapshotSequence, expectedExclusiveBeforeLineId)
            require(
                page.asOfSnapshotSeq == container.asOfSnapshotSeq && page.firstLineId == container.firstLineId &&
                    page.lastLineId == container.lastLineId,
            ) { "Transcript plaintext does not match authenticated metadata" }
            page
        } finally {
            plaintext.fill(0)
        }
    }

    fun deriveSnapshotKey(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: String,
    ): ByteArray = deriveObjectKey(
        pairwiseKey,
        sessionId,
        recipientDeviceId,
        keyEpoch,
        keyId,
        snapshotKeyPrefix,
        snapshotKeyId(recipientDeviceId, keyEpoch),
    )

    fun deriveTranscriptKey(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: String,
    ): ByteArray = deriveObjectKey(
        pairwiseKey,
        sessionId,
        recipientDeviceId,
        keyEpoch,
        keyId,
        transcriptKeyPrefix,
        transcriptKeyId(recipientDeviceId, keyEpoch),
    )

    fun deriveTerminalCoreSnapshotKey(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: String,
    ): ByteArray = deriveObjectKey(
        pairwiseKey,
        sessionId,
        recipientDeviceId,
        keyEpoch,
        keyId,
        terminalCoreSnapshotKeyPrefix,
        terminalCoreSnapshotKeyId(recipientDeviceId, keyEpoch),
    )

    fun snapshotAssociatedData(value: TerminalSnapshotCiphertextV1): ByteArray {
        validateCommonContainerMetadata(
            value.sessionId,
            value.pcDeviceId,
            value.recipientDeviceId,
            value.keyEpoch,
            value.keyId,
            value.nonce,
            value.containerVersion,
            value.formatVersion,
            value.formatName,
            value.compression,
            value.uncompressedBytes.toULong(),
            MAX_SNAPSHOT_PLAINTEXT_BYTES,
            SNAPSHOT_FORMAT_NAME,
        )
        require(value.snapshotSeq > 0L) { "Snapshot sequence exceeds the supported signed range" }
        return ByteArrayOutputStream().use { output ->
            output.write(snapshotAadPrefix)
            output.write(uintBytes(value.containerVersion))
            output.write(value.sessionId.toByteArray())
            output.write(value.pcDeviceId.toByteArray())
            output.write(value.recipientDeviceId.toByteArray())
            output.write(longBytes(value.keyEpoch))
            writeString(output, value.keyId, "snapshot key ID")
            output.write(ulongBytes(value.snapshotSeq.toULong()))
            output.write(uintBytes(value.columns))
            output.write(uintBytes(value.rows))
            writeString(output, value.formatName, "snapshot format name")
            output.write(uintBytes(value.formatVersion))
            output.write(uintBytes(value.compressionValue))
            output.write(ulongBytes(value.uncompressedBytes.toULong()))
            output.write(value.nonce.toByteArray())
            output.toByteArray()
        }
    }

    fun transcriptAssociatedData(value: TerminalTranscriptCiphertextV1): ByteArray {
        validateCommonContainerMetadata(
            value.sessionId,
            value.pcDeviceId,
            value.recipientDeviceId,
            value.keyEpoch,
            value.keyId,
            value.nonce,
            value.containerVersion,
            value.formatVersion,
            value.formatName,
            value.compression,
            value.uncompressedBytes.toULong(),
            MAX_TRANSCRIPT_PLAINTEXT_BYTES,
            TRANSCRIPT_FORMAT_NAME,
        )
        require(value.asOfSnapshotSeq > 0L && value.firstLineId > 0L && value.lastLineId >= value.firstLineId) {
            "Transcript sequence metadata exceeds the supported signed range"
        }
        return ByteArrayOutputStream().use { output ->
            output.write(transcriptAadPrefix)
            output.write(uintBytes(value.containerVersion))
            output.write(value.sessionId.toByteArray())
            output.write(value.pcDeviceId.toByteArray())
            output.write(value.recipientDeviceId.toByteArray())
            output.write(longBytes(value.keyEpoch))
            writeString(output, value.keyId, "transcript key ID")
            output.write(ulongBytes(value.asOfSnapshotSeq.toULong()))
            output.write(ulongBytes(value.firstLineId.toULong()))
            output.write(ulongBytes(value.lastLineId.toULong()))
            writeString(output, value.formatName, "transcript format name")
            output.write(uintBytes(value.formatVersion))
            output.write(uintBytes(value.compressionValue))
            output.write(ulongBytes(value.uncompressedBytes.toULong()))
            output.write(value.nonce.toByteArray())
            output.toByteArray()
        }
    }

    fun terminalCoreSnapshotAssociatedData(value: TerminalCoreSnapshotCiphertextV2): ByteArray {
        validateTerminalCoreSnapshotCryptographicMetadata(value)
        return ByteArrayOutputStream().use { output ->
            output.write(terminalCoreSnapshotAadPrefix)
            output.write(uintBytes(value.containerVersion))
            output.write(value.sessionId.toByteArray())
            output.write(value.pcDeviceId.toByteArray())
            output.write(value.recipientDeviceId.toByteArray())
            output.write(longBytes(value.keyEpoch))
            writeString(output, value.keyId, "terminal-core snapshot key ID")
            output.write(ulongBytes(value.snapshotSeq.toULong()))
            output.write(uintBytes(value.columns))
            output.write(uintBytes(value.rows))
            writeString(output, value.formatName, "terminal-core snapshot format name")
            output.write(uintBytes(value.formatVersion))
            output.write(uintBytes(value.compressionValue))
            output.write(ulongBytes(value.uncompressedBytes.toULong()))
            output.write(ulongBytes(value.initialTranscriptBeforeLineId.toULong()))
            output.write(ulongBytes(value.observedLocalInputEpoch.toULong()))
            output.write(value.nonce.toByteArray())
            output.toByteArray()
        }
    }

    private fun validateTerminalCoreSnapshotContainer(
        value: TerminalCoreSnapshotCiphertextV2,
        sessionId: UUID,
        pcDeviceId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: String,
        sequence: ULong,
        columns: Int,
        rows: Int,
    ) {
        validateTerminalCoreSnapshotCryptographicMetadata(value)
        require(
            value.sessionId.toByteArray().contentEquals(SessionCrypto.uuidBytes(sessionId)) &&
                value.pcDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(pcDeviceId)) &&
                value.recipientDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(recipientDeviceId)) &&
                value.keyEpoch == keyEpoch && value.keyId == keyId &&
                value.keyId == terminalCoreSnapshotKeyId(recipientDeviceId, keyEpoch) &&
                value.snapshotSeq.toULong() == sequence && value.columns == columns && value.rows == rows &&
                value.ciphertext.size() in 16..MAX_TERMINAL_CORE_SNAPSHOT_OBJECT_BYTES,
        ) { "Terminal-core snapshot object metadata does not match the requested object" }
    }

    private fun validateTerminalCoreSnapshotCryptographicMetadata(
        value: TerminalCoreSnapshotCiphertextV2,
    ) {
        require(
            value.containerVersion == TERMINAL_CORE_SNAPSHOT_CONTAINER_VERSION &&
                value.sessionId.size() == 16 && value.pcDeviceId.size() == 16 &&
                value.recipientDeviceId.size() == 16 && value.keyEpoch > 0 &&
                utf8Length(value.keyId, "terminal-core snapshot key ID") in 8..128 &&
                value.nonce.size() == 12 &&
                value.formatVersion == TERMINAL_CORE_SNAPSHOT_FORMAT_VERSION &&
                value.formatName == TERMINAL_CORE_SNAPSHOT_FORMAT_NAME &&
                value.compression == SnapshotCompressionV1.SNAPSHOT_COMPRESSION_V1_NONE &&
                value.uncompressedBytes > 0L &&
                value.uncompressedBytes.toULong() <= MAX_TERMINAL_CORE_SNAPSHOT_PLAINTEXT_BYTES.toULong() &&
                value.snapshotSeq > 0L && value.columns in 1..MAX_COLUMNS && value.rows in 1..MAX_ROWS &&
                value.initialTranscriptBeforeLineId > 0L && value.observedLocalInputEpoch >= 0L,
        ) { "Terminal-core snapshot cryptographic metadata is invalid or unsupported" }
    }

    private fun validateTerminalCoreSnapshotMetadata(
        snapshotSequence: ULong,
        columns: Int,
        rows: Int,
        initialTranscriptBeforeLineId: ULong,
        observedLocalInputEpoch: ULong,
        checkpoint: ByteArray,
    ) {
        require(
            snapshotSequence in 1uL..MAX_SUPPORTED_UINT64 && columns in 1..MAX_COLUMNS &&
                rows in 1..MAX_ROWS && initialTranscriptBeforeLineId in 1uL..MAX_SUPPORTED_UINT64 &&
                observedLocalInputEpoch <= MAX_SUPPORTED_UINT64 && checkpoint.isNotEmpty() &&
                checkpoint.size <= MAX_TERMINAL_CORE_SNAPSHOT_PLAINTEXT_BYTES,
        ) { "Terminal-core snapshot metadata is invalid or unsupported" }
    }

    fun validateSnapshot(value: TerminalSnapshotV1) {
        require(
            value.formatVersion == FORMAT_VERSION && value.snapshotSeq > 0L &&
                value.columns in 1..MAX_COLUMNS && value.rows in 1..MAX_ROWS &&
                value.activeScreen in setOf(
                    TerminalScreenKindV1.TERMINAL_SCREEN_KIND_V1_PRIMARY,
                    TerminalScreenKindV1.TERMINAL_SCREEN_KIND_V1_ALTERNATE,
                ) && value.parserState == SnapshotParserStateV1.SNAPSHOT_PARSER_STATE_V1_GROUND &&
                value.hasPrimaryScreen() && value.hasAlternateScreen() && value.hasModes() &&
                value.hasPalette() && value.hasProjection(),
        ) { "Snapshot root metadata is invalid or unsupported" }
        require(value.scrollbackRowsCount == 0 && value.scrollbackFirstLineId == 0L) {
            "Phase 1 snapshot scrollback and its sentinel must be empty"
        }
        validateStyles(value.styleTableList, value.hyperlinkTableList)
        validatePalette(value.palette)
        var previousTab = -1
        value.tabStopsList.forEach { tab ->
            require(tab > previousTab && tab < value.columns) { "Snapshot tab stops are invalid" }
            previousTab = tab
        }
        validateModes(value.modes)
        val primaryCells = validateScreen(
            value.primaryScreen,
            value.columns,
            value.rows,
            value.snapshotSeq.toULong(),
            value.styleTableCount,
        )
        val alternateCells = validateScreen(
            value.alternateScreen,
            value.columns,
            value.rows,
            value.snapshotSeq.toULong(),
            value.styleTableCount,
        )
        var previousPrimaryLineId = 0L
        value.primaryScreen.visibleRowsList.forEach { row ->
            require(row.lineId > previousPrimaryLineId) { "Primary line IDs must be positive and strictly increasing" }
            previousPrimaryLineId = row.lineId
        }
        require(previousPrimaryLineId < Long.MAX_VALUE && value.nextTranscriptLineId == previousPrimaryLineId + 1L) {
            "Snapshot next transcript line ID is inconsistent with the primary screen"
        }
        value.alternateScreen.visibleRowsList.forEach { row ->
            require(row.lineId >= 0L) { "Alternate line ID exceeds the supported signed range" }
        }
        val cells = primaryCells + alternateCells
        require(cells <= MAX_EXPANDED_SNAPSHOT_CELLS) { "Snapshot expanded cell count exceeds the Phase 1 bound" }
        require(utf8Length(value.title, "snapshot title") <= MAX_TITLE_BYTES) { "Snapshot title exceeds the Phase 1 bound" }
        validateProjection(value.projection)
    }

    /** New V1 producers emit zero alternate IDs; importers ignore legacy non-zero values. */
    fun validateSnapshotForProducer(value: TerminalSnapshotV1) {
        validateSnapshot(value)
        require(value.alternateScreen.visibleRowsList.all { it.lineId == 0L }) {
            "Phase 1 producers must emit zero alternate line IDs"
        }
    }

    fun normalizeSnapshotForImport(value: TerminalSnapshotV1): TerminalSnapshotV1 {
        validateSnapshot(value)
        if (value.alternateScreen.visibleRowsList.all { it.lineId == 0L }) return value
        val alternate = value.alternateScreen.toBuilder()
        value.alternateScreen.visibleRowsList.forEachIndexed { index, row ->
            alternate.setVisibleRows(index, row.toBuilder().setLineId(0))
        }
        return value.toBuilder().setAlternateScreen(alternate).build()
    }

    fun initialTranscriptBeforeLineId(value: TerminalSnapshotV1): ULong {
        validateSnapshot(value)
        return value.primaryScreen.visibleRowsList.first().lineId.toULong()
    }

    fun validateInitialTranscriptPage(snapshot: TerminalSnapshotV1, page: TerminalTranscriptPageV1) {
        validateTranscriptForRequest(page, snapshot.snapshotSeq.toULong(), initialTranscriptBeforeLineId(snapshot))
    }

    fun validateTranscript(value: TerminalTranscriptPageV1) {
        require(
            value.formatVersion == FORMAT_VERSION && value.asOfSnapshotSeq > 0L &&
                value.rowsCount in 1..MAX_TRANSCRIPT_ROWS && value.hasPalette(),
        ) { "Transcript root metadata is invalid" }
        validateStyles(value.styleTableList, value.hyperlinkTableList)
        validatePalette(value.palette)
        val first = value.firstLineId
        val last = value.lastLineId
        require(
            first > 0L && last >= first && value.rowsList.first().lineId == first &&
                value.rowsList.last().lineId == last &&
                if (value.hasMoreBefore) value.nextBeforeLineId == first else value.nextBeforeLineId == 0L,
        ) { "Transcript cursor metadata is invalid" }
        var previousLine = 0L
        var totalCells = 0L
        value.rowsList.forEach { row ->
            val line = row.lineId
            val sealedAt = row.sealedAtSeq
            require(line > previousLine && sealedAt > 0L && sealedAt <= value.asOfSnapshotSeq) {
                "Transcript row sequence metadata is invalid"
            }
            val rowCells = validateRow(row, null, value.styleTableCount)
            require(rowCells in 1..MAX_COLUMNS) { "Transcript row width is invalid" }
            totalCells += rowCells
            previousLine = line
        }
        require(totalCells <= MAX_TRANSCRIPT_ROWS.toLong() * MAX_COLUMNS) {
            "Transcript expanded cell count exceeds the Phase 1 bound"
        }
    }

    fun validateTranscriptForRequest(
        value: TerminalTranscriptPageV1,
        expectedAsOfSnapshotSequence: ULong,
        exclusiveBeforeLineId: ULong,
    ) {
        validateTranscript(value)
        require(
            expectedAsOfSnapshotSequence in 1uL..MAX_SUPPORTED_UINT64 &&
                exclusiveBeforeLineId in 1uL..MAX_SUPPORTED_UINT64 &&
                value.asOfSnapshotSeq.toULong() == expectedAsOfSnapshotSequence &&
                value.lastLineId.toULong() < exclusiveBeforeLineId,
        ) { "Transcript page does not match its fixed snapshot anchor and exclusive cursor" }
    }

    private fun parseSnapshotContainer(value: ByteArray): TerminalSnapshotCiphertextV1 = try {
        TerminalSnapshotCiphertextV1.parseFrom(value)
    } catch (error: InvalidProtocolBufferException) {
        throw IllegalArgumentException("Snapshot container is invalid protobuf", error)
    }

    private fun parseTranscriptContainer(value: ByteArray): TerminalTranscriptCiphertextV1 = try {
        TerminalTranscriptCiphertextV1.parseFrom(value)
    } catch (error: InvalidProtocolBufferException) {
        throw IllegalArgumentException("Transcript container is invalid protobuf", error)
    }

    private fun validateSnapshotContainer(
        value: TerminalSnapshotCiphertextV1,
        sessionId: UUID,
        pcDeviceId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: String,
        sequence: ULong,
        columns: Int,
        rows: Int,
    ) {
        validateCommonContainerMetadata(
            value.sessionId,
            value.pcDeviceId,
            value.recipientDeviceId,
            value.keyEpoch,
            value.keyId,
            value.nonce,
            value.containerVersion,
            value.formatVersion,
            value.formatName,
            value.compression,
            value.uncompressedBytes.toULong(),
            MAX_SNAPSHOT_PLAINTEXT_BYTES,
            SNAPSHOT_FORMAT_NAME,
        )
        require(
            value.sessionId.toByteArray().contentEquals(SessionCrypto.uuidBytes(sessionId)) &&
                value.pcDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(pcDeviceId)) &&
                value.recipientDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(recipientDeviceId)) &&
                value.keyEpoch == keyEpoch && value.keyId == keyId && value.keyId == snapshotKeyId(recipientDeviceId, keyEpoch) &&
                value.snapshotSeq.toULong() == sequence && sequence in 1uL..MAX_SUPPORTED_UINT64 &&
                value.snapshotSeq > 0L && value.columns == columns && value.rows == rows &&
                columns in 1..MAX_COLUMNS && rows in 1..MAX_ROWS && value.ciphertext.size() in 16..MAX_SNAPSHOT_OBJECT_BYTES,
        ) { "Snapshot object metadata does not match the requested object" }
    }

    private fun validateTranscriptContainer(
        value: TerminalTranscriptCiphertextV1,
        sessionId: UUID,
        pcDeviceId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: String,
        asOf: ULong,
        first: ULong,
        last: ULong,
    ) {
        validateCommonContainerMetadata(
            value.sessionId,
            value.pcDeviceId,
            value.recipientDeviceId,
            value.keyEpoch,
            value.keyId,
            value.nonce,
            value.containerVersion,
            value.formatVersion,
            value.formatName,
            value.compression,
            value.uncompressedBytes.toULong(),
            MAX_TRANSCRIPT_PLAINTEXT_BYTES,
            TRANSCRIPT_FORMAT_NAME,
        )
        require(
            value.sessionId.toByteArray().contentEquals(SessionCrypto.uuidBytes(sessionId)) &&
                value.pcDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(pcDeviceId)) &&
                value.recipientDeviceId.toByteArray().contentEquals(SessionCrypto.uuidBytes(recipientDeviceId)) &&
                value.keyEpoch == keyEpoch && value.keyId == keyId && value.keyId == transcriptKeyId(recipientDeviceId, keyEpoch) &&
                value.asOfSnapshotSeq.toULong() == asOf && value.firstLineId.toULong() == first &&
                value.lastLineId.toULong() == last && asOf in 1uL..MAX_SUPPORTED_UINT64 &&
                first in 1uL..MAX_SUPPORTED_UINT64 && last in first..MAX_SUPPORTED_UINT64 &&
                value.asOfSnapshotSeq > 0L && value.firstLineId > 0L && value.lastLineId > 0L &&
                value.ciphertext.size() in 16..MAX_TRANSCRIPT_OBJECT_BYTES,
        ) { "Transcript object metadata does not match the requested object" }
    }

    private fun validateCommonContainerMetadata(
        sessionId: ByteString,
        pcDeviceId: ByteString,
        recipientDeviceId: ByteString,
        keyEpoch: Long,
        keyId: String,
        nonce: ByteString,
        containerVersion: Int,
        formatVersion: Int,
        formatName: String,
        compression: SnapshotCompressionV1,
        uncompressedBytes: ULong,
        maxPlaintextBytes: Int,
        expectedFormatName: String,
    ) {
        require(
            containerVersion == CONTAINER_VERSION && sessionId.size() == 16 && pcDeviceId.size() == 16 &&
                recipientDeviceId.size() == 16 && keyEpoch > 0 && utf8Length(keyId, "object key ID") in 8..128 &&
                nonce.size() == 12 && formatVersion == FORMAT_VERSION && formatName == expectedFormatName &&
                compression == SnapshotCompressionV1.SNAPSHOT_COMPRESSION_V1_NONE &&
                uncompressedBytes > 0u && uncompressedBytes <= maxPlaintextBytes.toULong(),
        ) { "Snapshot object cryptographic metadata is invalid or unsupported" }
    }

    private fun validateScreen(
        screen: TerminalScreenV1,
        columns: Int,
        rows: Int,
        snapshotSequence: ULong,
        styleCount: Int,
    ): Long {
        require(
            screen.visibleRowsCount == rows && screen.hasCursor() && screen.hasSavedCursor() && screen.hasCharset() &&
                screen.scrollTop in 0 until rows && screen.scrollBottom in screen.scrollTop until rows &&
                screen.marginLeft in 0 until columns && screen.marginRight in screen.marginLeft until columns &&
                screen.currentStyleIndex in 0 until styleCount && screen.pendingWrap == screen.cursor.pendingWrap,
        ) { "Snapshot screen metadata is invalid" }
        validateCursor(screen.cursor, columns, rows, styleCount)
        validateCursor(screen.savedCursor, columns, rows, styleCount)
        validateCharset(screen.charset)
        require(utf8Length(screen.lastPrintedGrapheme, "last printed grapheme") <= MAX_GRAPHEME_BYTES) {
            "Last printed grapheme exceeds the Phase 1 bound"
        }
        var cells = 0L
        screen.visibleRowsList.forEach { row ->
            require(row.sealedAtSeq >= 0L && row.sealedAtSeq.toULong() <= snapshotSequence) {
                "Snapshot row sequence exceeds snapshot_seq or the supported signed range"
            }
            cells += validateRow(row, columns, styleCount)
        }
        return cells
    }

    private fun validateRow(row: TerminalRowV1, expectedCells: Int?, styleCount: Int): Int {
        require(row.runsCount > 0) { "Terminal row has no cells" }
        var expanded = 0
        var expectsWideContinuation = false
        var wideLeadStyleIndex = -1
        row.runsList.forEach { run ->
            require(run.repeat in 1..MAX_COLUMNS && run.styleIndex in 0 until styleCount) {
                "Terminal cell run metadata is invalid"
            }
            require(utf8Length(run.grapheme, "terminal grapheme") <= MAX_GRAPHEME_BYTES) {
                "Terminal grapheme exceeds the Phase 1 bound"
            }
            if (run.wideContinuation) {
                require(
                    expectsWideContinuation && run.repeat == 1 && run.displayWidth == 0 &&
                        run.grapheme.isEmpty() && run.styleIndex == wideLeadStyleIndex,
                ) {
                    "Wide-cell continuation is invalid"
                }
                expectsWideContinuation = false
                wideLeadStyleIndex = -1
            } else {
                require(
                    !expectsWideContinuation && run.displayWidth in 1..2 &&
                        (run.grapheme.isEmpty() || run.repeat == 1) &&
                        (run.displayWidth == 1 || run.grapheme.isNotEmpty()),
                ) { "Terminal cell run width is invalid" }
                expectsWideContinuation = run.displayWidth == 2
                wideLeadStyleIndex = if (expectsWideContinuation) run.styleIndex else -1
            }
            expanded = Math.addExact(expanded, run.repeat)
            require(expanded <= MAX_COLUMNS) { "Terminal row exceeds the Phase 1 width bound" }
        }
        require(!expectsWideContinuation && (expectedCells == null || expanded == expectedCells)) {
            "Terminal row does not fill the declared width"
        }
        return expanded
    }

    private fun validateCursor(cursor: TerminalCursorStateV1, columns: Int, rows: Int, styleCount: Int) {
        require(
            cursor.column in 0 until columns && cursor.row in 0 until rows &&
                cursor.style in setOf(
                    TerminalCursorStyleV1.TERMINAL_CURSOR_STYLE_V1_BLOCK,
                    TerminalCursorStyleV1.TERMINAL_CURSOR_STYLE_V1_UNDERLINE,
                    TerminalCursorStyleV1.TERMINAL_CURSOR_STYLE_V1_BAR,
                ) && cursor.renditionStyleIndex in 0 until styleCount && cursor.hasCharset(),
        ) { "Terminal cursor metadata is invalid" }
        validateCharset(cursor.charset)
    }

    private fun validateCharset(charset: TerminalCharsetStateV1) {
        val supportedCharsets = setOf(
            TerminalCharsetV1.TERMINAL_CHARSET_V1_ASCII,
            TerminalCharsetV1.TERMINAL_CHARSET_V1_DEC_SPECIAL_GRAPHICS,
            TerminalCharsetV1.TERMINAL_CHARSET_V1_UK,
            TerminalCharsetV1.TERMINAL_CHARSET_V1_LATIN_1,
        )
        require(
            charset.g0 in supportedCharsets && charset.g1 in supportedCharsets &&
                charset.g2 in supportedCharsets && charset.g3 in supportedCharsets &&
                charset.gl in 0..3 && charset.gr in 0..3,
        ) { "Terminal charset state is invalid or unsupported" }
    }

    private fun validateModes(modes: TerminalModesV1) {
        require(
            modes.mouseTracking in setOf(
                TerminalMouseTrackingV1.TERMINAL_MOUSE_TRACKING_V1_NONE,
                TerminalMouseTrackingV1.TERMINAL_MOUSE_TRACKING_V1_X10,
                TerminalMouseTrackingV1.TERMINAL_MOUSE_TRACKING_V1_NORMAL,
                TerminalMouseTrackingV1.TERMINAL_MOUSE_TRACKING_V1_BUTTON_EVENT,
                TerminalMouseTrackingV1.TERMINAL_MOUSE_TRACKING_V1_ANY_EVENT,
            ) && modes.mouseEncoding in setOf(
                TerminalMouseEncodingV1.TERMINAL_MOUSE_ENCODING_V1_DEFAULT,
                TerminalMouseEncodingV1.TERMINAL_MOUSE_ENCODING_V1_UTF8,
                TerminalMouseEncodingV1.TERMINAL_MOUSE_ENCODING_V1_SGR,
                TerminalMouseEncodingV1.TERMINAL_MOUSE_ENCODING_V1_URXVT,
                TerminalMouseEncodingV1.TERMINAL_MOUSE_ENCODING_V1_SGR_PIXELS,
            ),
        ) { "Terminal modes are invalid or unsupported" }
    }

    private fun validateProjection(projection: TerminalSessionProjectionV1) {
        require(
            projection.lifecycle in setOf(
                TerminalLifecycleV1.TERMINAL_LIFECYCLE_V1_RUNNING,
                TerminalLifecycleV1.TERMINAL_LIFECYCLE_V1_INTERRUPTED,
                TerminalLifecycleV1.TERMINAL_LIFECYCLE_V1_ENDED,
            ) &&
                ((projection.lifecycle == TerminalLifecycleV1.TERMINAL_LIFECYCLE_V1_ENDED) == projection.hasExitCode()) &&
                projection.observedLocalInputEpoch >= 0L,
        ) { "Terminal session projection is invalid" }
    }

    private fun validateStyles(styles: List<TerminalStyleV1>, hyperlinks: List<TerminalHyperlinkV1>) {
        require(styles.size in 1..MAX_STYLES && hyperlinks.size <= MAX_HYPERLINKS) { "Terminal style table size is invalid" }
        hyperlinks.forEach { hyperlink ->
            require(
                utf8Length(hyperlink.id, "hyperlink ID") <= MAX_HYPERLINK_ID_BYTES &&
                    utf8Length(hyperlink.uri, "hyperlink URI") in 1..MAX_HYPERLINK_URI_BYTES &&
                    utf8Length(hyperlink.params, "hyperlink parameters") <= MAX_HYPERLINK_PARAMS_BYTES,
            ) { "Terminal hyperlink exceeds the Phase 1 bound" }
        }
        styles.forEach { style ->
            require(
                style.hasForeground() && style.hasBackground() && style.hasUnderlineColor() &&
                    style.underlineStyle in setOf(
                        TerminalUnderlineStyleV1.TERMINAL_UNDERLINE_STYLE_V1_NONE,
                        TerminalUnderlineStyleV1.TERMINAL_UNDERLINE_STYLE_V1_SINGLE,
                        TerminalUnderlineStyleV1.TERMINAL_UNDERLINE_STYLE_V1_DOUBLE,
                        TerminalUnderlineStyleV1.TERMINAL_UNDERLINE_STYLE_V1_CURLY,
                        TerminalUnderlineStyleV1.TERMINAL_UNDERLINE_STYLE_V1_DOTTED,
                        TerminalUnderlineStyleV1.TERMINAL_UNDERLINE_STYLE_V1_DASHED,
                    ) &&
                    style.attributes and SUPPORTED_STYLE_ATTRIBUTES.inv() == 0 &&
                    style.hyperlinkIndexPlusOne.toUInt() <= hyperlinks.size.toUInt(),
            ) { "Terminal style metadata is invalid" }
            validateColor(style.foreground)
            validateColor(style.background)
            validateColor(style.underlineColor)
        }
    }

    private fun validateColor(color: TerminalColorRefV1) {
        val valid = when (color.valueCase) {
            TerminalColorRefV1.ValueCase.DEFAULT_COLOR -> color.defaultColor
            TerminalColorRefV1.ValueCase.PALETTE_INDEX -> color.paletteIndex.toUInt() <= 255u
            TerminalColorRefV1.ValueCase.RGB -> color.rgb.toUInt() <= 0x00ff_ffffu
            else -> false
        }
        require(valid) { "Terminal color reference is invalid" }
    }

    private fun validatePalette(palette: TerminalPaletteV1) {
        require(
            palette.colorsRgbCount == 256 && palette.defaultForegroundRgb.toUInt() <= 0x00ff_ffffu &&
                palette.defaultBackgroundRgb.toUInt() <= 0x00ff_ffffu && palette.defaultCursorRgb.toUInt() <= 0x00ff_ffffu &&
                palette.colorsRgbList.all { it.toUInt() <= 0x00ff_ffffu },
        ) { "Terminal palette is invalid" }
    }

    private fun deriveObjectKey(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
        keyId: String,
        prefix: ByteArray,
        expectedKeyId: String,
    ): ByteArray {
        require(pairwiseKey.size == 32) { "Pairwise key must be 32 bytes" }
        require(sessionId != ZERO_UUID && recipientDeviceId != ZERO_UUID && keyEpoch > 0 && keyId == expectedKeyId) {
            "Snapshot key metadata is invalid"
        }
        val keyIdBytes = strictUtf8(keyId, "snapshot key ID")
        require(keyIdBytes.size in 8..128) { "Snapshot key ID length is invalid" }
        val info = ByteArrayOutputStream().use { output ->
            output.write(prefix)
            output.write(SessionCrypto.uuidBytes(recipientDeviceId))
            output.write(longBytes(keyEpoch))
            output.write(ushortBytes(keyIdBytes.size))
            output.write(keyIdBytes)
            output.toByteArray()
        }
        return try {
            val generator = HKDFBytesGenerator(SHA256Digest())
            generator.init(HKDFParameters(pairwiseKey, SessionCrypto.uuidBytes(sessionId), info))
            ByteArray(32).also { generator.generateBytes(it, 0, it.size) }
        } finally {
            keyIdBytes.fill(0)
            info.fill(0)
        }
    }

    private fun validatePairwiseAndIdentity(
        pairwiseKey: ByteArray,
        sessionId: UUID,
        pcDeviceId: UUID,
        recipientDeviceId: UUID,
        keyEpoch: Long,
    ) {
        require(pairwiseKey.size == 32) { "Pairwise key must be 32 bytes" }
        require(
            sessionId != ZERO_UUID && pcDeviceId != ZERO_UUID && recipientDeviceId != ZERO_UUID &&
                pcDeviceId != recipientDeviceId && keyEpoch > 0,
        ) { "Snapshot identity metadata is invalid" }
    }

    private fun keyId(prefix: String, recipientDeviceId: UUID, keyEpoch: Long): String {
        require(recipientDeviceId != ZERO_UUID) { "Recipient device ID is required" }
        require(keyEpoch > 0) { "Key epoch must be positive" }
        return prefix + recipientDeviceId.toString().replace("-", "") + ":" + keyEpoch.toString().padStart(8, '0')
    }

    private fun randomNonce(): ByteArray = ByteArray(12).also(SecureRandom()::nextBytes)

    private fun strictUtf8(value: String, label: String): ByteArray = try {
        val encoded = Charsets.UTF_8.newEncoder()
            .onMalformedInput(CodingErrorAction.REPORT)
            .onUnmappableCharacter(CodingErrorAction.REPORT)
            .encode(CharBuffer.wrap(value))
        ByteArray(encoded.remaining()).also(encoded::get)
    } catch (error: Exception) {
        throw IllegalArgumentException("$label is not valid Unicode", error)
    }

    private fun utf8Length(value: String, label: String): Int = strictUtf8(value, label).let { bytes ->
        try { bytes.size } finally { bytes.fill(0) }
    }

    private fun writeString(output: ByteArrayOutputStream, value: String, label: String) {
        val encoded = strictUtf8(value, label)
        try {
            require(encoded.size <= 0xffff) { "$label is too long" }
            output.write(ushortBytes(encoded.size))
            output.write(encoded)
        } finally {
            encoded.fill(0)
        }
    }

    private fun ushortBytes(value: Int): ByteArray = ByteBuffer.allocate(2).order(ByteOrder.BIG_ENDIAN).putShort(value.toShort()).array()
    private fun uintBytes(value: Int): ByteArray = ByteBuffer.allocate(4).order(ByteOrder.BIG_ENDIAN).putInt(value).array()
    private fun ulongBytes(value: ULong): ByteArray = ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(value.toLong()).array()
    private fun longBytes(value: Long): ByteArray = ByteBuffer.allocate(8).order(ByteOrder.BIG_ENDIAN).putLong(value).array()

    private val ZERO_UUID = UUID(0, 0)
}
