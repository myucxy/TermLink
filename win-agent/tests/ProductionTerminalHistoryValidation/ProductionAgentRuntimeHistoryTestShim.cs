using System;
using System.IO;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.Agent
{
    // The validation project links the history data-plane sources so it can exercise
    // their internal contracts without widening production visibility. The linked
    // Host History service only needs this narrow event projection seam.
    internal static class ProductionAgentRuntime
    {
        internal static HistoryEventV1 BuildHistoryEvent(LocalFrame frame)
        {
            if (frame == null || frame.Type == LocalFrameType.SessionRemoved) return null;
            var messageType = MapMessageType(frame);
            if (messageType == MessageType.Unspecified) return null;
            var payload = BuildPayload(frame).ToByteArray();
            return new HistoryEventV1
            {
                Seq = frame.Sequence,
                MessageType = messageType,
                Payload = ByteString.CopyFrom(payload)
            };
        }

        private static MessageType MapMessageType(LocalFrame frame)
        {
            if (frame.Type == LocalFrameType.Output) return MessageType.OutputChunk;
            if (frame.Type == LocalFrameType.BrokerOutput) return MessageType.BrokerOutput;
            if (frame.Type == LocalFrameType.Resize) return MessageType.ResizeApplied;
            if (frame.Type == LocalFrameType.HostTheme) return MessageType.HostTheme;
            if (frame.Type == LocalFrameType.HostResizeResult) return MessageType.HostResizeResult;
            if (frame.Type == LocalFrameType.HostGeometry) return MessageType.HostGeometry;
            if (frame.Type == LocalFrameType.Title) return MessageType.SessionUpsert;
            if (frame.Type == LocalFrameType.Gap) return MessageType.Gap;
            if (frame.Type == LocalFrameType.Lifecycle)
            {
                var lifecycle = LocalPayloadCodec.ParseLifecycle(frame.Payload, out _);
                if (lifecycle == LocalLifecycleState.Running) return MessageType.SessionStarted;
                if (lifecycle == LocalLifecycleState.Interrupted) return MessageType.SessionInterrupted;
                if (lifecycle == LocalLifecycleState.Ended) return MessageType.SessionEnded;
            }
            if (frame.Type == LocalFrameType.LocalInputObserved) return MessageType.LeaseRevoked;
            if (frame.Type == LocalFrameType.RemoteInputResult)
            {
                var inputId = LocalPayloadCodec.ParseRemoteInputResult(frame.Payload, out var result);
                _ = inputId;
                return result == LocalRemoteInputResult.Accepted
                    ? MessageType.InputAccepted
                    : MessageType.InputRejected;
            }
            return MessageType.Unspecified;
        }

        private static IMessage BuildPayload(LocalFrame frame)
        {
            if (frame.Type == LocalFrameType.Output)
                return new OutputChunkPayload
                {
                    Output = ByteString.CopyFrom(frame.Payload),
                    SourceEventSeq = frame.Sequence,
                    PresentationRevision = frame.Sequence
                };
            if (frame.Type == LocalFrameType.BrokerOutput)
            {
                using var reference = TermLink.TerminalCore.NativeTerminalBrokerOutputReferenceV1.Decode(frame.Payload);
                if (reference.SourceSequence != frame.Sequence)
                    throw new InvalidDataException("BrokerOutput reference source sequence is invalid.");
                return new BrokerOutputPayload { ReferenceV1 = ByteString.CopyFrom(frame.Payload) };
            }
            if (frame.Type == LocalFrameType.HostTheme)
                return new HostThemePayload { ThemeV1 = ByteString.CopyFrom(frame.Payload) };
            if (frame.Type == LocalFrameType.HostResizeResult)
                return new HostResizeResultPayload { ResultV1 = ByteString.CopyFrom(frame.Payload) };
            if (frame.Type == LocalFrameType.HostGeometry)
                return new HostGeometryPayload { GeometryV1 = ByteString.CopyFrom(frame.Payload) };
            if (frame.Type == LocalFrameType.Resize)
            {
                LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                return new ResizePayload { Columns = (uint)columns, Rows = (uint)rows };
            }
            if (frame.Type == LocalFrameType.Gap)
            {
                LocalPayloadCodec.ParseGap(frame.Payload, out var start, out var end);
                return new GapPayload { Start = start, End = end, ReasonCode = "LOCAL_BUFFER_GAP" };
            }
            if (frame.Type == LocalFrameType.LocalInputObserved)
                return new LeasePayload
                {
                    ReasonCode = "LOCAL_INPUT_PRIORITY",
                    LocalInputEpoch = LocalPayloadCodec.ParseLocalInputEpoch(frame.Payload)
                };
            if (frame.Type == LocalFrameType.RemoteInputResult)
            {
                var inputId = LocalPayloadCodec.ParseRemoteInputResult(frame.Payload, out var result);
                return new InputResultPayload
                {
                    InputId = ByteString.CopyFrom(Convert.FromHexString(inputId.ToString("N"))),
                    Accepted = result == LocalRemoteInputResult.Accepted,
                    ReasonCode = InputResultReason(result)
                };
            }
            if (frame.Type == LocalFrameType.Lifecycle)
            {
                var lifecycle = LocalPayloadCodec.ParseLifecycle(frame.Payload, out var exitCode);
                var payload = new SessionUpsertPayload
                {
                    ShellType = "custom",
                    Lifecycle = lifecycle == LocalLifecycleState.Ended
                        ? "ENDED"
                        : lifecycle == LocalLifecycleState.Interrupted ? "INTERRUPTED" : "RUNNING",
                    TitleCipher = ByteString.Empty
                };
                if (lifecycle == LocalLifecycleState.Ended) payload.ExitCode = exitCode;
                return payload;
            }
            if (frame.Type == LocalFrameType.Title)
                return new SessionUpsertPayload
                {
                    ShellType = "custom",
                    Lifecycle = "CHANGED",
                    TitleCipher = ByteString.Empty,
                    Title = LocalPayloadCodec.ParseTitle(frame.Payload)
                };
            throw new InvalidDataException("Unsupported local history frame.");
        }

        private static string InputResultReason(LocalRemoteInputResult result)
        {
            if (result == LocalRemoteInputResult.Accepted) return string.Empty;
            if (result == LocalRemoteInputResult.LocalInputSuperseded) return "LOCAL_INPUT_PRIORITY";
            if (result == LocalRemoteInputResult.ConPtyClosed) return "SESSION_INPUT_CLOSED";
            if (result == LocalRemoteInputResult.InputSequenceRejected) return "INPUT_SEQUENCE_REJECTED";
            if (result == LocalRemoteInputResult.ControlLeaseRejected) return "CONTROL_LEASE_REJECTED";
            if (result == LocalRemoteInputResult.SessionInputUnavailable) return "SESSION_INPUT_UNAVAILABLE";
            if (result == LocalRemoteInputResult.InputPayloadRejected) return "INPUT_PAYLOAD_REJECTED";
            return "CONPTY_WRITE_FAILED";
        }
    }
}
