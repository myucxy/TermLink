using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TermLink.Agent;

namespace TermLink.ProductionDirectTransport.Validation
{
    internal static class DirectTransportDiagnosticsValidation
    {
        private static readonly string[] AllowedFields =
        {
            "timestamp_utc",
            "component",
            "state",
            "reason_code",
            "relay_generation",
            "network_generation",
            "path_epoch",
            "selected_pair_rank",
            "local_candidate_type",
            "remote_candidate_type",
            "direct_routing_permitted"
        };

        internal static void Validate()
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "TermLink-direct-diagnostics-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                ValidateStrictOptIn(root);
                ValidateAllowlistSanitizationAndConcurrency(root);
                ValidateIoFailureIsFailOpen(root);
            }
            finally
            {
                try { Directory.Delete(root, true); }
                catch { }
            }
        }

        private static void ValidateStrictOptIn(string root)
        {
            foreach (var value in new[] { null, string.Empty, "0", "true", "yes", " 1 " })
            {
                using var diagnostic = ProductionDirectTransportDiagnostics.CreateForValidation(value, root);
                Require(!diagnostic.Enabled && !diagnostic.Observe(
                        "RELAY_ONLY", "NONE", 1, 1, 1, 0, "NONE", "NONE"),
                    "Direct diagnostics were not strict opt-in.");
            }
            Require(!Directory.Exists(System.IO.Path.Combine(root, "TermLink")),
                "Disabled Direct diagnostics created a data directory.");
            Require(ProductionDirectTransportDiagnostics.IsEnabled("1") &&
                    !ProductionDirectTransportDiagnostics.IsEnabled(" 1 ") &&
                    !ProductionDirectTransportDiagnostics.IsEnabled("true"),
                "The Direct diagnostics environment flag accepted a value other than exact 1.");
        }

        private static void ValidateAllowlistSanitizationAndConcurrency(string root)
        {
            var now = new DateTime(2026, 9, 2, 12, 34, 56, DateTimeKind.Utc);
            using var diagnostic = ProductionDirectTransportDiagnostics.CreateForValidation(
                "1", root, () => now);
            Require(diagnostic.Enabled && diagnostic.Observe(
                    "SHADOW_READY_NOT_ROUTED",
                    string.Empty,
                    7,
                    3,
                    42,
                    5,
                    "HOST",
                    "SRFLX"),
                "Enabled Direct diagnostics did not append shadow evidence.");
            Require(diagnostic.WaitForIdleForValidation(TimeSpan.FromSeconds(5)),
                "The first asynchronous Direct diagnostic did not drain.");

            var lines = File.ReadAllLines(diagnostic.Path);
            Require(lines.Length == 1, "The first Direct diagnostic did not produce one JSONL row.");
            using (var document = JsonDocument.Parse(lines[0]))
            {
                var record = document.RootElement;
                Require(record.EnumerateObject().Select(value => value.Name)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .SequenceEqual(AllowedFields.OrderBy(value => value, StringComparer.Ordinal)) &&
                        record.GetProperty("timestamp_utc").GetString() == now.ToString("O") &&
                        record.GetProperty("component").GetString() == "windows-direct-shadow" &&
                        record.GetProperty("state").GetString() == "SHADOW_READY_NOT_ROUTED" &&
                        record.GetProperty("reason_code").GetString() == "NONE" &&
                        record.GetProperty("relay_generation").GetInt64() == 7 &&
                        record.GetProperty("network_generation").GetInt64() == 3 &&
                        record.GetProperty("path_epoch").GetUInt64() == 42 &&
                        record.GetProperty("selected_pair_rank").GetUInt32() == 5 &&
                        record.GetProperty("local_candidate_type").GetString() == "HOST" &&
                        record.GetProperty("remote_candidate_type").GetString() == "SRFLX" &&
                        !record.GetProperty("direct_routing_permitted").GetBoolean(),
                    "The Direct diagnostic schema or selected-pair categories changed.");
            }

            var forbiddenId = Guid.NewGuid().ToString("D");
            var malformed = "https://192.0.2.10:4444/path?token=secret-" + forbiddenId;
            Require(diagnostic.Observe(
                    "BAD\r\nSTATE=" + malformed,
                    malformed,
                    -8,
                    -9,
                    43,
                    99,
                    "192.0.2.10",
                    "TOKEN"),
                "A malformed diagnostic input prevented a redacted append.");
            Require(diagnostic.WaitForIdleForValidation(TimeSpan.FromSeconds(5)),
                "The sanitized asynchronous Direct diagnostic did not drain.");
            lines = File.ReadAllLines(diagnostic.Path);
            Require(lines.Length == 2, "The sanitized Direct diagnostic row was not appended.");
            using (var document = JsonDocument.Parse(lines[1]))
            {
                var record = document.RootElement;
                Require(record.GetProperty("state").GetString() == "UNKNOWN" &&
                        record.GetProperty("reason_code").GetString() == "UNKNOWN" &&
                        record.GetProperty("relay_generation").GetInt64() == 0 &&
                        record.GetProperty("network_generation").GetInt64() == 0 &&
                        record.GetProperty("selected_pair_rank").GetUInt32() == 0 &&
                        record.GetProperty("local_candidate_type").GetString() == "NONE" &&
                        record.GetProperty("remote_candidate_type").GetString() == "NONE",
                    "Malformed Direct diagnostic fields were not fail-closed.");
            }
            Require(!File.ReadAllText(diagnostic.Path).Contains(malformed, StringComparison.Ordinal) &&
                    !File.ReadAllText(diagnostic.Path).Contains(forbiddenId, StringComparison.Ordinal),
                "A URL, token, address, port, or identifier reached Direct diagnostics.");
            Require(diagnostic.Observe(
                    "BAD\r\nSTATE=" + malformed,
                    malformed,
                    -8,
                    -9,
                    43,
                    99,
                    "192.0.2.10",
                    "TOKEN") && diagnostic.WaitForIdleForValidation(TimeSpan.FromSeconds(5)) &&
                    File.ReadAllLines(diagnostic.Path).Length == 2,
                "An identical Direct diagnostic state was not deduplicated.");

            Require(diagnostic.Observe(
                    "NEGOTIATING",
                    "NONE",
                    7,
                    3,
                    43,
                    ProductionDirectNativeAbiV4.MaximumPairsTotal + 1,
                    "HOST",
                    "SRFLX"),
                "An out-of-range Direct pair rank prevented a redacted append.");
            Require(diagnostic.WaitForIdleForValidation(TimeSpan.FromSeconds(5)),
                "The redacted out-of-range Direct diagnostic did not drain.");
            lines = File.ReadAllLines(diagnostic.Path);
            using (var document = JsonDocument.Parse(lines[^1]))
            {
                var record = document.RootElement;
                Require(record.GetProperty("selected_pair_rank").GetUInt32() == 0 &&
                        record.GetProperty("local_candidate_type").GetString() == "NONE" &&
                        record.GetProperty("remote_candidate_type").GetString() == "NONE",
                    "An out-of-range Direct pair rank reached diagnostics.");
            }

            var accepted = 0;
            Parallel.For(1, 129, generation =>
            {
                if (diagnostic.Observe(
                        "GATHERING",
                        "NONE",
                        7,
                        generation,
                        44,
                        0,
                        "NONE",
                        "NONE"))
                    Interlocked.Increment(ref accepted);
            });
            Require(accepted > 0 && accepted <= 128 &&
                    diagnostic.WaitForIdleForValidation(TimeSpan.FromSeconds(5)),
                "Concurrent Direct diagnostics did not drain within the bounded queue contract.");
            lines = File.ReadAllLines(diagnostic.Path);
            Require(lines.Length == 3 + accepted && lines.All(IsAllowedJsonLine),
                "Accepted concurrent Direct diagnostic appends were lost, interleaved, or malformed.");
            diagnostic.Dispose();
            Require(diagnostic.WaitForCompletionForValidation(TimeSpan.FromSeconds(5)),
                "The asynchronous Direct diagnostic writer did not finish during test cleanup.");
        }

        private static void ValidateIoFailureIsFailOpen(string root)
        {
            var blockedRoot = System.IO.Path.Combine(root, "not-a-directory");
            File.WriteAllText(blockedRoot, "fixture");
            using var diagnostic = ProductionDirectTransportDiagnostics.CreateForValidation(
                "1", blockedRoot, () => DateTime.UtcNow);
            Require(diagnostic.Observe(
                    "GATHERING", "NONE", 1, 0, 1, 0, "NONE", "NONE"),
                "A Direct diagnostic could not be admitted independently of filesystem failure.");
            Require(diagnostic.WaitForIdleForValidation(TimeSpan.FromSeconds(5)),
                "A Direct diagnostics filesystem failure blocked the independent writer.");

            var supervisor = new ProductionDirectTransportSupervisor();
            supervisor.ObserveRelayConnected(true);
            Require(supervisor.TryBeginAttempt(
                    Guid.NewGuid(), Guid.NewGuid(), 1, out var attempt, out _),
                "A diagnostics filesystem failure affected Direct attempt admission.");
            supervisor.MarkGatheringComplete(attempt);
            Require(supervisor.TryFallbackAttempt(attempt, "NETWORK_CHANGED") &&
                    supervisor.Snapshot().State == ProductionDirectPathState.RelayOnly &&
                    supervisor.Snapshot().RelayConnected,
                "A diagnostics filesystem failure affected Relay fallback authority.");
            diagnostic.Dispose();
            Require(diagnostic.WaitForCompletionForValidation(TimeSpan.FromSeconds(5)),
                "The failed Direct diagnostic writer did not finish before cleanup.");
        }

        private static bool IsAllowedJsonLine(string line)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.EnumerateObject().Select(value => value.Name)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .SequenceEqual(AllowedFields.OrderBy(value => value, StringComparer.Ordinal));
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
