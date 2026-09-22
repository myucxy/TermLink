package dev.termlink.network.direct

import com.google.protobuf.ByteString
import dev.termlink.crypto.DeviceIdentity
import dev.termlink.network.production.AccessTokenProvider
import dev.termlink.protocol.transport.v1.DirectCarrierV1
import dev.termlink.protocol.transport.v1.DirectPathTicketV1
import dev.termlink.protocol.transport.v1.TransportSignalEnvelopeV1
import dev.termlink.protocol.transport.v1.TransportSignalTypeV1
import java.security.SecureRandom
import java.io.IOException
import java.util.Base64
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.take
import kotlinx.coroutines.flow.toList
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.delay
import kotlinx.coroutines.withTimeout
import okhttp3.Call
import okhttp3.EventListener
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.Dispatcher
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okhttp3.mockwebserver.RecordedRequest
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString as OkioByteString
import org.bouncycastle.crypto.params.Ed25519PrivateKeyParameters
import org.bouncycastle.crypto.params.Ed25519PublicKeyParameters
import org.bouncycastle.crypto.signers.Ed25519Signer
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectTransportSignalingClientTest {
    private val now = 1_900_000_000_000L

    @Test
    fun featureOffPerformsNoHttpOrWebSocketWork() {
        val server = MockWebServer().also(MockWebServer::start)
        val fixture = fixture(server)
        try {
            assertEquals(DirectSignalingCallResult.Disabled, fixture.client.fetchConfig())
            assertFalse(fixture.client.startSignaling())
            assertEquals(0, server.requestCount)
            assertEquals(DirectTransportSignalingState.Disabled, fixture.client.state.value)
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun configAcceptsServerDataGateAndItsWithdrawalWithoutChangingShadowRequirements() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing)))
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing, directData = true)))
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing)))
        server.start()
        val fixture = fixture(server, enabled = true)
        try {
            val defaultConfig = fixture.client.fetchConfig() as DirectSignalingCallResult.Success
            assertFalse(defaultConfig.value.directRoutingPermitted)
            assertFalse(DirectTransportSignalingClient.hasDirectDataCapabilities(defaultConfig.value))

            val routedConfig = fixture.client.fetchConfig() as DirectSignalingCallResult.Success
            assertTrue(routedConfig.value.directRoutingPermitted)
            assertTrue(
                DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in routedConfig.value.capabilities,
            )
            assertTrue(
                DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in
                    routedConfig.value.capabilities,
            )
            assertTrue(DirectTransportSignalingClient.hasDirectDataCapabilities(routedConfig.value))

            val withdrawnConfig = fixture.client.fetchConfig() as DirectSignalingCallResult.Success
            assertFalse(withdrawnConfig.value.directRoutingPermitted)
            assertFalse(DirectTransportSignalingClient.hasDirectDataCapabilities(withdrawnConfig.value))
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun legacyDataOnlyConfigParsesButCannotPermitDirectCutover() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                configJson(
                    signing,
                    directRoutingPermitted = true,
                    capabilitiesOverride = listOf(
                        "direct_signaling_v1",
                        "direct_transport_shadow_v1",
                        "direct_transport_data_v1",
                    ),
                ),
            ),
        )
        server.start()
        val fixture = fixture(server, enabled = true)
        try {
            val legacy = fixture.client.fetchConfig() as DirectSignalingCallResult.Success
            assertTrue(legacy.value.directRoutingPermitted)
            assertTrue(
                DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in legacy.value.capabilities,
            )
            assertFalse(
                DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in
                    legacy.value.capabilities,
            )
            assertFalse(DirectTransportSignalingClient.hasDirectDataCapabilities(legacy.value))
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun configRejectsInconsistentUnknownAndDuplicateCapabilities() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val invalidConfigs = listOf(
            configJson(signing, directRoutingPermitted = true),
            configJson(signing, directData = true, directRoutingPermitted = false),
            configJson(
                signing,
                capabilitiesOverride = listOf("direct_signaling_v1", "direct_transport_v1"),
            ),
            configJson(
                signing,
                capabilitiesOverride = listOf(
                    "direct_signaling_v1",
                    "direct_transport_shadow_v1",
                    "direct_future_v1",
                ),
            ),
            configJson(
                signing,
                capabilitiesOverride = listOf(
                    "direct_signaling_v1",
                    "direct_transport_shadow_v1",
                    "direct_signaling_v1",
                ),
            ),
            configJson(
                signing,
                directRoutingPermitted = true,
                capabilitiesOverride = listOf(
                    "direct_signaling_v1",
                    "direct_transport_shadow_v1",
                    "direct_transport_data_v1",
                    "direct_transport_data_v1",
                ),
            ),
        )
        invalidConfigs.forEach { body ->
            server.enqueue(MockResponse().setResponseCode(200).setBody(body))
        }
        server.start()
        val fixture = fixture(server, enabled = true)
        try {
            invalidConfigs.forEach {
                val rejected = fixture.client.fetchConfig() as DirectSignalingCallResult.Fault
                assertEquals(DirectSignalingFaultCode.INVALID_CONFIGURATION, rejected.fault.code)
            }
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun routedConfigPreservesShadowTicketAndSocketWithoutAdvertisingDataCapability() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val local = UUID.randomUUID()
        val target = UUID.randomUUID()
        val connection = UUID.randomUUID()
        val ticket = signing.ticket(local, target, connection, now)
        val hello = CompletableDeferred<String>()
        server.enqueue(
            MockResponse().setResponseCode(200).setBody(
                configJson(signing, multipair = true, directData = true),
            ),
        )
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(ticket)))
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        hello.complete(text)
                        webSocket.send(validWelcome(multipair = true))
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = local, multipair = true)
        try {
            val config = fixture.client.fetchConfig() as DirectSignalingCallResult.Success
            assertTrue(DirectTransportSignalingClient.hasDirectDataCapabilities(config.value))
            val issued = fixture.client.issueTicket(target, connection, config.value)
            assertTrue(issued is DirectSignalingCallResult.Success)

            assertTrue(fixture.client.startSignaling())
            val clientHello = withTimeout(5_000) { hello.await() }
            assertTrue(clientHello.contains(DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY))
            assertFalse(clientHello.contains(DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY))
            assertTrue(
                fixture.client.state.first { it is DirectTransportSignalingState.Connected } is
                    DirectTransportSignalingState.Connected,
            )
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
            ticket.fill(0)
        }
    }

    @Test
    fun enabledDataCapabilitiesAreAdvertisedOnceButADataOnlyEchoKeepsShadowConnected() = runBlocking {
        val server = MockWebServer()
        val hello = CompletableDeferred<String>()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        hello.complete(text)
                        webSocket.send(
                            validWelcome(
                                capabilitiesOverride = listOf(
                                    "direct_transport_shadow_v1",
                                    "direct_transport_data_v1",
                                ),
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
        val local = UUID.randomUUID()
        val peer = UUID.randomUUID()
        val fixture = fixture(server, enabled = true, deviceId = local, directData = true)
        try {
            assertTrue(fixture.client.startSignaling())
            val clientHello = withTimeout(5_000) { hello.await() }
            assertEquals(1, clientHello.windowed(
                DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY.length,
            ).count { it == DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY })
            assertEquals(1, clientHello.windowed(
                DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY.length,
            ).count { it == DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY })
            val connected = fixture.client.state.first {
                it is DirectTransportSignalingState.Connected
            } as DirectTransportSignalingState.Connected
            val snapshot = fixture.client.negotiatedCapabilities.value
            assertEquals(connected.generation, snapshot.generation)
            assertTrue(DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in snapshot.capabilities)
            assertFalse(
                DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in
                    snapshot.capabilities,
            )
            assertFalse(fixture.client.isDirectDataV1Negotiated(connected.generation))

            assertTrue(
                fixture.client.send(
                    signal(UUID.randomUUID(), local, peer, 1).toBuilder()
                        .setSignalType(TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_CLOSE)
                        .build(),
                ),
            )
            assertFalse(
                fixture.client.send(
                    signal(UUID.randomUUID(), local, peer, 1).toBuilder()
                        .setSignalType(
                            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_PROPOSAL,
                        )
                        .build(),
                ),
            )
            val fault = fixture.client.state.value as DirectTransportSignalingState.Fault
            assertEquals(DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED, fault.fault.code)
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun echoedDataCapabilityPublishesGenerationAndAllowsCutoverSignalsBothWays() = runBlocking {
        val server = MockWebServer()
        val serverSocket = CompletableDeferred<WebSocket>()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        webSocket.send(validWelcome(directData = true))
                        serverSocket.complete(webSocket)
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val local = UUID.randomUUID()
        val peer = UUID.randomUUID()
        val connection = UUID.randomUUID()
        val fixture = fixture(server, enabled = true, deviceId = local, directData = true)
        try {
            val received = async(start = CoroutineStart.UNDISPATCHED) {
                fixture.client.incoming.take(4).toList()
            }
            assertTrue(fixture.client.startSignaling())
            val connected = fixture.client.state.first {
                it is DirectTransportSignalingState.Connected
            } as DirectTransportSignalingState.Connected
            val negotiated = fixture.client.negotiatedCapabilities.value
            assertEquals(connected.generation, negotiated.generation)
            assertTrue(DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in negotiated.capabilities)
            assertTrue(
                DirectTransportSignalingClient.DIRECT_SESSION_CUTOVER_CAPABILITY in
                    negotiated.capabilities,
            )
            assertTrue(fixture.client.isDirectDataV1Negotiated(connected.generation))

            val cutoverTypes = listOf(
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_PROPOSAL,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_ACCEPT,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CUTOVER_COMMIT,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_FALLBACK,
            )
            cutoverTypes.forEachIndexed { index, type ->
                assertTrue(
                    fixture.client.send(
                        signal(connection, local, peer, (index + 1).toLong()).toBuilder()
                            .setSignalType(type)
                            .build(),
                    ),
                )
            }
            val activeServerSocket = withTimeout(5_000) { serverSocket.await() }
            cutoverTypes.forEachIndexed { index, type ->
                val envelope = signal(connection, peer, local, (index + 1).toLong()).toBuilder()
                    .setSignalType(type)
                    .build()
                assertTrue(activeServerSocket.send(OkioByteString.of(*envelope.toByteArray())))
            }
            assertEquals(cutoverTypes, withTimeout(5_000) { received.await() }.map { it.signalType })

            fixture.client.close()
            val retired = fixture.client.negotiatedCapabilities.value
            assertTrue(retired.generation > connected.generation)
            assertTrue(retired.capabilities.isEmpty())
            assertFalse(fixture.client.isDirectDataV1Negotiated(connected.generation))
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun reconnectImmediatelyRevokesDataUntilTheNextGenerationEchoesIt() = runBlocking {
        val server = MockWebServer()
        val firstSocket = CompletableDeferred<WebSocket>()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        webSocket.send(validWelcome(directData = true))
                        firstSocket.complete(webSocket)
                    }
                },
            ),
        )
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        webSocket.send(validWelcome())
                    }

                    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                        webSocket.close(code, reason)
                    }
                },
            ),
        )
        server.start()
        val fixture = fixture(server, enabled = true, directData = true)
        try {
            assertTrue(fixture.client.startSignaling())
            val firstConnected = fixture.client.state.first {
                it is DirectTransportSignalingState.Connected
            } as DirectTransportSignalingState.Connected
            assertTrue(fixture.client.isDirectDataV1Negotiated(firstConnected.generation))
            assertTrue(withTimeout(5_000) { firstSocket.await() }.close(1000, "replace generation"))

            val secondConnected = withTimeout(5_000) {
                fixture.client.state.first {
                    it is DirectTransportSignalingState.Connected &&
                        it.generation != firstConnected.generation
                }
            } as DirectTransportSignalingState.Connected
            val snapshot = fixture.client.negotiatedCapabilities.value
            assertEquals(secondConnected.generation, snapshot.generation)
            assertTrue(snapshot.capabilities.isNotEmpty())
            assertFalse(DirectTransportSignalingClient.DIRECT_DATA_CAPABILITY in snapshot.capabilities)
            assertFalse(fixture.client.isDirectDataV1Negotiated(firstConnected.generation))
            assertFalse(fixture.client.isDirectDataV1Negotiated(secondConnected.generation))
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun welcomeRejectsUnadvertisedOrDuplicateDataCapability() = runBlocking {
        val cases = listOf(
            false to validWelcome(directData = true),
            true to validWelcome(
                capabilitiesOverride = listOf(
                    "direct_transport_shadow_v1",
                    "direct_session_cutover_v1",
                ),
            ),
            true to validWelcome(
                capabilitiesOverride = listOf(
                    "direct_transport_shadow_v1",
                    "direct_transport_data_v1",
                    "direct_transport_data_v1",
                ),
            ),
        )
        cases.forEach { (directDataEnabled, welcome) ->
            val server = MockWebServer()
            server.enqueue(
                MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            webSocket.send(welcome)
                        }

                        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                            webSocket.close(code, reason)
                        }
                    },
                ),
            )
            server.start()
            val fixture = fixture(server, enabled = true, directData = directDataEnabled)
            try {
                assertTrue(fixture.client.startSignaling())
                val fault = withTimeout(5_000) {
                    fixture.client.state.first {
                        it is DirectTransportSignalingState.Fault &&
                            it.fault.code == DirectSignalingFaultCode.SIGNAL_PROTOCOL_REJECTED
                    }
                } as DirectTransportSignalingState.Fault
                assertFalse(fault.willRetry)
                assertTrue(fixture.client.negotiatedCapabilities.value.capabilities.isEmpty())
            } finally {
                fixture.close()
                server.shutdown()
            }
        }
    }

    @Test
    fun httpControlUsesCanonicalDeviceProofAndSignalUsesIndependentProof() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val signalOpened = CompletableDeferred<Unit>()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path) {
                DirectTransportSignalingClient.CONFIG_PATH -> MockResponse()
                    .setResponseCode(200)
                    .setBody(configJson(signing))
                DirectTransportSignalingClient.SIGNAL_PATH -> MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            signalOpened.complete(Unit)
                            webSocket.send(validWelcome())
                        }

                        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                            webSocket.close(code, reason)
                        }
                    },
                )
                else -> MockResponse().setResponseCode(404)
            }
        }
        server.start()
        val fixture = fixture(server, enabled = true)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            val configRequest = server.takeRequest(5, TimeUnit.SECONDS)
            assertNotNull(configRequest)
            assertProof(
                checkNotNull(configRequest),
                fixture.identity.signingPublicKey,
                DirectTransportSignalingProof.DEVICE_PROOF_DOMAIN,
                DirectTransportSignalingClient.CONFIG_PATH,
            )

            assertTrue(fixture.client.startSignaling())
            withTimeout(5_000) { signalOpened.await() }
            val signalRequest = server.takeRequest(5, TimeUnit.SECONDS)
            assertNotNull(signalRequest)
            assertProof(
                checkNotNull(signalRequest),
                fixture.identity.signingPublicKey,
                DirectTransportSignalingProof.SIGNAL_PROOF_DOMAIN,
                DirectTransportSignalingClient.SIGNAL_PATH,
            )
            assertTrue(
                fixture.client.state.first { it is DirectTransportSignalingState.Connected } is
                    DirectTransportSignalingState.Connected,
            )
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun multipairIsAdvertisedAndTypesElevenToFourteenAreAcceptedOnlyAfterV4Config() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val hello = CompletableDeferred<String>()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path) {
                DirectTransportSignalingClient.CONFIG_PATH -> MockResponse()
                    .setResponseCode(200)
                    .setBody(configJson(signing, multipair = true))
                DirectTransportSignalingClient.SIGNAL_PATH -> MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            hello.complete(text)
                            webSocket.send(validWelcome(multipair = true))
                        }

                        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                            webSocket.close(code, reason)
                        }
                    },
                )
                else -> MockResponse().setResponseCode(404)
            }
        }
        server.start()
        val local = UUID.randomUUID()
        val peer = UUID.randomUUID()
        val fixture = fixture(server, enabled = true, deviceId = local, multipair = true)
        try {
            val config = fixture.client.fetchConfig() as DirectSignalingCallResult.Success
            assertTrue(DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY in config.value.capabilities)
            assertTrue(fixture.client.multipairV2Active)
            assertTrue(fixture.client.startSignaling())
            assertTrue(withTimeout(5_000) { hello.await() }.contains(
                DirectTransportSignalingClient.DIRECT_MULTIPAIR_CAPABILITY,
            ))
            fixture.client.state.first { it is DirectTransportSignalingState.Connected }
            val connection = UUID.randomUUID()
            for (type in listOf(
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2,
                TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2,
            )) {
                val sequence = (type.number - 10).toLong()
                assertTrue(
                    fixture.client.send(
                        signal(connection, local, peer, sequence).toBuilder().setSignalType(type).build(),
                    ),
                )
            }
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun negotiatedMultipairSocketDeliversInboundTypesElevenToFourteen() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        val expected = listOf(
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2,
        )
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path) {
                DirectTransportSignalingClient.CONFIG_PATH -> MockResponse()
                    .setResponseCode(200)
                    .setBody(configJson(signing, multipair = true))
                DirectTransportSignalingClient.SIGNAL_PATH -> MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            webSocket.send(validWelcome(multipair = true))
                            expected.forEachIndexed { index, type ->
                                val envelope = signal(
                                    connectionId,
                                    pcId,
                                    androidId,
                                    sequence = (index + 1).toLong(),
                                ).toBuilder().setSignalType(type).build()
                                webSocket.send(OkioByteString.of(*envelope.toByteArray()))
                            }
                        }

                        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                            webSocket.close(code, reason)
                        }
                    },
                )
                else -> MockResponse().setResponseCode(404)
            }
        }
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = androidId, multipair = true)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            val received = async(start = CoroutineStart.UNDISPATCHED) {
                fixture.client.incoming.take(expected.size).toList()
            }
            assertTrue(fixture.client.startSignaling())
            assertEquals(expected, withTimeout(5_000) { received.await() }.map { it.signalType })
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun frozenV3SocketRejectsEveryMultipairOnlyInboundType() = runBlocking {
        val types = listOf(
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_ACK_V2,
            TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_NOMINATION_RESULT_V2,
        )
        for (type in types) {
            val server = MockWebServer()
            val androidId = UUID.randomUUID()
            val pcId = UUID.randomUUID()
            val connectionId = UUID.randomUUID()
            server.enqueue(
                MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            webSocket.send(validWelcome())
                            val envelope = signal(connectionId, pcId, androidId, sequence = 1)
                                .toBuilder().setSignalType(type).build()
                            webSocket.send(OkioByteString.of(*envelope.toByteArray()))
                        }
                    },
                ),
            )
            server.start()
            val fixture = fixture(server, enabled = true, deviceId = androidId, multipair = false)
            try {
                assertTrue(fixture.client.startSignaling())
                val fault = withTimeout(5_000) {
                    fixture.client.state.first {
                        it is DirectTransportSignalingState.Fault &&
                            it.fault.code == DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED
                    }
                } as DirectTransportSignalingState.Fault
                assertFalse("legacy socket retried rejected $type", fault.willRetry)
            } finally {
                fixture.close()
                server.shutdown()
            }
        }
    }

    @Test
    fun multipairSocketRejectsUnknownTypeAboveFourteen() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest): MockResponse = when (request.path) {
                DirectTransportSignalingClient.CONFIG_PATH -> MockResponse()
                    .setResponseCode(200)
                    .setBody(configJson(signing, multipair = true))
                DirectTransportSignalingClient.SIGNAL_PATH -> MockResponse().withWebSocketUpgrade(
                    object : WebSocketListener() {
                        override fun onMessage(webSocket: WebSocket, text: String) {
                            webSocket.send(validWelcome(multipair = true))
                            val envelope = signal(connectionId, pcId, androidId, sequence = 1)
                                .toBuilder().setSignalTypeValue(15).build()
                            webSocket.send(OkioByteString.of(*envelope.toByteArray()))
                        }
                    },
                )
                else -> MockResponse().setResponseCode(404)
            }
        }
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = androidId, multipair = true)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            assertTrue(fixture.client.startSignaling())
            val fault = withTimeout(5_000) {
                fixture.client.state.first {
                    it is DirectTransportSignalingState.Fault &&
                        it.fault.code == DirectSignalingFaultCode.SIGNAL_FRAME_REJECTED
                }
            } as DirectTransportSignalingState.Fault
            assertFalse(fault.willRetry)
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun refreshedConfigRevokesPreviouslyPublishedMultipairCapability() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing, multipair = true)))
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing, multipair = false)))
        server.start()
        val fixture = fixture(server, enabled = true, multipair = true)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            assertTrue(fixture.client.multipairV2Active)
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            assertFalse(fixture.client.multipairV2Active)
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun serverWideMultipairConfigDoesNotMakeFrozenV3AdvertiseOrSendV2() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing, multipair = true)))
        server.start()
        val local = UUID.randomUUID()
        val peer = UUID.randomUUID()
        val fixture = fixture(server, enabled = true, deviceId = local, multipair = false)
        try {
            assertTrue(fixture.client.fetchConfig() is DirectSignalingCallResult.Success)
            assertFalse(fixture.client.multipairV2Active)
            assertFalse(
                fixture.client.send(
                    signal(UUID.randomUUID(), local, peer, 1).toBuilder()
                        .setSignalType(
                            TransportSignalTypeV1
                                .TRANSPORT_SIGNAL_TYPE_V1_CONNECTIVITY_CHECK_BATCH_V2,
                        )
                        .build(),
                ),
            )
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun ticketRequestIsCanonicalAndTicketValidationIsStrict() {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val deviceId = UUID.randomUUID()
        val targetId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        val validTicket = signing.ticket(deviceId, targetId, connectionId, now)
        val tamperedTicket = validTicket.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }
        val wrongConnectionTicket = signing.ticket(deviceId, targetId, UUID.randomUUID(), now)
        val expiredTicket = signing.ticket(deviceId, targetId, connectionId, now - 1_200_001)
        val overlongTicket = signing.ticket(
            deviceId,
            targetId,
            connectionId,
            now,
            lifetimeMillis = 1_200_001,
        )
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(validTicket)))
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(tamperedTicket)))
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(wrongConnectionTicket)))
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(expiredTicket)))
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(overlongTicket)))
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = deviceId)
        val config = parsedConfig(signing)
        try {
            val accepted = fixture.client.issueTicket(targetId, connectionId, config)
            assertTrue(accepted is DirectSignalingCallResult.Success)
            val ticket = (accepted as DirectSignalingCallResult.Success).value
            assertArrayEquals(validTicket, ticket.wireBytes)

            val request = checkNotNull(server.takeRequest(5, TimeUnit.SECONDS))
            assertEquals(DirectTransportSignalingClient.TICKET_PATH, request.path)
            assertEquals(
                "{\"target_device_id\":\"$targetId\",\"connection_id\":\"$connectionId\",\"requested_carriers\":[\"UDP_QUIC\"]}",
                request.body.clone().readUtf8(),
            )
            assertProof(
                request,
                fixture.identity.signingPublicKey,
                DirectTransportSignalingProof.DEVICE_PROOF_DOMAIN,
                DirectTransportSignalingClient.TICKET_PATH,
            )

            val rejected = fixture.client.issueTicket(targetId, connectionId, config)
            assertEquals(
                DirectSignalingFaultCode.TICKET_REJECTED,
                (rejected as DirectSignalingCallResult.Fault).fault.code,
            )
            repeat(3) {
                val strictRejection = fixture.client.issueTicket(targetId, connectionId, config)
                assertEquals(
                    DirectSignalingFaultCode.TICKET_REJECTED,
                    (strictRejection as DirectSignalingCallResult.Fault).fault.code,
                )
            }
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun incomingSignalSequenceReplayAndOversizeAreFenced() = runBlocking {
        val server = MockWebServer()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        val signal = signal(connectionId, pcId, androidId, sequence = 1)
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        webSocket.send(validWelcome())
                        val bytes = OkioByteString.of(*signal.toByteArray())
                        webSocket.send(bytes)
                        webSocket.send(bytes)
                    }
                },
            ),
        )
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = androidId)
        try {
            val received = async(start = CoroutineStart.UNDISPATCHED) {
                fixture.client.incoming.first()
            }
            fixture.client.startSignaling()
            assertEquals(1L, withTimeout(5_000) { received.await() }.signalSeq)
            val fault = withTimeout(5_000) {
                fixture.client.state.first {
                    it is DirectTransportSignalingState.Fault &&
                        it.fault.code == DirectSignalingFaultCode.SIGNAL_REPLAYED
                }
            } as DirectTransportSignalingState.Fault
            assertFalse(fault.willRetry)

            val oversized = signal.toBuilder()
                .setSignalSeq(2)
                .setCiphertext(ByteString.copyFrom(ByteArray(DirectTransportSignalingClient.MAX_SIGNAL_FRAME_BYTES)))
                .build()
            assertFalse(fixture.client.send(oversized))
            assertTrue(
                fixture.client.state.value is DirectTransportSignalingState.Fault,
            )
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun signalSequenceMustStartAtOneAndAdvanceExactly() = runBlocking {
        val inboundServer = MockWebServer()
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        inboundServer.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    override fun onMessage(webSocket: WebSocket, text: String) {
                        webSocket.send(validWelcome())
                        webSocket.send(
                            OkioByteString.of(
                                *signal(connectionId, pcId, androidId, sequence = 2).toByteArray(),
                            ),
                        )
                    }
                },
            ),
        )
        inboundServer.start()
        val inbound = fixture(inboundServer, enabled = true, deviceId = androidId)
        try {
            inbound.client.startSignaling()
            val fault = withTimeout(5_000) {
                inbound.client.state.first {
                    it is DirectTransportSignalingState.Fault &&
                        it.fault.code == DirectSignalingFaultCode.SIGNAL_REPLAYED
                }
            } as DirectTransportSignalingState.Fault
            assertFalse(fault.willRetry)
        } finally {
            inbound.close()
            inboundServer.shutdown()
        }

        val outboundServer = MockWebServer()
        outboundServer.enqueue(
            MockResponse().withWebSocketUpgrade(object : WebSocketListener() {}),
        )
        outboundServer.start()
        val outbound = fixture(outboundServer, enabled = true, deviceId = androidId)
        try {
            outbound.client.startSignaling()
            assertTrue(outbound.client.send(signal(connectionId, androidId, pcId, sequence = 1)))
            assertFalse(outbound.client.send(signal(connectionId, androidId, pcId, sequence = 3)))
            val fault = outbound.client.state.value as DirectTransportSignalingState.Fault
            assertEquals(DirectSignalingFaultCode.SIGNAL_REPLAYED, fault.fault.code)
            assertFalse(fault.willRetry)
        } finally {
            outbound.close()
            outboundServer.shutdown()
        }
    }

    @Test
    fun outboundQueueOverflowClosesOnlySignaling() {
        val server = MockWebServer()
        server.enqueue(
            MockResponse().withWebSocketUpgrade(
                object : WebSocketListener() {
                    // Deliberately never Welcome, retaining the bounded pre-Welcome queue.
                },
            ),
        )
        server.start()
        val relaySentinel = AtomicBoolean(true)
        val androidId = UUID.randomUUID()
        val pcId = UUID.randomUUID()
        val connectionId = UUID.randomUUID()
        val fixture = fixture(server, enabled = true, deviceId = androidId)
        try {
            fixture.client.startSignaling()
            repeat(DirectTransportSignalingClient.MAX_QUEUED_SIGNAL_FRAMES) { index ->
                assertTrue(fixture.client.send(signal(connectionId, androidId, pcId, (index + 1).toLong())))
            }
            assertFalse(
                fixture.client.send(
                    signal(
                        connectionId,
                        androidId,
                        pcId,
                        (DirectTransportSignalingClient.MAX_QUEUED_SIGNAL_FRAMES + 1).toLong(),
                    ),
                ),
            )
            val fault = fixture.client.state.value as DirectTransportSignalingState.Fault
            assertEquals(DirectSignalingFaultCode.SIGNAL_QUEUE_OVERFLOW, fault.fault.code)
            assertFalse(fault.willRetry)
            assertTrue("signalling overflow must not touch realtime", relaySentinel.get())
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun http503AndSignalTimeoutNeverTouchRelaySentinel() = runBlocking {
        val server = MockWebServer()
        val relaySentinel = AtomicBoolean(true)
        server.enqueue(MockResponse().setResponseCode(503).setBody("{}"))
        server.enqueue(MockResponse().setSocketPolicy(okhttp3.mockwebserver.SocketPolicy.NO_RESPONSE))
        server.start()
        val retryWaits = CopyOnWriteArrayList<Long>()
        val fixture = fixture(
            server,
            enabled = true,
            retryDelay = DirectSignalingRetryDelay { retryWaits += it },
        )
        try {
            val response = fixture.client.fetchConfig()
            val httpFault = response as DirectSignalingCallResult.Fault
            assertEquals(DirectSignalingFaultCode.HTTP_UNAVAILABLE, httpFault.fault.code)
            assertEquals(503, httpFault.fault.httpStatus)
            assertTrue(relaySentinel.get())

            fixture.client.startSignaling()
            val fault = withTimeout(15_000) {
                fixture.client.state.first { it is DirectTransportSignalingState.Fault }
            } as DirectTransportSignalingState.Fault
            assertEquals(DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED, fault.fault.code)
            assertTrue(relaySentinel.get())
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun retryableSignalFailuresContinueAfterInitialBurstWithoutTerminalFault() = runBlocking {
        val server = MockWebServer()
        server.dispatcher = object : Dispatcher() {
            override fun dispatch(request: RecordedRequest) = MockResponse().setResponseCode(503)
        }
        server.start()
        val retryWaits = CopyOnWriteArrayList<Long>()
        val fixture = fixture(
            server,
            enabled = true,
            retryDelay = DirectSignalingRetryDelay { wait ->
                retryWaits += wait
                if (retryWaits.size >= 4) kotlinx.coroutines.awaitCancellation()
            },
        )
        try {
            assertTrue(fixture.client.startSignaling())
            withTimeout(5_000) {
                while (retryWaits.size < 4) delay(10)
            }
            val fault = fixture.client.state.value as DirectTransportSignalingState.Fault
            assertEquals(DirectSignalingFaultCode.SIGNAL_CONNECT_FAILED, fault.fault.code)
            assertTrue(fault.willRetry)
        } finally {
            fixture.close()
            server.shutdown()
        }
    }

    @Test
    fun cancellingConfigRequestCancelsTheExactOkHttpCall() = runBlocking {
        val server = MockWebServer()
        server.enqueue(MockResponse().setSocketPolicy(okhttp3.mockwebserver.SocketPolicy.NO_RESPONSE))
        server.start()
        val failed = CompletableDeferred<Unit>()
        val http = OkHttpClient.Builder()
            .eventListener(object : EventListener() {
                override fun callFailed(call: Call, ioe: IOException) {
                    failed.complete(Unit)
                }
            })
            .build()
        val fixture = fixture(server, enabled = true, httpClient = http)
        try {
            val request = async(start = CoroutineStart.UNDISPATCHED) {
                fixture.client.fetchConfigCancellable()
            }
            withTimeout(2_000) {
                while (server.requestCount == 0) delay(10)
            }
            request.cancelAndJoin()
            withTimeout(2_000) { failed.await() }
            assertTrue(request.isCancelled)
            assertEquals(0, http.dispatcher.runningCallsCount())
        } finally {
            fixture.close()
            http.dispatcher.executorService.shutdownNow()
            http.connectionPool.evictAll()
            server.shutdown()
        }
    }

    @Test
    fun cancellableConfigAndTicketPreserveValidatedSuccess() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val local = UUID.randomUUID()
        val target = UUID.randomUUID()
        val connection = UUID.randomUUID()
        val ticket = signing.ticket(local, target, connection, now)
        server.enqueue(MockResponse().setResponseCode(200).setBody(configJson(signing)))
        server.enqueue(MockResponse().setResponseCode(201).setBody(ticketResponse(ticket)))
        server.start()
        val fixture = fixture(server, enabled = true, deviceId = local)
        try {
            val config = fixture.client.fetchConfigCancellable()
            assertTrue(config is DirectSignalingCallResult.Success)
            val issued = fixture.client.issueTicketCancellable(
                target,
                connection,
                (config as DirectSignalingCallResult.Success).value,
            )
            assertTrue(issued is DirectSignalingCallResult.Success)
            assertArrayEquals(ticket, (issued as DirectSignalingCallResult.Success).value.wireBytes)
            assertEquals(2, server.requestCount)
        } finally {
            fixture.close()
            server.shutdown()
            signing.destroy()
            ticket.fill(0)
        }
    }

    @Test
    fun cancellingConfigAfterHeadersCancelsAStalledResponseBody() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        server.enqueue(
            MockResponse()
                .setResponseCode(200)
                .setBody(configJson(signing) + " ".repeat(65_536))
                .throttleBody(1, 1, TimeUnit.SECONDS),
        )
        server.start()
        val headersStarted = CompletableDeferred<Unit>()
        val failed = CompletableDeferred<Unit>()
        val http = OkHttpClient.Builder()
            .eventListener(object : EventListener() {
                override fun responseHeadersStart(call: Call) {
                    headersStarted.complete(Unit)
                }

                override fun callFailed(call: Call, ioe: IOException) {
                    failed.complete(Unit)
                }
            })
            .build()
        val fixture = fixture(server, enabled = true, httpClient = http)
        try {
            val request = async(start = CoroutineStart.UNDISPATCHED) {
                fixture.client.fetchConfigCancellable()
            }
            withTimeout(2_000) { headersStarted.await() }
            request.cancelAndJoin()
            withTimeout(2_000) { failed.await() }
            assertTrue(request.isCancelled)
            assertEquals(0, http.dispatcher.runningCallsCount())
        } finally {
            fixture.close()
            http.dispatcher.executorService.shutdownNow()
            http.connectionPool.evictAll()
            server.shutdown()
            signing.destroy()
        }
    }

    @Test
    fun cancellingTicketRequestCancelsTheExactOkHttpCall() = runBlocking {
        val server = MockWebServer()
        val signing = TicketSigningFixture()
        val local = UUID.randomUUID()
        val target = UUID.randomUUID()
        val connection = UUID.randomUUID()
        server.enqueue(MockResponse().setSocketPolicy(okhttp3.mockwebserver.SocketPolicy.NO_RESPONSE))
        server.start()
        val failed = CompletableDeferred<Unit>()
        val http = OkHttpClient.Builder()
            .eventListener(object : EventListener() {
                override fun callFailed(call: Call, ioe: IOException) {
                    failed.complete(Unit)
                }
            })
            .build()
        val fixture = fixture(server, enabled = true, deviceId = local, httpClient = http)
        try {
            val request = async(start = CoroutineStart.UNDISPATCHED) {
                fixture.client.issueTicketCancellable(target, connection, parsedConfig(signing))
            }
            withTimeout(2_000) {
                while (server.requestCount == 0) {
                    check(!request.isCompleted) { "ticket call completed before reaching HTTP" }
                    delay(10)
                }
            }
            request.cancelAndJoin()
            withTimeout(2_000) { failed.await() }
            assertTrue(request.isCancelled)
            assertEquals(0, http.dispatcher.runningCallsCount())
        } finally {
            fixture.close()
            http.dispatcher.executorService.shutdownNow()
            http.connectionPool.evictAll()
            server.shutdown()
            signing.destroy()
        }
    }

    private fun fixture(
        server: MockWebServer,
        enabled: Boolean = false,
        deviceId: UUID = UUID.randomUUID(),
        retryDelay: DirectSignalingRetryDelay = DirectSignalingRetryDelay { },
        multipair: Boolean = false,
        directData: Boolean = false,
        httpClient: OkHttpClient = OkHttpClient(),
    ): ClientFixture {
        val identity = DeviceIdentity.generate()
        val client = DirectTransportSignalingClient(
            rootUrl = server.url("/").toString(),
            tokenProvider = AccessTokenProvider { "access-token" },
            deviceId = deviceId,
            identity = identity,
            role = "android",
            ticketSignatureVerifier = bouncyTicketVerifier(),
            feature = DirectTransportSignalingFeature(enabled),
            multipairV2Enabled = multipair,
            directDataV1Enabled = directData,
            httpClient = httpClient,
            clockMillis = { now },
            random = FixedSecureRandom(),
            retryDelay = retryDelay,
            retryJitter = DirectSignalingRetryJitter { 0 },
            signalHandshakeTimeoutMillis = 100,
            allowCleartextForLocalTest = true,
        )
        return ClientFixture(client, identity)
    }

    private fun parsedConfig(signing: TicketSigningFixture): DirectTransportServerConfig =
        DirectTransportServerConfig(
            protocolVersion = 1,
            capabilities = setOf("direct_signaling_v1", "direct_transport_shadow_v1"),
            directRoutingPermitted = false,
            ticketSigningKeys = mapOf(
                signing.keyId to DirectTicketSigningKey(signing.keyId, signing.publicKey),
            ),
            stunServers = listOf("stun:one.test:3478"),
        )

    private fun configJson(
        signing: TicketSigningFixture,
        multipair: Boolean = false,
        directData: Boolean = false,
        directRoutingPermitted: Boolean = directData,
        capabilitiesOverride: List<String>? = null,
    ): String {
        val capabilities = capabilitiesOverride ?: buildList {
            add("direct_signaling_v1")
            add("direct_transport_shadow_v1")
            if (multipair) add("direct_transport_shadow_multipair_v2")
            if (directData) {
                add("direct_transport_data_v1")
                add("direct_session_cutover_v1")
            }
        }
        val capabilityJson = capabilities.joinToString(",") { "\"$it\"" }
        return """{"protocol_version":1,"capabilities":[$capabilityJson],"direct_routing_permitted":$directRoutingPermitted,"ticket_signing_keys":[{"key_id":"${signing.keyId}","algorithm":"Ed25519","public_key":"${signing.publicKey.base64Url()}"}],"stun_servers":["stun:one.test:3478"]}"""
    }

    private fun ticketResponse(ticket: ByteArray): String =
        """{"protocol_version":1,"ticket_protobuf":"${ticket.base64Url()}"}"""

    private fun validWelcome(
        multipair: Boolean = false,
        directData: Boolean = false,
        capabilitiesOverride: List<String>? = null,
    ): String {
        val capabilities = capabilitiesOverride ?: buildList {
            add("direct_transport_shadow_v1")
            if (multipair) add("direct_transport_shadow_multipair_v2")
            if (directData) {
                add("direct_transport_data_v1")
                add("direct_session_cutover_v1")
            }
        }
        val capabilityJson = capabilities.joinToString(",") { "\"$it\"" }
        return """{"type":"SERVER_WELCOME","protocol_version":1,"capabilities":[$capabilityJson],"heartbeat_interval_ms":15000,"max_signal_frame_bytes":65536}"""
    }

    private fun signal(
        connectionId: UUID,
        senderId: UUID,
        recipientId: UUID,
        sequence: Long,
    ): TransportSignalEnvelopeV1 = TransportSignalEnvelopeV1.newBuilder()
        .setProtocolVersion(1)
        .setSignalType(TransportSignalTypeV1.TRANSPORT_SIGNAL_TYPE_V1_PATH_OFFER)
        .setConnectionId(ByteString.copyFrom(connectionId.bytes()))
        .setSenderDeviceId(ByteString.copyFrom(senderId.bytes()))
        .setRecipientDeviceId(ByteString.copyFrom(recipientId.bytes()))
        .setPathEpoch(1)
        .setSignalSeq(sequence)
        .setSignalAck(0)
        .setSentAtMs(now)
        .setKeyId(ByteString.copyFromUtf8("direct-epoch-1"))
        .setNonce(ByteString.copyFrom(ByteArray(12) { sequence.toByte() }))
        .setCiphertext(ByteString.copyFrom(ByteArray(32) { 0x5a }))
        .build()

    private fun assertProof(
        request: RecordedRequest,
        publicKey: ByteArray,
        expectedDomain: String,
        expectedPath: String,
    ) {
        assertEquals(expectedPath, request.path)
        assertEquals("Bearer access-token", request.getHeader("Authorization"))
        val timestamp = checkNotNull(request.getHeader("X-TermLink-Timestamp")).toLong()
        val nonce = checkNotNull(request.getHeader("X-TermLink-Nonce"))
        val signature = checkNotNull(request.getHeader("X-TermLink-Signature")).decodeBase64Url()
        val canonical = DirectTransportSignalingProof.canonical(
            expectedDomain,
            request.method ?: "GET",
            expectedPath,
            timestamp,
            nonce,
            request.body.clone().readByteArray(),
        )
        assertTrue(verify(publicKey, canonical, signature))
    }

    private fun bouncyTicketVerifier(): DirectSignalingTicketSignatureVerifier =
        DirectSignalingTicketSignatureVerifier(::verify)

    private fun verify(publicKey: ByteArray, message: ByteArray, signature: ByteArray): Boolean {
        val verifier = Ed25519Signer()
        verifier.init(false, Ed25519PublicKeyParameters(publicKey, 0))
        verifier.update(message, 0, message.size)
        return verifier.verifySignature(signature)
    }

    private data class ClientFixture(
        val client: DirectTransportSignalingClient,
        val identity: DeviceIdentity,
    ) : AutoCloseable {
        override fun close() {
            client.close()
            identity.destroy()
        }
    }

    private inner class TicketSigningFixture {
        private val privateKey = Ed25519PrivateKeyParameters(SecureRandom())
        val publicKey: ByteArray = privateKey.generatePublicKey().encoded
        val keyId = "direct-v1-test"

        fun ticket(
            initiator: UUID,
            responder: UUID,
            connection: UUID,
            now: Long,
            lifetimeMillis: Long = 20 * 60 * 1_000L,
        ): ByteArray {
            val claims = DirectPathTicketV1.newBuilder()
                .setFormatVersion(1)
                .setTicketId(ByteString.copyFrom(UUID.randomUUID().bytes()))
                .setAccountId(ByteString.copyFrom(UUID.randomUUID().bytes()))
                .setInitiatorDeviceId(ByteString.copyFrom(initiator.bytes()))
                .setResponderDeviceId(ByteString.copyFrom(responder.bytes()))
                .setConnectionId(ByteString.copyFrom(connection.bytes()))
                .setPathEpoch(1)
                .setIssuedAtMs(now)
                .setExpiresAtMs(now + lifetimeMillis)
                .addAllowedCarriers(DirectCarrierV1.DIRECT_CARRIER_V1_UDP_QUIC)
                .setServerNonce(ByteString.copyFrom(ByteArray(16) { 0x33 }))
                .setServerSigningKeyId(ByteString.copyFromUtf8(keyId))
                .build()
            val message = "TERMLINK-DIRECT-TICKET-V1\n".encodeToByteArray() + claims.toByteArray()
            val signer = Ed25519Signer()
            signer.init(true, privateKey)
            signer.update(message, 0, message.size)
            return claims.toBuilder()
                .setServerSignature(ByteString.copyFrom(signer.generateSignature()))
                .build()
                .toByteArray()
        }

        fun destroy() {
            privateKey.encoded.fill(0)
        }
    }

    private class FixedSecureRandom : SecureRandom() {
        private var next = 1
        override fun nextBytes(bytes: ByteArray) {
            bytes.indices.forEach { index -> bytes[index] = (next + index).toByte() }
            next++
        }
    }

    private fun UUID.bytes(): ByteArray = java.nio.ByteBuffer.allocate(16)
        .putLong(mostSignificantBits)
        .putLong(leastSignificantBits)
        .array()

    private fun ByteArray.base64Url(): String = Base64.getUrlEncoder().withoutPadding().encodeToString(this)
    private fun String.decodeBase64Url(): ByteArray = Base64.getUrlDecoder().decode(this)
}
