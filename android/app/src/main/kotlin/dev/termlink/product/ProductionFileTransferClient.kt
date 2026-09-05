package dev.termlink.product

import android.net.Uri
import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.crypto.SessionCrypto
import dev.termlink.network.production.DeviceProof
import dev.termlink.protocol.filetransfer.v2.FileTransferAckV2
import dev.termlink.protocol.filetransfer.v2.FileTransferChunkV2
import dev.termlink.protocol.filetransfer.v2.FileTransferCompleteAckV2
import dev.termlink.protocol.filetransfer.v2.FileTransferEnvelopeV2
import dev.termlink.protocol.filetransfer.v2.FileTransferErrorV2
import dev.termlink.protocol.filetransfer.v2.FileTransferMessageTypeV2
import dev.termlink.protocol.filetransfer.v2.FileTransferOfferV2
import dev.termlink.protocol.filetransfer.v2.FileTransferPayloadV2
import dev.termlink.protocol.filetransfer.v2.FileTransferResumeV2
import java.net.URI
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.UUID
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.delay
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.add
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.put
import kotlinx.serialization.json.putJsonArray
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString as OkioByteString

internal data class RealtimeFileTransferDescriptor(
    val transferId: UUID,
    val filename: String,
    val sizeBytes: Long,
    val sha256: ByteArray,
    val directoryZip: Boolean,
)

internal class RemoteFileTransferException(
    message: String,
    val retryable: Boolean,
    cause: Throwable? = null,
) : Exception(message, cause)

/** Storage-free E2EE receiver with durable contiguous-offset resume. */
internal class ProductionFileTransferClient(
    rootUrl: String,
    private val accessToken: () -> String,
    private val localDeviceId: UUID,
    private val identity: DeviceIdentity,
    private val pcDeviceId: UUID,
    private val pcExchangePublicKey: ByteArray,
    private val keyEpoch: Long,
    private val transferId: UUID,
    private val cache: FileTransferCache,
    httpClient: OkHttpClient,
    allowCleartextForLocalTest: Boolean,
) {
    private val websocketUrl = normalizeWebSocket(rootUrl, allowCleartextForLocalTest)
    private val httpClient = httpClient.newBuilder()
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(15, TimeUnit.SECONDS)
        .build()
    private val random = SecureRandom()

    init {
        require(localDeviceId != UUID(0, 0) && pcDeviceId != UUID(0, 0) && localDeviceId != pcDeviceId)
        require(pcExchangePublicKey.size == 32 && pcExchangePublicKey.any { it.toInt() != 0 })
        require(keyEpoch > 0 && transferId != UUID(0, 0))
    }

    suspend fun receive(
        destination: Uri,
        expectedDirectoryZip: Boolean,
        initialDescriptor: RealtimeFileTransferDescriptor? = null,
        onOffer: (RealtimeFileTransferDescriptor, Long) -> Unit,
        onProgress: (Long) -> Unit,
        onVerifying: () -> Unit,
        completionConfirmed: suspend () -> Boolean,
    ): RealtimeFileTransferDescriptor {
        val pairwiseKey = SessionCrypto.derivePairwiseKey(
            privateKey = identity.exchange.privateKey,
            peerPublicKey = pcExchangePublicKey,
            pcDeviceId = pcDeviceId,
            androidDeviceId = localDeviceId,
            keyEpoch = keyEpoch,
        )
        val keyId = keyId(transferId, keyEpoch)
        initialDescriptor?.let {
            require(it.transferId == transferId && it.directoryZip == expectedDirectoryZip) {
                "Initial file-transfer descriptor does not match the requested transfer"
            }
        }
        var expected: RealtimeFileTransferDescriptor? = initialDescriptor
        val completionState = CompletionState(
            documentCommitted = initialDescriptor?.let {
                cache.isDocumentCommitted(transferId, destination, it.sizeBytes, it.sha256)
            } ?: false,
        )
        var retryDelay = 250L
        try {
            while (true) {
                currentCoroutineContext().ensureActive()
                var connection: Connection? = null
                try {
                    if (expected != null && completionState.documentCommitted && completionConfirmed()) {
                        return expected
                    }
                    connection = openSocket()
                    awaitWelcome(connection)
                    val result = receiveOnSocket(
                        connection = connection,
                        pairwiseKey = pairwiseKey,
                        keyId = keyId,
                        expectedDirectoryZip = expectedDirectoryZip,
                        initialExpected = expected,
                        destination = destination,
                        completionState = completionState,
                        onOffer = { descriptor, committed ->
                            expected = descriptor
                            onOffer(descriptor, committed)
                        },
                        onProgress = onProgress,
                        onVerifying = onVerifying,
                        completionConfirmed = completionConfirmed,
                    )
                    expected = result.descriptor
                    return result.descriptor
                } catch (error: CancellationException) {
                    throw error
                } catch (error: RemoteFileTransferException) {
                    if (!error.retryable) throw error
                } catch (error: Throwable) {
                    if (error is IllegalArgumentException || error is SecurityException) {
                        throw RemoteFileTransferException(
                            "File transfer validation failed / 文件传输校验失败。",
                            retryable = false,
                            cause = error,
                        )
                    }
                } finally {
                    connection?.socket?.cancel()
                    connection?.events?.close()
                }
                delay(retryDelay)
                retryDelay = (retryDelay * 2).coerceAtMost(15_000L)
            }
        } finally {
            pairwiseKey.fill(0)
            keyId.fill(0)
        }
    }

    private suspend fun receiveOnSocket(
        connection: Connection,
        pairwiseKey: ByteArray,
        keyId: ByteArray,
        expectedDirectoryZip: Boolean,
        initialExpected: RealtimeFileTransferDescriptor?,
        destination: Uri,
        completionState: CompletionState,
        onOffer: (RealtimeFileTransferDescriptor, Long) -> Unit,
        onProgress: (Long) -> Unit,
        onVerifying: () -> Unit,
        completionConfirmed: suspend () -> Boolean,
    ): ReceiveResult {
        var expected = initialExpected
        var outgoingSequence = 0uL
        var incomingSequence = 0uL
        var completed = false
        while (true) {
            if (completed && completionConfirmed()) {
                return ReceiveResult(requireNotNull(expected))
            }
            val timeout = if (completed || completionState.documentCommitted) {
                COMPLETION_POLL_INTERVAL_MILLIS
            } else {
                FILE_TRANSFER_RESPONSE_TIMEOUT_MILLIS
            }
            val event = withTimeoutOrNull(timeout) { connection.events.receive() }
            if (event == null) {
                if (completionState.documentCommitted && completionConfirmed()) {
                    return ReceiveResult(requireNotNull(expected))
                }
                if (completed || completionState.documentCommitted) continue
                throw RemoteFileTransferException(
                    "File-transfer peer timed out / 文件传输对端响应超时。",
                    retryable = true,
                )
            }
            when (event) {
                is SocketEvent.Failed -> throw RemoteFileTransferException(
                    "File-transfer connection failed / 文件传输连接失败。",
                    retryable = true,
                    cause = event.error,
                )
                SocketEvent.Closed -> throw RemoteFileTransferException(
                    "File-transfer connection closed / 文件传输连接已关闭。",
                    retryable = true,
                )
                is SocketEvent.Text -> throw RemoteFileTransferException(
                    "Unexpected file-transfer text frame / 文件传输收到异常文本帧。",
                    retryable = false,
                )
                is SocketEvent.Binary -> {
                    val opened = openEnvelope(event.bytes, pairwiseKey, keyId, incomingSequence)
                    incomingSequence = opened.sequence
                    when (opened.payload.bodyCase) {
                        FileTransferPayloadV2.BodyCase.OFFER -> {
                            val descriptor = descriptor(opened.payload.offer, expectedDirectoryZip)
                            if (expected != null) require(sameDescriptor(expected!!, descriptor)) {
                                "File-transfer offer changed during resume"
                            }
                            expected = descriptor
                            if (!completionState.documentCommitted) {
                                completionState.documentCommitted = cache.isDocumentCommitted(
                                    transferId,
                                    destination,
                                    descriptor.sizeBytes,
                                    descriptor.sha256,
                                )
                            }
                            val committed = cache.committedOffset(transferId, descriptor.sizeBytes)
                            onOffer(descriptor, committed)
                            sendPayload(
                                connection.socket,
                                pairwiseKey,
                                keyId,
                                FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_RESUME,
                                ++outgoingSequence,
                                FileTransferPayloadV2.newBuilder().setResume(
                                    FileTransferResumeV2.newBuilder()
                                        .setCommittedOffset(committed.toULong().toLong()),
                                ).build(),
                            )
                        }
                        FileTransferPayloadV2.BodyCase.CHUNK -> {
                            val descriptor = requireNotNull(expected) { "File chunk arrived before offer" }
                            val chunk = opened.payload.chunk
                            val data = chunk.data.toByteArray()
                            try {
                                val committed = cache.commitChunk(
                                    transferId,
                                    descriptor.sizeBytes,
                                    chunk.offset,
                                    data,
                                )
                                onProgress(committed.committedOffset)
                                if (committed.acknowledged) {
                                    sendPayload(
                                        connection.socket,
                                        pairwiseKey,
                                        keyId,
                                        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_ACK,
                                        ++outgoingSequence,
                                        FileTransferPayloadV2.newBuilder().setAck(
                                            FileTransferAckV2.newBuilder()
                                                .setCommittedOffset(committed.committedOffset),
                                        ).build(),
                                    )
                                } else {
                                    sendPayload(
                                        connection.socket,
                                        pairwiseKey,
                                        keyId,
                                        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_RESUME,
                                        ++outgoingSequence,
                                        FileTransferPayloadV2.newBuilder().setResume(
                                            FileTransferResumeV2.newBuilder()
                                                .setCommittedOffset(committed.committedOffset),
                                        ).build(),
                                    )
                                }
                            } finally { data.fill(0) }
                        }
                        FileTransferPayloadV2.BodyCase.COMPLETE -> {
                            val descriptor = requireNotNull(expected) { "Completion arrived before offer" }
                            val complete = opened.payload.complete
                            require(complete.sizeBytes == descriptor.sizeBytes &&
                                MessageDigest.isEqual(complete.sha256.toByteArray(), descriptor.sha256)
                            ) { "File-transfer completion metadata changed" }
                            onVerifying()
                            try {
                                cache.verify(transferId, descriptor.sizeBytes, descriptor.sha256)
                            } catch (error: IllegalArgumentException) {
                                sendPayload(
                                    connection.socket,
                                    pairwiseKey,
                                    keyId,
                                    FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_ERROR,
                                    ++outgoingSequence,
                                    FileTransferPayloadV2.newBuilder().setError(
                                        FileTransferErrorV2.newBuilder()
                                            .setErrorCode("SHA256_MISMATCH")
                                            .setRetryable(false),
                                    ).build(),
                                )
                                throw error
                            }
                            if (!completionState.documentCommitted) {
                                cache.copyToDocument(
                                    transferId,
                                    destination,
                                    descriptor.sizeBytes,
                                    onProgress,
                                )
                                cache.markDocumentCommitted(
                                    transferId,
                                    destination,
                                    descriptor.sizeBytes,
                                    descriptor.sha256,
                                )
                                completionState.documentCommitted = true
                            }
                            sendPayload(
                                connection.socket,
                                pairwiseKey,
                                keyId,
                                FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_COMPLETE_ACK,
                                ++outgoingSequence,
                                FileTransferPayloadV2.newBuilder().setCompleteAck(
                                    FileTransferCompleteAckV2.newBuilder()
                                        .setCommittedOffset(descriptor.sizeBytes)
                                        .setSha256(ByteString.copyFrom(descriptor.sha256)),
                                ).build(),
                            )
                            completed = true
                        }
                        FileTransferPayloadV2.BodyCase.CANCEL -> throw RemoteFileTransferException(
                            "File transfer was cancelled by Windows / 文件传输已被 Windows 端取消。",
                            retryable = false,
                        )
                        FileTransferPayloadV2.BodyCase.ERROR -> throw RemoteFileTransferException(
                            "Windows rejected the file transfer / Windows 端拒绝了文件传输。",
                            retryable = opened.payload.error.retryable,
                        )
                        else -> throw RemoteFileTransferException(
                            "Unexpected file-transfer message / 文件传输消息无效。",
                            retryable = false,
                        )
                    }
                }
            }
        }
    }

    private fun openSocket(): Connection {
        val events = Channel<SocketEvent>(capacity = 16)
        val target = URI(websocketUrl).rawPath
        val proof = DeviceProof.create(identity, localDeviceId.toString(), "GET", target, ByteArray(0))
        val request = Request.Builder()
            .url(websocketUrl)
            .header("Authorization", "Bearer ${accessToken()}")
            .header("X-TermLink-Device", proof.deviceId)
            .header("X-TermLink-Timestamp", proof.timestampMillis.toString())
            .header("X-TermLink-Nonce", proof.nonce)
            .header("X-TermLink-Signature", proof.signature)
            .header("Sec-WebSocket-Protocol", "termlink.file-transfer.v2")
            .build()
        val socket = httpClient.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                val hello = buildJsonObject {
                    put("role", "android")
                    put("transfer_id", transferId.toString())
                    putJsonArray("protocol_versions") { add(2) }
                    putJsonArray("capabilities") {
                        add("e2ee")
                        add("resume_offset_v2")
                    }
                }
                if (!webSocket.send(Json.encodeToString(JsonObject.serializer(), hello))) {
                    events.trySend(SocketEvent.Failed(IllegalStateException("ClientHello was rejected")))
                }
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                events.trySend(SocketEvent.Text(text))
            }

            override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                events.trySend(SocketEvent.Binary(bytes.toByteArray()))
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                events.trySend(SocketEvent.Failed(t))
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                events.trySend(SocketEvent.Closed)
            }
        })
        return Connection(socket, events)
    }

    private suspend fun awaitWelcome(connection: Connection) {
        val event = withTimeoutOrNull(FILE_TRANSFER_HANDSHAKE_TIMEOUT_MILLIS) {
            connection.events.receive()
        } ?: throw RemoteFileTransferException(
            "File-transfer Relay handshake timed out / 文件传输中继握手超时。",
            retryable = true,
        )
        when (event) {
            is SocketEvent.Text -> {
                val root = Json.parseToJsonElement(event.value) as? JsonObject
                    ?: error("File-transfer welcome is invalid")
                require(root["type"]?.jsonPrimitive?.content == "welcome" &&
                    root["protocol_version"]?.jsonPrimitive?.content == "2" &&
                    root["capabilities"]?.jsonArray?.map { it.jsonPrimitive.content }?.containsAll(
                        listOf("e2ee", "resume_offset_v2"),
                    ) == true
                ) { "File-transfer welcome is invalid" }
            }
            is SocketEvent.Failed -> throw event.error
            SocketEvent.Closed -> error("File-transfer Relay closed during handshake")
            is SocketEvent.Binary -> error("File-transfer welcome must be text")
        }
    }

    private fun sendPayload(
        socket: WebSocket,
        pairwiseKey: ByteArray,
        keyId: ByteArray,
        messageType: FileTransferMessageTypeV2,
        sequence: ULong,
        payload: FileTransferPayloadV2,
    ) {
        val nonce = ByteArray(12).also(random::nextBytes)
        val envelope = FileTransferEnvelopeV2.newBuilder()
            .setProtocolVersion(2)
            .setTransferId(ByteString.copyFrom(uuidBytes(transferId)))
            .setSenderDeviceId(ByteString.copyFrom(uuidBytes(localDeviceId)))
            .setRecipientDeviceId(ByteString.copyFrom(uuidBytes(pcDeviceId)))
            .setMessageType(messageType)
            .setSequence(sequence.toLong())
            .setKeyEpoch(keyEpoch)
            .setKeyId(ByteString.copyFrom(keyId))
            .setNonce(ByteString.copyFrom(nonce))
            .clearCiphertext()
            .build()
        val plaintext = payload.toByteArray()
        val key = SessionCrypto.deriveFileTransferKey(
            pairwiseKey,
            transferId,
            CryptoDirection.ANDROID_TO_PC,
            keyEpoch,
            keyId,
        )
        val ciphertext = try {
            SessionCrypto.encrypt(key, nonce, associatedData(envelope), plaintext)
        } finally {
            key.fill(0)
            plaintext.fill(0)
            nonce.fill(0)
        }
        val wire = envelope.toBuilder().setCiphertext(ByteString.copyFrom(ciphertext)).build().toByteArray()
        ciphertext.fill(0)
        try {
            if (!socket.send(OkioByteString.of(*wire))) {
                throw RemoteFileTransferException(
                    "File-transfer socket rejected output / 文件传输连接拒绝发送。",
                    retryable = true,
                )
            }
        } finally { wire.fill(0) }
    }

    private fun openEnvelope(
        wire: ByteArray,
        pairwiseKey: ByteArray,
        expectedKeyId: ByteArray,
        previousSequence: ULong,
    ): OpenedPayload {
        try {
            require(wire.size in 1..FILE_TRANSFER_MAX_WIRE_BYTES)
            val envelope = FileTransferEnvelopeV2.parseFrom(wire)
            require(envelope.protocolVersion == 2 &&
                envelope.transferId.toByteArray().contentEquals(uuidBytes(transferId)) &&
                envelope.senderDeviceId.toByteArray().contentEquals(uuidBytes(pcDeviceId)) &&
                envelope.recipientDeviceId.toByteArray().contentEquals(uuidBytes(localDeviceId)) &&
                envelope.sequence.toULong() > previousSequence && envelope.keyEpoch == keyEpoch &&
                envelope.keyId.toByteArray().contentEquals(expectedKeyId) &&
                envelope.nonce.size() == 12 && envelope.ciphertext.size() >= 16
            ) { "File-transfer envelope metadata is invalid" }
            val key = SessionCrypto.deriveFileTransferKey(
                pairwiseKey,
                transferId,
                CryptoDirection.PC_TO_ANDROID,
                keyEpoch,
                expectedKeyId,
            )
            val plaintext = try {
                SessionCrypto.decrypt(
                    key,
                    envelope.nonce.toByteArray(),
                    associatedData(envelope),
                    envelope.ciphertext.toByteArray(),
                )
            } finally { key.fill(0) }
            try {
                val payload = FileTransferPayloadV2.parseFrom(plaintext)
                require(messageMatches(envelope.messageType, payload.bodyCase)) {
                    "File-transfer message type does not match its payload"
                }
                return OpenedPayload(envelope.sequence.toULong(), payload)
            } finally { plaintext.fill(0) }
        } finally { wire.fill(0) }
    }

    private fun descriptor(
        offer: FileTransferOfferV2,
        expectedDirectoryZip: Boolean,
    ): RealtimeFileTransferDescriptor {
        val filename = offer.filename
        val size = offer.sizeBytes
        val sha256 = offer.sha256.toByteArray()
        require(filename.isNotBlank() && filename.encodeToByteArray().size <= 255 &&
            filename.none { it == '/' || it == '\\' || it == '\u0000' || it == '\r' || it == '\n' })
        require(size in 0..MAX_REMOTE_FILE_TRANSFER_BYTES && sha256.size == 32)
        require(offer.directoryZip == expectedDirectoryZip && offer.chunkBytes == FILE_TRANSFER_CHUNK_BYTES)
        return RealtimeFileTransferDescriptor(transferId, filename, size, sha256, offer.directoryZip)
    }

    private fun sameDescriptor(
        left: RealtimeFileTransferDescriptor,
        right: RealtimeFileTransferDescriptor,
    ): Boolean = left.transferId == right.transferId && left.filename == right.filename &&
        left.sizeBytes == right.sizeBytes && left.directoryZip == right.directoryZip &&
        MessageDigest.isEqual(left.sha256, right.sha256)

    private fun messageMatches(
        messageType: FileTransferMessageTypeV2,
        bodyCase: FileTransferPayloadV2.BodyCase,
    ): Boolean = when (messageType) {
        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_OFFER -> bodyCase == FileTransferPayloadV2.BodyCase.OFFER
        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_RESUME -> bodyCase == FileTransferPayloadV2.BodyCase.RESUME
        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_CHUNK -> bodyCase == FileTransferPayloadV2.BodyCase.CHUNK
        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_ACK -> bodyCase == FileTransferPayloadV2.BodyCase.ACK
        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_COMPLETE -> bodyCase == FileTransferPayloadV2.BodyCase.COMPLETE
        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_COMPLETE_ACK -> bodyCase == FileTransferPayloadV2.BodyCase.COMPLETE_ACK
        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_CANCEL -> bodyCase == FileTransferPayloadV2.BodyCase.CANCEL
        FileTransferMessageTypeV2.FILE_TRANSFER_MESSAGE_TYPE_V2_ERROR -> bodyCase == FileTransferPayloadV2.BodyCase.ERROR
        else -> false
    }

    private fun associatedData(envelope: FileTransferEnvelopeV2): ByteArray =
        envelope.toBuilder().clearCiphertext().build().toByteArray()

    private fun keyId(transferId: UUID, epoch: Long): ByteArray =
        "file-transfer-v2:${transferId.toString().replace("-", "")}:$epoch".encodeToByteArray()

    private fun uuidBytes(value: UUID): ByteArray = ByteBuffer.allocate(16)
        .order(ByteOrder.BIG_ENDIAN)
        .putLong(value.mostSignificantBits)
        .putLong(value.leastSignificantBits)
        .array()

    private fun normalizeWebSocket(rootUrl: String, allowCleartext: Boolean): String {
        val root = URI(rootUrl.trimEnd('/'))
        require(root.rawUserInfo == null && !root.host.isNullOrBlank() &&
            root.rawQuery == null && root.rawFragment == null &&
            (root.rawPath.isNullOrEmpty() || root.rawPath == "/"))
        val scheme = when (root.scheme.lowercase()) {
            "https" -> "wss"
            "http" -> {
                require(allowCleartext) { "Cleartext file transfer is disabled" }
                "ws"
            }
            else -> error("Unsupported file-transfer URL")
        }
        return URI(scheme, null, root.host, root.port, "/v1/file-transfers", null, null).toString()
    }

    private data class Connection(val socket: WebSocket, val events: Channel<SocketEvent>)
    private data class ReceiveResult(val descriptor: RealtimeFileTransferDescriptor)

    private class CompletionState(var documentCommitted: Boolean = false)
    private data class OpenedPayload(val sequence: ULong, val payload: FileTransferPayloadV2)
    private sealed interface SocketEvent {
        data class Text(val value: String) : SocketEvent
        data class Binary(val bytes: ByteArray) : SocketEvent
        data class Failed(val error: Throwable) : SocketEvent
        data object Closed : SocketEvent
    }

    private companion object {
        const val FILE_TRANSFER_MAX_WIRE_BYTES = 320 * 1024
        const val FILE_TRANSFER_HANDSHAKE_TIMEOUT_MILLIS = 15_000L
        const val FILE_TRANSFER_RESPONSE_TIMEOUT_MILLIS = 20_000L
        const val COMPLETION_POLL_INTERVAL_MILLIS = 750L
    }
}
