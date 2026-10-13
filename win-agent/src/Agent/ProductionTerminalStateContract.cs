using System;
using System.Collections.Generic;

namespace TermLink.Agent
{
    // Bump this identifier when a session may produce new mandatory state.
    // It is decoder support, never permission to change a running session's profile.
    internal static class ProductionTerminalStateContract
    {
        internal const string State = TermLink.LocalIpc.LocalPayloadCodec.TerminalStateContract;
        internal const string Attach = "terminal_attach_v1";

        internal static string Validate(IEnumerable<string> offered, bool relaySupportsAttach, bool relaySupportsGeometry)
            => Validate(offered, relaySupportsAttach, relaySupportsGeometry, State);

        internal static string Validate(IEnumerable<string> offered, bool relaySupportsAttach, bool relaySupportsGeometry,
            string requiredState)
        {
            var unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (var capability in offered ?? Array.Empty<string>())
            {
                if (!ProductionRealtimeCapabilities.ValidCapability(capability) ||
                    !unique.Add(capability) || unique.Count > ProductionRealtimeCapabilities.MaximumCapabilities)
                    return "TERMINAL_CAPABILITIES_INVALID";
            }
            if (!relaySupportsAttach || !relaySupportsGeometry) return "TERMINAL_RELAY_UPGRADE_REQUIRED";
            if (requiredState != State &&
                requiredState != TermLink.LocalIpc.TerminalSemanticProfile.Graphics.StateContract &&
                requiredState != TermLink.LocalIpc.TerminalSemanticProfile.GraphicsUnicode16.StateContract)
                return "TERMINAL_STATE_UNSUPPORTED";
            return unique.Contains(requiredState) ? string.Empty : "TERMINAL_CLIENT_UPGRADE_REQUIRED";
        }
    }
}
