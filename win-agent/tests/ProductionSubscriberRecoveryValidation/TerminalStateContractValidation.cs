using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.ProductionSubscriberRecovery.Validation;

internal static class TerminalStateContractValidation
{
    public static void Run(Type runtimeType)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var contract = runtimeType.Assembly.GetType("TermLink.Agent.ProductionTerminalStateContract", true);
        var validate = contract.GetMethod("Validate", flags, null,
            new[] { typeof(IEnumerable<string>), typeof(bool), typeof(bool) }, null);
        const string state = "terminal_state_2_21_v2";
        string Check(string[] values, bool attach = true, bool geometry = true) =>
            (string)validate.Invoke(null, new object[] { values, attach, geometry });
        Require(Check(new[] { state }) == "", "Current state contract must be admitted.");
        Require(Check(Array.Empty<string>()) == "TERMINAL_CLIENT_UPGRADE_REQUIRED", "Missing state must be rejected.");
        Require(Check(new[] { "terminal_state_future_v1" }) == "TERMINAL_CLIENT_UPGRADE_REQUIRED", "Unknown state cannot imply current support.");
        Require(Check(new[] { state, "future_optional_v1" }) == "", "Future optional support must not break the known contract.");
        Require(Check(new[] { state }, false) == "TERMINAL_RELAY_UPGRADE_REQUIRED", "Old Relay must not receive new frames.");
        Require(Check(new[] { state }, geometry: false) == "TERMINAL_RELAY_UPGRADE_REQUIRED", "Missing geometry routing must reject.");
        foreach (var invalid in new[] { new[] { state, state }, new[] { "UPPERCASE" }, new[] { "" }, new[] { new string('a', 65) },
            Enumerable.Range(0, 65).Select(index => "cap_" + index).ToArray() })
            Require(Check(invalid) == "TERMINAL_CAPABILITIES_INVALID", "Malformed/oversized capability lists must reject atomically.");

        var subscriberType = runtimeType.GetNestedType("Subscriber", BindingFlags.NonPublic);
        var subscriber = Activator.CreateInstance(subscriberType, nonPublic: true);
        // No socket, key, registry or outbox is installed: a rejected attachment
        // must stop before touching any of them or advancing a delivery cursor.
        var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
        var send = runtimeType.GetMethod("SendEncryptedCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        var result = (Task<bool>)send.Invoke(runtime, new object[] { null, Guid.NewGuid(), subscriber,
            MessageType.HostGeometry, Direction.PcToAndroid, 1UL, new byte[32], CancellationToken.None });
        Require(!result.GetAwaiter().GetResult(), "Unconfirmed attachment published terminal state.");
        Require((ulong)subscriberType.GetField("LastSentSequence").GetValue(subscriber) == 0,
            "Rejected attachment advanced the terminal cursor.");
        Console.WriteLine("Terminal state negotiation: old/missing/future/malformed capabilities and pre-publication isolation passed.");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
