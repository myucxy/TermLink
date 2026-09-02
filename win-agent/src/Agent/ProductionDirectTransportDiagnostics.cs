using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.Agent
{
    internal interface IProductionDirectTransportDiagnosticsWriter
    {
        void Append(string path, byte[] bytes, long maximumFileBytes);
    }

    /// <summary>
    /// Opt-in, bounded evidence for the Direct shadow path. The API accepts
    /// only non-identifying primitives so IDs, endpoints, keys, signalling
    /// payloads, and terminal data cannot accidentally enter the JSONL file.
    /// Every filesystem failure is contained inside this optional observer.
    /// </summary>
    internal sealed class ProductionDirectTransportDiagnostics : IDisposable
    {
        internal const string EnvironmentName = "TERMLINK_DIRECT_TRANSPORT_DIAGNOSTICS";
        internal const string FileName = "TermLink-direct-shadow.jsonl";
        internal const long MaximumFileBytes = 1024 * 1024;
        internal const int PendingLineCapacity = 64;
        private const int MaximumLineBytes = 2048;
        private const string Component = "windows-direct-shadow";

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly HashSet<string> States = new HashSet<string>(StringComparer.Ordinal)
        {
            "DISABLED",
            "WAITING_FOR_RELAY",
            "CONFIGURING",
            "GATHERING",
            "NEGOTIATING",
            "SHADOW_READY_NOT_ROUTED",
            "RELAY_ONLY",
            "STOPPED"
        };
        private static readonly HashSet<string> Reasons = new HashSet<string>(StringComparer.Ordinal)
        {
            "NONE",
            "FEATURE_DISABLED",
            "RELAY_UNAVAILABLE",
            "NETWORK_CHANGED",
            "STOPPED",
            "DIRECT_ABI_V4_UNAVAILABLE",
            "DIRECT_ATTEMPT_REJECTED",
            "DIRECT_ATTEMPT_TIMEOUT",
            "DIRECT_CANDIDATE_BATCH_REJECTED",
            "DIRECT_CANDIDATE_DUPLICATED",
            "DIRECT_CHECK_BATCH_NATIVE_REJECTED",
            "DIRECT_CHECK_RESULT_REJECTED",
            "DIRECT_CHECKS_EXHAUSTED",
            "DIRECT_HOST_ROUTE_UNSAFE",
            "DIRECT_IDENTITY_REJECTED",
            "DIRECT_MULTIPAIR_FAILED",
            "DIRECT_MULTIPAIR_NOT_NEGOTIATED",
            "DIRECT_NATIVE_EVENT_FENCE",
            "DIRECT_NATIVE_FAILED",
            "DIRECT_NO_AUTHORIZED_PEER",
            "DIRECT_NOMINATION_ACK_FAILED",
            "DIRECT_NOMINATION_NATIVE_REJECTED",
            "DIRECT_NOMINATION_PAIR_MISMATCH",
            "DIRECT_NOMINATION_READY_REJECTED",
            "DIRECT_OFFER_REJECTED",
            "DIRECT_OFFER_TIMEOUT",
            "DIRECT_PAIR_REFERENCES_UNKNOWN_CANDIDATE",
            "DIRECT_PAIR_VALIDATION_REJECTED",
            "DIRECT_RESULT_FAILED",
            "DIRECT_SHADOW_BINDING_MISMATCH",
            "DIRECT_SHADOW_READY_REJECTED",
            "DIRECT_SIGN_REQUEST_REJECTED",
            "DIRECT_SIGNAL_CURSOR_REJECTED",
            "DIRECT_SIGNAL_FAILED",
            "DIRECT_SIGNAL_PAYLOAD_REJECTED",
            "DIRECT_SIGNAL_QUEUE_FAILED",
            "DIRECT_SIGNAL_QUEUE_FULL",
            "DIRECT_SIGNAL_START_FAILED",
            "DIRECT_SIGNATURE_REJECTED",
            "DIRECT_TICKET_KEY_MISSING"
        };

        private readonly bool enabled;
        private readonly string path;
        private readonly object observationLock = new object();
        private readonly Func<DateTime> utcNow;
        private readonly IProductionDirectTransportDiagnosticsWriter writer;
        private readonly BlockingCollection<byte[]> pendingLines;
        private readonly Task writerTask;
        private string lastFingerprint;
        private int acceptedLineCount;
        private int completedLineCount;
        private int disposed;

        private ProductionDirectTransportDiagnostics(
            bool enabled,
            string path,
            Func<DateTime> utcNow,
            IProductionDirectTransportDiagnosticsWriter writer,
            int pendingLineCapacity)
        {
            this.enabled = enabled;
            this.path = path;
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
            if (!enabled) return;
            if (pendingLineCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(pendingLineCapacity));
            this.writer = writer ?? ProductionDirectTransportDiagnosticsFileWriter.Instance;
            pendingLines = new BlockingCollection<byte[]>(
                new ConcurrentQueue<byte[]>(), pendingLineCapacity);
            writerTask = Task.Factory.StartNew(
                WritePendingLines,
                CancellationToken.None,
                TaskCreationOptions.DenyChildAttach | TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
        }

        internal bool Enabled => enabled;
        internal string Path => path;

        internal static bool IsEnabled(string value) =>
            string.Equals(value, "1", StringComparison.Ordinal);

        internal static ProductionDirectTransportDiagnostics CreateForProcess()
        {
            try
            {
                var enabled = IsEnabled(Environment.GetEnvironmentVariable(EnvironmentName));
                if (!enabled)
                    return new ProductionDirectTransportDiagnostics(false, null, null, null, 0);
                var root = System.IO.Path.Combine(
                    ProductionAccountStorage.ResolveLocalApplicationDataRoot(),
                    "TermLink",
                    "logs");
                return new ProductionDirectTransportDiagnostics(
                    true,
                    System.IO.Path.Combine(root, FileName),
                    null,
                    null,
                    PendingLineCapacity);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return new ProductionDirectTransportDiagnostics(false, null, null, null, 0);
            }
        }

        internal static ProductionDirectTransportDiagnostics CreateForValidation(
            string environmentValue,
            string localApplicationDataRoot,
            Func<DateTime> utcNow = null,
            IProductionDirectTransportDiagnosticsWriter writer = null,
            int pendingLineCapacity = PendingLineCapacity)
        {
            if (!IsEnabled(environmentValue))
                return new ProductionDirectTransportDiagnostics(false, null, utcNow, null, 0);
            if (string.IsNullOrWhiteSpace(localApplicationDataRoot))
                throw new ArgumentException(
                    "An isolated LocalApplicationData root is required.",
                    nameof(localApplicationDataRoot));
            var root = System.IO.Path.Combine(
                System.IO.Path.GetFullPath(localApplicationDataRoot),
                "TermLink",
                "logs");
            return new ProductionDirectTransportDiagnostics(
                true,
                System.IO.Path.Combine(root, FileName),
                utcNow,
                writer,
                pendingLineCapacity);
        }

        internal bool Observe(
            string state,
            string reasonCode,
            long relayGeneration,
            long networkGeneration,
            ulong pathEpoch,
            uint selectedPairRank,
            string localCandidateType,
            string remoteCandidateType)
        {
            if (!enabled || Volatile.Read(ref disposed) != 0) return false;
            try
            {
                var safeState = States.Contains(state ?? string.Empty) ? state : "UNKNOWN";
                var requestedReason = string.IsNullOrEmpty(reasonCode) ? "NONE" : reasonCode;
                var safeReason = Reasons.Contains(requestedReason) ? requestedReason : "UNKNOWN";
                var safeRelayGeneration = Math.Max(0, relayGeneration);
                var safeNetworkGeneration = Math.Max(0, networkGeneration);
                var hasPair = selectedPairRank is > 0 and <= ProductionDirectNativeAbiV4.MaximumPairsTotal &&
                    ValidCandidateType(localCandidateType) &&
                    ValidCandidateType(remoteCandidateType);
                var safeRank = hasPair ? selectedPairRank : 0;
                var safeLocalType = hasPair ? localCandidateType : "NONE";
                var safeRemoteType = hasPair ? remoteCandidateType : "NONE";
                var fingerprint = string.Join("|",
                    safeState,
                    safeReason,
                    safeRelayGeneration,
                    safeNetworkGeneration,
                    pathEpoch,
                    safeRank,
                    safeLocalType,
                    safeRemoteType);

                var line = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["timestamp_utc"] = utcNow().ToUniversalTime().ToString("O"),
                    ["component"] = Component,
                    ["state"] = safeState,
                    ["reason_code"] = safeReason,
                    ["relay_generation"] = safeRelayGeneration,
                    ["network_generation"] = safeNetworkGeneration,
                    ["path_epoch"] = pathEpoch,
                    ["selected_pair_rank"] = safeRank,
                    ["local_candidate_type"] = safeLocalType,
                    ["remote_candidate_type"] = safeRemoteType,
                    ["direct_routing_permitted"] = false
                });
                var bytes = Utf8.GetBytes(line + System.Environment.NewLine);
                if (bytes.Length > MaximumLineBytes) return false;

                lock (observationLock)
                {
                    if (disposed != 0) return false;
                    if (string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal))
                        return true;
                    if (!pendingLines.TryAdd(bytes, 0)) return false;
                    lastFingerprint = fingerprint;
                    Interlocked.Increment(ref acceptedLineCount);
                    return true;
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                return false;
            }
        }

        internal static string StateCode(ProductionDirectShadowState state) => state switch
        {
            ProductionDirectShadowState.Disabled => "DISABLED",
            ProductionDirectShadowState.WaitingForRelay => "WAITING_FOR_RELAY",
            ProductionDirectShadowState.Configuring => "CONFIGURING",
            ProductionDirectShadowState.Gathering => "GATHERING",
            ProductionDirectShadowState.Negotiating => "NEGOTIATING",
            ProductionDirectShadowState.ShadowReadyNotRouted => "SHADOW_READY_NOT_ROUTED",
            ProductionDirectShadowState.RelayOnly => "RELAY_ONLY",
            ProductionDirectShadowState.Stopped => "STOPPED",
            _ => "UNKNOWN"
        };

        internal static string CandidateType(ProductionDirectCandidateKindV4 kind) => kind switch
        {
            ProductionDirectCandidateKindV4.Host => "HOST",
            ProductionDirectCandidateKindV4.ServerReflexive => "SRFLX",
            _ => "NONE"
        };

        private static bool ValidCandidateType(string value) =>
            string.Equals(value, "HOST", StringComparison.Ordinal) ||
            string.Equals(value, "SRFLX", StringComparison.Ordinal);

        internal bool WaitForIdleForValidation(TimeSpan timeout)
        {
            if (!enabled) return true;
            var target = Volatile.Read(ref acceptedLineCount);
            var timer = Stopwatch.StartNew();
            while (Volatile.Read(ref completedLineCount) < target)
            {
                if (timer.Elapsed >= timeout) return false;
                Thread.Sleep(1);
            }
            return true;
        }

        internal bool WaitForCompletionForValidation(TimeSpan timeout)
        {
            if (writerTask == null) return true;
            try { return writerTask.Wait(timeout); }
            catch (AggregateException error) when (error.InnerExceptions.All(
                value => value is not OutOfMemoryException))
            {
                return true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0 || pendingLines == null) return;
            pendingLines.CompleteAdding();
        }

        private void WritePendingLines()
        {
            try
            {
                foreach (var bytes in pendingLines.GetConsumingEnumerable())
                {
                    try { writer.Append(path, bytes, MaximumFileBytes); }
                    catch (Exception error) when (error is not OutOfMemoryException) { }
                    finally { Interlocked.Increment(ref completedLineCount); }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
            finally { pendingLines.Dispose(); }
        }
    }

    internal sealed class ProductionDirectTransportDiagnosticsFileWriter :
        IProductionDirectTransportDiagnosticsWriter
    {
        private static readonly ConcurrentDictionary<string, object> PathLocks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        private ProductionDirectTransportDiagnosticsFileWriter() { }

        internal static ProductionDirectTransportDiagnosticsFileWriter Instance { get; } =
            new ProductionDirectTransportDiagnosticsFileWriter();

        public void Append(string path, byte[] bytes, long maximumFileBytes)
        {
            var pathLock = PathLocks.GetOrAdd(path, _ => new object());
            lock (pathLock)
            {
                var directory = System.IO.Path.GetDirectoryName(path);
                Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maximumFileBytes)
                    File.Move(path, path + ".1", true);
                using var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.Read);
                stream.Seek(0, SeekOrigin.End);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
        }
    }
}
