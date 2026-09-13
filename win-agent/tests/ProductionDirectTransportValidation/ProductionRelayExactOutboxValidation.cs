using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class ProductionRelayExactOutboxValidation
    {
        internal static async Task ValidateAsync()
        {
            await ValidateOwnershipExactBytesOrderAndDurablePruneAsync().ConfigureAwait(false);
            await ValidateDedupMismatchCapacityAndPeerIsolationAsync().ConfigureAwait(false);
            await ValidateFailureReconnectAndGenerationFenceAsync().ConfigureAwait(false);
            await ValidateSessionPeerAndDisposeZeroAsync().ConfigureAwait(false);
        }

        private static async Task ValidateOwnershipExactBytesOrderAndDurablePruneAsync()
        {
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var first = Bytes(1, 37);
            var second = Bytes(2, 53);
            var expectedFirst = first.ToArray();
            var expectedSecond = second.ToArray();
            var sent = new ConcurrentQueue<Sent>();
            var senderThread = 0;
            var enqueueThread = Environment.CurrentManagedThreadId;
            await using var outbox = new ProductionRelayExactOutbox(retryDelay: TimeSpan.FromMilliseconds(5));

            Require(outbox.CanAccept(Envelope(peer, session, 1, first)),
                "The empty outbox rejected a valid ownership preflight.");
            Require(outbox.TryEnqueue(Envelope(peer, session, 1, first)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred &&
                outbox.TryEnqueue(Envelope(peer, session, 2, second)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred,
                "A successful enqueue did not report deferred ownership transfer.");
            Require(sent.IsEmpty,
                "Enqueue touched the Relay sender instead of leaving it to the flush worker.");
            Require(outbox.ObserveRelayConnected(41, (request, _) =>
            {
                senderThread = Environment.CurrentManagedThreadId;
                sent.Enqueue(new Sent(
                    request.RelayGeneration,
                    request.PeerDeviceId,
                    request.SessionId,
                    request.EnvelopeSequence,
                    request.ExactEnvelope.ToArray()));
                return Task.FromResult(true);
            }), "The initial Relay generation was not installed.");

            await WaitUntilAsync(() => sent.Count == 2).ConfigureAwait(false);
            Require(senderThread != enqueueThread,
                "The caller thread invoked the Relay socket sender.");
            var delivered = sent.ToArray();
            Require(delivered.Select(value => value.Sequence).SequenceEqual(new ulong[] { 1, 2 }) &&
                delivered[0].Generation == 41 && delivered[1].Generation == 41 &&
                delivered[0].Peer == peer && delivered[1].Session == session &&
                delivered[0].Bytes.SequenceEqual(expectedFirst) &&
                delivered[1].Bytes.SequenceEqual(expectedSecond),
                "The flush worker changed exact bytes, metadata, or per-peer queue order.");
            var retained = outbox.Snapshot().Single();
            Require(retained.PendingFrames == 2 &&
                retained.PendingBytes == first.Length + second.Length &&
                retained.SentInCurrentGeneration == 2,
                "Transport send removed durable-ACK-owned Relay entries.");
            var replay = expectedSecond.ToArray();
            Require(outbox.TryEnqueue(Envelope(peer, session, 2, replay)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred && IsZero(replay),
                "An exact cached replay was not deduplicated and rescheduled.");
            await WaitUntilAsync(() => sent.Count == 3).ConfigureAwait(false);
            delivered = sent.ToArray();
            Require(delivered[2].Sequence == 2 &&
                delivered[2].Bytes.SequenceEqual(expectedSecond),
                "An exact cached replay did not resend in the active Relay generation.");

            Require(outbox.ObserveTransportReceipt(peer, session, 1) &&
                outbox.Snapshot().Single().PendingFrames == 2 &&
                !IsZero(first),
                "A transport receipt was incorrectly treated as a durable ACK.");
            Require(outbox.PruneDurablyAcknowledged(peer, session, 1) == 1 && IsZero(first) &&
                !IsZero(second),
                "A durable ACK did not prune and zero only its contiguous entry.");
            Require(outbox.PruneDurablyAcknowledged(peer, session, 2) == 1 && IsZero(second) &&
                outbox.Snapshot().Count == 0,
                "The final durable ACK did not clear its exact-byte storage.");
            Zero(expectedFirst, expectedSecond);
            foreach (var value in delivered) CryptographicOperations.ZeroMemory(value.Bytes);
        }

        private static async Task ValidateDedupMismatchCapacityAndPeerIsolationAsync()
        {
            var peerA = Guid.NewGuid();
            var peerB = Guid.NewGuid();
            var session = Guid.NewGuid();
            var owned = Bytes(10, 4);
            var duplicate = owned.ToArray();
            var mismatch = Bytes(99, 4);
            var mismatchBefore = mismatch.ToArray();
            var secondA = Bytes(20, 4);
            var rejectedA = Bytes(30, 4);
            var acceptedB = Bytes(40, 4);
            await using var outbox = new ProductionRelayExactOutbox(
                frameCapacityPerPeer: 2,
                byteCapacityPerPeer: 8,
                retryDelay: TimeSpan.FromMilliseconds(5));
            Require(!outbox.CanAccept(Guid.NewGuid(), 9),
                "Capacity preflight accepted an oversized first peer entry.");

            Require(outbox.TryEnqueue(Envelope(peerA, session, 1, owned)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred,
                "The first idempotency key was rejected.");
            Require(outbox.TryEnqueue(Envelope(peerA, session, 1, duplicate)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred && IsZero(duplicate) &&
                outbox.Snapshot().Single().PendingFrames == 1,
                "An identical duplicate was stored twice or not safely consumed.");

            var mismatchClosed = false;
            try { outbox.TryEnqueue(Envelope(peerA, session, 1, mismatch)); }
            catch (InvalidOperationException) { mismatchClosed = true; }
            Require(mismatchClosed && mismatch.SequenceEqual(mismatchBefore),
                "A same-key/different-byte collision did not fail closed as caller-owned.");

            Require(outbox.TryEnqueue(Envelope(peerA, session, 2, secondA)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred &&
                !outbox.CanAccept(peerA, 1) &&
                outbox.TryEnqueue(Envelope(peerA, session, 3, rejectedA)) ==
                    ProductionRelayExactEnqueueOutcome.RejectedNotOwned &&
                rejectedA.SequenceEqual(Bytes(30, 4)),
                "A full peer queue took ownership or changed rejected bytes.");
            Require(outbox.CanAccept(peerB, acceptedB.Length) &&
                outbox.TryEnqueue(Envelope(peerB, session, 1, acceptedB)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred,
                "One saturated peer applied backpressure to an isolated peer.");
            Require(outbox.Snapshot().OrderBy(value => value.PendingFrames)
                    .Select(value => value.PendingFrames).SequenceEqual(new[] { 1, 2 }) &&
                ProductionRelayExactOutbox.MaximumFramesPerPeer == 256 &&
                ProductionRelayExactOutbox.MaximumBytesPerPeer == 8L * 1024 * 1024,
                "Per-peer bounds or isolation are not the production contract.");

            Zero(mismatch, mismatchBefore, rejectedA);
        }

        private static async Task ValidateFailureReconnectAndGenerationFenceAsync()
        {
            var peer = Guid.NewGuid();
            var session = Guid.NewGuid();
            var first = Bytes(50, 17);
            var second = Bytes(60, 19);
            var generationTenCall = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var generationEleven = new ConcurrentQueue<Sent>();
            await using var outbox = new ProductionRelayExactOutbox(retryDelay: TimeSpan.FromSeconds(2));
            Require(outbox.TryEnqueue(Envelope(peer, session, 1, first)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred &&
                outbox.TryEnqueue(Envelope(peer, session, 2, second)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred,
                "Reconnect validation could not populate the queue.");
            outbox.ObserveRelayConnected(10, (request, _) =>
            {
                generationTenCall.TrySetResult(true);
                return Task.FromResult(false);
            });
            await generationTenCall.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Require(outbox.ObserveRelayDisconnected(10),
                "The failed Relay generation did not disconnect.");
            Require(outbox.Snapshot().Single().PendingFrames == 2,
                "A send failure removed the peer queue head.");
            Require(outbox.ObserveRelayConnected(11, (request, _) =>
            {
                generationEleven.Enqueue(Snapshot(request));
                return Task.FromResult(true);
            }), "The replacement Relay generation was not installed.");
            await WaitUntilAsync(() => generationEleven.Count == 2).ConfigureAwait(false);
            Require(generationEleven.Select(value => value.Sequence)
                    .SequenceEqual(new ulong[] { 1, 2 }) &&
                generationEleven.All(value => value.Generation == 11),
                "Reconnect did not restart the retained queue in original order.");

            Require(outbox.ObserveRelayDisconnected(11),
                "The successful generation did not disconnect for fence validation.");
            var third = Bytes(70, 23);
            Require(outbox.TryEnqueue(Envelope(peer, session, 3, third)) ==
                    ProductionRelayExactEnqueueOutcome.OwnedDeferred,
                "The generation-fence entry was rejected.");
            var oldStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var currentGeneration = new ConcurrentQueue<Sent>();
            outbox.ObserveRelayConnected(20, async (request, _) =>
            {
                // Generations 1 and 2 are already durable-outbox entries and
                // therefore replay first. Block only the newly queued entry.
                if (request.EnvelopeSequence == 3)
                {
                    oldStarted.TrySetResult(true);
                    await releaseOld.Task.ConfigureAwait(false);
                }
                return true;
            });
            await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Require(outbox.ObserveRelayConnected(21, (request, _) =>
            {
                currentGeneration.Enqueue(Snapshot(request));
                return Task.FromResult(true);
            }), "A newer Relay generation did not fence the in-flight sender.");
            releaseOld.TrySetResult(true);
            await WaitUntilAsync(() => currentGeneration.Any(value => value.Sequence == 3))
                .ConfigureAwait(false);
            Require(currentGeneration.Any(value => value.Sequence == 3 && value.Generation == 21) &&
                outbox.Snapshot().Single().SentInCurrentGeneration == 3,
                "An old generation result incorrectly marked a new generation item sent.");
            Require(!outbox.ObserveRelayDisconnected(20),
                "A stale Relay disconnect replaced the current generation.");

            foreach (var value in generationEleven.Concat(currentGeneration))
                CryptographicOperations.ZeroMemory(value.Bytes);
        }

        private static async Task ValidateSessionPeerAndDisposeZeroAsync()
        {
            var peerA = Guid.NewGuid();
            var peerB = Guid.NewGuid();
            var sessionA = Guid.NewGuid();
            var sessionB = Guid.NewGuid();
            var sessionABytes = Bytes(80, 11);
            var sessionBBytes = Bytes(90, 13);
            var peerBBytes = Bytes(100, 15);
            var disposeBytes = Bytes(110, 21);
            var outbox = new ProductionRelayExactOutbox(retryDelay: TimeSpan.FromMilliseconds(5));
            outbox.TryEnqueue(Envelope(peerA, sessionA, 1, sessionABytes));
            outbox.TryEnqueue(Envelope(peerA, sessionB, 1, sessionBBytes));
            outbox.TryEnqueue(Envelope(peerB, sessionA, 1, peerBBytes));
            Require(outbox.PruneSession(peerA, sessionA) == 1 && IsZero(sessionABytes) &&
                !IsZero(sessionBBytes) && !IsZero(peerBBytes),
                "Session prune crossed its peer/session boundary.");
            Require(outbox.PrunePeer(peerA) == 1 && IsZero(sessionBBytes) && !IsZero(peerBBytes),
                "Peer prune crossed into another peer.");
            outbox.TryEnqueue(Envelope(peerB, sessionB, 1, disposeBytes));
            await outbox.DisposeAsync().ConfigureAwait(false);
            Require(IsZero(peerBBytes) && IsZero(disposeBytes),
                "Account/outbox disposal did not zero all remaining owned exact bytes.");
        }

        private static ProductionDirectDataRelayEnvelope Envelope(
            Guid peer,
            Guid session,
            ulong sequence,
            byte[] exact) => new()
        {
            PeerDeviceId = peer,
            SessionId = session,
            EnvelopeSequence = sequence,
            ExactEnvelope = exact
        };

        private static Sent Snapshot(ProductionRelayExactSendRequest request) => new(
            request.RelayGeneration,
            request.PeerDeviceId,
            request.SessionId,
            request.EnvelopeSequence,
            request.ExactEnvelope.ToArray());

        private static byte[] Bytes(byte seed, int length) =>
            Enumerable.Range(0, length).Select(index => unchecked((byte)(seed + index))).ToArray();

        private static bool IsZero(byte[] value) => value.All(item => item == 0);

        private static void Zero(params byte[][] values)
        {
            foreach (var value in values)
                if (value != null) CryptographicOperations.ZeroMemory(value);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!predicate())
                await Task.Delay(10, timeout.Token).ConfigureAwait(false);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private sealed class Sent
        {
            internal Sent(long generation, Guid peer, Guid session, ulong sequence, byte[] bytes)
            {
                Generation = generation;
                Peer = peer;
                Session = session;
                Sequence = sequence;
                Bytes = bytes;
            }

            internal long Generation { get; }
            internal Guid Peer { get; }
            internal Guid Session { get; }
            internal ulong Sequence { get; }
            internal byte[] Bytes { get; }
        }
    }
}
