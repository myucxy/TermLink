package dev.termlink.network.direct

import java.net.InetAddress
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class LanHostCandidateCollectorTest {
    @Test
    fun strictlyFiltersInactiveVirtualAndNonLanAddressesAndUsesOnePort() {
        val endpoint = FakeDirectDatagramEndpoint(localPort = 48_765)
        var nextId = 0
        val collector = LanHostCandidateCollector(
            interfaceProvider = DirectNetworkInterfaceProvider {
                listOf(
                    network(
                        name = "wlan0",
                        addresses = listOf(
                            "192.168.20.7" to 24,
                            "10.4.5.6" to 8,
                            "fd12:3456::7" to 64,
                            "127.0.0.1" to 8,
                            "169.254.1.2" to 16,
                            "8.8.8.8" to 24,
                            "224.0.0.1" to 4,
                        ),
                    ),
                    network("tun0", listOf("10.9.0.2" to 24), isVirtual = true),
                    network("wlan1", listOf("192.168.30.4" to 24), active = false),
                    network("rmnet0", listOf("10.20.30.40" to 24)),
                )
            },
            idSource = DirectCandidateIdSource { ByteArray(16) { (++nextId).toByte() } },
        )

        val candidates = collector.collect(endpoint)

        assertEquals(setOf("10.4.5.6", "192.168.20.7", "fd12:3456:0:0:0:0:0:7"), candidates.map { it.address.hostAddress }.toSet())
        assertTrue(candidates.all { it.kind == DirectCandidateKind.HOST })
        assertTrue(candidates.all { it.port == endpoint.localPort })
        assertTrue(candidates.all { it.relatedCandidateId == null })
        assertEquals(candidates.map { it.address.address.toList() }.size, candidates.map { it.address.address.toList() }.distinct().size)
    }

    @Test
    fun hostCandidatesAreBoundedAndDuplicateAddressesAreRemoved() {
        val addresses = (1..24).flatMap { index ->
            listOf("192.168.1.$index" to 24, "192.168.1.$index" to 24)
        }
        val collector = LanHostCandidateCollector(
            interfaceProvider = DirectNetworkInterfaceProvider { listOf(network("wlan0", addresses)) },
            idSource = DirectCandidateIdSource { ByteArray(16) { 4 } },
        )

        val candidates = collector.collect(FakeDirectDatagramEndpoint())

        assertEquals(LanHostCandidateCollector.MAX_HOST_CANDIDATES, candidates.size)
        assertEquals(candidates.size, candidates.map { it.address }.distinct().size)
    }

    @Test
    fun lanPairRejectsIpv4NetworkAndBroadcastEndpoints() {
        val local = testCandidate(1, DirectCandidateKind.HOST, "192.168.7.20", prefixLength = 24)

        assertFalse(
            DirectAddressPolicy.sameSubnet(
                local,
                testCandidate(2, DirectCandidateKind.HOST, "192.168.7.0", prefixLength = 24),
            ),
        )
        assertFalse(
            DirectAddressPolicy.sameSubnet(
                local,
                testCandidate(3, DirectCandidateKind.HOST, "192.168.7.255", prefixLength = 24),
            ),
        )
        assertTrue(
            DirectAddressPolicy.sameSubnet(
                local,
                testCandidate(4, DirectCandidateKind.HOST, "192.168.7.30", prefixLength = 24),
            ),
        )
    }

    private fun network(
        name: String,
        addresses: List<Pair<String, Int>>,
        isVirtual: Boolean = false,
        active: Boolean = true,
    ): DirectNetworkInterface = DirectNetworkInterface(
        name = name,
        isUp = true,
        isLoopback = false,
        isVirtual = isVirtual,
        activeRouteEligible = active,
        addresses = addresses.map { (address, prefix) ->
            DirectInterfaceAddress(InetAddress.getByName(address), prefix)
        },
    )
}
