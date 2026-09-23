using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TermLink.Agent
{
    /// <summary>
    /// Owns the bounded Relay Hello/Welcome capability contract. Direct data is
    /// optional and is never added to the capabilities required to keep Relay
    /// connected.
    /// </summary>
    internal static class ProductionRealtimeCapabilities
    {
        internal const int MaximumWelcomeBytes = 16 * 1024;
        internal const int MaximumCapabilities = 64;
        internal const int MaximumCapabilityBytes = 64;
        internal const string DirectTransportDataV1 = "direct_transport_data_v1";
        internal const string DirectSessionCutoverV1 = "direct_session_cutover_v1";

        private static readonly string[] Required =
        {
            "cumulative_ack",
            "e2ee_x25519_chacha20poly1305",
            "flow_control_v1",
            "lease_v1"
        };

        internal static IReadOnlyList<string> BuildAdvertisedCapabilities(
            bool terminalSnapshotEnabled,
            bool typedInputEnabled,
            bool directTransportDataEnabled)
        {
            var capabilities = new List<string>
            {
                "cumulative_ack",
                "e2ee_x25519_chacha20poly1305",
                "flow_control_v1",
                "lease_v1",
                "bounded_event_replay_v1",
                "terminal_presentation_metadata_v1",
                "remote_agent_update_v1",
                "host_history_v1",
                "host_session_catalog_v1"
            };
            if (terminalSnapshotEnabled) capabilities.Add("terminal_snapshot_v2");
            if (typedInputEnabled) capabilities.Add("terminal_typed_input_v1");
            if (directTransportDataEnabled)
            {
                capabilities.Add(DirectTransportDataV1);
                capabilities.Add(DirectSessionCutoverV1);
            }
            return capabilities.AsReadOnly();
        }

        internal static ProductionRealtimeWelcome ParseWelcome(
            ReadOnlyMemory<byte> payload,
            IReadOnlyCollection<string> advertisedCapabilities)
        {
            if (payload.Length < 1 || payload.Length > MaximumWelcomeBytes)
                throw new InvalidDataException("Production Relay welcome exceeds its size limit.");
            if (advertisedCapabilities == null)
                throw new ArgumentNullException(nameof(advertisedCapabilities));

            var advertised = new HashSet<string>(advertisedCapabilities, StringComparer.Ordinal);
            if (advertised.Count != advertisedCapabilities.Count ||
                Required.Any(capability => !advertised.Contains(capability)))
            {
                throw new ArgumentException(
                    "Advertised Relay capabilities are invalid.",
                    nameof(advertisedCapabilities));
            }

            try
            {
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw InvalidWelcome();

                var typeSeen = false;
                var versionSeen = false;
                var capabilitiesSeen = false;
                var negotiated = new List<string>();
                var unique = new HashSet<string>(StringComparer.Ordinal);

                foreach (var property in root.EnumerateObject())
                {
                    switch (property.Name)
                    {
                        case "type":
                            if (typeSeen || property.Value.ValueKind != JsonValueKind.String ||
                                !string.Equals(property.Value.GetString(), "welcome", StringComparison.Ordinal))
                            {
                                throw InvalidWelcome();
                            }
                            typeSeen = true;
                            break;
                        case "protocol_version":
                            if (versionSeen || property.Value.ValueKind != JsonValueKind.Number ||
                                !property.Value.TryGetUInt32(out var version) || version != 1)
                            {
                                throw InvalidWelcome();
                            }
                            versionSeen = true;
                            break;
                        case "capabilities":
                            if (capabilitiesSeen || property.Value.ValueKind != JsonValueKind.Array)
                                throw InvalidWelcome();
                            capabilitiesSeen = true;
                            foreach (var item in property.Value.EnumerateArray())
                            {
                                if (negotiated.Count >= MaximumCapabilities ||
                                    item.ValueKind != JsonValueKind.String)
                                {
                                    throw InvalidWelcome();
                                }
                                var capability = item.GetString();
                                if (!ValidCapability(capability) || !unique.Add(capability) ||
                                    !advertised.Contains(capability))
                                {
                                    throw InvalidWelcome();
                                }
                                negotiated.Add(capability);
                            }
                            break;
                    }
                }

                if (!typeSeen || !versionSeen || !capabilitiesSeen ||
                    Required.Any(capability => !unique.Contains(capability)))
                {
                    throw InvalidWelcome();
                }
                return new ProductionRealtimeWelcome(negotiated);
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Production Relay welcome is invalid.", error);
            }
        }

        private static bool ValidCapability(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > MaximumCapabilityBytes)
                return false;
            foreach (var character in value)
            {
                if ((character < 'a' || character > 'z') &&
                    (character < '0' || character > '9') && character != '_')
                {
                    return false;
                }
            }
            return true;
        }

        private static InvalidDataException InvalidWelcome() =>
            new InvalidDataException("Production Relay welcome contract is invalid.");
    }

    internal sealed class ProductionRealtimeWelcome
    {
        internal ProductionRealtimeWelcome(IEnumerable<string> capabilities)
        {
            Capabilities = Array.AsReadOnly((capabilities ??
                throw new ArgumentNullException(nameof(capabilities))).ToArray());
        }

        public IReadOnlyList<string> Capabilities { get; }
        public bool DirectTransportDataV1 => Capabilities.Contains(
            ProductionRealtimeCapabilities.DirectTransportDataV1,
            StringComparer.Ordinal);
        public bool DirectSessionCutoverV1 => Capabilities.Contains(
            ProductionRealtimeCapabilities.DirectSessionCutoverV1,
            StringComparer.Ordinal);
        public bool DirectDataPlaneV1 =>
            DirectTransportDataV1 && DirectSessionCutoverV1;
    }

    /// <summary>
    /// A generation-fenced, read-only view of capabilities negotiated on the
    /// current physical WSS connection. Clearing a socket generation removes
    /// every negotiated capability without resetting the stale-generation
    /// fence.
    /// </summary>
    internal sealed class ProductionRealtimeCapabilityState
    {
        private readonly object sync = new object();
        private long highestGeneration;
        private long activeGeneration;
        private string[] capabilities = Array.Empty<string>();

        public bool TryMarkConnected(long generation, ProductionRealtimeWelcome welcome)
        {
            if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
            if (welcome == null) throw new ArgumentNullException(nameof(welcome));
            lock (sync)
            {
                if (generation <= highestGeneration) return false;
                highestGeneration = generation;
                activeGeneration = generation;
                capabilities = welcome.Capabilities.ToArray();
                return true;
            }
        }

        public void Clear()
        {
            lock (sync)
            {
                activeGeneration = 0;
                capabilities = Array.Empty<string>();
            }
        }

        public ProductionRealtimeCapabilitySnapshot Snapshot()
        {
            lock (sync)
            {
                return new ProductionRealtimeCapabilitySnapshot(
                    activeGeneration,
                    capabilities);
            }
        }
    }

    internal sealed class ProductionRealtimeCapabilitySnapshot
    {
        internal ProductionRealtimeCapabilitySnapshot(long physicalGeneration, IEnumerable<string> capabilities)
        {
            PhysicalGeneration = physicalGeneration;
            Capabilities = Array.AsReadOnly((capabilities ??
                throw new ArgumentNullException(nameof(capabilities))).ToArray());
        }

        public bool Connected => PhysicalGeneration > 0;
        public long PhysicalGeneration { get; }
        public IReadOnlyList<string> Capabilities { get; }
        public bool DirectTransportDataV1 => Capabilities.Contains(
            ProductionRealtimeCapabilities.DirectTransportDataV1,
            StringComparer.Ordinal);
        public bool DirectSessionCutoverV1 => Capabilities.Contains(
            ProductionRealtimeCapabilities.DirectSessionCutoverV1,
            StringComparer.Ordinal);
        public bool DirectDataPlaneV1 =>
            DirectTransportDataV1 && DirectSessionCutoverV1;
    }
}
