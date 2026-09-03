using System;

namespace TermLink.Agent
{
    /// <summary>
    /// Tracks the idempotency identity of a queue claim whose outcome may be
    /// ambiguous to the client. A key is retained only while no HTTP response
    /// was received, or while the Server explicitly reports that the same
    /// idempotency record is still in progress.
    /// </summary>
    internal sealed class ProductionClaimIdempotencyTracker
    {
        private const int MaximumInProgressResponsesPerKey = 4;
        private readonly object sync = new object();
        private string pendingKey;
        private int inProgressResponses;

        internal string BeginAttempt()
        {
            lock (sync)
                return pendingKey ??= Guid.NewGuid().ToString("D");
        }

        internal void ObserveDefinitiveResponse(string key)
        {
            lock (sync)
            {
                if (!string.Equals(pendingKey, key, StringComparison.Ordinal)) return;
                pendingKey = null;
                inProgressResponses = 0;
            }
        }

        internal void ObserveInProgress(string key)
        {
            lock (sync)
            {
                if (!string.Equals(pendingKey, key, StringComparison.Ordinal)) return;
                inProgressResponses++;
                if (inProgressResponses < MaximumInProgressResponsesPerKey) return;

                // A Server interruption can leave an in-progress idempotency
                // record without a replayable response. The claimed queue item
                // remains protected by its lease, so a bounded key rotation
                // lets the single worker resume polling without waiting for the
                // record's full retention period.
                pendingKey = null;
                inProgressResponses = 0;
            }
        }
    }
}
