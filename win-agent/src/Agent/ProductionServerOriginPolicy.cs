using System;
using System.Net;
using System.Net.Sockets;

namespace TermLink.Agent
{
    /// <summary>
    /// Origin policy for the TermLink server connection. HTTPS is the default
    /// contract; plain HTTP is additionally accepted for loopback or
    /// private-network origins so a local development server (Docker on the
    /// LAN) can be exercised end to end without a TLS edge. Production
    /// deployments keep their HTTPS origins and are unaffected.
    /// </summary>
    internal static class ProductionServerOriginPolicy
    {
        public static bool IsAllowedUrl(string url) =>
            Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsAllowedOrigin(uri);

        public static bool IsAllowedOrigin(Uri uri) =>
            uri != null && (
                uri.Scheme == Uri.UriSchemeHttps ||
                (uri.Scheme == Uri.UriSchemeHttp && IsPrivateOrLoopbackHost(uri.Host)));

        public static bool IsPrivateOrLoopbackHost(string hostname)
        {
            if (string.IsNullOrWhiteSpace(hostname)) return false;
            var host = hostname.Trim().TrimStart('[').TrimEnd(']').ToLowerInvariant();
            if (host == "localhost" || host.EndsWith(".localhost")) return true;
            if (!IPAddress.TryParse(host, out var ip)) return false;
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = ip.GetAddressBytes();
                return bytes[0] == 10 ||
                    (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) ||
                    (bytes[0] == 192 && bytes[1] == 168);
            }
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = ip.GetAddressBytes();
                return bytes[0] == 0xfc || bytes[0] == 0xfd; // ULA fc00::/7
            }
            return false;
        }
    }
}
