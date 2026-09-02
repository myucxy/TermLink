package dev.termlink.network.direct

import dev.termlink.protocol.transport.v1.DirectConnectivityDatagramV1
import java.net.InetAddress
import java.net.InetSocketAddress
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertThrows
import org.junit.Assert.assertTrue
import org.junit.Test

class DirectProbeTest {
    private val local = testCandidate(1, DirectCandidateKind.HOST, "192.168.1.2")
    private val remote = testCandidate(2, DirectCandidateKind.HOST, "192.168.1.20")
    private val localTemplate = testProbeTemplate(10, local, remote)
    private val remoteTemplate = testProbeTemplate(11, local, remote)
    private val localKey = ByteArray(32) { 21 }
    private val remoteKey = ByteArray(32) { 22 }
    private val now = 1_900_000_000_000L

    @Test
    fun codecAuthenticatesCanonicalProbeAndAcknowledgement() {
        val localCodec = DirectProbeCodec(localKey)
        val remoteCodec = DirectProbeCodec(remoteKey)
        try {
            val probeNonce = ByteArray(16) { 31 }
            val responseNonce = ByteArray(16) { 32 }
            val probe = localCodec.encodeProbe(localTemplate, probeNonce, now)
            val acknowledgement = remoteCodec.encodeAcknowledgement(
                remoteTemplate,
                probeNonce,
                responseNonce,
                now + 1,
            )
            val probeProto = parseProbe(probe)
            val acknowledgementProto = parseProbe(acknowledgement)

            val decodedProbe = localCodec.decode(
                probe,
                DirectProbeExpectation(localTemplate, DirectProbeKind.PROBE),
                now,
            )
            val decodedAck = remoteCodec.decode(
                acknowledgement,
                DirectProbeExpectation(remoteTemplate, DirectProbeKind.PROBE_ACK, probeNonce),
                now + 1,
            )

            assertEquals(DirectProbeKind.PROBE, decodedProbe.kind)
            assertEquals(null, decodedProbe.responseNonce)
            assertArrayEquals(responseNonce, decodedAck.responseNonce)
            assertEquals(
                DirectProbeCodec.ANTI_AMPLIFICATION_PADDING_BYTES,
                probeProto.antiAmplificationPadding.size(),
            )
            assertTrue(acknowledgementProto.antiAmplificationPadding.isEmpty)
            assertTrue(acknowledgement.size <= probe.size)
            assertTrue(probe.size <= DirectProbeCodec.MAX_PACKET_BYTES)
            assertTrue(acknowledgement.size <= DirectProbeCodec.MAX_PACKET_BYTES)
        } finally {
            localCodec.close()
            remoteCodec.close()
        }
    }

    @Test
    fun codecRejectsTamperingUnknownFieldsWrongKeyAndReplay() {
        val codec = DirectProbeCodec(localKey)
        val wrongCodec = DirectProbeCodec(ByteArray(32) { 99.toByte() })
        try {
            val nonce = ByteArray(16) { 5 }
            val valid = codec.encodeProbe(localTemplate, nonce, now)
            val tampered = valid.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() }
            val withUnknownField = valid + byteArrayOf(0xa0.toByte(), 0x06, 0x01)

            assertRejected { codec.decode(tampered, probeExpectation(), now) }
            assertRejected { codec.decode(withUnknownField, probeExpectation(), now) }
            assertRejected { wrongCodec.decode(valid, probeExpectation(), now) }
            assertRejected { codec.decode(valid, probeExpectation(), now + 30_001) }
            assertRejected {
                codec.decode(
                    valid,
                    probeExpectation().copy(template = localTemplate.copy(senderDeviceId = ByteArray(16) { 44 })),
                    now,
                )
            }
        } finally {
            codec.close()
            wrongCodec.close()
        }
    }

    @Test
    fun schedulerAlwaysSendsLanPairBeforeServerReflexivePair() {
        val endpoint = FakeDirectDatagramEndpoint()
        val srflxLocal = testCandidate(3, DirectCandidateKind.SERVER_REFLEXIVE, "1.1.1.1")
        val srflxRemote = testCandidate(4, DirectCandidateKind.SERVER_REFLEXIVE, "9.9.9.9")
        val codecs = List(4) { DirectProbeCodec(ByteArray(32) { (it + 1).toByte() }) }
        try {
            val srflxAttempt = attempt(srflxLocal, srflxRemote, codecs[0], codecs[1])
            val lanAttempt = attempt(local, remote, codecs[2], codecs[3])

            val result = DirectProbeScheduler(
                nonceSource = DirectProbeNonceSource { ByteArray(16) { 9 } },
                clockMillis = { now },
            ).probe(endpoint, listOf(srflxAttempt, lanAttempt), totalBudgetMillis = 100)

            assertEquals(null, result)
            assertTrue(endpoint.sent.isNotEmpty())
            assertEquals(remote.address, endpoint.sent.first().second.address)
            val firstNonLan = endpoint.sent.indexOfFirst { it.second.address == srflxRemote.address }
            assertTrue(firstNonLan >= DirectProbeScheduler.MAX_SENDS_PER_PAIR)
        } finally {
            codecs.forEach(DirectProbeCodec::close)
        }
    }

    @Test
    fun schedulerAcceptsOnlyAuthenticatedAckFromExactEndpointAndReportsSamePort() {
        val endpoint = FakeDirectDatagramEndpoint(localPort = 49_999)
        val outboundCodec = DirectProbeCodec(localKey)
        val inboundCodec = DirectProbeCodec(remoteKey)
        try {
            endpoint.onSend = { packet, target ->
                val proto = parseProbe(packet)
                val acknowledgement = inboundCodec.encodeAcknowledgement(
                    remoteTemplate,
                    proto.probeNonce.toByteArray(),
                    ByteArray(16) { 88.toByte() },
                    now,
                )
                endpoint.incoming += DirectReceivedDatagram(acknowledgement, target)
            }
            val success = DirectProbeScheduler(
                nonceSource = DirectProbeNonceSource { ByteArray(16) { 77 } },
                clockMillis = { now },
            ).probe(
                endpoint,
                listOf(
                    DirectProbeAttempt(
                        DirectProbePair(local, remote),
                        localTemplate,
                        remoteTemplate,
                        outboundCodec,
                        inboundCodec,
                    ),
                ),
                totalBudgetMillis = 500,
            )

            assertNotNull(success)
            assertEquals(endpoint.localPort, success?.localPort)
            assertArrayEquals(ByteArray(16) { 88.toByte() }, success?.responseNonce)
        } finally {
            outboundCodec.close()
            inboundCodec.close()
        }
    }

    @Test
    fun schedulerRejectsAcknowledgementLargerThanItsProbe() {
        val endpoint = FakeDirectDatagramEndpoint()
        val outboundCodec = DirectProbeCodec(localKey)
        val inboundCodec = DirectProbeCodec(remoteKey)
        try {
            endpoint.onSend = { packet, target ->
                val proto = parseProbe(packet)
                val acknowledgement = inboundCodec.encodeAcknowledgement(
                    remoteTemplate,
                    proto.probeNonce.toByteArray(),
                    ByteArray(16) { 33 },
                    now,
                ) + byteArrayOf(0xa0.toByte(), 0x06, 0x01)
                assertTrue(acknowledgement.size > packet.size)
                endpoint.incoming += DirectReceivedDatagram(acknowledgement, target)
            }

            val success = DirectProbeScheduler(
                nonceSource = DirectProbeNonceSource { ByteArray(16) { 44 } },
                clockMillis = { now },
            ).probe(
                endpoint,
                listOf(
                    DirectProbeAttempt(
                        DirectProbePair(local, remote),
                        localTemplate,
                        remoteTemplate,
                        outboundCodec,
                        inboundCodec,
                    ),
                ),
                totalBudgetMillis = 100,
            )

            assertEquals(null, success)
        } finally {
            outboundCodec.close()
            inboundCodec.close()
        }
    }

    @Test
    fun responderNeverAcknowledgesWrongSourceOrUnauthenticatedProbe() {
        val endpoint = FakeDirectDatagramEndpoint()
        val inboundCodec = DirectProbeCodec(localKey)
        val outboundCodec = DirectProbeCodec(remoteKey)
        val scheduler = DirectProbeScheduler(
            nonceSource = DirectProbeNonceSource { ByteArray(16) { 8 } },
            clockMillis = { now },
        )
        try {
            val expectedSource = InetSocketAddress(remote.address, remote.port)
            val probe = inboundCodec.encodeProbe(localTemplate, ByteArray(16) { 7 }, now)
            val wrongSource = DirectReceivedDatagram(
                probe,
                InetSocketAddress(InetAddress.getByName("192.168.1.21"), remote.port),
            )
            val tampered = DirectReceivedDatagram(
                probe.copyOf().also { it[it.lastIndex] = (it.last().toInt() xor 1).toByte() },
                expectedSource,
            )

            assertFalse(
                scheduler.acknowledge(
                    endpoint,
                    wrongSource,
                    expectedSource,
                    inboundCodec,
                    probeExpectation(),
                    outboundCodec,
                    remoteTemplate,
                ),
            )
            assertFalse(
                scheduler.acknowledge(
                    endpoint,
                    tampered,
                    expectedSource,
                    inboundCodec,
                    probeExpectation(),
                    outboundCodec,
                    remoteTemplate,
                ),
            )
            assertTrue(endpoint.sent.isEmpty())
        } finally {
            inboundCodec.close()
            outboundCodec.close()
        }
    }

    @Test
    fun schedulerRejectsUnboundedPairSet() {
        val codec = DirectProbeCodec(localKey)
        try {
            val one = attempt(local, remote, codec, codec)
            assertThrows(IllegalArgumentException::class.java) {
                DirectProbeScheduler().probe(
                    FakeDirectDatagramEndpoint(),
                    List(DirectProbeScheduler.MAX_CANDIDATE_PAIRS + 1) { one },
                )
            }
        } finally {
            codec.close()
        }
    }

    private fun attempt(
        localCandidate: DirectCandidate,
        remoteCandidate: DirectCandidate,
        outboundCodec: DirectProbeCodec,
        inboundCodec: DirectProbeCodec,
    ): DirectProbeAttempt = DirectProbeAttempt(
        pair = DirectProbePair(localCandidate, remoteCandidate),
        outboundTemplate = testProbeTemplate(10, localCandidate, remoteCandidate),
        expectedAcknowledgementTemplate = testProbeTemplate(11, localCandidate, remoteCandidate),
        outboundCodec = outboundCodec,
        inboundCodec = inboundCodec,
    )

    private fun probeExpectation(): DirectProbeExpectation =
        DirectProbeExpectation(localTemplate, DirectProbeKind.PROBE)

    private fun assertRejected(action: () -> Unit) {
        assertThrows(DirectProbeValidationException::class.java, action)
    }

    private fun parseProbe(packet: ByteArray): DirectConnectivityDatagramV1 =
        DirectConnectivityDatagramV1.parseFrom(
            packet.copyOfRange(DirectProbeCodec.WIRE_PREFIX.size, packet.size),
        )
}
