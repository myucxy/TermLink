using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace TermLink.Agent
{
    internal sealed record ProductionDirectInterface(IPAddress Address, int PrefixLength, bool HasGateway);

    // Each attempt retains one exact native socket through QUIC promotion. Alternate
    // interfaces are tried on subsequent authenticated offers, never substituted underneath it.
    internal sealed class ProductionDirectInterfaceSelection
    {
        private readonly Dictionary<(Guid Peer, IPAddress Address), DateTime> failures = new();
        private readonly object sync = new();

        internal static IReadOnlyList<ProductionDirectInterface> Capture()
        {
            var result = new List<ProductionDirectInterface>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(value =>
                value.OperationalStatus == OperationalStatus.Up &&
                value.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel))
            {
                try
                {
                    var properties = nic.GetIPProperties();
                    foreach (var item in properties.UnicastAddresses.Where(value =>
                        ProductionDirectCandidateCollector.IsAllowedHostAddress(value.Address)))
                        result.Add(new(item.Address, item.PrefixLength, properties.GatewayAddresses.Any(value =>
                            value.Address.AddressFamily == item.Address.AddressFamily &&
                            !value.Address.Equals(IPAddress.Any) && !value.Address.Equals(IPAddress.IPv6Any))));
                }
                catch (NetworkInformationException) { /* Interface disappeared during enumeration. */ }
            }
            return result.DistinctBy(value => value.Address).ToArray();
        }

        internal static IPAddress RouteSource(IPAddress target, int port)
        {
            try
            {
                using var socket = new Socket(target.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                // UDP Connect asks the OS for the route; it sends no packet to the target.
                socket.Connect(new IPEndPoint(target, port));
                return ((IPEndPoint)socket.LocalEndPoint).Address;
            }
            catch (SocketException) { return null; }
        }

        internal ProductionDirectInterface Select(Guid peer, IReadOnlyList<ProductionDirectInterface> interfaces,
            IReadOnlyList<IPAddress> peerHosts, ISet<IPAddress> routeSources)
        {
            lock (sync)
            {
                var now = DateTime.UtcNow;
                foreach (var key in failures.Where(value => now - value.Value > TimeSpan.FromMinutes(2) ||
                    !interfaces.Any(nic => nic.Address.Equals(value.Key.Address))).Select(value => value.Key).ToArray())
                    failures.Remove(key);
                var eligible = interfaces.Where(value => value.HasGateway || routeSources.Contains(value.Address) ||
                    peerHosts.Any(remote => SameLan(value.Address, remote, value.PrefixLength))).ToArray();
                // No default route/STUN DNS can still leave a usable LAN-only interface.
                return (eligible.Length == 0 ? interfaces : eligible)
                    .OrderBy(value => failures.ContainsKey((peer, value.Address)) ? 1 : 0)
                    .ThenBy(value => failures.GetValueOrDefault((peer, value.Address)))
                    .ThenBy(value => peerHosts.Any(remote => SameLan(value.Address, remote, value.PrefixLength)) ? 0 :
                        routeSources.Contains(value.Address) ? 1 : value.HasGateway ? 2 : 3)
                    .ThenBy(value => value.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ThenBy(value => value.Address.ToString(), StringComparer.Ordinal)
                    .FirstOrDefault() ?? throw new InvalidOperationException("DIRECT_NO_ACTIVE_INTERFACE");
            }
        }

        internal void Report(Guid peer, IPAddress address, bool succeeded)
        {
            if (address == null) return;
            lock (sync)
            {
                if (succeeded) failures.Remove((peer, address));
                else
                {
                    if (failures.Count >= 256)
                        failures.Remove(failures.MinBy(value => value.Value).Key);
                    failures[(peer, address)] = DateTime.UtcNow;
                }
            }
        }

        internal static bool SameLan(IPAddress local, IPAddress remote, int prefixLength)
        {
            if (local == null || remote == null || local.AddressFamily != remote.AddressFamily) return false;
            var left = local.GetAddressBytes(); var right = remote.GetAddressBytes();
            if (!IsPrivate(left) || !IsPrivate(right) || prefixLength < 1 || prefixLength > left.Length * 8) return false;
            if (right.Length == 4 && prefixLength <= 30)
            {
                var hostBits = Enumerable.Range(prefixLength, 32 - prefixLength)
                    .Select(bit => (right[bit / 8] & (1 << (7 - bit % 8))) != 0).ToArray();
                if (hostBits.All(value => value) || hostBits.All(value => !value)) return false;
            }
            var whole = prefixLength / 8; var bits = prefixLength % 8;
            return left.AsSpan(0, whole).SequenceEqual(right.AsSpan(0, whole)) &&
                (bits == 0 || (left[whole] >> (8 - bits)) == (right[whole] >> (8 - bits)));
        }

        private static bool IsPrivate(byte[] value) => value.Length == 4
            ? value[0] == 10 || (value[0] == 172 && value[1] is >= 16 and <= 31) ||
                (value[0] == 192 && value[1] == 168)
            : value.Length == 16 && (value[0] & 0xfe) == 0xfc;
    }
}
