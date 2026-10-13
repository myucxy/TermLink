using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.LocalIpc;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.AgentUpdate.Validation
{
    internal static class TerminalProtocolDiagnosticPublicationValidation
    {
        private const BindingFlags StaticPrivate = BindingFlags.Static | BindingFlags.NonPublic;

        internal static void Run()
        {
            var directory = Path.Combine(Path.GetTempPath(), "termlink-relay-diagnostic-" + Guid.NewGuid().ToString("N"));
            var store = new TerminalFailureStore(directory);
            var session = Guid.NewGuid();
            const string diagnostic = "TL-DIAG/2\nsequence=<ESC>[?9999h\n";
            try
            {
                Require(store.TryWrite(session, diagnostic), "Authority diagnostic write failed.");
                Require(store.ReadAuthorityBody(session) == diagnostic, "Authority body was not extracted exactly.");
                Require(store.ReadAuthorityBody(Guid.NewGuid()) == null, "Another session received authority evidence.");
                var ended = Payload(session, LocalLifecycleState.Ended, 125, store);
                Require(ended.HasExitCode && ended.ExitCode == 125 && ended.HasTerminalProtocolDiagnostic &&
                    ended.TerminalProtocolDiagnostic == diagnostic, "Fail-closed Ended omitted the bounded diagnostic body.");
                Require(Encoding.UTF8.GetByteCount(ended.TerminalProtocolDiagnostic) <= 10 * 1024,
                    "Recipient diagnostic exceeded its byte limit.");
                Require(!ended.TerminalProtocolDiagnostic.Contains("source=", StringComparison.Ordinal) &&
                    !ended.TerminalProtocolDiagnostic.Contains("session=", StringComparison.Ordinal),
                    "Local diagnostic header leaked into the recipient payload.");
                Require(!Payload(session, LocalLifecycleState.Ended, 0, store).HasTerminalProtocolDiagnostic,
                    "Normal Ended disclosed unrelated authority evidence.");
                Require(!Payload(session, LocalLifecycleState.Running, 0, store).HasTerminalProtocolDiagnostic,
                    "Running lifecycle disclosed authority evidence.");
                Require(!Payload(Guid.NewGuid(), LocalLifecycleState.Ended, 125, store).HasTerminalProtocolDiagnostic,
                    "A different session disclosed authority evidence.");
                Require(!Payload(session, LocalLifecycleState.Ended, 125, store, false).HasTerminalProtocolDiagnostic,
                    "History-mode payload disclosed the realtime-only diagnostic.");
                var historyEvent = ProductionAgentRuntime.BuildHistoryEvent(new LocalFrame(
                    LocalFrameType.Lifecycle, session, 1,
                    LocalPayloadCodec.Lifecycle(LocalLifecycleState.Ended, 125)));
                Require(!SessionUpsertPayload.Parser.ParseFrom(historyEvent.Payload).HasTerminalProtocolDiagnostic,
                    "Recipient-only diagnostic entered the local history event.");

                var inputOnly = Guid.NewGuid();
                Require(store.TryWriteInput(inputOnly,
                    "TL-INPUT-DIAG/1\ndirection=input_encoding\nunsupported_scalar=U+1F6A7\n"),
                    "Input-only fixture write failed.");
                Require(store.ReadAuthorityBody(inputOnly) == null &&
                    !Payload(inputOnly, LocalLifecycleState.Ended, 125, store).HasTerminalProtocolDiagnostic,
                    "Input-only evidence leaked through the authority field.");

                var malformed = Guid.NewGuid();
                var malformedText = "source=windows_authority\nsession=" + malformed.ToString("D") +
                    "\nobserved_at=invalid\nhost_action=stopping_input_and_session_bridge\n\nTL-DIAG/2\nraw=\u001b[2J\n";
                var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(malformedText),
                    Encoding.ASCII.GetBytes("TermLink terminal diagnostic v1"), DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory, malformed.ToString("N") + ".dpapi"), protectedBytes);
                Require(store.ReadAuthorityBody(malformed) == null &&
                    !Payload(malformed, LocalLifecycleState.Ended, 125, store).HasTerminalProtocolDiagnostic,
                    "Malformed evidence entered the sealed payload.");
                var corrupt = Guid.NewGuid();
                File.WriteAllBytes(Path.Combine(directory, corrupt.ToString("N") + ".dpapi"), new byte[50]);
                var endedWithoutReadableEvidence = Payload(corrupt, LocalLifecycleState.Ended, 125, store);
                Require(endedWithoutReadableEvidence.HasExitCode &&
                    !endedWithoutReadableEvidence.HasTerminalProtocolDiagnostic,
                    "Unreadable local evidence interrupted the Ended lifecycle.");

                var legacy = SessionUpsertPayload.Parser.ParseFrom(new SessionUpsertPayload
                {
                    Lifecycle = "ENDED", ExitCode = 125
                }.ToByteArray());
                Require(!legacy.HasTerminalProtocolDiagnostic, "Old field-free Ended was misread as diagnostic.");
                var httpMetadata = ProductionAgentRuntime.BuildPCUpsertBodyWithTerminalCommands(
                    "diagnostic-test", "0.3.0", 1, new[] { ManagedTerminalLauncher.PowerShell7ShellType },
                    Array.Empty<TerminalCommandCapability>());
                Require(!Encoding.UTF8.GetString(httpMetadata).Contains("terminal_protocol_diagnostic", StringComparison.Ordinal) &&
                    !Encoding.UTF8.GetString(httpMetadata).Contains(diagnostic, StringComparison.Ordinal),
                    "Recipient-only diagnostic appeared in PC HTTP metadata.");
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private static SessionUpsertPayload Payload(Guid session, LocalLifecycleState lifecycle, int exitCode,
            TerminalFailureStore store, bool includeTerminalDiagnostic = true)
        {
            var method = typeof(ProductionAgentRuntime).GetMethod("BuildRealtimePayload", StaticPrivate)
                ?? throw new MissingMethodException("BuildRealtimePayload");
            var frame = new LocalFrame(LocalFrameType.Lifecycle, session, 1,
                LocalPayloadCodec.Lifecycle(lifecycle, exitCode));
            return (SessionUpsertPayload)method.Invoke(null,
                new object[] { frame, ManagedShellType.PowerShell7, null, store, includeTerminalDiagnostic });
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
