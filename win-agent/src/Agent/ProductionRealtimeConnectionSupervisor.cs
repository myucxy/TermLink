using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    /// <summary>
    /// Owns only the physical Relay connection generation and its retry policy.
    /// Session, renderer, terminal-core, and payload failures can be observed as
    /// soft failures, but this type deliberately has no socket reference and no
    /// callback capable of stopping the device connection.
    /// </summary>
    internal sealed class ProductionRealtimeConnectionSupervisor : IDisposable
    {
        internal static readonly TimeSpan DefaultStableConnectionDuration = TimeSpan.FromSeconds(30);
        private const int ManualRetryWake = 1;
        private const int NetworkResumeWake = 2;
        private readonly object sync = new object();
        private readonly ProductionReconnectPreferences preferences;
        private readonly Func<int> nextJitterMilliseconds;
        private readonly Func<DateTime> utcNow;
        private readonly TimeSpan stableConnectionDuration;
        private readonly SemaphoreSlim immediateRetrySignal = new SemaphoreSlim(0, 1);
        private bool physicalConnected;
        private long physicalGeneration;
        private int consecutivePhysicalFailures;
        private long scheduledReconnects;
        private long sessionSoftFailures;
        private int pendingWakeReasons;
        private DateTime physicalConnectedAtUtc;

        public ProductionRealtimeConnectionSupervisor(
            ProductionReconnectPreferences preferences,
            Func<int> nextJitterMilliseconds = null,
            Func<DateTime> utcNow = null,
            TimeSpan? stableConnectionDuration = null)
        {
            this.preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
            this.preferences.Validate();
            this.nextJitterMilliseconds = nextJitterMilliseconds ??
                (() => RandomNumberGenerator.GetInt32(0, 500));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            this.stableConnectionDuration = stableConnectionDuration ?? DefaultStableConnectionDuration;
            if (this.stableConnectionDuration <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(stableConnectionDuration));
        }

        public ProductionRealtimeConnectionSnapshot Snapshot()
        {
            lock (sync)
            {
                return new ProductionRealtimeConnectionSnapshot(
                    physicalConnected,
                    physicalGeneration,
                    consecutivePhysicalFailures,
                    scheduledReconnects,
                    sessionSoftFailures);
            }
        }

        public long MarkPhysicalConnected()
        {
            lock (sync)
            {
                physicalConnected = true;
                physicalGeneration = checked(physicalGeneration + 1);
                physicalConnectedAtUtc = utcNow();

                // A network-resume notification can race the successful
                // handshake. It is stale once this generation is connected.
                // Preserve a coalesced explicit manual retry, if one exists.
                pendingWakeReasons &= ~NetworkResumeWake;
                if (pendingWakeReasons == 0) immediateRetrySignal.Wait(0);
                return physicalGeneration;
            }
        }

        public void MarkPhysicalDisconnected()
        {
            lock (sync) physicalConnected = false;
        }

        /// <summary>
        /// Resets accumulated backoff only after the exact physical generation
        /// has stayed connected for the configured stability window. The caller
        /// may probe this from an existing pump loop; no background timer or
        /// generation-crossing continuation is retained here.
        /// </summary>
        public bool TryMarkPhysicalGenerationStable(long generation)
        {
            lock (sync)
            {
                if (!physicalConnected || generation != physicalGeneration ||
                    utcNow() - physicalConnectedAtUtc < stableConnectionDuration)
                {
                    return false;
                }
                consecutivePhysicalFailures = 0;
                return true;
            }
        }

        public ProductionRealtimeReconnectPlan SchedulePhysicalFailure()
        {
            lock (sync)
            {
                physicalConnected = false;
                consecutivePhysicalFailures = checked(consecutivePhysicalFailures + 1);
                scheduledReconnects = checked(scheduledReconnects + 1);
                var jitter = nextJitterMilliseconds();
                if (jitter < 0 || jitter >= 500)
                    throw new InvalidOperationException("Reconnect jitter must be between 0 and 499 milliseconds.");
                return new ProductionRealtimeReconnectPlan(
                    consecutivePhysicalFailures,
                    preferences.DelayAfterFailure(consecutivePhysicalFailures) +
                        TimeSpan.FromMilliseconds(jitter));
            }
        }

        public void ObserveSessionSoftFailure()
        {
            lock (sync) sessionSoftFailures = checked(sessionSoftFailures + 1);
        }

        public void RequestManualRetry()
        {
            SignalImmediateRetry(ManualRetryWake, requireDisconnected: false);
        }

        public void NotifyNetworkAvailable()
        {
            SignalImmediateRetry(NetworkResumeWake, requireDisconnected: true);
        }

        public async Task<bool> WaitForRetryOrResumeAsync(TimeSpan delay, CancellationToken token)
        {
            if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
            if (!await immediateRetrySignal.WaitAsync(delay, token).ConfigureAwait(false)) return false;
            lock (sync)
            {
                pendingWakeReasons = 0;
            }
            return true;
        }

        private void SignalImmediateRetry(int reason, bool requireDisconnected)
        {
            lock (sync)
            {
                if (requireDisconnected && physicalConnected) return;
                var release = pendingWakeReasons == 0;
                pendingWakeReasons |= reason;
                if (!release) return;
                try { immediateRetrySignal.Release(); }
                catch (SemaphoreFullException) { }
            }
        }

        public void Dispose()
        {
            immediateRetrySignal.Dispose();
        }
    }

    internal sealed class ProductionRealtimeConnectionSnapshot
    {
        public ProductionRealtimeConnectionSnapshot(
            bool physicalConnected,
            long physicalGeneration,
            int consecutivePhysicalFailures,
            long scheduledReconnects,
            long sessionSoftFailures)
        {
            PhysicalConnected = physicalConnected;
            PhysicalGeneration = physicalGeneration;
            ConsecutivePhysicalFailures = consecutivePhysicalFailures;
            ScheduledReconnects = scheduledReconnects;
            SessionSoftFailures = sessionSoftFailures;
        }

        public bool PhysicalConnected { get; }
        public long PhysicalGeneration { get; }
        public int ConsecutivePhysicalFailures { get; }
        public long ScheduledReconnects { get; }
        public long SessionSoftFailures { get; }
    }

    internal sealed class ProductionRealtimeReconnectPlan
    {
        public ProductionRealtimeReconnectPlan(int consecutivePhysicalFailures, TimeSpan delay)
        {
            ConsecutivePhysicalFailures = consecutivePhysicalFailures;
            Delay = delay;
        }

        public int ConsecutivePhysicalFailures { get; }
        public TimeSpan Delay { get; }
    }
}
