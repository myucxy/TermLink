package dev.termlink.product

import com.google.protobuf.ByteString
import dev.termlink.crypto.BrokerCaptureCrypto
import dev.termlink.history.HistoryRepository
import dev.termlink.model.TerminalBufferBudget
import dev.termlink.network.production.ProductionHostHistoryObject
import dev.termlink.protocol.realtime.v1.BrokerCaptureSelectionV1
import dev.termlink.protocol.realtime.v1.HostHistoryObjectKindV1
import dev.termlink.session.RemoteSessionEvent
import dev.termlink.terminal.api.TerminalBrokerOutputReferenceV1
import dev.termlink.terminal.nativecore.NativeBrokerCaptureCodecV1
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.util.UUID

/** Resolves one ordered BrokerOutput before it can enter the renderer or durable ACK. */
internal suspend fun resolveBrokerOutput(
    sequence: Long,
    referenceV1: ByteArray,
    sessionId: UUID,
    pcDeviceId: UUID,
    recipientDeviceId: UUID,
    trustedKeyEpoch: Long,
    pairwiseKey: ByteArray,
    history: HistoryRepository,
    request: suspend (BrokerCaptureSelectionV1) -> ProductionHostHistoryObject,
    budget: TerminalBufferBudget = TerminalBufferBudget.global,
    stateContract: String = BrokerCaptureCrypto.STATE_CONTRACT_V1,
): RemoteSessionEvent.BrokerOutput = TerminalBrokerOutputReferenceV1.decode(referenceV1).use referenceUse@{ reference ->
    require(reference.sourceSequence == sequence) { "Broker Output sequence differs from its ordered event" }
    val output = reference.copyOutput()
    try {
        val digest = reference.copySha256()
        try {
        if (reference.isInline) {
            val reservation = budget.reserve(reference.captureBytes.toLong())
            var transferred = false
            var canonical: ByteArray? = null
            try {
                val bytes = reference.copyInlineCaptures()
                canonical = bytes
                verifyBrokerCanonical(sequence, output, bytes)
                val event = RemoteSessionEvent.BrokerOutput(sequence, output, bytes,
                    resourceLease = reservation)
                transferred = true
                return@referenceUse event
            } finally {
                canonical?.fill(0)
                if (!transferred) reservation.close()
            }
        }

        val keyId = BrokerCaptureCrypto.keyId(recipientDeviceId, trustedKeyEpoch, stateContract)
        var cached = history.getBrokerCapture(sessionId, reference.objectId, trustedKeyEpoch)
        if (cached == null) {
            val selection = BrokerCaptureSelectionV1.newBuilder()
                .setObjectId(ByteString.copyFrom(ByteBuffer.allocate(16)
                    .putLong(reference.objectId.mostSignificantBits)
                    .putLong(reference.objectId.leastSignificantBits).array()))
                .setSourceEventSeq(sequence)
                .setCanonicalBytes(reference.captureBytes)
                .setCanonicalSha256(ByteString.copyFrom(digest))
                .build()
            request(selection).use { remote ->
                require(remote.kind == HostHistoryObjectKindV1.HOST_HISTORY_OBJECT_KIND_V1_BROKER_CAPTURE &&
                    remote.formatName == "TERMLINK_BROKER_CAPTURE_V1" && remote.formatVersion == 1 &&
                    remote.brokerCapture == selection && remote.objectId == reference.objectId &&
                    remote.startSequence == sequence.toULong() && remote.endSequence == sequence.toULong() &&
                    remote.keyId == keyId) { "Broker capture response differs from trusted request" }
                require(remote.ciphertextSha256.size == 32 &&
                    MessageDigest.isEqual(MessageDigest.getInstance("SHA-256").digest(remote.sealedObject),
                        remote.ciphertextSha256)) { "Broker capture ciphertext digest differs" }
                val opened = BrokerCaptureCrypto.open(pairwiseKey, remote.sealedObject,
                    sessionId, pcDeviceId, recipientDeviceId, reference.objectId,
                    sequence.toULong(), trustedKeyEpoch, reference.captureBytes, digest,
                    stateContract)
                try { verifyBrokerCanonical(sequence, output, opened) }
                finally { opened.fill(0) }
                remote.takeReservation().use { admitted ->
                    cached = history.putBrokerCapture(sessionId, reference.objectId,
                        sequence.toULong(), trustedKeyEpoch, reference.captureBytes, digest,
                        keyId, remote.sealedObject, remote.ciphertextSha256,
                        admittedBy = admitted)
                }
            }
        }
        requireNotNull(cached).use { stored ->
            require(stored.metadata.sessionId == sessionId &&
                stored.metadata.captureId == reference.objectId &&
                stored.metadata.sourceSequence == sequence.toULong() &&
                stored.metadata.keyEpoch == trustedKeyEpoch && stored.metadata.keyId == keyId &&
                stored.metadata.canonicalBytes == reference.captureBytes &&
                MessageDigest.isEqual(stored.metadata.canonicalSha256, digest)) {
                "Cached Broker capture differs from ordered reference"
            }
            val canonical = BrokerCaptureCrypto.open(pairwiseKey, stored.ciphertext,
                sessionId, pcDeviceId, recipientDeviceId, reference.objectId,
                sequence.toULong(), trustedKeyEpoch, reference.captureBytes, digest,
                stateContract)
            try {
                verifyBrokerCanonical(sequence, output, canonical)
                val reservation = stored.takeReservation()
                var transferred = false
                try {
                    val event = RemoteSessionEvent.BrokerOutput(sequence, output, canonical,
                        resourceLease = reservation)
                    transferred = true
                    event
                } finally { if (!transferred) reservation.close() }
            } finally { canonical.fill(0) }
        }
        } finally { digest.fill(0) }
    } finally { output.fill(0) }
}

private fun verifyBrokerCanonical(sequence: Long, output: ByteArray, canonical: ByteArray) {
    NativeBrokerCaptureCodecV1.decode(canonical).use { group ->
        require(group.sourceSequence == sequence) { "Broker capture source sequence differs" }
        dev.termlink.terminal.nativecore.NativeBrokerOutputV1(sequence, output, group.captures).use { }
    }
}
