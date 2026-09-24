package dev.termlink.network.direct

import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.NetworkInterface
import java.security.SecureRandom

internal data class DirectInterfaceAddress(
    val address: InetAddress,
    val prefixLength: Int,
    val broadcastAddress: InetAddress? = null,
)

internal data class DirectNetworkInterface(
    val name: String,
    val isUp: Boolean,
    val isLoopback: Boolean,
    val isVirtual: Boolean,
    /** Must mean that Android's active Network owns this interface, not merely that it exists. */
    val activeRouteEligible: Boolean,
    val addresses: List<DirectInterfaceAddress>,
)

internal fun interface DirectNetworkInterfaceProvider {
    fun current(): List<DirectNetworkInterface>
}

internal object SystemDirectNetworkInterfaceProvider : DirectNetworkInterfaceProvider {
    override fun current(): List<DirectNetworkInterface> =
        NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().mapNotNull { network ->
            runCatching {
                DirectNetworkInterface(
                    name = network.name.orEmpty(),
                    isUp = network.isUp,
                    isLoopback = network.isLoopback,
                    isVirtual = network.isVirtual,
                    // This fallback is intentionally conservative by interface name. A future app-level
                    // integration should supply ConnectivityManager's active Network interface instead.
                    activeRouteEligible = DirectAddressPolicy.isConservativeLanInterfaceName(network.name.orEmpty()),
                    addresses = network.interfaceAddresses.mapNotNull { binding ->
                        val address = binding.address ?: return@mapNotNull null
                        DirectInterfaceAddress(
                            address,
                            binding.networkPrefixLength.toInt(),
                            binding.broadcast,
                        )
                    },
                )
            }.getOrNull()
        }
}

internal fun interface DirectCandidateIdSource {
    fun next(): ByteArray
}

internal class LanHostCandidateCollector(
    private val interfaceProvider: DirectNetworkInterfaceProvider = SystemDirectNetworkInterfaceProvider,
    private val idSource: DirectCandidateIdSource = DirectCandidateIdSource {
        ByteArray(DirectCandidate.ID_BYTES).also(SecureRandom()::nextBytes)
    },
) {
    fun collect(endpoint: DirectDatagramEndpoint): List<DirectCandidate> {
        require(endpoint.localPort in DirectCandidate.MIN_CANDIDATE_PORT..DirectCandidate.MAX_CANDIDATE_PORT) {
            "host candidates require one bound high UDP port"
        }
        val seenAddresses = HashSet<List<Byte>>()
        val accepted = interfaceProvider.current()
            .asSequence()
            .filter(DirectAddressPolicy::isEligibleInterface)
            .flatMap { network -> network.addresses.asSequence() }
            .filter(DirectAddressPolicy::isLanHostBinding)
            .filter { binding -> DirectAddressPolicy.isUsablePrefix(binding) }
            .filter { binding -> seenAddresses.add(binding.address.address.toList()) }
            .sortedWith(
                compareByDescending<DirectInterfaceAddress> { DirectAddressPolicy.addressPreference(it.address) }
                    .thenBy { it.address.hostAddress },
            )
            .take(MAX_HOST_CANDIDATES)
            .toList()

        return accepted.mapIndexed { index, binding ->
            val id = idSource.next().copyOf()
            require(id.size == DirectCandidate.ID_BYTES) { "candidate id source returned an invalid id" }
            DirectCandidate(
                id = id,
                kind = DirectCandidateKind.HOST,
                address = InetAddress.getByAddress(binding.address.address.copyOf()),
                port = endpoint.localPort,
                priority = HOST_PRIORITY_BASE - index,
                prefixLength = binding.prefixLength,
            )
        }
    }

    internal companion object {
        internal const val MAX_HOST_CANDIDATES = 16
        internal const val HOST_PRIORITY_BASE = 2_130_706_431
    }
}

internal object DirectAddressPolicy {
    private val allowedLanInterface = Regex("^(wlan|wifi|eth|en|ap)[a-zA-Z0-9_.-]*$")
    private val deniedVirtualInterface = Regex("^(tun|tap|wg|ppp|veth|docker|dummy|lo)[a-zA-Z0-9_.-]*$")

    internal fun isConservativeLanInterfaceName(name: String): Boolean =
        allowedLanInterface.matches(name) && !deniedVirtualInterface.matches(name)

    internal fun isEligibleInterface(network: DirectNetworkInterface): Boolean =
        network.isUp && !network.isLoopback && !network.isVirtual && network.activeRouteEligible &&
            isConservativeLanInterfaceName(network.name)

    internal fun isUsablePrefix(binding: DirectInterfaceAddress): Boolean = when (binding.address) {
        is Inet4Address -> binding.prefixLength in 1..32
        is Inet6Address -> binding.prefixLength in 1..128
        else -> false
    }

    /** Only RFC1918 IPv4 and IPv6 ULA are advertised as LAN-first host candidates. */
    internal fun isLanHostAddress(address: InetAddress): Boolean {
        if (address.isAnyLocalAddress || address.isLoopbackAddress || address.isLinkLocalAddress ||
            address.isMulticastAddress
        ) return false
        val bytes = address.address
        return when (address) {
            is Inet4Address -> {
                val first = bytes[0].toInt() and 0xff
                val second = bytes[1].toInt() and 0xff
                first == 10 || (first == 172 && second in 16..31) || (first == 192 && second == 168)
            }

            is Inet6Address -> (bytes[0].toInt() and 0xfe) == 0xfc
            else -> false
        }
    }

    internal fun isLanHostBinding(binding: DirectInterfaceAddress): Boolean =
        isLanHostAddress(binding.address) &&
            binding.broadcastAddress?.address?.contentEquals(binding.address.address) != true

    internal fun isCandidateEndpoint(address: InetAddress, port: Int): Boolean =
        port in DirectCandidate.MIN_CANDIDATE_PORT..DirectCandidate.MAX_CANDIDATE_PORT &&
            !address.isAnyLocalAddress && !address.isLoopbackAddress && !address.isLinkLocalAddress &&
            !address.isMulticastAddress && when (address) {
                is Inet4Address -> (address.address[0].toInt() and 0xff) !in 224..255
                is Inet6Address -> true
                else -> false
            }

    internal fun addressPreference(address: InetAddress): Int = when (address) {
        is Inet4Address -> 2
        is Inet6Address -> 1
        else -> 0
    }

    internal fun sameSubnet(local: DirectCandidate, remote: DirectCandidate): Boolean {
        val prefix = local.prefixLength ?: return false
        if (!isCandidateEndpoint(remote.address, remote.port)) return false
        return sameSubnet(local.address, remote.address, prefix)
    }

    internal fun sameSubnet(local: InetAddress, remote: InetAddress, prefix: Int): Boolean {
        val left = local.address
        val right = remote.address
        if (left.size != right.size || prefix !in 1..(left.size * Byte.SIZE_BITS) ||
            !isCandidateEndpoint(remote, 49_152)
        ) return false
        val completeBytes = prefix / Byte.SIZE_BITS
        val remainingBits = prefix % Byte.SIZE_BITS
        for (index in 0 until completeBytes) {
            if (left[index] != right[index]) return false
        }
        if (remainingBits != 0) {
            val mask = (0xff shl (Byte.SIZE_BITS - remainingBits)) and 0xff
            if ((left[completeBytes].toInt() and mask) != (right[completeBytes].toInt() and mask)) {
                return false
            }
        }
        return remote !is Inet4Address || isIpv4UnicastHost(right, prefix)
    }

    private fun isIpv4UnicastHost(address: ByteArray, prefixLength: Int): Boolean {
        if (prefixLength !in 1..30) return true
        var hostHasZero = false
        var hostHasOne = false
        for (bit in prefixLength until 32) {
            val one = (address[bit / 8].toInt() and (1 shl (7 - bit % 8))) != 0
            hostHasOne = hostHasOne || one
            hostHasZero = hostHasZero || !one
        }
        return hostHasZero && hostHasOne
    }
}
