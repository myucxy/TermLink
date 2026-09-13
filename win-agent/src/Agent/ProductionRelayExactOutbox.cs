using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    /// <summary>
    /// Reports ownership independently from network delivery. The production
    /// outbox sends only from its worker, so a successful enqueue normally
    /// returns <see cref="OwnedDeferred"/>.
    /// </summary>
    internal enum ProductionRelayExactEnqueueOutcome
    {
        RejectedNotOwned,
        OwnedDeferred,
        OwnedSent
    }

    /// <summary>
    /// A borrowed view used only for the duration of one sender invocation.
    /// The sender must not retain the request or its ExactEnvelope memory.
    /// </summary>
    internal sealed class ProductionRelayExactSendRequest
    {
        internal long RelayGeneration { get; init; }
        internal Guid PeerDeviceId { get; init; }
        internal Guid SessionId { get; init; }
        internal ulong EnvelopeSequence { get; init; }
        internal ReadOnlyMemory<byte> ExactEnvelope { get; init; }
    }

    internal sealed class ProductionRelayExactOutboxPeerSnapshot
    {
        internal Guid PeerDeviceId { get; init; }
        internal int PendingFrames { get; init; }
        internal long PendingBytes { get; init; }
        internal int SentInCurrentGeneration { get; init; }
        internal bool RelayConnected { get; init; }
        internal long RelayGeneration { get; init; }
    }

    /// <summary>
    /// Relay-owned, bounded, exact-byte retry storage. Enqueue is synchronous;
    /// only the single flush worker can invoke the active Relay socket sender.
    /// Successful sends remain owned until an application-level durable ACK or
    /// an explicit prune. Transport delivery is never treated as a durable ACK.
    /// </summary>
    internal sealed class ProductionRelayExactOutbox : IAsyncDisposable
    {
        internal const int MaximumFramesPerPeer = 256;
        internal const long MaximumBytesPerPeer = 8L * 1024 * 1024;

        private readonly object sync = new();
        private readonly Dictionary<Guid, PeerQueue> peers = new();
        private readonly SemaphoreSlim wake = new(0, 1);
        private readonly CancellationTokenSource lifetime = new();
        private readonly int frameCapacityPerPeer;
        private readonly long byteCapacityPerPeer;
        private readonly TimeSpan retryDelay;
        private readonly Task flushWorker;
        private Func<ProductionRelayExactSendRequest, CancellationToken, Task<bool>> relaySender;
        private CancellationTokenSource relayConnectionCancellation;
        private long relayGeneration;
        private long relayConnectionEpoch;
        private bool relayConnected;
        private int wakePending;
        private int disposed;

        internal ProductionRelayExactOutbox(
            int frameCapacityPerPeer = MaximumFramesPerPeer,
            long byteCapacityPerPeer = MaximumBytesPerPeer,
            TimeSpan? retryDelay = null)
        {
            if (frameCapacityPerPeer is < 1 or > MaximumFramesPerPeer)
                throw new ArgumentOutOfRangeException(nameof(frameCapacityPerPeer));
            if (byteCapacityPerPeer is < 1 or > MaximumBytesPerPeer)
                throw new ArgumentOutOfRangeException(nameof(byteCapacityPerPeer));
            var effectiveRetryDelay = retryDelay ?? TimeSpan.FromMilliseconds(250);
            if (effectiveRetryDelay < TimeSpan.Zero || effectiveRetryDelay > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(retryDelay));
            this.frameCapacityPerPeer = frameCapacityPerPeer;
            this.byteCapacityPerPeer = byteCapacityPerPeer;
            this.retryDelay = effectiveRetryDelay;
            flushWorker = FlushWorkerAsync(lifetime.Token);
        }

        /// <summary>
        /// Capacity preflight only. A successful result does not reserve space;
        /// callers must still handle RejectedNotOwned from TryEnqueue.
        /// </summary>
        internal bool CanAccept(Guid peerDeviceId, int exactEnvelopeBytes)
        {
            if (peerDeviceId == Guid.Empty || exactEnvelopeBytes < 1 ||
                exactEnvelopeBytes > byteCapacityPerPeer)
                return false;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0) return false;
                return !peers.TryGetValue(peerDeviceId, out var peer) ||
                    peer.PendingFrames < frameCapacityPerPeer &&
                    peer.PendingBytes <= byteCapacityPerPeer - (long)exactEnvelopeBytes;
            }
        }

        /// <summary>
        /// Exact preflight that also recognizes an already-owned idempotent
        /// duplicate. A same-key/different-byte Envelope is never acceptable.
        /// </summary>
        internal bool CanAccept(ProductionDirectDataRelayEnvelope envelope)
        {
            if (!HasValidIdentity(envelope)) return false;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0) return false;
                if (!peers.TryGetValue(envelope.PeerDeviceId, out var peer))
                    return envelope.ExactEnvelope.LongLength <= byteCapacityPerPeer;
                var key = new EnvelopeKey(envelope.SessionId, envelope.EnvelopeSequence);
                if (peer.ByKey.TryGetValue(key, out var existing))
                    return ExactEquals(existing.ExactEnvelope, envelope.ExactEnvelope);
                if (peer.DurableAcks.TryGetValue(envelope.SessionId, out var acknowledged) &&
                    envelope.EnvelopeSequence <= acknowledged)
                    return true;
                return peer.PendingFrames < frameCapacityPerPeer &&
                    peer.PendingBytes <= byteCapacityPerPeer - envelope.ExactEnvelope.LongLength;
            }
        }

        /// <summary>
        /// Transfers ownership only when OwnedDeferred/OwnedSent is returned.
        /// RejectedNotOwned and exceptions leave the byte array caller-owned.
        /// </summary>
        internal ProductionRelayExactEnqueueOutcome TryEnqueue(
            ProductionDirectDataRelayEnvelope envelope)
        {
            if (!HasValidIdentity(envelope))
                throw new ArgumentException(
                    "Relay Envelope 标识无效。/ Relay Envelope identity is invalid.",
                    nameof(envelope));
            ThrowIfDisposed();
            var accepted = false;
            var shouldWake = false;
            lock (sync)
            {
                ThrowIfDisposed();
                if (!peers.TryGetValue(envelope.PeerDeviceId, out var peer))
                {
                    peer = new PeerQueue(envelope.PeerDeviceId);
                    peers.Add(envelope.PeerDeviceId, peer);
                }
                var key = new EnvelopeKey(envelope.SessionId, envelope.EnvelopeSequence);
                if (peer.ByKey.TryGetValue(key, out var existing))
                {
                    if (!ExactEquals(existing.ExactEnvelope, envelope.ExactEnvelope))
                        throw new InvalidOperationException(
                            "Relay Envelope 的相同键对应了不同字节，已拒绝发送。/ " +
                            "The same Relay Envelope key contained different bytes; sending was rejected.");
                    accepted = true;
                    if (!ReferenceEquals(existing.ExactEnvelope, envelope.ExactEnvelope))
                        CryptographicOperations.ZeroMemory(envelope.ExactEnvelope);
                    // The durable publisher can explicitly replay the same
                    // exact key after SUBSCRIBE/flow recovery. Keep one stored
                    // frame, but schedule another transport attempt even in
                    // the same Relay generation.
                    existing.ResendVersion = existing.ResendVersion == long.MaxValue
                        ? 1
                        : existing.ResendVersion + 1;
                    existing.LastSentConnectionEpoch = 0;
                    if (relayConnected) SignalWorker();
                    return ProductionRelayExactEnqueueOutcome.OwnedDeferred;
                }
                if (peer.DurableAcks.TryGetValue(envelope.SessionId, out var acknowledged) &&
                    envelope.EnvelopeSequence <= acknowledged)
                {
                    // The caller transferred a stale retry after its durable
                    // application ACK won the race. Consume and clear it.
                    accepted = true;
                    CryptographicOperations.ZeroMemory(envelope.ExactEnvelope);
                    return ProductionRelayExactEnqueueOutcome.OwnedDeferred;
                }
                if (peer.PendingFrames >= frameCapacityPerPeer ||
                    peer.PendingBytes > byteCapacityPerPeer - envelope.ExactEnvelope.LongLength)
                    return ProductionRelayExactEnqueueOutcome.RejectedNotOwned;

                var entry = new Entry(
                    envelope.PeerDeviceId,
                    envelope.SessionId,
                    envelope.EnvelopeSequence,
                    envelope.ExactEnvelope);
                entry.Node = peer.Ordered.AddLast(entry);
                peer.ByKey.Add(key, entry);
                peer.PendingFrames++;
                peer.PendingBytes += envelope.ExactEnvelope.LongLength;
                accepted = true;
                shouldWake = relayConnected;
            }
            if (shouldWake) SignalWorker();
            // The flush worker is the only sender and is deliberately
            // asynchronous with respect to this ownership transfer.
            return accepted
                ? ProductionRelayExactEnqueueOutcome.OwnedDeferred
                : ProductionRelayExactEnqueueOutcome.RejectedNotOwned;
        }

        /// <summary>
        /// Installs a generation-fenced sender. Re-observing an already active
        /// generation is idempotent and cannot trigger duplicate replay.
        /// </summary>
        internal bool ObserveRelayConnected(
            long generation,
            Func<ProductionRelayExactSendRequest, CancellationToken, Task<bool>> sender)
        {
            if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
            _ = sender ?? throw new ArgumentNullException(nameof(sender));
            if (Volatile.Read(ref disposed) != 0) return false;
            CancellationTokenSource previous = null;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0 || generation < relayGeneration) return false;
                if (relayConnected && generation == relayGeneration) return false;
                previous = relayConnectionCancellation;
                relayConnectionCancellation = new CancellationTokenSource();
                relayGeneration = generation;
                relayConnectionEpoch = checked(relayConnectionEpoch + 1);
                relayConnected = true;
                relaySender = sender;
            }
            CancelAndDispose(previous);
            SignalWorker();
            return true;
        }

        internal bool ObserveRelayDisconnected(long generation)
        {
            if (generation < 1) throw new ArgumentOutOfRangeException(nameof(generation));
            if (Volatile.Read(ref disposed) != 0) return false;
            CancellationTokenSource previous = null;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0 || generation < relayGeneration ||
                    !relayConnected && generation == relayGeneration)
                    return false;
                previous = relayConnectionCancellation;
                relayConnectionCancellation = null;
                relayGeneration = generation;
                relayConnected = false;
                relaySender = null;
            }
            CancelAndDispose(previous);
            SignalWorker();
            return true;
        }

        /// <summary>
        /// Records a transport-layer receipt without changing ownership or
        /// pruning. Only PruneDurablyAcknowledged may apply a durable cursor.
        /// </summary>
        internal bool ObserveTransportReceipt(
            Guid peerDeviceId,
            Guid sessionId,
            ulong envelopeSequence)
        {
            if (peerDeviceId == Guid.Empty || sessionId == Guid.Empty || envelopeSequence == 0)
                return false;
            lock (sync)
            {
                return Volatile.Read(ref disposed) == 0 &&
                    peers.TryGetValue(peerDeviceId, out var peer) &&
                    peer.ByKey.ContainsKey(new EnvelopeKey(sessionId, envelopeSequence));
            }
        }

        internal int PruneDurablyAcknowledged(
            Guid peerDeviceId,
            Guid sessionId,
            ulong highestContiguousSequence)
        {
            if (peerDeviceId == Guid.Empty || sessionId == Guid.Empty)
                throw new ArgumentException(
                    "Relay ACK 标识无效。/ Relay ACK identity is invalid.");
            if (Volatile.Read(ref disposed) != 0) return 0;
            List<byte[]> clear = null;
            var removed = 0;
            lock (sync)
            {
                if (Volatile.Read(ref disposed) != 0) return 0;
                if (!peers.TryGetValue(peerDeviceId, out var peer))
                {
                    peer = new PeerQueue(peerDeviceId);
                    peers.Add(peerDeviceId, peer);
                }
                if (!peer.DurableAcks.TryGetValue(sessionId, out var previous) ||
                    highestContiguousSequence > previous)
                    peer.DurableAcks[sessionId] = highestContiguousSequence;
                else
                    highestContiguousSequence = previous;
                var node = peer.Ordered.First;
                while (node != null)
                {
                    var next = node.Next;
                    var entry = node.Value;
                    if (entry.SessionId == sessionId &&
                        entry.EnvelopeSequence <= highestContiguousSequence)
                    {
                        RemoveEntryLocked(peer, entry, ref clear);
                        removed++;
                    }
                    node = next;
                }
            }
            ZeroAll(clear);
            return removed;
        }

        internal int PruneSession(Guid peerDeviceId, Guid sessionId)
        {
            if (peerDeviceId == Guid.Empty || sessionId == Guid.Empty) return 0;
            return PruneWhere(peerDeviceId, entry => entry.SessionId == sessionId, clearAckSession: sessionId);
        }

        internal int PrunePeer(Guid peerDeviceId)
        {
            if (peerDeviceId == Guid.Empty || Volatile.Read(ref disposed) != 0) return 0;
            List<byte[]> clear = null;
            var removed = 0;
            lock (sync)
            {
                if (!peers.Remove(peerDeviceId, out var peer)) return 0;
                foreach (var entry in peer.Ordered)
                {
                    entry.Removed = true;
                    removed++;
                    if (!entry.InFlight) (clear ??= new List<byte[]>()).Add(entry.ExactEnvelope);
                }
                peer.Ordered.Clear();
                peer.ByKey.Clear();
                peer.DurableAcks.Clear();
                peer.PendingFrames = 0;
                peer.PendingBytes = 0;
            }
            ZeroAll(clear);
            return removed;
        }

        internal IReadOnlyList<ProductionRelayExactOutboxPeerSnapshot> Snapshot()
        {
            lock (sync)
            {
                return peers.Values
                    .Where(peer => peer.PendingFrames > 0)
                    .OrderBy(peer => peer.PeerDeviceId)
                    .Select(peer => new ProductionRelayExactOutboxPeerSnapshot
                    {
                        PeerDeviceId = peer.PeerDeviceId,
                        PendingFrames = peer.PendingFrames,
                        PendingBytes = peer.PendingBytes,
                        SentInCurrentGeneration = relayConnected
                            ? peer.Ordered.Count(entry =>
                                !entry.Removed && entry.LastSentConnectionEpoch == relayConnectionEpoch)
                            : 0,
                        RelayConnected = relayConnected,
                        RelayGeneration = relayGeneration
                    })
                    .ToArray();
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            CancellationTokenSource connection;
            lock (sync)
            {
                connection = relayConnectionCancellation;
                relayConnectionCancellation = null;
                relayConnected = false;
                relaySender = null;
            }
            CancelAndDispose(connection);
            lifetime.Cancel();
            SignalWorker();
            try { await flushWorker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }

            var clear = new List<byte[]>();
            lock (sync)
            {
                foreach (var peer in peers.Values)
                foreach (var entry in peer.Ordered)
                {
                    entry.Removed = true;
                    clear.Add(entry.ExactEnvelope);
                }
                peers.Clear();
            }
            ZeroAll(clear);
            lifetime.Dispose();
            wake.Dispose();
        }

        private async Task FlushWorkerAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try { await wake.WaitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                Interlocked.Exchange(ref wakePending, 0);

                while (!token.IsCancellationRequested)
                {
                    var batch = TakeSendBatch();
                    if (batch.Count == 0) break;
                    var failed = false;
                    CancellationToken retryToken = token;
                    foreach (var candidate in batch)
                    {
                        Func<ProductionRelayExactSendRequest, CancellationToken, Task<bool>> sender;
                        CancellationToken connectionToken;
                        if (!TryBeginSend(candidate, out sender, out connectionToken))
                        {
                            ReleaseSkippedCandidate(candidate);
                            continue;
                        }
                        retryToken = connectionToken;
                        var sent = false;
                        try
                        {
                            sent = await sender(new ProductionRelayExactSendRequest
                            {
                                RelayGeneration = candidate.RelayGeneration,
                                PeerDeviceId = candidate.Entry.PeerDeviceId,
                                SessionId = candidate.Entry.SessionId,
                                EnvelopeSequence = candidate.Entry.EnvelopeSequence,
                                ExactEnvelope = candidate.Entry.ExactEnvelope
                            }, connectionToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (connectionToken.IsCancellationRequested) { }
                        catch (Exception error) when (error is not OutOfMemoryException &&
                            error is not StackOverflowException && error is not AccessViolationException)
                        {
                            sent = false;
                        }
                        CompleteSend(candidate, sent);
                        if (!sent && !connectionToken.IsCancellationRequested) failed = true;
                    }
                    if (failed && retryDelay > TimeSpan.Zero)
                    {
                        try { await Task.Delay(retryDelay, retryToken).ConfigureAwait(false); }
                        catch (OperationCanceledException) { }
                    }
                }
            }
        }

        private List<SendCandidate> TakeSendBatch()
        {
            lock (sync)
            {
                var result = new List<SendCandidate>();
                if (!relayConnected || relaySender == null || relayConnectionCancellation == null)
                    return result;
                foreach (var peer in peers.Values.OrderBy(value => value.PeerDeviceId))
                {
                    Entry selected = null;
                    foreach (var entry in peer.Ordered)
                    {
                        if (entry.Removed) continue;
                        if (entry.InFlight) break;
                        if (entry.LastSentConnectionEpoch == relayConnectionEpoch) continue;
                        selected = entry;
                        break;
                    }
                    if (selected == null) continue;
                    selected.InFlight = true;
                    result.Add(new SendCandidate(
                        selected,
                        relayGeneration,
                        relayConnectionEpoch,
                        selected.ResendVersion,
                        relaySender,
                        relayConnectionCancellation.Token));
                }
                return result;
            }
        }

        private bool TryBeginSend(
            SendCandidate candidate,
            out Func<ProductionRelayExactSendRequest, CancellationToken, Task<bool>> sender,
            out CancellationToken connectionToken)
        {
            lock (sync)
            {
                sender = candidate.Sender;
                connectionToken = candidate.ConnectionToken;
                return !candidate.Entry.Removed && candidate.Entry.InFlight && relayConnected &&
                    relayConnectionEpoch == candidate.ConnectionEpoch &&
                    relayGeneration == candidate.RelayGeneration &&
                    ReferenceEquals(relaySender, candidate.Sender) &&
                    !candidate.ConnectionToken.IsCancellationRequested;
            }
        }

        private void CompleteSend(SendCandidate candidate, bool sent)
        {
            byte[] clear = null;
            lock (sync)
            {
                candidate.Entry.InFlight = false;
                if (candidate.Entry.Removed)
                    clear = candidate.Entry.ExactEnvelope;
                else if (sent && relayConnected &&
                    relayConnectionEpoch == candidate.ConnectionEpoch &&
                    relayGeneration == candidate.RelayGeneration &&
                    ReferenceEquals(relaySender, candidate.Sender) &&
                    candidate.ResendVersion == candidate.Entry.ResendVersion)
                    candidate.Entry.LastSentConnectionEpoch = candidate.ConnectionEpoch;
            }
            if (clear != null) CryptographicOperations.ZeroMemory(clear);
        }

        private void ReleaseSkippedCandidate(SendCandidate candidate)
        {
            byte[] clear = null;
            lock (sync)
            {
                candidate.Entry.InFlight = false;
                if (candidate.Entry.Removed) clear = candidate.Entry.ExactEnvelope;
            }
            if (clear != null) CryptographicOperations.ZeroMemory(clear);
        }

        private int PruneWhere(Guid peerDeviceId, Func<Entry, bool> predicate, Guid? clearAckSession)
        {
            if (Volatile.Read(ref disposed) != 0) return 0;
            List<byte[]> clear = null;
            var removed = 0;
            lock (sync)
            {
                if (!peers.TryGetValue(peerDeviceId, out var peer)) return 0;
                var node = peer.Ordered.First;
                while (node != null)
                {
                    var next = node.Next;
                    if (predicate(node.Value))
                    {
                        RemoveEntryLocked(peer, node.Value, ref clear);
                        removed++;
                    }
                    node = next;
                }
                if (clearAckSession.HasValue)
                    peer.DurableAcks.Remove(clearAckSession.Value);
            }
            ZeroAll(clear);
            return removed;
        }

        private static void RemoveEntryLocked(PeerQueue peer, Entry entry, ref List<byte[]> clear)
        {
            if (entry.Removed) return;
            entry.Removed = true;
            if (entry.Node?.List != null) peer.Ordered.Remove(entry.Node);
            peer.ByKey.Remove(new EnvelopeKey(entry.SessionId, entry.EnvelopeSequence));
            peer.PendingFrames--;
            peer.PendingBytes -= entry.ExactEnvelope.LongLength;
            if (!entry.InFlight) (clear ??= new List<byte[]>()).Add(entry.ExactEnvelope);
        }

        private void SignalWorker()
        {
            if (Volatile.Read(ref disposed) != 0 && !lifetime.IsCancellationRequested) return;
            if (Interlocked.Exchange(ref wakePending, 1) != 0) return;
            try { wake.Release(); }
            catch (ObjectDisposedException) { }
            catch (SemaphoreFullException) { }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(ProductionRelayExactOutbox));
        }

        private static bool HasValidIdentity(ProductionDirectDataRelayEnvelope envelope) =>
            envelope != null && envelope.PeerDeviceId != Guid.Empty &&
            envelope.SessionId != Guid.Empty && envelope.EnvelopeSequence > 0 &&
            envelope.ExactEnvelope is { Length: > 0 };

        private static bool ExactEquals(byte[] left, byte[] right) =>
            left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);

        private static void ZeroAll(IEnumerable<byte[]> values)
        {
            if (values == null) return;
            foreach (var value in values)
                if (value != null) CryptographicOperations.ZeroMemory(value);
        }

        private static void CancelAndDispose(CancellationTokenSource source)
        {
            if (source == null) return;
            try { source.Cancel(); }
            catch (ObjectDisposedException) { }
            source.Dispose();
        }

        private readonly struct EnvelopeKey : IEquatable<EnvelopeKey>
        {
            internal EnvelopeKey(Guid sessionId, ulong sequence)
            {
                SessionId = sessionId;
                Sequence = sequence;
            }

            private Guid SessionId { get; }
            private ulong Sequence { get; }
            public bool Equals(EnvelopeKey other) =>
                SessionId == other.SessionId && Sequence == other.Sequence;
            public override bool Equals(object value) => value is EnvelopeKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(SessionId, Sequence);
        }

        private sealed class PeerQueue
        {
            internal PeerQueue(Guid peerDeviceId) => PeerDeviceId = peerDeviceId;
            internal Guid PeerDeviceId { get; }
            internal LinkedList<Entry> Ordered { get; } = new();
            internal Dictionary<EnvelopeKey, Entry> ByKey { get; } = new();
            internal Dictionary<Guid, ulong> DurableAcks { get; } = new();
            internal int PendingFrames { get; set; }
            internal long PendingBytes { get; set; }
        }

        private sealed class Entry
        {
            internal Entry(Guid peerDeviceId, Guid sessionId, ulong sequence, byte[] exactEnvelope)
            {
                PeerDeviceId = peerDeviceId;
                SessionId = sessionId;
                EnvelopeSequence = sequence;
                ExactEnvelope = exactEnvelope;
            }

            internal Guid PeerDeviceId { get; }
            internal Guid SessionId { get; }
            internal ulong EnvelopeSequence { get; }
            internal byte[] ExactEnvelope { get; }
            internal LinkedListNode<Entry> Node { get; set; }
            internal long LastSentConnectionEpoch { get; set; }
            internal long ResendVersion { get; set; } = 1;
            internal bool InFlight { get; set; }
            internal bool Removed { get; set; }
        }

        private sealed class SendCandidate
        {
            internal SendCandidate(
                Entry entry,
                long relayGeneration,
                long connectionEpoch,
                long resendVersion,
                Func<ProductionRelayExactSendRequest, CancellationToken, Task<bool>> sender,
                CancellationToken connectionToken)
            {
                Entry = entry;
                RelayGeneration = relayGeneration;
                ConnectionEpoch = connectionEpoch;
                ResendVersion = resendVersion;
                Sender = sender;
                ConnectionToken = connectionToken;
            }

            internal Entry Entry { get; }
            internal long RelayGeneration { get; }
            internal long ConnectionEpoch { get; }
            internal long ResendVersion { get; }
            internal Func<ProductionRelayExactSendRequest, CancellationToken, Task<bool>> Sender { get; }
            internal CancellationToken ConnectionToken { get; }
        }
    }
}
