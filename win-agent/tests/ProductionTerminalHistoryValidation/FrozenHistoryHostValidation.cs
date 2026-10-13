using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

internal static class FrozenHistoryHostValidation
{
    private static int assertions;
    private static readonly Guid Pc = Guid.Parse("f1a74fd4-257e-4f88-9bbb-555555555555");
    private static readonly Guid Phone = Guid.Parse("e1b13d5a-351f-46c7-a542-666666666666");
    private static readonly Guid OtherPhone = Guid.Parse("7a54bd90-522a-46c7-a542-777777777777");

    public static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "termlink-frozen-history-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        try
        {
            var session = Guid.NewGuid();
            var store = new ProductionTerminalHistoryStore(root, Pc, _ => TerminalSemanticProfile.Graphics);
            using var producer = new ProductionTerminalHistoryProducer(store, Pc,
                terminalCoreShadowEnabled: false, terminalCoreSnapshotAuthorityEnabled: true,
                terminalCoreShadowObserver: null);
            var output = string.Join("\r\n", Enumerable.Range(0, 120).Select(i => "frozen-line-" + i)) + "\r\n";
            producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 1,
                Encoding.UTF8.GetBytes(output)));
            Require(producer.ProcessSession(session).SnapshotEligible, "Graphics snapshot must be eligible.");
            var recipient = new ProductionHistoryRecipient { DeviceId = Phone, KeyEpoch = 3, PairwiseKey = key };
            long ticks = Stopwatch.GetTimestamp();
            using var host = new ProductionFrozenHistoryService(store, Pc, _ => false, () => ticks);
            var reorderedCancel = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            host.CancelBegin(session, Phone, reorderedCancel.RequestId);
            host.CancelBegin(session, Phone, reorderedCancel.RequestId);
            using var cancelledBeforeSelection = host.Select(session, reorderedCancel, recipient,
                reorderedCancel.RequestId, CancellationToken.None);
            Require(cancelledBeforeSelection.ErrorCode == "STALE_CAPTURE",
                "A CANCEL received before BEGIN selection must prevent later capture creation.");
            var begin = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var first = host.Select(session, begin, recipient, begin.RequestId, CancellationToken.None);
            Require(first.Status == HostHistoryResponseStatusV1.Ok && first.Object != null &&
                first.FrozenHistory.Token.Length == 32 &&
                first.Object.FormatName == "TERMLINK_FROZEN_HISTORY_PAGE_V2" &&
                first.Object.FormatVersion == 1,
                "BEGIN must capture a graphics TLHPV2 page.");
            Require(first.FrozenHistory.CaptureRevision == 2 && first.Object.SnapshotSequence == 1,
                "Capture revision must be S+1.");
            Require(first.FrozenHistory.StateContract == TerminalSemanticProfile.Graphics.StateContract,
                "Frozen capture must remain bound to the persisted Graphics profile.");
            Require(first.Object.SealedObject.Length <= 512 * 1024,
                "The success fixture must require exactly one response chunk.");
            using (var delivered = new ProductionHostHistoryResponseRequest(session, Phone,
                HostHistoryRequestKindV1.FrozenBegin))
            {
                var ack = delivered.PrepareChunkAcknowledgement(first.Object.ObjectId, 0);
                Require(!delivered.Acknowledge(Guid.NewGuid(), 0) && !ack.IsCompleted,
                    "An unrelated chunk ACK completed the BEGIN response.");
                Require(delivered.Acknowledge(first.Object.ObjectId, 0),
                    "The real final-chunk ACK was rejected.");
                ack.GetAwaiter().GetResult();
            }
            Require(!host.MarkBeginDelivered(session, OtherPhone, begin.RequestId) &&
                !host.MarkBeginDelivered(Guid.NewGuid(), Phone, begin.RequestId),
                "A different recipient or session marked the BEGIN delivered.");
            Require(host.MarkBeginDelivered(session, Phone, begin.RequestId),
                "The fully ACKed BEGIN was not marked delivered.");
            host.CancelBegin(session, Guid.NewGuid(), begin.RequestId);
            var plaintext = Open(first, session, begin, recipient);
            try
            {
                var parsed = NativeTerminalHistoryPageV2.Decode(plaintext, first.FrozenHistory.CaptureId);
                Require(parsed.Lines.Count == 1 && parsed.HasMoreBefore &&
                    parsed.RequestedAnchor == NativeTerminalHistoryCapture.LatestAnchor,
                    "The first page must contain one immutable line and an older page anchor.");
                Require(first.Object.Columns == parsed.Columns &&
                    first.Object.Rows == parsed.Lines.Sum(line => checked((int)line.RowCount)),
                    "The outer frozen page geometry must match its encrypted TLHPV2 page.");
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
            var rejectedProfile = false;
            try
            {
                var invalid = Open(first, session, begin, recipient,
                    TerminalSemanticProfile.Legacy.StateContract);
                CryptographicOperations.ZeroMemory(invalid);
            }
            catch (InvalidDataException) { rejectedProfile = true; }
            Require(rejectedProfile, "A frozen page must reject another semantic profile.");
            producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 2,
                Encoding.UTF8.GetBytes("output after capture\r\n")));
            Require(producer.ProcessSession(session).SnapshotEligible,
                "New output must remain eligible for a newer durable snapshot.");
            var newer = store.ReadExactGraphicsSnapshot(session, 2, out var currentSequence);
            try { Require(newer != null && currentSequence == 2,
                "The durable outbox must advance after freezing the old capture.");
            }
            finally { if (newer?.Plaintext != null) CryptographicOperations.ZeroMemory(newer.Plaintext); }

            using var duplicate = host.Select(session, begin, recipient, begin.RequestId, CancellationToken.None);
            Require(duplicate.Status == HostHistoryResponseStatusV1.Ok &&
                duplicate.FrozenHistory.Token.Equals(first.FrozenHistory.Token) &&
                duplicate.Object.SealedObject.SequenceEqual(first.Object.SealedObject),
                "Unrelated sender CANCEL must not alter BEGIN retry or ciphertext.");
            using (var failedRetry = new ProductionHostHistoryResponseRequest(session, Phone,
                HostHistoryRequestKindV1.FrozenBegin))
            {
                var ack = failedRetry.PrepareChunkAcknowledgement(duplicate.Object.ObjectId, 0);
                var timeout = Task.Delay(10);
                Require(Task.WhenAny(ack, timeout).GetAwaiter().GetResult() == timeout,
                    "The READY retry fixture did not encounter a missing ACK.");
                failedRetry.Cancel();
                host.CancelUndeliveredBegin(session, Phone, begin.RequestId);
            }
            using var afterDeliveredRetryFailure = host.Select(session, begin, recipient,
                begin.RequestId, CancellationToken.None);
            Require(afterDeliveredRetryFailure.Status == HostHistoryResponseStatusV1.Ok &&
                afterDeliveredRetryFailure.FrozenHistory.Token.Equals(first.FrozenHistory.Token),
                "A failed retry removed the previously delivered READY capture.");
            var unsupported = begin.Clone();
            unsupported.SubscriberCapabilities.Clear();
            using var oldPeer = host.Select(session, unsupported, recipient,
                unsupported.RequestId, CancellationToken.None);
            Require(oldPeer.ErrorCode == "UNSUPPORTED_FORMAT",
                "A peer without both frozen page capabilities must be rejected.");
            var differentBegin = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var busy = host.Select(session, differentBegin, recipient,
                differentBegin.RequestId, CancellationToken.None);
            Require(busy.ErrorCode == "CAPTURE_LIMIT",
                "A second BEGIN cannot replace the active recipient lease.");

            var pageRequest = Request(HostHistoryRequestKindV1.FrozenPage, 1,
                first.Object.NextBeforeLineId);
            Bind(pageRequest, first.FrozenHistory);
            using var second = host.Select(session, pageRequest, recipient, pageRequest.RequestId,
                CancellationToken.None);
            Require(second.Status == HostHistoryResponseStatusV1.Ok &&
                second.Object.RequestBeforeLineId == first.Object.NextBeforeLineId &&
                second.Object.FirstLineId != first.Object.FirstLineId,
                "PAGE must read the same capture at the requested anchor.");
            using var repeatedPage = host.Select(session, pageRequest, recipient, pageRequest.RequestId,
                CancellationToken.None);
            Require(repeatedPage.Object.SealedObject.SequenceEqual(second.Object.SealedObject),
                "PAGE retry must reuse the exact ciphertext.");
            using (var pageCancellation = new CancellationTokenSource())
            {
                pageCancellation.Cancel();
                var cancelledPage = false;
                try { using var ignored = host.Select(session, pageRequest, recipient,
                    pageRequest.RequestId, pageCancellation.Token); }
                catch (OperationCanceledException) { cancelledPage = true; }
                Require(cancelledPage, "Cancelling PAGE must stop only that request.");
            }
            using var afterCancel = host.Select(session, pageRequest, recipient, pageRequest.RequestId,
                CancellationToken.None);
            Require(afterCancel.Status == HostHistoryResponseStatusV1.Ok &&
                afterCancel.Object.SealedObject.SequenceEqual(second.Object.SealedObject),
                "A cancelled PAGE must not release its browsing lease.");

            var impostor = new ProductionHistoryRecipient { DeviceId = Guid.NewGuid(),
                KeyEpoch = recipient.KeyEpoch, PairwiseKey = key };
            using var wrongDevice = host.Select(session, pageRequest, impostor,
                pageRequest.RequestId, CancellationToken.None);
            Require(wrongDevice.ErrorCode == "STALE_CAPTURE", "A different recipient must be denied.");
            var wrongEpochIdentity = new ProductionHistoryRecipient { DeviceId = Phone,
                KeyEpoch = 4, PairwiseKey = key };
            using var wrongEpoch = host.Select(session, pageRequest, wrongEpochIdentity,
                pageRequest.RequestId, CancellationToken.None);
            Require(wrongEpoch.ErrorCode == "STALE_CAPTURE", "An old capture must not cross key epochs.");
            var wrongTokenRequest = pageRequest.Clone();
            wrongTokenRequest.FrozenHistory.Token = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
            using var wrongToken = host.Select(session, wrongTokenRequest, recipient,
                wrongTokenRequest.RequestId, CancellationToken.None);
            Require(wrongToken.ErrorCode == "STALE_CAPTURE", "A native capture ID is not authorization.");
            var wrongFormatRequest = pageRequest.Clone();
            wrongFormatRequest.FrozenHistory.PageFormatVersion = FrozenHistoryCrypto.PageFormatV1;
            using var wrongFormat = host.Select(session, wrongFormatRequest, recipient,
                wrongFormatRequest.RequestId, CancellationToken.None);
            Require(wrongFormat.ErrorCode == "UNSUPPORTED_FORMAT",
                "Graphics captures must not silently fall back to TLHPV1.");

            var invalidAnchor = pageRequest.Clone();
            invalidAnchor.SnapshotSeq = 99;
            using var wrongAnchor = host.Select(session, invalidAnchor, recipient,
                invalidAnchor.RequestId, CancellationToken.None);
            Require(wrongAnchor.ErrorCode == "STALE_ANCHOR", "PAGE must bind exact snapshot S.");
            var tooSmall = pageRequest.Clone();
            tooSmall.FrozenHistory.MaxCells = 1;
            using var budget = host.Select(session, tooSmall, recipient,
                tooSmall.RequestId, CancellationToken.None);
            Require(budget.ErrorCode == "PAGE_BUDGET" && budget.FrozenHistory.RequiredCells > 1,
                "A whole line that exceeds the cell budget must return its requirement.");

            var release = Request(HostHistoryRequestKindV1.FrozenRelease, 1, 0);
            Bind(release, first.FrozenHistory);
            var forgedRelease = release.Clone();
            forgedRelease.FrozenHistory.Token = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32));
            using var deniedTokenRelease = host.Select(session, forgedRelease, recipient,
                forgedRelease.RequestId, CancellationToken.None);
            Require(deniedTokenRelease.ErrorCode == "STALE_CAPTURE",
                "RELEASE must require the bound opaque token.");
            var wrongReleaseS = release.Clone();
            wrongReleaseS.SnapshotSeq = 2;
            using var deniedRelease = host.Select(session, wrongReleaseS, recipient,
                wrongReleaseS.RequestId, CancellationToken.None);
            Require(deniedRelease.ErrorCode == "STALE_ANCHOR",
                "RELEASE cannot discard another snapshot generation.");
            using var released = host.Select(session, release, recipient, release.RequestId, CancellationToken.None);
            using var releasedAgain = host.Select(session, release, recipient, release.RequestId, CancellationToken.None);
            Require(released.Status == HostHistoryResponseStatusV1.Ok && released.Object == null &&
                releasedAgain.Status == HostHistoryResponseStatusV1.Ok &&
                released.FrozenHistory.SnapshotSeq == 1 && releasedAgain.FrozenHistory.SnapshotSeq == 1,
                "RELEASE must be empty and idempotent.");
            using var releasedBeginReplay = host.Select(session, begin, recipient, begin.RequestId,
                CancellationToken.None);
            Require(releasedBeginReplay.ErrorCode == "STALE_CAPTURE",
                "The same BEGIN request ID cannot allocate a new capture after RELEASE.");
            using var stale = host.Select(session, pageRequest, recipient, pageRequest.RequestId,
                CancellationToken.None);
            Require(stale.ErrorCode == "STALE_CAPTURE", "Released captures must reject PAGE.");

            var changed = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var anchorChanged = host.Select(session, changed, recipient, changed.RequestId,
                CancellationToken.None);
            Require(anchorChanged.ErrorCode == "ANCHOR_CHANGED" &&
                anchorChanged.AvailableSnapshotSequence == 2,
                "BEGIN must reject an obsolete S and disclose only the newer available S.");
            var future = Request(HostHistoryRequestKindV1.FrozenBegin, 99,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var futureChanged = host.Select(session, future, recipient, future.RequestId,
                CancellationToken.None);
            Require(futureChanged.ErrorCode == "ANCHOR_CHANGED" &&
                futureChanged.AvailableSnapshotSequence == 0,
                "BEGIN must not suggest an older S for a future anchor.");

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            var didCancel = false;
            try { using var ignored = host.Select(session, begin, recipient, begin.RequestId, cancelled.Token); }
            catch (OperationCanceledException) { didCancel = true; }
            Require(didCancel, "Cancellation must not create another capture.");

            var fresh = Request(HostHistoryRequestKindV1.FrozenBegin, 2,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var freshSelection = host.Select(session, fresh, recipient, fresh.RequestId,
                CancellationToken.None);
            Require(freshSelection.Status == HostHistoryResponseStatusV1.Ok,
                "A released slot must be reusable.");
            var extraSessions = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
            foreach (var extraSession in extraSessions)
            {
                producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, extraSession, 1,
                    Encoding.UTF8.GetBytes("separate capture\r\n")));
                Require(producer.ProcessSession(extraSession).SnapshotEligible,
                    "Additional graphics sessions must produce native snapshots.");
            }
            for (var i = 0; i < 3; i++)
            {
                var extraBegin = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                    NativeTerminalHistoryCapture.LatestAnchor);
                using var extra = host.Select(extraSessions[i], extraBegin, recipient,
                    extraBegin.RequestId, CancellationToken.None);
                Require(extra.Status == HostHistoryResponseStatusV1.Ok,
                    "The registry must admit up to four concurrent captures.");
            }
            var fifthBegin = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var fifth = host.Select(extraSessions[3], fifthBegin, recipient,
                fifthBegin.RequestId, CancellationToken.None);
            Require(fifth.ErrorCode == "CAPTURE_LIMIT", "A fifth capture must be rejected.");
            host.InvalidateSession(extraSessions[0]);
            using var freed = host.Select(extraSessions[3], fifthBegin, recipient,
                fifthBegin.RequestId, CancellationToken.None);
            Require(freed.Status == HostHistoryResponseStatusV1.Ok,
                "Session invalidation must free native and registry quota.");
            ticks += 299L * Stopwatch.Frequency;
            Require(host.MarkBeginDelivered(session, Phone, fresh.RequestId),
                "Marking a live BEGIN near its deadline failed.");
            ticks += 2L * Stopwatch.Frequency;
            var expiredPage = Request(HostHistoryRequestKindV1.FrozenPage, 2,
                freshSelection.Object.NextBeforeLineId);
            Bind(expiredPage, freshSelection.FrozenHistory);
            using var expired = host.Select(session, expiredPage, recipient, expiredPage.RequestId,
                CancellationToken.None);
            Require(expired.ErrorCode == "STALE_CAPTURE", "The monotonic five-minute lease must expire.");

            var noChunkBegin = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var noChunk = host.Select(extraSessions[0], noChunkBegin, recipient,
                noChunkBegin.RequestId, CancellationToken.None);
            Require(noChunk.Status == HostHistoryResponseStatusV1.Ok,
                "A capture may exist before its first response chunk is sent.");
            Require(!host.MarkBeginDelivered(extraSessions[0], OtherPhone, noChunkBegin.RequestId) &&
                !host.MarkBeginDelivered(extraSessions[1], Phone, noChunkBegin.RequestId),
                "The wrong scope marked an undelivered BEGIN as READY.");
            host.CancelUndeliveredBegin(extraSessions[0], Phone, noChunkBegin.RequestId);
            using var noChunkReplay = host.Select(extraSessions[0], noChunkBegin, recipient,
                noChunkBegin.RequestId, CancellationToken.None);
            Require(noChunkReplay.ErrorCode == "STALE_CAPTURE",
                "Cancelling before the first chunk must release and tombstone BEGIN.");
            var replacement = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var replacementSelection = host.Select(extraSessions[0], replacement, recipient,
                replacement.RequestId, CancellationToken.None);
            Require(replacementSelection.Status == HostHistoryResponseStatusV1.Ok,
                "A new BEGIN ID must use the released capture slot.");

            var firstChunkBegin = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var firstChunk = host.Select(extraSessions[1], firstChunkBegin, recipient,
                firstChunkBegin.RequestId, CancellationToken.None);
            using (var pending = new ProductionHostHistoryResponseRequest(extraSessions[1], Phone,
                HostHistoryRequestKindV1.FrozenBegin))
            {
                var ack = pending.PrepareChunkAcknowledgement(firstChunk.Object.ObjectId, 0);
                pending.Cancel();
                Require(pending.Kind == HostHistoryRequestKindV1.FrozenBegin && ack.IsCanceled,
                    "CANCEL after the first chunk must stop its ACK wait.");
                host.CancelUndeliveredBegin(extraSessions[1], Phone, firstChunkBegin.RequestId);
            }
            using var firstChunkReplay = host.Select(extraSessions[1], firstChunkBegin, recipient,
                firstChunkBegin.RequestId, CancellationToken.None);
            Require(firstChunkReplay.ErrorCode == "STALE_CAPTURE",
                "CANCEL after the first chunk must not leave an unreachable capture.");
            var timeoutBegin = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var timeoutSelection = host.Select(extraSessions[2], timeoutBegin, recipient,
                timeoutBegin.RequestId, CancellationToken.None);
            Require(timeoutSelection.Status == HostHistoryResponseStatusV1.Ok,
                "The ACK timeout fixture must own a real frozen capture.");
            using (var awaitingAck = new ProductionHostHistoryResponseRequest(extraSessions[2], Phone,
                HostHistoryRequestKindV1.FrozenBegin))
            {
                var ack = awaitingAck.PrepareChunkAcknowledgement(timeoutSelection.Object.ObjectId, 0);
                var timeout = Task.Delay(10);
                Require(Task.WhenAny(ack, timeout).GetAwaiter().GetResult() == timeout && !ack.IsCompleted,
                    "The missing chunk ACK did not time out.");
                awaitingAck.Cancel();
                Require(ack.IsCanceled, "ACK timeout did not stop the pending BEGIN response.");
                host.CancelUndeliveredBegin(extraSessions[2], Phone, timeoutBegin.RequestId);
            }
            using var timedOutReplay = host.Select(extraSessions[2], timeoutBegin, recipient,
                timeoutBegin.RequestId, CancellationToken.None);
            Require(timedOutReplay.ErrorCode == "STALE_CAPTURE",
                "A timed-out BEGIN ID allocated another capture.");
            var timeoutReplacement = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var afterTimeout = host.Select(extraSessions[2], timeoutReplacement, recipient,
                timeoutReplacement.RequestId, CancellationToken.None);
            Require(afterTimeout.Status == HostHistoryResponseStatusV1.Ok,
                "ACK timeout did not free the recipient's capture slot for a fresh ID.");
            var timeoutOtherRecipient = new ProductionHistoryRecipient { DeviceId = OtherPhone,
                KeyEpoch = recipient.KeyEpoch, PairwiseKey = key };
            using var otherRecipientAfterTimeout = host.Select(extraSessions[2], timeoutBegin,
                timeoutOtherRecipient, timeoutBegin.RequestId, CancellationToken.None);
            Require(otherRecipientAfterTimeout.Status == HostHistoryResponseStatusV1.Ok,
                "One recipient's timed-out BEGIN tombstone affected another recipient.");
            using var otherSessionAfterTimeout = host.Select(extraSessions[3], timeoutBegin,
                recipient, timeoutBegin.RequestId, CancellationToken.None);
            Require(otherSessionAfterTimeout.Status == HostHistoryResponseStatusV1.Ok,
                "One session's timed-out BEGIN tombstone affected another session.");
            using (var pagePending = new ProductionHostHistoryResponseRequest(extraSessions[0], Phone,
                HostHistoryRequestKindV1.FrozenPage))
            {
                pagePending.Cancel();
                Require(pagePending.Kind == HostHistoryRequestKindV1.FrozenPage,
                    "PAGE cancellation must remain request scoped.");
            }
            var replacementPage = Request(HostHistoryRequestKindV1.FrozenPage, 1,
                replacementSelection.Object.NextBeforeLineId);
            Bind(replacementPage, replacementSelection.FrozenHistory);
            using var afterPageCancel = host.Select(extraSessions[0], replacementPage, recipient,
                replacementPage.RequestId, CancellationToken.None);
            Require(afterPageCancel.Status == HostHistoryResponseStatusV1.Ok,
                "A PAGE CANCEL must not release its BEGIN lease.");

            // Saturation cannot silently forget a CANCEL or block another recipient.
            for (var i = 0; i < 1025; i++)
                host.CancelBegin(Guid.NewGuid(), Phone,
                    ByteString.CopyFrom(Guid.NewGuid().ToByteArray()));
            host.InvalidateSession(extraSessions[0]);
            var blockedBegin = Request(HostHistoryRequestKindV1.FrozenBegin, 2,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var blocked = host.Select(session, blockedBegin, recipient,
                blockedBegin.RequestId, CancellationToken.None);
            Require(blocked.ErrorCode == "CAPTURE_LIMIT",
                "A saturated recipient must be blocked for the CANCEL retry window.");
            var otherRecipient = new ProductionHistoryRecipient { DeviceId = OtherPhone,
                KeyEpoch = recipient.KeyEpoch, PairwiseKey = key };
            var otherBegin = Request(HostHistoryRequestKindV1.FrozenBegin, 2,
                NativeTerminalHistoryCapture.LatestAnchor);
            using var otherSelection = host.Select(session, otherBegin, otherRecipient,
                otherBegin.RequestId, CancellationToken.None);
            Require(otherSelection.Status == HostHistoryResponseStatusV1.Ok,
                "One recipient's CANCEL quota must not block another recipient's BEGIN.");
            ticks += 301L * Stopwatch.Frequency;
            using var admitted = host.Select(session, blockedBegin, recipient,
                blockedBegin.RequestId, CancellationToken.None);
            Require(admitted.Status == HostHistoryResponseStatusV1.Ok,
                "The saturated recipient must resume after its CANCEL window expires.");
            ValidateUnicode16(Path.Combine(root, "unicode16"), key);
            Console.WriteLine("Frozen Host History validation passed: " + assertions + " assertions.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void ValidateUnicode16(string root, byte[] key)
    {
        var profile = TerminalSemanticProfile.GraphicsUnicode16;
        var session = Guid.NewGuid();
        var store = new ProductionTerminalHistoryStore(root, Pc, _ => profile);
        using var producer = new ProductionTerminalHistoryProducer(store, Pc,
            terminalCoreShadowEnabled: false, terminalCoreSnapshotAuthorityEnabled: true,
            terminalCoreShadowObserver: null);
        producer.AppendCommittedFrame(new LocalFrame(LocalFrameType.Output, session, 1,
            Encoding.UTF8.GetBytes(string.Join("\r\n", Enumerable.Range(0, 60)
                .Select(index => "unicode frozen line-" + index)) + "\r\n")));
        Require(producer.ProcessSession(session).SnapshotEligible,
            "Unicode16 frozen fixture must have an eligible durable snapshot.");
        var recipient = new ProductionHistoryRecipient { DeviceId = Phone, KeyEpoch = 3, PairwiseKey = key };
        using var host = new ProductionFrozenHistoryService(store, Pc, _ => false);
        var request = Request(HostHistoryRequestKindV1.FrozenBegin, 1,
            NativeTerminalHistoryCapture.LatestAnchor);
        using var selected = host.Select(session, request, recipient, request.RequestId, CancellationToken.None);
        Require(selected.Status == HostHistoryResponseStatusV1.Ok && selected.Object != null &&
            selected.FrozenHistory.StateContract == profile.StateContract &&
            selected.Object.KeyId == FrozenHistoryCrypto.KeyId(Phone, 3, profile.StateContract) &&
            selected.Object.KeyId != FrozenHistoryCrypto.KeyId(Phone, 3),
            "Unicode16 frozen response must bind both metadata and key ID to its persisted profile.");
        var plaintext = Open(selected, session, request, recipient);
        try { Require(NativeTerminalHistoryPageV2.Decode(plaintext,
            selected.FrozenHistory.CaptureId).Lines.Count > 0, "Unicode16 frozen page must contain scrollback."); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        var rejected = false;
        try
        {
            var wrongProfile = Open(selected, session, request, recipient,
                TerminalSemanticProfile.Graphics.StateContract);
            CryptographicOperations.ZeroMemory(wrongProfile);
        }
        catch (InvalidDataException) { rejected = true; }
        Require(rejected, "Unicode16 frozen page must reject the v1 semantic profile.");
    }

    private static HostHistoryRequestPayload Request(HostHistoryRequestKindV1 kind, ulong s, ulong anchor)
    {
        var request = new HostHistoryRequestPayload
        {
            RequestId = ByteString.CopyFrom(Guid.NewGuid().ToByteArray()), Kind = kind,
            SnapshotSeq = s, BeforeLineId = anchor,
            FrozenHistory = new FrozenHistoryRequest { MaxLines = 1, MaxCells = 65536,
                MaxUtf8Bytes = 4 * 1024 * 1024, PageFormatVersion = FrozenHistoryCrypto.PageFormatV2 }
        };
        request.SubscriberCapabilities.Add("host_history_frozen_v1");
        request.SubscriberCapabilities.Add("tlhp_v2");
        return request;
    }

    private static void Bind(HostHistoryRequestPayload request, FrozenHistoryMetadata metadata)
    {
        request.FrozenHistory.Token = metadata.Token;
        request.FrozenHistory.CaptureId = metadata.CaptureId;
        request.FrozenHistory.CaptureRevision = metadata.CaptureRevision;
    }

    private static byte[] Open(ProductionHostHistorySelection selected, Guid session,
        HostHistoryRequestPayload request, ProductionHistoryRecipient recipient,
        string stateContract = null) =>
        FrozenHistoryCrypto.Open(recipient.PairwiseKey, selected.Object.SealedObject,
            session, Pc, recipient.DeviceId, recipient.KeyEpoch,
            selected.FrozenHistory.Token.ToByteArray(), selected.FrozenHistory.CaptureId,
            selected.FrozenHistory.CaptureRevision, request.SnapshotSeq, request.BeforeLineId,
            request.FrozenHistory.MaxLines, request.FrozenHistory.MaxCells,
            request.FrozenHistory.MaxUtf8Bytes, request.FrozenHistory.PageFormatVersion,
            stateContract ?? selected.FrozenHistory.StateContract);

    private static void Require(bool valid, string message)
    {
        assertions++;
        if (!valid) throw new InvalidDataException(message);
    }
}
