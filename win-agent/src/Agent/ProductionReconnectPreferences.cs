using System;
using System.IO;
using System.Text.Json;

namespace TermLink.Agent
{
    /// <summary>
    /// Non-sensitive Relay reconnect preferences shared by the Windows UI and
    /// the embedded Agent. A bounded fast-retry count never stops a local
    /// SessionHost: after it is exhausted the Agent continues low-frequency
    /// probes until a manual retry, a successful connection, or shutdown.
    /// </summary>
    public sealed class ProductionReconnectPreferences
    {
        internal const string StoragePathAppContextKey =
            "TermLink.ProductionReconnectPreferences.StoragePath";
        public const int MinimumInitialDelaySeconds = 1;
        public const int MaximumInitialDelaySeconds = 60;
        public const int MinimumMaximumDelaySeconds = 1;
        public const int MaximumMaximumDelaySeconds = 300;
        public const int MaximumFastAttemptsLimit = 1000;
        internal const int SlowProbeDelaySeconds = 60;

        public ProductionReconnectPreferences()
        {
            InitialDelaySeconds = 1;
            MaximumDelaySeconds = 15;
            MaximumFastAttempts = 0;
        }

        public int InitialDelaySeconds { get; set; }
        public int MaximumDelaySeconds { get; set; }

        /// <summary>
        /// Zero means unlimited exponential retries. A positive value limits
        /// only the fast retry phase; local sessions remain alive and the Agent
        /// continues probing at a low cadence afterwards.
        /// </summary>
        public int MaximumFastAttempts { get; set; }

        public static ProductionReconnectPreferences Load()
        {
            var path = ResolveStoragePath();
            try
            {
                if (!File.Exists(path)) return new ProductionReconnectPreferences();
                var stored = JsonSerializer.Deserialize<ProductionReconnectPreferences>(
                    File.ReadAllBytes(path));
                return stored != null && stored.IsValid()
                    ? stored.Clone()
                    : new ProductionReconnectPreferences();
            }
            catch (Exception error) when (
                error is IOException ||
                error is UnauthorizedAccessException ||
                error is JsonException)
            {
                // Reconnect preferences are optional. A damaged or temporarily
                // inaccessible file must not prevent the Agent from starting.
                return new ProductionReconnectPreferences();
            }
        }

        public static void Save(ProductionReconnectPreferences preferences)
        {
            if (preferences == null) throw new ArgumentNullException(nameof(preferences));
            preferences.Validate();
            var path = ResolveStoragePath();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(preferences.Clone());
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(directory))
                throw new InvalidOperationException("Reconnect preference directory is unavailable.");
            Directory.CreateDirectory(directory);
            InputDeduplicator.AtomicWrite(path, bytes);
        }

        internal TimeSpan DelayAfterFailure(int consecutiveFailures)
        {
            if (consecutiveFailures < 1)
                throw new ArgumentOutOfRangeException(nameof(consecutiveFailures));
            Validate();
            if (MaximumFastAttempts > 0 && consecutiveFailures > MaximumFastAttempts)
            {
                return TimeSpan.FromSeconds(SlowProbeDelaySeconds);
            }

            var exponent = Math.Min(consecutiveFailures - 1, 30);
            var seconds = InitialDelaySeconds * Math.Pow(2, exponent);
            return TimeSpan.FromSeconds(Math.Min(MaximumDelaySeconds, seconds));
        }

        internal bool IsValid()
        {
            return InitialDelaySeconds >= MinimumInitialDelaySeconds &&
                InitialDelaySeconds <= MaximumInitialDelaySeconds &&
                MaximumDelaySeconds >= MinimumMaximumDelaySeconds &&
                MaximumDelaySeconds <= MaximumMaximumDelaySeconds &&
                MaximumDelaySeconds >= InitialDelaySeconds &&
                MaximumFastAttempts >= 0 &&
                MaximumFastAttempts <= MaximumFastAttemptsLimit;
        }

        internal void Validate()
        {
            if (!IsValid())
            {
                throw new ArgumentOutOfRangeException(
                    nameof(ProductionReconnectPreferences),
                    "Reconnect preferences are outside the supported range.");
            }
        }

        private ProductionReconnectPreferences Clone()
        {
            return new ProductionReconnectPreferences
            {
                InitialDelaySeconds = InitialDelaySeconds,
                MaximumDelaySeconds = MaximumDelaySeconds,
                MaximumFastAttempts = MaximumFastAttempts
            };
        }

        private static string ResolveStoragePath()
        {
            var overridePath = AppContext.GetData(StoragePathAppContextKey) as string;
            if (!string.IsNullOrWhiteSpace(overridePath)) return Path.GetFullPath(overridePath);
            return Path.Combine(
                ProductionAccountStorage.ResolveLocalApplicationDataRoot(),
                "TermLink",
                "Agent",
                "connection.preferences.json");
        }
    }
}
