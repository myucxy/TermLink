using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class ProductionDirectInterfaceSelectionValidation
    {
        internal static async Task ValidateAsync()
        {
            var dead = new ProductionDirectInterface(IPAddress.Parse("10.20.1.236"), 24, false);
            var primary = new ProductionDirectInterface(IPAddress.Parse("192.168.9.28"), 24, true);
            var backup = new ProductionDirectInterface(IPAddress.Parse("192.168.50.2"), 24, true);
            var lan = new ProductionDirectInterface(IPAddress.Parse("172.19.32.1"), 20, false);
            var interfaces = new[] { dead, primary, backup, lan };
            var routes = new HashSet<IPAddress> { primary.Address };
            var peer = Guid.NewGuid();
            var selector = new ProductionDirectInterfaceSelection();
            Require(selector.Select(peer, interfaces, Array.Empty<IPAddress>(), routes) == primary,
                "Enumeration order overrode the OS route.");
            selector.Report(peer, primary.Address, false);
            Require(selector.Select(peer, interfaces, Array.Empty<IPAddress>(), routes) == backup,
                "A failed interface was selected again instead of the alternate uplink.");
            Require(selector.Select(Guid.NewGuid(), interfaces, Array.Empty<IPAddress>(), routes) == primary,
                "One peer's failure penalized another peer.");
            selector.Report(peer, primary.Address, true);
            Require(selector.Select(peer, interfaces, new[] { IPAddress.Parse("172.19.40.2") }, routes) == lan,
                "LAN without a gateway or with a /20 mask was omitted.");
            Require(!ProductionDirectInterfaceSelection.SameLan(lan.Address, IPAddress.Parse("172.19.48.2"), 20),
                "An off-subnet host was accepted as LAN.");
            Require(!ProductionDirectInterfaceSelection.SameLan(primary.Address, IPAddress.Parse("8.8.8.8"), 0),
                "An unsafe prefix/public target was accepted.");
            // Real adapter enumeration with no DNS dependency: broken STUN lookup must retain host candidates.
            var platform = new ProductionWindowsDirectShadowPlatform();
            var gathered = await platform.PrepareGatherAsync(new[] { "stun:no-such-stun.invalid:3478" }, CancellationToken.None);
            Require(gathered.AdvertisedLocalAddress != null && gathered.StunServers.Count == 0,
                "STUN DNS failure discarded LAN candidates.");
            Require(gathered.IsNetworkCurrent(), "An unchanged interface invalidated its path.");
        }

        private static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
