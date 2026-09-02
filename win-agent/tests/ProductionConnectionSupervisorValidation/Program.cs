using System;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.ProductionConnectionSupervisor.Validation
{
    internal static class Program
    {
        private static async Task<int> Main()
        {
            try
            {
                await ValidateSessionFailureIsolationAsync().ConfigureAwait(false);
                await ValidatePhysicalFailureAndCoalescedResumeAsync().ConfigureAwait(false);
                ValidateStableGenerationBackoffReset();
                Console.WriteLine("Production connection supervisor validation passed.");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error);
                return 1;
            }
        }

        private static Task ValidateSessionFailureIsolationAsync()
        {
            var supervisor = CreateSupervisor();
            var generation = supervisor.MarkPhysicalConnected();
            supervisor.ObserveSessionSoftFailure();

            var afterSoftFailure = supervisor.Snapshot();
            Require(afterSoftFailure.PhysicalConnected,
                "A session-soft-failure signal disconnected the physical Relay generation.");
            Require(afterSoftFailure.PhysicalGeneration == generation,
                "A session-soft-failure signal replaced the physical Relay generation.");
            Require(afterSoftFailure.ScheduledReconnects == 0,
                "A session-soft-failure signal scheduled a device reconnect.");
            Require(afterSoftFailure.SessionSoftFailures == 1,
                "The session-soft-failure diagnostic was not retained.");
            return Task.CompletedTask;
        }

        private static async Task ValidatePhysicalFailureAndCoalescedResumeAsync()
        {
            var supervisor = CreateSupervisor();
            var generation = supervisor.MarkPhysicalConnected();
            var reconnect = supervisor.SchedulePhysicalFailure();
            var failed = supervisor.Snapshot();

            Require(!failed.PhysicalConnected,
                "A genuine physical network failure left the generation connected.");
            Require(failed.PhysicalGeneration == generation,
                "Scheduling a reconnect fabricated a successful generation.");
            Require(failed.ScheduledReconnects == 1 && reconnect.ConsecutivePhysicalFailures == 1,
                "A genuine network failure did not schedule exactly one reconnect.");
            Require(reconnect.Delay == TimeSpan.FromSeconds(1),
                "The supervisor did not own the configured first retry delay.");

            supervisor.NotifyNetworkAvailable();
            supervisor.NotifyNetworkAvailable();
            supervisor.RequestManualRetry();
            Require(await supervisor.WaitForRetryOrResumeAsync(
                    TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false),
                "A network-resume/manual signal did not wake the retry delay.");
            Require(!await supervisor.WaitForRetryOrResumeAsync(
                    TimeSpan.FromMilliseconds(40), CancellationToken.None).ConfigureAwait(false),
                "Coalesced network-resume/manual signals scheduled duplicate immediate retries.");
            Require(supervisor.SchedulePhysicalFailure().Delay == TimeSpan.FromSeconds(2),
                "A retry wake incorrectly reset the accumulated physical-failure backoff.");
        }

        private static ProductionRealtimeConnectionSupervisor CreateSupervisor()
        {
            return new ProductionRealtimeConnectionSupervisor(
                new ProductionReconnectPreferences
                {
                    InitialDelaySeconds = 1,
                    MaximumDelaySeconds = 4,
                    MaximumFastAttempts = 0
                },
                nextJitterMilliseconds: () => 0);
        }

        private static void ValidateStableGenerationBackoffReset()
        {
            var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var supervisor = new ProductionRealtimeConnectionSupervisor(
                new ProductionReconnectPreferences
                {
                    InitialDelaySeconds = 1,
                    MaximumDelaySeconds = 16,
                    MaximumFastAttempts = 0
                },
                nextJitterMilliseconds: () => 0,
                utcNow: () => now,
                stableConnectionDuration: TimeSpan.FromSeconds(30));

            var firstGeneration = supervisor.MarkPhysicalConnected();
            now = now.AddSeconds(2);
            Require(supervisor.SchedulePhysicalFailure().Delay == TimeSpan.FromSeconds(1),
                "The initial physical failure did not start at the first backoff step.");
            var secondGeneration = supervisor.MarkPhysicalConnected();
            now = now.AddSeconds(2);
            Require(supervisor.SchedulePhysicalFailure().Delay == TimeSpan.FromSeconds(2),
                "A short connected generation reset exponential backoff.");

            var thirdGeneration = supervisor.MarkPhysicalConnected();
            now = now.AddMilliseconds(29_900);
            Require(!supervisor.TryMarkPhysicalGenerationStable(thirdGeneration),
                "A generation reset backoff before its stability window elapsed.");
            now = now.AddMilliseconds(100);
            Require(!supervisor.TryMarkPhysicalGenerationStable(firstGeneration) &&
                    !supervisor.TryMarkPhysicalGenerationStable(secondGeneration),
                "A stale generation reset the current connection's backoff.");
            Require(supervisor.TryMarkPhysicalGenerationStable(thirdGeneration),
                "The current generation did not reset backoff after its stability window.");
            supervisor.MarkPhysicalDisconnected();
            Require(supervisor.SchedulePhysicalFailure().Delay == TimeSpan.FromSeconds(1),
                "A stable generation did not reset the next physical retry to the first step.");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
