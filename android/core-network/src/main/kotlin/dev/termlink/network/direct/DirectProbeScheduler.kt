package dev.termlink.network.direct

import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.concurrent.TimeUnit
import kotlin.math.min

internal data class DirectProbePair(
    val localCandidate: DirectCandidate,
    val remoteCandidate: DirectCandidate,
) {
    init {
        require(localCandidate.port in DirectCandidate.MIN_CANDIDATE_PORT..DirectCandidate.MAX_CANDIDATE_PORT)
        require(DirectAddressPolicy.isCandidateEndpoint(remoteCandidate.address, remoteCandidate.port)) {
            "remote probe candidate is not a safe endpoint"
        }
    }

    val target: InetSocketAddress
        get() = InetSocketAddress(remoteCandidate.address, remoteCandidate.port)

    val lanPreferred: Boolean
        get() = localCandidate.kind == DirectCandidateKind.HOST &&
            remoteCandidate.kind == DirectCandidateKind.HOST &&
            DirectAddressPolicy.sameSubnet(localCandidate, remoteCandidate)
}

internal data class DirectProbeAttempt(
    val pair: DirectProbePair,
    val outboundTemplate: DirectProbeTemplate,
    val expectedAcknowledgementTemplate: DirectProbeTemplate,
    val outboundCodec: DirectProbeCodec,
    val inboundCodec: DirectProbeCodec,
) {
    init {
        require(outboundTemplate.pathEpoch == expectedAcknowledgementTemplate.pathEpoch) {
            "probe direction templates must use the same epoch"
        }
        require(
            outboundTemplate.connectionId.contentEquals(expectedAcknowledgementTemplate.connectionId),
        ) { "probe direction templates must use the same connection" }
        require(outboundTemplate.initiatorCandidateId.contentEquals(pair.localCandidate.id) ||
            outboundTemplate.responderCandidateId.contentEquals(pair.localCandidate.id)
        ) { "outbound probe template does not reference the local candidate" }
        require(expectedAcknowledgementTemplate.initiatorCandidateId.contentEquals(pair.localCandidate.id) ||
            expectedAcknowledgementTemplate.responderCandidateId.contentEquals(pair.localCandidate.id)
        ) { "acknowledgement template does not reference the local candidate" }
    }
}

internal data class DirectProbeSuccess(
    val pair: DirectProbePair,
    val responseNonce: ByteArray,
    val localPort: Int,
)

internal fun interface DirectProbeNonceSource {
    fun next(): ByteArray
}

/**
 * A deliberately small connectivity-check scheduler. It proves authenticated bidirectional UDP
 * reachability only; success is not QUIC readiness and must not activate a direct data path.
 */
internal class DirectProbeScheduler(
    private val nonceSource: DirectProbeNonceSource = DirectProbeNonceSource {
        ByteArray(DirectProbeCodec.NONCE_BYTES).also(SecureRandom()::nextBytes)
    },
    private val clockMillis: () -> Long = System::currentTimeMillis,
    private val monotonicNanos: () -> Long = System::nanoTime,
) {
    fun probe(
        endpoint: DirectDatagramEndpoint,
        attempts: List<DirectProbeAttempt>,
        totalBudgetMillis: Int = DEFAULT_TOTAL_BUDGET_MILLIS,
    ): DirectProbeSuccess? {
        require(attempts.size <= MAX_CANDIDATE_PAIRS) { "too many direct candidate pairs" }
        require(totalBudgetMillis in 1..MAX_TOTAL_BUDGET_MILLIS) { "probe budget is out of range" }
        if (attempts.isEmpty()) return null
        val ordered = attempts.distinctBy { attempt ->
            attempt.pair.localCandidate.id.toList() to attempt.pair.remoteCandidate.id.toList()
        }.sortedWith(
            compareByDescending<DirectProbeAttempt> { it.pair.lanPreferred }
                .thenByDescending { it.pair.localCandidate.kind == DirectCandidateKind.HOST }
                .thenByDescending {
                    it.pair.localCandidate.priority.toLong() + it.pair.remoteCandidate.priority.toLong()
                },
        )
        val startedAt = monotonicNanos()
        val deadline = startedAt + TimeUnit.MILLISECONDS.toNanos(totalBudgetMillis.toLong())
        val lanDeadline = min(deadline, startedAt + TimeUnit.MILLISECONDS.toNanos(LAN_FIRST_WINDOW_MILLIS))

        ordered.forEach { attempt ->
            val pairDeadline = if (attempt.pair.lanPreferred) lanDeadline else deadline
            repeat(MAX_SENDS_PER_PAIR) {
                if (monotonicNanos() >= pairDeadline) return@repeat
                val probeNonce = nonceSource.next().copyOf()
                require(probeNonce.size == DirectProbeCodec.NONCE_BYTES) {
                    "probe nonce source returned an invalid nonce"
                }
                val packet = attempt.outboundCodec.encodeProbe(
                    attempt.outboundTemplate,
                    probeNonce,
                    clockMillis(),
                )
                endpoint.send(packet, attempt.pair.target)

                var receivedThisSend = 0
                val receiveDeadline = min(
                    pairDeadline,
                    monotonicNanos() + TimeUnit.MILLISECONDS.toNanos(RECEIVE_SLICE_MILLIS.toLong()),
                )
                while (monotonicNanos() < receiveDeadline && receivedThisSend++ < MAX_PACKETS_PER_SEND) {
                    val timeoutMillis = TimeUnit.NANOSECONDS.toMillis(receiveDeadline - monotonicNanos())
                        .coerceIn(1L, RECEIVE_SLICE_MILLIS.toLong())
                        .toInt()
                    val datagram = try {
                        endpoint.receive(DirectProbeCodec.MAX_PACKET_BYTES, timeoutMillis)
                    } catch (_: SocketTimeoutException) {
                        break
                    }
                    if (!sameEndpoint(datagram.source, attempt.pair.target)) continue
                    if (datagram.payload.size > packet.size) continue
                    val decoded = try {
                        attempt.inboundCodec.decode(
                            datagram.payload,
                            DirectProbeExpectation(
                                template = attempt.expectedAcknowledgementTemplate,
                                kind = DirectProbeKind.PROBE_ACK,
                                expectedProbeNonce = probeNonce,
                            ),
                            clockMillis(),
                        )
                    } catch (_: DirectProbeValidationException) {
                        continue
                    }
                    return DirectProbeSuccess(
                        pair = attempt.pair,
                        responseNonce = checkNotNull(decoded.responseNonce),
                        localPort = endpoint.localPort,
                    )
                }
            }
        }
        return null
    }

    /** Validates one peer PROBE and sends one bounded PROBE_ACK to that exact source. */
    fun acknowledge(
        endpoint: DirectDatagramEndpoint,
        datagram: DirectReceivedDatagram,
        expectedSource: InetSocketAddress,
        inboundCodec: DirectProbeCodec,
        inboundExpectation: DirectProbeExpectation,
        outboundCodec: DirectProbeCodec,
        outboundTemplate: DirectProbeTemplate,
    ): Boolean {
        if (!sameEndpoint(datagram.source, expectedSource) || inboundExpectation.kind != DirectProbeKind.PROBE) {
            return false
        }
        val probe = try {
            inboundCodec.decode(datagram.payload, inboundExpectation, clockMillis())
        } catch (_: DirectProbeValidationException) {
            return false
        }
        val responseNonce = nonceSource.next().copyOf()
        require(responseNonce.size == DirectProbeCodec.NONCE_BYTES) {
            "probe nonce source returned an invalid nonce"
        }
        val acknowledgement = outboundCodec.encodeAcknowledgement(
            outboundTemplate,
            probe.probeNonce,
            responseNonce,
            clockMillis(),
        )
        if (acknowledgement.size > datagram.payload.size) return false
        endpoint.send(acknowledgement, expectedSource)
        return true
    }

    private fun sameEndpoint(left: InetSocketAddress, right: InetSocketAddress): Boolean =
        left.port == right.port && !left.isUnresolved && !right.isUnresolved &&
            MessageDigest.isEqual(left.address.address, right.address.address)

    internal companion object {
        internal const val LAN_FIRST_WINDOW_MILLIS = 700L
        internal const val RECEIVE_SLICE_MILLIS = 150
        internal const val MAX_SENDS_PER_PAIR = 4
        internal const val MAX_PACKETS_PER_SEND = 8
        internal const val MAX_CANDIDATE_PAIRS = 32
        internal const val DEFAULT_TOTAL_BUDGET_MILLIS = 10_000
        internal const val MAX_TOTAL_BUDGET_MILLIS = 10_000
    }
}
