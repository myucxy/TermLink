using System;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    /// <summary>
    /// Owns one Host History response stream. Cancellation is deliberately
    /// separate from disposal: callers signal cancellation, while the serving
    /// task performs the final Dispose after it has stopped using the token.
    /// </summary>
    internal sealed class ProductionHostHistoryResponseRequest : IDisposable
    {
        private readonly object sync = new object();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly CancellationToken cancellationToken;
        private readonly TaskCompletionSource<bool> completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<bool> chunkAcknowledgement;
        private Guid expectedObjectId;
        private uint expectedChunkIndex;
        private bool cancelRequested;
        private bool disposed;

        public ProductionHostHistoryResponseRequest(Guid sessionId, Guid deviceId)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            if (deviceId == Guid.Empty) throw new ArgumentException("Device ID is required.", nameof(deviceId));
            SessionId = sessionId;
            DeviceId = deviceId;
            cancellationToken = cancellation.Token;
        }

        public Guid SessionId { get; }
        public Guid DeviceId { get; }
        public CancellationToken CancellationToken => cancellationToken;
        public Task Completion => completion.Task;

        public Task PrepareChunkAcknowledgement(Guid objectId, uint chunkIndex)
        {
            if (objectId == Guid.Empty) throw new ArgumentException("Object ID is required.", nameof(objectId));
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(ProductionHostHistoryResponseRequest));
                if (cancelRequested) throw new OperationCanceledException(cancellationToken);
                if (chunkAcknowledgement != null)
                    throw new InvalidOperationException("A host history chunk acknowledgement is already pending.");
                expectedObjectId = objectId;
                expectedChunkIndex = chunkIndex;
                chunkAcknowledgement = new TaskCompletionSource<bool>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return chunkAcknowledgement.Task;
            }
        }

        public bool Acknowledge(Guid objectId, uint chunkIndex)
        {
            TaskCompletionSource<bool> pending;
            lock (sync)
            {
                if (disposed || cancelRequested || chunkAcknowledgement == null ||
                    objectId != expectedObjectId || chunkIndex != expectedChunkIndex)
                {
                    return false;
                }
                pending = chunkAcknowledgement;
                chunkAcknowledgement = null;
                expectedObjectId = Guid.Empty;
                expectedChunkIndex = 0;
            }
            return pending.TrySetResult(true);
        }

        public void Cancel()
        {
            TaskCompletionSource<bool> pending;
            lock (sync)
            {
                if (disposed || cancelRequested) return;
                cancelRequested = true;
                pending = chunkAcknowledgement;
                chunkAcknowledgement = null;
                expectedObjectId = Guid.Empty;
                expectedChunkIndex = 0;
            }
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            pending?.TrySetCanceled(cancellationToken);
        }

        public void Dispose()
        {
            TaskCompletionSource<bool> pending;
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                cancelRequested = true;
                pending = chunkAcknowledgement;
                chunkAcknowledgement = null;
                expectedObjectId = Guid.Empty;
                expectedChunkIndex = 0;
            }
            try { cancellation.Cancel(); }
            catch (ObjectDisposedException) { }
            pending?.TrySetCanceled(cancellationToken);
            cancellation.Dispose();
            completion.TrySetResult(true);
        }
    }

    /// <summary>
    /// Represents successful ownership of one SemaphoreSlim slot. A cancelled
    /// waiter never constructs a lease and therefore can never over-release the
    /// shared Host History response gate.
    /// </summary>
    internal sealed class ProductionHostHistoryGateLease : IDisposable
    {
        private SemaphoreSlim gate;

        private ProductionHostHistoryGateLease(SemaphoreSlim gate)
        {
            this.gate = gate;
        }

        public static async Task<ProductionHostHistoryGateLease> AcquireAsync(
            SemaphoreSlim gate,
            CancellationToken cancellationToken)
        {
            if (gate == null) throw new ArgumentNullException(nameof(gate));
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new ProductionHostHistoryGateLease(gate);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref gate, null)?.Release();
        }
    }
}
