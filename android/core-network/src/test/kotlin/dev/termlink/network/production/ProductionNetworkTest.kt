package dev.termlink.network.production

import com.google.protobuf.ByteString
import dev.termlink.crypto.CryptoDirection
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.protocol.realtime.v1.Direction
import dev.termlink.protocol.realtime.v1.Envelope
import dev.termlink.protocol.realtime.v1.MessageType
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.AbstractExecutorService
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.channels.Channel
import okhttp3.OkHttpClient
import okhttp3.Dispatcher
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString as OkioByteString
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.junit.Assert.assertEquals
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ProductionNetworkTest {
    @Test
    fun receivedEnvelopeRetainsExactWireBytesAndClearsOnIdempotentClose() {
        val envelope = testEnvelope(UUID.randomUUID(), UUID.randomUUID(), UUID.randomUUID(), 1)
        val raw = byteArrayOf(0x98.toByte(), 0x06, 0x7b) + envelope.toByteArray()
        val expected = raw.copyOf()
        val parsed = Envelope.parseFrom(raw)
        val released = AtomicInteger(0)
        val received = ProductionReceivedEnvelope(
            envelope = parsed,
            wireBytes = raw.size,
            generation = 7,
            releaseReservation = { released.incrementAndGet() },
            rawBytes = raw,
        )
        val borrowed = received.rawBytes

        assertArrayEquals(expected, received.exactBytes)
        assertArrayEquals(expected, received.copyRawBytes())
        received.close()
        received.close()

        assertEquals(1, released.get())
        assertTrue(borrowed.all { it == 0.toByte() })
    }

    @Test
    fun closeObservedWrapperRetainsRawBytesUntilBothReservationsClose() {
        val envelope = testEnvelope(UUID.randomUUID(), UUID.randomUUID(), UUID.randomUUID(), 2)
        val raw = byteArrayOf(0x98.toByte(), 0x06, 0x7b) + envelope.toByteArray()
        val expected = raw.copyOf()
        val released = AtomicInteger(0)
        val observed = AtomicInteger(0)
        val root = ProductionReceivedEnvelope(
            envelope = Envelope.parseFrom(raw),
            wireBytes = raw.size,
            generation = 8,
            releaseReservation = { released.incrementAndGet() },
            rawBytes = raw,
        )
        val borrowed = root.rawBytes
        val wrapper = root.withCloseObserver { observed.incrementAndGet() }

        root.close()
        assertArrayEquals(expected, borrowed)
        assertEquals(0, observed.get())

        wrapper.close()
        wrapper.close()
        assertEquals(1, released.get())
        assertEquals(1, observed.get())
        assertTrue(borrowed.all { it == 0.toByte() })
    }

    @Test
    fun channelDiscardAndCloseClearRawOwners() = runBlocking {
        val firstEnvelope = testEnvelope(UUID.randomUUID(), UUID.randomUUID(), UUID.randomUUID(), 3)
        val secondEnvelope = testEnvelope(UUID.randomUUID(), UUID.randomUUID(), UUID.randomUUID(), 4)
        val firstRaw = firstEnvelope.toByteArray()
        val secondRaw = secondEnvelope.toByteArray()
        val firstBorrowed = firstRaw
        val secondBorrowed = secondRaw
        val channel = Channel<ProductionReceivedEnvelope>(
            capacity = 1,
            onUndeliveredElement = ProductionReceivedEnvelope::close,
        )
        assertTrue(
            channel.trySend(
                ProductionReceivedEnvelope(
                    envelope = firstEnvelope,
                    wireBytes = firstRaw.size,
                    generation = 1,
                    releaseReservation = {},
                    rawBytes = firstRaw,
                ),
            ).isSuccess,
        )
        val second = ProductionReceivedEnvelope(
            envelope = secondEnvelope,
            wireBytes = secondRaw.size,
            generation = 1,
            releaseReservation = {},
            rawBytes = secondRaw,
        )
        assertFalse(channel.trySend(second).isSuccess)
        // trySend reports a rejected element but does not invoke the
        // undelivered callback on every Channel implementation; the producer
        // therefore owns this explicit overflow release, as the WebSocket
        // listener does too.
        second.close()
        assertTrue(secondBorrowed.all { it == 0.toByte() })

        channel.cancel()
        assertTrue(firstBorrowed.all { it == 0.toByte() })
    }

    @Test
    fun sendExactSendsCallerBytesWithoutReserializing() = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        val sent = CompletableDeferred<ByteArray>()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    private var welcomed = false

                    override fun onMessage(webSocket: WebSocket, text: String) {
                        if (!welcomed) {
                            welcomed = true
                            check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                        }
                    }

                    override fun onMessage(webSocket: WebSocket, bytes: OkioByteString) {
                        sent.complete(bytes.toByteArray())
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = androidId,
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
        )
        try {
            val connected = async(start = CoroutineStart.UNDISPATCHED) {
                client.state.first { it == RealtimeState.Connected }
            }
            client.connect()
            withTimeout(5_000) { connected.await() }

            val canonical = testEnvelope(sessionId, androidId, pcId, 5)
            val exact = byteArrayOf(0x98.toByte(), 0x06, 0x7b) + canonical.toByteArray()
            val parsed = Envelope.parseFrom(exact)
            assertTrue(client.sendExact(parsed, exact))
            assertArrayEquals(exact, withTimeout(5_000) { sent.await() })
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun realtimeHelloAddsOnlyExplicitExperimentalCapabilities() {
        val baseline = productionRealtimeCapabilities(emptySet())
        val enabled = productionRealtimeCapabilities(
            required = setOf("terminal_snapshot_v1"),
            optionalAdvertised = setOf("terminal_snapshot_v1", "direct_transport_data_v1"),
        )

        assertTrue("terminal_snapshot_v1" !in baseline)
        assertEquals(1, enabled.count { it == "terminal_snapshot_v1" })
        assertEquals(1, enabled.count { it == "direct_transport_data_v1" })
        assertEquals(enabled.sorted(), enabled)
    }

    @Test
    fun optionalCapabilityIsAdvertisedButNotRequiredForRelayConnection(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val helloSeen = CompletableDeferred<String>()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        helloSeen.complete(text)
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = UUID.randomUUID(),
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
            optionalAdvertisedCapabilities = setOf("direct_transport_data_v1"),
        )
        try {
            val connected = async(start = CoroutineStart.UNDISPATCHED) {
                client.state.first { it == RealtimeState.Connected }
            }
            client.connect()
            withTimeout(5_000) { connected.await() }

            assertTrue(withTimeout(5_000) { helloSeen.await() }.contains("direct_transport_data_v1"))
            val negotiation = client.negotiatedCapabilities.value
            assertEquals(client.diagnostics().connectedGeneration, negotiation.generation)
            assertTrue("direct_transport_data_v1" !in negotiation.capabilities)
            assertEquals(RealtimeState.Connected, client.state.replayCache.last())
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun optionalNegotiationIsScopedToOnePhysicalGeneration(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val hellosSeen = AtomicInteger(0)
        listOf(true, false).forEach { echoOptional ->
            server.enqueue(
                MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            assertTrue(text.contains("direct_transport_data_v1"))
                            hellosSeen.incrementAndGet()
                            val capabilities = if (echoOptional) {
                                ",\"capabilities\":[\"direct_transport_data_v1\"]"
                            } else {
                                ""
                            }
                            check(
                                webSocket.send(
                                    "{\"type\":\"welcome\",\"protocol_version\":1$capabilities}",
                                ),
                            )
                        }

                        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                            webSocket.close(code, reason)
                        }
                    },
                ),
            )
        }
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = UUID.randomUUID(),
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
            optionalAdvertisedCapabilities = setOf("direct_transport_data_v1"),
        )
        try {
            client.connect()
            val first = withTimeout(5_000) {
                client.negotiatedCapabilities.first {
                    "direct_transport_data_v1" in it.capabilities
                }
            }
            assertEquals(first.generation, client.diagnostics().connectedGeneration)

            client.connect()
            withTimeout(5_000) {
                client.state.first { it == RealtimeState.Connected }
            }
            val second = client.negotiatedCapabilities.value
            assertTrue(second.generation > first.generation)
            assertTrue("direct_transport_data_v1" !in second.capabilities)
            assertEquals(second.generation, client.diagnostics().connectedGeneration)
            assertEquals(2, hellosSeen.get())
            assertEquals(RealtimeState.Connected, client.state.replayCache.last())
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun realtimeAcceptsWelcomeThatEchoesExplicitSnapshotCapability(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        assertTrue(text.contains("terminal_snapshot_v1"))
                        check(
                            webSocket.send(
                                "{\"type\":\"welcome\",\"protocol_version\":1," +
                                    "\"capabilities\":[\"terminal_snapshot_v1\"]}",
                            ),
                        )
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = UUID.randomUUID(),
            identity = identity,
            role = "android",
            additionalCapabilities = setOf("terminal_snapshot_v1"),
            allowCleartextForLocalTest = true,
        )
        try {
            val connected = async(start = CoroutineStart.UNDISPATCHED) {
                client.state.first { it == RealtimeState.Connected }
            }
            client.connect()
            assertEquals(RealtimeState.Connected, withTimeout(5_000) { connected.await() })
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun negotiatedLivenessProbeCompletesOnMatchingPongWithoutChangingGeneration(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val probeSeen = CompletableDeferred<String>()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    private var welcomed = false

                    override fun onMessage(webSocket: WebSocket, text: String) {
                        if (!welcomed) {
                            welcomed = true
                            assertTrue(text.contains("\"liveness_probe_v1\""))
                            check(
                                webSocket.send(
                                    "{\"type\":\"welcome\",\"protocol_version\":1," +
                                        "\"capabilities\":[\"liveness_probe_v1\"]}",
                                ),
                            )
                            return
                        }
                        val match = Regex("\\\"probe_id\\\":\\\"([^\\\"]+)\\\"").find(text)
                        val probeId = match?.groupValues?.get(1)
                            ?: error("liveness probe did not carry a probe_id")
                        assertTrue(text.contains("\"type\":\"ping\""))
                        probeSeen.complete(probeId)
                        check(webSocket.send("{\"type\":\"pong\",\"probe_id\":\"$probeId\"}"))
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = UUID.randomUUID(),
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
        )
        try {
            val connected = async(start = CoroutineStart.UNDISPATCHED) {
                client.state.first { it == RealtimeState.Connected }
            }
            client.connect()
            withTimeout(5_000) { connected.await() }
            val generation = client.diagnostics().connectedGeneration
            assertTrue(client.supportsLivenessProbe(generation))
            assertTrue(client.probeLiveness(generation, 1_000))
            assertTrue(runCatching { UUID.fromString(withTimeout(1_000) { probeSeen.await() }) }.isSuccess)
            assertEquals(generation, client.diagnostics().connectedGeneration)
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun cancelledLivenessProbeReleasesGenerationGateForSuccessorProbe(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val probesSeen = AtomicInteger(0)
        val firstProbeSeen = CompletableDeferred<Unit>()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    private var welcomed = false

                    override fun onMessage(webSocket: WebSocket, text: String) {
                        if (!welcomed) {
                            welcomed = true
                            check(
                                webSocket.send(
                                    "{\"type\":\"welcome\",\"protocol_version\":1," +
                                        "\"capabilities\":[\"liveness_probe_v1\"]}",
                                ),
                            )
                            return
                        }
                        val probeId = Regex("\\\"probe_id\\\":\\\"([^\\\"]+)\\\"")
                            .find(text)?.groupValues?.get(1)
                            ?: error("liveness probe did not carry a probe_id")
                        if (probesSeen.incrementAndGet() == 1) {
                            firstProbeSeen.complete(Unit)
                        } else {
                            check(webSocket.send("{\"type\":\"pong\",\"probe_id\":\"$probeId\"}"))
                        }
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = UUID.randomUUID(),
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
        )
        try {
            val connected = async(start = CoroutineStart.UNDISPATCHED) {
                client.state.first { it == RealtimeState.Connected }
            }
            client.connect()
            withTimeout(5_000) { connected.await() }
            val generation = client.diagnostics().connectedGeneration

            val cancelled = async { client.probeLiveness(generation, 10_000) }
            withTimeout(1_000) { firstProbeSeen.await() }
            cancelled.cancelAndJoin()

            assertTrue(client.probeLiveness(generation, 1_000))
            assertEquals(2, probesSeen.get())
            assertEquals(generation, client.diagnostics().connectedGeneration)
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun realtimeRejectsWelcomeMissingExplicitSnapshotCapability(): Unit = runBlocking {
        listOf(
            "{\"type\":\"welcome\",\"protocol_version\":1}",
            "{\"type\":\"welcome\",\"protocol_version\":1," +
                "\"capabilities\":[\"cumulative_ack\"]}",
        ).forEach { welcome -> assertWelcomeRejectedByProtocol(welcome) }
    }

    @Test
    fun realtimeRejectsMalformedWelcomeCapabilitiesByProtocolClose(): Unit = runBlocking {
        listOf(
            "{\"type\":\"welcome\",\"protocol_version\":1," +
                "\"capabilities\":\"terminal_snapshot_v1\"}",
            "{\"type\":\"welcome\",\"protocol_version\":1," +
                "\"capabilities\":[1,\"terminal_snapshot_v1\"]}",
            "{\"type\":\"welcome\",\"protocol_version\":1," +
                "\"capabilities\":[\"terminal_snapshot_v1\",\"terminal_snapshot_v1\"]}",
            "{\"type\":\"welcome\",\"protocol_version\":1," +
                "\"capabilities\":[\"terminal_snapshot_v1\",\"INVALID CAPABILITY\"]}",
        ).forEach { welcome -> assertWelcomeRejectedByProtocol(welcome) }
    }

    @Test
    fun synchronousHandshakeFailureBeforeNewWebSocketReturnsIsNotDropped(): Unit = runBlocking {
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(401))
        server.start()
        val identity = DeviceIdentity.generate()
        val executor = InlineExecutorService()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "expired" },
            deviceId = UUID.randomUUID(),
            identity = identity,
            role = "android",
            httpClient = OkHttpClient.Builder().dispatcher(Dispatcher(executor)).build(),
            allowCleartextForLocalTest = true,
        )
        try {
            val rejected = async(start = CoroutineStart.UNDISPATCHED) {
                client.state.first { it is RealtimeState.AuthenticationRejected }
            }

            client.connect()

            val state = withTimeout(5_000) { rejected.await() }
            assertTrue(state is RealtimeState.AuthenticationRejected)
            assertEquals(401, (state as RealtimeState.AuthenticationRejected).status)
        } finally {
            client.close()
            executor.shutdownNow()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun realtimeNormalizesSharedRestClientForLongLivedWebSockets() {
        val restClient = OkHttpClient.Builder()
            .readTimeout(10, TimeUnit.SECONDS)
            .pingInterval(0, TimeUnit.MILLISECONDS)
            .build()

        val realtimeClient = ProductionRealtimeClient.normalizedWebSocketHttpClient(restClient)

        assertEquals(0, realtimeClient.readTimeoutMillis)
        assertEquals(15_000, realtimeClient.pingIntervalMillis)
        assertTrue(realtimeClient.dispatcher === restClient.dispatcher)
        assertTrue(realtimeClient.connectionPool === restClient.connectionPool)
        assertEquals(10_000, restClient.readTimeoutMillis)
        assertEquals(0, restClient.pingIntervalMillis)
    }

    @Test
    fun smallFrameCountTriggersReceivePressureBeforeHardQueueLimit() {
        assertTrue(ProductionRealtimeClient.HIGH_BUFFERED_EVENTS < ProductionRealtimeClient.MAX_BUFFERED_EVENTS)
        assertTrue(ProductionRealtimeClient.LOW_BUFFERED_EVENTS < ProductionRealtimeClient.HIGH_BUFFERED_EVENTS)
        assertTrue(
            ProductionRealtimeClient.shouldPauseReceive(
                ProductionRealtimeClient.HIGH_BUFFERED_EVENTS,
                1,
            ),
        )
        assertTrue(
            !ProductionRealtimeClient.shouldPauseReceive(
                ProductionRealtimeClient.HIGH_BUFFERED_EVENTS - 1,
                1,
            ),
        )
        assertTrue(
            !ProductionRealtimeClient.shouldResumeReceive(
                ProductionRealtimeClient.LOW_BUFFERED_EVENTS + 1,
                1,
            ),
        )
        assertTrue(
            ProductionRealtimeClient.shouldResumeReceive(
                ProductionRealtimeClient.LOW_BUFFERED_EVENTS,
                1,
            ),
        )
    }

    @Test
    fun apiRequestCarriesValidOidcAndDeviceProof() {
        val server = MockWebServer()
        server.enqueue(MockResponse().setResponseCode(200).setHeader("Content-Type", "application/json").setBody("{\"items\":[]}"))
        server.start()
        val identity = DeviceIdentity.generate()
        val deviceId = UUID.randomUUID()
        try {
            ProductionApiClient(server.url("/").toString(), AccessTokenProvider { "oidc-token" }, deviceId, identity, allowCleartextForLocalTest = true).use {
                it.get("/v1/devices")
            }
            val request = server.takeRequest()
            assertEquals("Bearer oidc-token", request.getHeader("Authorization"))
            assertEquals(deviceId.toString(), request.getHeader("X-TermLink-Device"))
            val timestamp = request.getHeader("X-TermLink-Timestamp")!!
            val nonce = request.getHeader("X-TermLink-Nonce")!!
            val signature = java.util.Base64.getUrlDecoder().decode(request.getHeader("X-TermLink-Signature"))
            val bodyHash = MessageDigest.getInstance("SHA-256").digest(ByteArray(0)).joinToString("") { "%02x".format(it) }
            val canonical = listOf("TERMLINK-DEVICE-PROOF-V1", "GET", "/v1/devices", timestamp, nonce, bodyHash).joinToString("\n").encodeToByteArray()
            val verifier = Ed25519Signer().apply { init(false, Ed25519PublicKeyParameters(identity.signingPublicKey, 0)); update(canonical, 0, canonical.size) }
            assertTrue(verifier.verifySignature(signature))
        } finally {
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun productionEnvelopeMatchesCrossPlatformCiphertextVector() {
        val envelope = ProductionRealtimeClient.encryptedEnvelope(
            sessionId = UUID.fromString("fedcba98-7654-3210-ffff-eeeeeeeeeeee"),
            senderId = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff"),
            messageType = MessageType.OUTPUT_CHUNK,
            direction = CryptoDirection.PC_TO_ANDROID,
            sequence = 42,
            acknowledgement = 7,
            keyId = "epoch-00000007".encodeToByteArray(),
            nonce = hex("aabbccdd000000000000002a"),
            sessionKey = hex("e95cacb46c64bb90b7a7311b3ae253add4e29dddfb2ded34d576cb866da7695c"),
            plaintext = hex("1b5b33316d5465726d4c696e6b1b5b306d0d0a"),
            nowMillis = 1_800_000_000_123,
        )
        assertEquals("6a97c75f0d8c984923467129d973a325134d69c9bd792587797651196a7498a68e3c44", envelope.ciphertext.toByteArray().toHex())
        assertEquals("1b5b33316d5465726d4c696e6b1b5b306d0d0a", ProductionRealtimeClient.decryptEnvelope(envelope, hex("e95cacb46c64bb90b7a7311b3ae253add4e29dddfb2ded34d576cb866da7695c")).toHex())
    }

    @Test
    fun androidTransportAcceptsOutputReplayOnTheCurrentSocket(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                        listOf(1L, 2L, 1L, 3L).forEach { sequence ->
                            check(webSocket.send(OkioByteString.of(*testEnvelope(sessionId, pcId, androidId, sequence).toByteArray())))
                        }
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = androidId,
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
        )
        try {
            val received = mutableListOf<Envelope>()
            val collector = launch(start = CoroutineStart.UNDISPATCHED) {
                client.incoming.take(4).toList(received)
            }
            client.connect()
            withTimeout(5_000) { collector.join() }
            assertEquals(listOf(1L, 2L, 1L, 3L), received.map(Envelope::getSeq))
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun receiveOverflowRetiresPhysicalGenerationAndKeepsOnlyContiguousPrefix(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                        for (sequence in 1L..300L) {
                            check(webSocket.send(OkioByteString.of(*testEnvelope(sessionId, pcId, androidId, sequence).toByteArray())))
                        }
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = androidId,
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
        )
        val firstReceived = CompletableDeferred<Unit>()
        val releaseCollector = CompletableDeferred<Unit>()
        val received = CopyOnWriteArrayList<Long>()
        val collector = launch(start = CoroutineStart.UNDISPATCHED) {
            client.incoming.collect { envelope ->
                received += envelope.seq
                if (envelope.seq == 1L) {
                    firstReceived.complete(Unit)
                    releaseCollector.await()
                }
            }
        }
        try {
            client.connect()
            withTimeout(5_000) { firstReceived.await() }
            // MockWebServer.send() only confirms local enqueue. Wait until the
            // client callback has rejected the first frame beyond its bounded
            // application queue and retired that incomplete generation.
            withTimeout(5_000) {
                while (client.diagnostics().droppedApplicationFrames == 0L ||
                    client.diagnostics().connectedGeneration != 0
                ) {
                    delay(10)
                }
            }
            val overflow = client.diagnostics()
            assertTrue(overflow.observedApplicationFrames in 2L..300L)
            assertEquals(1L, overflow.droppedApplicationFrames)
            assertEquals("FAILED", overflow.state)
            assertTrue(overflow.generation > 1)
            assertEquals(0, overflow.connectedGeneration)
            val acceptedFrames = (overflow.observedApplicationFrames - overflow.droppedApplicationFrames).toInt()
            assertTrue(acceptedFrames in 2 until 300)
            releaseCollector.complete(Unit)
            withTimeout(5_000) {
                while (received.size < acceptedFrames) delay(10)
            }
            assertEquals(acceptedFrames, received.size)
            assertTrue("overflow must stop before all frames", received.last() < 300L)
            assertTrue(
                "overflow must retain one contiguous prefix: $received",
                received.zipWithNext().all { (left, right) -> right == left + 1L },
            )
        } finally {
            releaseCollector.complete(Unit)
            collector.cancelAndJoin()
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    @Test
    fun malformedEnvelopeIsDroppedWithoutReplacingPhysicalGeneration(): Unit = runBlocking {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val sessionId = UUID.randomUUID()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send("{\"type\":\"welcome\",\"protocol_version\":1}"))
                        check(webSocket.send(OkioByteString.of(0.toByte())))
                        check(
                            webSocket.send(
                                OkioByteString.of(*testEnvelope(sessionId, pcId, androidId, 1).toByteArray()),
                            ),
                        )
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = androidId,
            identity = identity,
            role = "android",
            allowCleartextForLocalTest = true,
        )
        try {
            val received = async(start = CoroutineStart.UNDISPATCHED) {
                client.incoming.first()
            }
            client.connect()
            assertEquals(1L, withTimeout(5_000) { received.await() }.seq)
            assertEquals("CONNECTED", client.diagnostics().state)
            assertEquals(1, client.diagnostics().generation)
            assertEquals(1, client.diagnostics().connectedGeneration)
        } finally {
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    private fun testEnvelope(sessionId: UUID, pcId: UUID, androidId: UUID, sequence: Long): Envelope =
        Envelope.newBuilder()
            .setProtocolVersion(1)
            .setMessageType(MessageType.OUTPUT_CHUNK)
            .setSessionId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(sessionId)))
            .setSenderDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(pcId)))
            .setRecipientDeviceId(ByteString.copyFrom(ProductionRealtimeClient.uuidBytes(androidId)))
            .setDirection(Direction.PC_TO_ANDROID)
            .setSeq(sequence)
            .setSentAtMs(1_800_000_000_000L + sequence)
            .setKeyId(ByteString.copyFromUtf8("epoch-00000001"))
            .setNonce(ByteString.copyFrom(ByteArray(12) { sequence.toByte() }))
            .setCiphertext(ByteString.copyFrom(ByteArray(16) { sequence.toByte() }))
            .build()

    private suspend fun assertWelcomeRejectedByProtocol(welcome: String): Unit = coroutineScope {
        val server = MockWebServer()
        val identity = DeviceIdentity.generate()
        val protocolClose = CompletableDeferred<Pair<Int, String>>()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        check(webSocket.send(welcome))
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        protocolClose.complete(code to reason)
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val client = ProductionRealtimeClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "oidc-token" },
            deviceId = UUID.randomUUID(),
            identity = identity,
            role = "android",
            additionalCapabilities = setOf("terminal_snapshot_v1"),
            allowCleartextForLocalTest = true,
        )
        val states = CopyOnWriteArrayList<RealtimeState>()
        val collector = launch(start = CoroutineStart.UNDISPATCHED) {
            client.state.collect(states::add)
        }
        try {
            val failed = async(start = CoroutineStart.UNDISPATCHED) {
                client.state.first { it is RealtimeState.Failed }
            }
            client.connect()
            val (code, reason) = withTimeout(5_000) { protocolClose.await() }
            withTimeout(5_000) { failed.await() }
            assertEquals(1002, code)
            assertEquals("invalid welcome control", reason)
            assertTrue("invalid Welcome must not emit Connected: $states", RealtimeState.Connected !in states)
        } finally {
            collector.cancelAndJoin()
            client.close()
            identity.destroy()
            server.shutdown()
        }
    }

    private class InlineExecutorService : AbstractExecutorService() {
        @Volatile private var stopped = false

        override fun execute(command: Runnable) = command.run()
        override fun shutdown() { stopped = true }
        override fun shutdownNow(): MutableList<Runnable> {
            stopped = true
            return mutableListOf()
        }
        override fun isShutdown(): Boolean = stopped
        override fun isTerminated(): Boolean = stopped
        override fun awaitTermination(timeout: Long, unit: TimeUnit): Boolean = stopped
    }

    private fun hex(value: String): ByteArray = value.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
}
