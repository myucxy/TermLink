using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using TermLink.Common;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

namespace TermLink.Agent;

// A capture is selectable only through its exact durable ordered WAL reference.
internal sealed class ProductionBrokerCaptureService
{
    private readonly ProductionTerminalHistoryStore history;
    private readonly Guid pcDeviceId;
    private readonly string captureRoot;

    internal ProductionBrokerCaptureService(ProductionTerminalHistoryStore history, Guid pcDeviceId)
    {
        this.history = history ?? throw new ArgumentNullException(nameof(history));
        if (pcDeviceId == Guid.Empty) throw new ArgumentException("PC device ID is required.", nameof(pcDeviceId));
        this.pcDeviceId = pcDeviceId;
        captureRoot = history.BrokerCaptureRootPath;
    }

    internal ProductionHostHistorySelection Select(Guid sessionId, HostHistoryRequestPayload request,
        ProductionHistoryRecipient recipient, CancellationToken cancellationToken)
    {
        if (request?.BrokerCapture == null || request.Kind != HostHistoryRequestKindV1.BrokerCapture ||
            request.RequestId.Length != 16 ||
            request.SnapshotSeq != 0 || request.BeforeLineId != 0 || request.AfterSeq != 0 ||
            request.ThroughSeq != 0 || request.MaxEvents != 0 ||
            request.FrozenHistory != null || request.StateDelta != null ||
            recipient == null || recipient.DeviceId == Guid.Empty || recipient.DeviceId == pcDeviceId ||
            recipient.PairwiseKey?.Length != 32 || recipient.KeyEpoch < 1)
            return Reject("INVALID_REQUEST");
        if (history.IsSessionDeleted(sessionId)) return Reject("BROKER_CAPTURE_MISSING");
        if (!request.SubscriberCapabilities.Contains("terminal_broker_captures_v1") ||
            !history.GetSessionProfile(sessionId).IsGraphics)
            return Reject("UNSUPPORTED_FORMAT");
        var wanted = request.BrokerCapture;
        if (wanted.ObjectId.Length != 16 || wanted.SourceEventSeq is 0 or >= long.MaxValue ||
            wanted.CanonicalBytes is < 136 or > BrokerCaptureCrypto.MaximumCanonicalBytes ||
            wanted.CanonicalSha256.Length != 32)
            return Reject("INVALID_REQUEST");
        var objectId = Guid.ParseExact(Convert.ToHexString(wanted.ObjectId.Span), "N");
        if (objectId == Guid.Empty) return Reject("INVALID_REQUEST");
        cancellationToken.ThrowIfCancellationRequested();
        System.Collections.Generic.IList<LocalFrame> frames;
        try
        {
            frames = history.ReadContiguousFrames(sessionId, wanted.SourceEventSeq - 1,
                wanted.SourceEventSeq, 1, 128 * 1024, out _);
        }
        catch (InvalidDataException) { return Reject("INVALID_BROKER_CAPTURE"); }
        catch (IOException) { return Reject("BROKER_CAPTURE_MISSING"); }
        if (frames.Count != 1)
            return Reject("BROKER_CAPTURE_MISSING");
        var frame = frames[0];
        try
        {
            if (frame.Type != LocalFrameType.BrokerOutput)
                return Reject("BROKER_CAPTURE_MISSING");
            using var reference = NativeTerminalBrokerOutputReferenceV1.Decode(frame.Payload);
            if (reference.IsInline || reference.SourceSequence != wanted.SourceEventSeq ||
                reference.ObjectId != objectId || reference.CaptureBytes != wanted.CanonicalBytes)
                return Reject("BROKER_CAPTURE_MISSING");
            var expectedHash = reference.CopySha256();
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, wanted.CanonicalSha256.Span))
                    return Reject("BROKER_CAPTURE_MISSING");
                cancellationToken.ThrowIfCancellationRequested();
                byte[] canonical;
                try
                {
                    using var captures = new KittyCaptureStore(captureRoot, sessionId);
                    canonical = captures.ReadExact(sessionId, wanted.SourceEventSeq, objectId,
                        checked((int)wanted.CanonicalBytes), expectedHash);
                }
                catch (InvalidDataException) { return Reject("INVALID_BROKER_CAPTURE"); }
                catch (IOException) { return Reject("BROKER_CAPTURE_MISSING"); }
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sealedObject = BrokerCaptureCrypto.Seal(recipient.PairwiseKey, sessionId,
                        pcDeviceId, recipient.DeviceId, objectId, wanted.SourceEventSeq,
                        recipient.KeyEpoch, canonical,
                        stateContract: history.GetSessionProfile(sessionId).StateContract);
                    if (sealedObject.Length != BrokerCaptureCrypto.HeaderBytes + canonical.Length + 16 ||
                        sealedObject.Length > BrokerCaptureCrypto.MaximumObjectBytes)
                    {
                        CryptographicOperations.ZeroMemory(sealedObject);
                        return Reject("INVALID_BROKER_CAPTURE");
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        CryptographicOperations.ZeroMemory(sealedObject);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    return new ProductionHostHistorySelection
                    {
                        Status = HostHistoryResponseStatusV1.Ok,
                        BrokerCapture = wanted.Clone(),
                        Object = new ProductionHostHistoryObject
                        {
                            Kind = HostHistoryObjectKindV1.BrokerCapture,
                            ObjectId = objectId,
                            StartSequence = wanted.SourceEventSeq,
                            EndSequence = wanted.SourceEventSeq,
                            KeyId = BrokerCaptureCrypto.KeyId(recipient.DeviceId, recipient.KeyEpoch,
                                history.GetSessionProfile(sessionId).StateContract),
                            FormatName = "TERMLINK_BROKER_CAPTURE_V1",
                            FormatVersion = 1,
                            SealedObject = sealedObject
                        }
                    };
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }
            }
            finally { CryptographicOperations.ZeroMemory(expectedHash); }
        }
        catch (InvalidDataException) { return Reject("INVALID_BROKER_CAPTURE"); }
        finally { CryptographicOperations.ZeroMemory(frame.Payload); }
    }

    internal void DeleteSession(Guid sessionId)
    {
        using var captures = new KittyCaptureStore(captureRoot, sessionId);
        captures.DeleteSession(sessionId);
    }

    internal void PersistAndVerifyPublishedFrame(LocalFrame frame)
    {
        if (frame == null || frame.Type != LocalFrameType.BrokerOutput)
            throw new ArgumentException("A BrokerOutput frame is required.", nameof(frame));
        using var reference = NativeTerminalBrokerOutputReferenceV1.Decode(frame.Payload);
        if (reference.SourceSequence != frame.Sequence) throw new InvalidDataException(
            "BrokerOutput source sequence differs from its ordered event.");
        history.AppendCommittedFrame(frame);
        if (reference.IsInline) return;
        var hash = reference.CopySha256();
        try
        {
            using var captures = new KittyCaptureStore(captureRoot, frame.SessionId);
            var canonical = captures.ReadExact(frame.SessionId, frame.Sequence,
                reference.ObjectId, reference.CaptureBytes, hash);
            CryptographicOperations.ZeroMemory(canonical);
        }
        finally { CryptographicOperations.ZeroMemory(hash); }
    }

    private static ProductionHostHistorySelection Reject(string code) => new()
    { Status = HostHistoryResponseStatusV1.Rejected, ErrorCode = code };
}
