using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

namespace TermLink.Agent
{
    internal enum ProductionTerminalHistoryArtifactKind
    {
        Transcript = 1,
        Snapshot = 2
    }

    internal sealed class ProductionTerminalHistoryArtifact
    {
        public string ArtifactId { get; set; }
        public ProductionTerminalHistoryArtifactKind Kind { get; set; }
        public int UploadOrder { get; set; }
        public ulong SnapshotSequence { get; set; }
        public ulong InitialTranscriptBeforeLineId { get; set; }
        public ulong ObservedLocalInputEpoch { get; set; }
        public int TranscriptPageCount { get; set; }
        public int Columns { get; set; }
        public int Rows { get; set; }
        public ulong FirstLineId { get; set; }
        public ulong LastLineId { get; set; }
        public ulong ExclusiveBeforeLineId { get; set; }
        public bool HasMoreBefore { get; set; }
        public ulong NextBeforeLineId { get; set; }
        public string FormatName { get; set; }
        public int FormatVersion { get; set; }
        public byte[] Plaintext { get; set; }

        internal ProductionTerminalHistoryArtifact Clone()
        {
            return new ProductionTerminalHistoryArtifact
            {
                ArtifactId = ArtifactId,
                Kind = Kind,
                UploadOrder = UploadOrder,
                SnapshotSequence = SnapshotSequence,
                InitialTranscriptBeforeLineId = InitialTranscriptBeforeLineId,
                ObservedLocalInputEpoch = ObservedLocalInputEpoch,
                TranscriptPageCount = TranscriptPageCount,
                Columns = Columns,
                Rows = Rows,
                FirstLineId = FirstLineId,
                LastLineId = LastLineId,
                ExclusiveBeforeLineId = ExclusiveBeforeLineId,
                HasMoreBefore = HasMoreBefore,
                NextBeforeLineId = NextBeforeLineId,
                FormatName = FormatName,
                FormatVersion = FormatVersion,
                Plaintext = Plaintext == null ? null : (byte[])Plaintext.Clone()
            };
        }
    }

    internal sealed class ProductionTerminalHistoryPreviousSnapshot
    {
        public Guid SessionId { get; set; }
        public ProductionTerminalHistoryArtifact Artifact { get; set; }
    }

    internal sealed class ProductionTerminalHistoryDeltaSnapshotPair : IDisposable
    {
        public ProductionTerminalHistoryArtifact Current { get; set; }
        public ProductionTerminalHistoryArtifact Previous { get; set; }

        public void Dispose()
        {
            if (Current?.Plaintext != null) CryptographicOperations.ZeroMemory(Current.Plaintext);
            if (Previous?.Plaintext != null) CryptographicOperations.ZeroMemory(Previous.Plaintext);
            Current = null;
            Previous = null;
        }
    }

    internal sealed class ProductionTerminalHistoryDelivery
    {
        public Guid RecipientDeviceId { get; set; }
        public long KeyEpoch { get; set; }
        public Dictionary<string, byte[]> SealedObjects { get; set; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        public HashSet<string> UploadedArtifactIds { get; set; } = new HashSet<string>(StringComparer.Ordinal);
    }

    internal sealed class ProductionTerminalHistoryOutbox
    {
        public Guid SessionId { get; set; }
        public ulong SnapshotSequence { get; set; }
        public List<ProductionTerminalHistoryArtifact> Artifacts { get; set; } = new List<ProductionTerminalHistoryArtifact>();
        public Dictionary<string, ProductionTerminalHistoryDelivery> Deliveries { get; set; } =
            new Dictionary<string, ProductionTerminalHistoryDelivery>(StringComparer.Ordinal);

        internal ProductionTerminalHistoryOutbox Clone()
        {
            var result = new ProductionTerminalHistoryOutbox
            {
                SessionId = SessionId,
                SnapshotSequence = SnapshotSequence,
                Artifacts = Artifacts.Select(value => value.Clone()).ToList()
            };
            foreach (var item in Deliveries)
            {
                var delivery = new ProductionTerminalHistoryDelivery
                {
                    RecipientDeviceId = item.Value.RecipientDeviceId,
                    KeyEpoch = item.Value.KeyEpoch,
                    UploadedArtifactIds = new HashSet<string>(item.Value.UploadedArtifactIds, StringComparer.Ordinal)
                };
                foreach (var sealedObject in item.Value.SealedObjects)
                    delivery.SealedObjects[sealedObject.Key] = (byte[])sealedObject.Value.Clone();
                result.Deliveries[item.Key] = delivery;
            }
            return result;
        }
    }

    internal sealed class ProductionTerminalHistoryCheckpoint
    {
        public Guid SessionId { get; set; }
        public ulong LastSequence { get; set; }
        public int AuthorityVersion { get; set; }
        public bool SnapshotEligible { get; set; }
        public string IneligibilityCode { get; set; }
        public byte[] SnapshotPlaintext { get; set; }
        public List<byte[]> TranscriptPlaintexts { get; set; } = new List<byte[]>();
    }

    internal sealed class ProductionTerminalTranscriptArchivePage
    {
        public Guid SessionId { get; set; }
        public ulong FirstLineId { get; set; }
        public ulong LastLineId { get; set; }
        public List<ProductionTerminalTranscriptArchiveRow> Rows { get; set; } =
            new List<ProductionTerminalTranscriptArchiveRow>();
    }

    internal sealed class ProductionTerminalTranscriptArchiveRow
    {
        public ulong LineId { get; set; }
        public ulong SealedAtSequence { get; set; }
        public bool Wrapped { get; set; }
        public List<ProductionTerminalTranscriptArchiveCell> Cells { get; set; } =
            new List<ProductionTerminalTranscriptArchiveCell>();
    }

    internal sealed class ProductionTerminalTranscriptArchiveCell
    {
        public int Repeat { get; set; }
        public string Character { get; set; }
        public int Foreground { get; set; }
        public int Background { get; set; }
        public ushort Attributes { get; set; }
        public int HyperlinkIndexPlusOne { get; set; }
        public int DisplayWidth { get; set; }
        public bool WideContinuation { get; set; }
    }

    internal sealed class ProductionTerminalHistoryPreparedUpload
    {
        public Guid SessionId { get; set; }
        public Guid RecipientDeviceId { get; set; }
        public long KeyEpoch { get; set; }
        public ProductionTerminalHistoryArtifact Artifact { get; set; }
        public byte[] SealedObject { get; set; }
    }

    internal sealed class ProductionHistoryChunkOutbox
    {
        public Guid SessionId { get; set; }
        public Dictionary<string, byte[]> SealedObjects { get; set; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    }

    internal sealed class ProductionTerminalHistoryStore
    {
        private const int MaximumTranscriptPublicationPageCount = 100_000;
        private const int GraphicsSnapshotV222RequiredFeaturesOffset = 64;
        private const ulong GraphicsSnapshotV222StandaloneC1Feature = 0x10_0000;
        private const string WalExtension = ".wal.dpapi";
        private const string CheckpointFileName = "checkpoint.dpapi";
        private const string OutboxFileName = "outbox.dpapi";
        private const string PreviousSnapshotFileName = "previous-snapshot.dpapi";
        private const long MaximumPreviousSnapshotProtectedBytes =
            2L * SnapshotCrypto.MaxTerminalCoreGraphicsSnapshotPlaintextBytes + 1024 * 1024;
        private const string ChunkOutboxFileName = "chunk-outbox.dpapi";
        private const string TranscriptArchiveDirectoryName = "transcript-archive";
        private const string TranscriptArchiveExtension = ".transcript.dpapi";
        private const string DeleteMarkerPrefix = ".delete-";
        private const string DeleteMarkerSuffix = ".marker";
        private const string InactiveSinceFileName = "inactive-since.dpapi";
        internal static readonly TimeSpan InactiveHistoryRetention = TimeSpan.FromDays(7);
        private readonly string root;
        private readonly Guid pcDeviceId;
        private readonly Action<string, byte[]> durableWrite;
        private readonly Func<Guid, TerminalSemanticProfile> profileProvider;
        private readonly ConcurrentDictionary<Guid, object> sessionLocks = new ConcurrentDictionary<Guid, object>();
        private readonly ConcurrentDictionary<Guid, TerminalSemanticProfile> validatedProfiles = new ConcurrentDictionary<Guid, TerminalSemanticProfile>();
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = false
        };

        public ProductionTerminalHistoryStore(string root, Guid pcDeviceId)
            : this(root, pcDeviceId, AtomicWrite, null)
        {
        }

        public ProductionTerminalHistoryStore(string root, Guid pcDeviceId, Func<Guid, TerminalSemanticProfile> profileProvider)
            : this(root, pcDeviceId, AtomicWrite, profileProvider)
        {
        }

        internal ProductionTerminalHistoryStore(string root, Guid pcDeviceId, Action<string, byte[]> durableWrite)
            : this(root, pcDeviceId, durableWrite, null)
        {
        }

        internal ProductionTerminalHistoryStore(
            string root,
            Guid pcDeviceId,
            Action<string, byte[]> durableWrite,
            Func<Guid, TerminalSemanticProfile> profileProvider)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("History store root is required.", nameof(root));
            if (pcDeviceId == Guid.Empty) throw new ArgumentException("PC device ID is required.", nameof(pcDeviceId));
            this.durableWrite = durableWrite ?? throw new ArgumentNullException(nameof(durableWrite));
            this.profileProvider = profileProvider;
            this.root = Path.GetFullPath(root);
            this.pcDeviceId = pcDeviceId;
            Directory.CreateDirectory(this.root);
            Directory.CreateDirectory(BrokerCaptureRootPath);
            CompleteInterruptedDeletes();
        }

        public string RootPath => root;
        internal string BrokerCaptureRootPath => Path.Combine(root, "broker-captures");
        // Consumers retain only metadata. Never retain or modify the borrowed payload.
        internal event Action<LocalFrame> FrameObserved;

        internal string TerminalCompatibilityError(Guid sessionId)
        {
            lock (SessionLock(sessionId))
            {
                try { RequireSessionProfile(sessionId, false); return string.Empty; }
                catch (TerminalHistoryProfileException error) { return error.Code; }
            }
        }

        internal TerminalSemanticProfile GetSessionProfile(Guid sessionId)
        {
            lock (SessionLock(sessionId))
            {
                RequireSessionProfile(sessionId, false);
                return validatedProfiles.TryGetValue(sessionId, out var profile) ? profile
                    : throw new TerminalHistoryProfileException(TerminalSessionProfile.Missing);
            }
        }

        public void AppendCommittedFrame(LocalFrame frame)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.SessionId == Guid.Empty || frame.Sequence == 0)
                throw new InvalidDataException("A durable terminal frame must have session and sequence identity.");
            var encoded = LocalFrameCodec.Encode(frame);
            try
            {
                lock (SessionLock(frame.SessionId))
                {
                    var directory = WalDirectory(frame.SessionId);
                    Directory.CreateDirectory(directory);
                    var path = Path.Combine(directory, frame.Sequence.ToString("D20") + WalExtension);
                    if (File.Exists(path))
                    {
                        var existing = ReadProtected(path, frame.SessionId, "wal");
                        try
                        {
                            if (!CryptographicOperations.FixedTimeEquals(existing, encoded))
                                throw new InvalidDataException("A terminal WAL sequence was replayed with different bytes.");
                        }
                        finally { CryptographicOperations.ZeroMemory(existing); }
                        FrameObserved?.Invoke(frame);
                        return;
                    }
                    WriteProtectedAtomic(path, frame.SessionId, "wal", encoded);
                    FrameObserved?.Invoke(frame);
                }
            }
            finally { CryptographicOperations.ZeroMemory(encoded); }
        }

        public IList<LocalFrame> ReadFrames(Guid sessionId) => ReadFramesAfter(sessionId, 0);

        internal IList<LocalFrame> ReadFrames(Guid sessionId, Func<bool> shouldYield) =>
            ReadFramesAfter(sessionId, 0, shouldYield);

        public IList<LocalFrame> ReadFramesAfter(Guid sessionId, ulong afterSequence)
        {
            return ReadFramesAfter(sessionId, afterSequence, shouldYield: null);
        }

        public IList<LocalFrame> ReadContiguousFrames(
            Guid sessionId,
            ulong afterSequence,
            ulong throughSequence,
            int maximumFrames,
            int maximumDecodedBytes,
            out bool gapDetected)
        {
            ValidateSessionId(sessionId);
            if (throughSequence <= afterSequence) throw new ArgumentOutOfRangeException(nameof(throughSequence));
            if (maximumFrames < 1) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
            if (maximumDecodedBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumDecodedBytes));
            lock (SessionLock(sessionId))
            {
                gapDetected = false;
                RequireSessionProfile(sessionId, false);
                var directory = ResolveWalDirectory(sessionId);
                var frames = new List<LocalFrame>(maximumFrames);
                if (!Directory.Exists(directory)) return frames;
                var sequence = checked(afterSequence + 1);
                var decodedBytes = 0;
                while (sequence <= throughSequence && frames.Count < maximumFrames)
                {
                    var path = Path.Combine(directory, sequence.ToString("D20") + WalExtension);
                    if (!File.Exists(path))
                    {
                        gapDetected = HasWalSequenceAfter(directory, sequence, throughSequence);
                        break;
                    }
                    var encoded = ReadProtected(path, sessionId, "wal");
                    try
                    {
                        if (encoded.Length > maximumDecodedBytes)
                            throw new InvalidDataException("A terminal WAL frame exceeds the history page bound.");
                        if (frames.Count > 0 && decodedBytes > maximumDecodedBytes - encoded.Length) break;
                        var frame = LocalFrameCodec.Decode(encoded);
                        if (frame.SessionId != sessionId || frame.Sequence != sequence)
                            throw new InvalidDataException("A terminal WAL frame identity is invalid.");
                        FrameObserved?.Invoke(frame);
                        frames.Add(frame);
                        decodedBytes = checked(decodedBytes + encoded.Length);
                    }
                    finally { CryptographicOperations.ZeroMemory(encoded); }
                    if (sequence == ulong.MaxValue) break;
                    sequence++;
                }
                return frames;
            }
        }

        private IList<LocalFrame> ReadFramesAfter(
            Guid sessionId,
            ulong afterSequence,
            Func<bool> shouldYield)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                RequireSessionProfile(sessionId, false);
                var directory = WalDirectory(sessionId);
                if (!Directory.Exists(directory)) return new List<LocalFrame>();
                var paths = Directory.GetFiles(directory, "*" + WalExtension)
                    .OrderBy(value => Path.GetFileName(value), StringComparer.Ordinal)
                    .ToArray();
                var frames = new List<LocalFrame>(paths.Length);
                ulong previous = 0;
                foreach (var path in paths)
                {
                    // DPAPI and protobuf decoding of a large persisted backlog
                    // is intentionally preemptible between immutable WAL files.
                    // Returning null discards only this in-memory read attempt.
                    if (shouldYield?.Invoke() == true) return null;
                    var name = Path.GetFileName(path);
                    var sequenceText = name.Substring(0, name.Length - WalExtension.Length);
                    if (!ulong.TryParse(sequenceText, out var sequence) || sequence == 0 || sequence <= previous)
                        throw new InvalidDataException("A terminal WAL filename is invalid.");
                    previous = sequence;
                    if (sequence <= afterSequence) continue;
                    var encoded = ReadProtected(path, sessionId, "wal");
                    try
                    {
                        var frame = LocalFrameCodec.Decode(encoded);
                        if (frame.SessionId != sessionId || frame.Sequence != sequence)
                            throw new InvalidDataException("A terminal WAL frame identity is invalid.");
                        FrameObserved?.Invoke(frame);
                        frames.Add(frame);
                    }
                    finally { CryptographicOperations.ZeroMemory(encoded); }
                }
                return frames;
            }
        }

        public IList<Guid> ListSessions()
        {
            var result = new List<Guid>();
            if (!Directory.Exists(root)) return result;
            foreach (var directory in new DirectoryInfo(root).GetDirectories())
            {
                if (Guid.TryParseExact(directory.Name, "N", out var sessionId) &&
                    !IsSessionDeleted(sessionId) &&
                    Directory.Exists(Path.Combine(directory.FullName, "wal")))
                {
                    result.Add(sessionId);
                }
            }
            result.Sort();
            return result;
        }

        public bool ContainsSession(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                return !File.Exists(DeleteMarkerPath(sessionId)) &&
                    Directory.Exists(ResolveSessionDirectory(sessionId));
            }
        }

        public bool IsSessionDeleted(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            // Markers are atomically published and never removed. Observing one
            // must not wait for WAL replay or recursive cleanup under SessionLock.
            return File.Exists(DeleteMarkerPath(sessionId));
        }

        public void AppendTranscriptArchivePage(
            Guid sessionId,
            IReadOnlyList<PortableTerminalRowV1> rows)
        {
            ValidateSessionId(sessionId);
            if (rows == null || rows.Count != PortableTerminalModelV1.TranscriptArchivePageRows)
                throw new InvalidDataException("A terminal transcript archive page has the wrong row count.");
            var page = ToArchivePage(sessionId, rows);
            ValidateTranscriptArchivePage(page, sessionId);
            lock (SessionLock(sessionId))
            {
                var directory = TranscriptArchiveDirectory(sessionId);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(
                    directory,
                    page.FirstLineId.ToString("D20") + "-" + page.LastLineId.ToString("D20") +
                    TranscriptArchiveExtension);
                if (File.Exists(path))
                {
                    var existing = ReadJson<ProductionTerminalTranscriptArchivePage>(
                        path,
                        sessionId,
                        "transcript-archive");
                    ValidateTranscriptArchivePage(existing, sessionId);
                    if (!TranscriptArchivePagesEqual(existing, page))
                        throw new InvalidDataException("A terminal transcript archive range changed after sealing.");
                    return;
                }
                WriteJson(path, sessionId, "transcript-archive", page);
            }
        }

        public IList<IReadOnlyList<PortableTerminalRowV1>> ReadTranscriptArchivePages(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                var directory = TranscriptArchiveDirectory(sessionId);
                var result = new List<IReadOnlyList<PortableTerminalRowV1>>();
                if (!Directory.Exists(directory)) return result;
                ulong previousLast = 0;
                foreach (var path in Directory.GetFiles(directory, "*" + TranscriptArchiveExtension)
                    .OrderBy(value => Path.GetFileName(value), StringComparer.Ordinal))
                {
                    var page = ReadJson<ProductionTerminalTranscriptArchivePage>(
                        path,
                        sessionId,
                        "transcript-archive");
                    ValidateTranscriptArchivePage(page, sessionId);
                    if (previousLast != 0 && page.FirstLineId != checked(previousLast + 1))
                        throw new InvalidDataException("Terminal transcript archive pages are not contiguous.");
                    result.Add(FromArchivePage(page));
                    previousLast = page.LastLineId;
                }
                return result;
            }
        }

        public void SaveCheckpoint(ProductionTerminalHistoryCheckpoint checkpoint)
        {
            ValidateCheckpoint(checkpoint);
            lock (SessionLock(checkpoint.SessionId))
            {
                WriteJson(Path.Combine(SessionDirectory(checkpoint.SessionId), CheckpointFileName), checkpoint.SessionId, "checkpoint", checkpoint);
            }
        }

        public ProductionTerminalHistoryCheckpoint LoadCheckpoint(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), CheckpointFileName);
                if (!File.Exists(path)) return null;
                var value = ReadJson<ProductionTerminalHistoryCheckpoint>(path, sessionId, "checkpoint");
                ValidateCheckpoint(value);
                if (value.SessionId != sessionId) throw new InvalidDataException("Terminal checkpoint session mismatch.");
                return value;
            }
        }

        public void SaveOutbox(ProductionTerminalHistoryOutbox outbox)
        {
            ValidateOutbox(outbox);
            lock (SessionLock(outbox.SessionId))
            {
                SaveOutboxLocked(outbox);
            }
        }

        public void SaveProduction(
            ProductionTerminalHistoryCheckpoint checkpoint,
            ProductionTerminalHistoryOutbox outbox)
        {
            ValidateCheckpoint(checkpoint);
            ValidateOutbox(outbox);
            if (!checkpoint.SnapshotEligible || checkpoint.SessionId != outbox.SessionId ||
                checkpoint.LastSequence != outbox.SnapshotSequence)
            {
                throw new InvalidDataException(
                    "Terminal checkpoint and outbox production are inconsistent.");
            }
            var snapshot = outbox.Artifacts.Single(
                value => value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot);
            var expectedAuthorityVersion =
                (snapshot.FormatName == SnapshotCrypto.TerminalCoreSnapshotFormatName &&
                snapshot.FormatVersion == SnapshotCrypto.TerminalCoreSnapshotFormatVersion) ||
                (snapshot.FormatName == SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatName &&
                snapshot.FormatVersion == SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatVersion)
                    ? 2
                    : snapshot.FormatName == SnapshotCrypto.SnapshotFormatName &&
                        snapshot.FormatVersion == SnapshotCrypto.FormatVersion
                        ? 1
                        : 0;
            if (checkpoint.AuthorityVersion != expectedAuthorityVersion ||
                !CryptographicOperations.FixedTimeEquals(
                    checkpoint.SnapshotPlaintext,
                    snapshot.Plaintext))
            {
                throw new InvalidDataException(
                    "Terminal checkpoint authority does not match its outbox snapshot.");
            }
            lock (SessionLock(checkpoint.SessionId))
            {
                var checkpointPath = Path.Combine(
                    SessionDirectory(checkpoint.SessionId),
                    CheckpointFileName);
                var existingCheckpoint = File.Exists(checkpointPath)
                    ? ReadJson<ProductionTerminalHistoryCheckpoint>(
                        checkpointPath,
                        checkpoint.SessionId,
                        "checkpoint")
                    : null;
                if (existingCheckpoint != null) ValidateCheckpoint(existingCheckpoint);
                var allowAuthorityUpgrade = existingCheckpoint != null &&
                    existingCheckpoint.SessionId == checkpoint.SessionId &&
                    existingCheckpoint.LastSequence == checkpoint.LastSequence &&
                    existingCheckpoint.AuthorityVersion < 2 &&
                    checkpoint.AuthorityVersion == 2;
                // Publish the retryable outbox first. A crash before the checkpoint write
                // leaves a detectable mismatch that the next WAL pass rebuilds; the reverse
                // order could expose a new checkpoint with stale delivery artifacts.
                if (!SaveOutboxLocked(outbox, allowAuthorityUpgrade))
                    throw new InvalidOperationException(
                        "A newer terminal history production already exists.");
                WriteJson(
                    checkpointPath,
                    checkpoint.SessionId,
                    "checkpoint",
                    checkpoint);
            }
        }

        private bool SaveOutboxLocked(
            ProductionTerminalHistoryOutbox outbox,
            bool allowAuthorityUpgrade = false)
        {
            var path = Path.Combine(SessionDirectory(outbox.SessionId), OutboxFileName);
            var previousPath = Path.Combine(SessionDirectory(outbox.SessionId), PreviousSnapshotFileName);
            var existing = File.Exists(path)
                ? ReadPersistedOutbox(path, outbox.SessionId)
                : null;
            if (existing != null)
            {
                if (existing.SnapshotSequence > outbox.SnapshotSequence) return false;
                // A newer checkpoint contains a newer visible screen and a freshly generated
                // transcript index. It supersedes incomplete older delivery state.
                if (existing.SnapshotSequence == outbox.SnapshotSequence)
                {
                    if (ArtifactsEqual(existing.Artifacts, outbox.Artifacts))
                    {
                        outbox.Deliveries = existing.Deliveries;
                    }
                    else if (!IsTerminalCoreAuthorityUpgrade(
                        existing,
                        outbox,
                        allowAuthorityUpgrade))
                    {
                        throw new InvalidDataException(
                            "A terminal outbox anchor changed its plaintext artifacts.");
                    }
                }
            }
            if (existing != null && existing.SnapshotSequence < outbox.SnapshotSequence)
            {
                // The current outbox remains the only delivery authority. Keep just its
                // snapshot in a separate DPAPI domain before replacing it with the new S.
                var previous = new ProductionTerminalHistoryPreviousSnapshot
                {
                    SessionId = outbox.SessionId,
                    Artifact = existing.Artifacts.Single(value =>
                        value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
                };
                try { WriteJson(previousPath, outbox.SessionId, "previous-snapshot", previous); }
                finally { CryptographicOperations.ZeroMemory(previous.Artifact.Plaintext); }
            }
            else if (existing == null && File.Exists(previousPath))
                File.Delete(previousPath);
            WriteJson(path, outbox.SessionId, "outbox", outbox);
            return true;
        }

        private static bool IsTerminalCoreAuthorityUpgrade(
            ProductionTerminalHistoryOutbox existing,
            ProductionTerminalHistoryOutbox replacement,
            bool allowAuthorityUpgrade)
        {
            var oldSnapshot = existing.Artifacts.SingleOrDefault(
                value => value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot);
            var newSnapshot = replacement.Artifacts.SingleOrDefault(
                value => value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot);
            var oldIsPortable = oldSnapshot != null &&
                oldSnapshot.FormatName == SnapshotCrypto.SnapshotFormatName &&
                oldSnapshot.FormatVersion == SnapshotCrypto.FormatVersion;
            var oldIsHybridCore = oldSnapshot != null &&
                oldSnapshot.FormatName == SnapshotCrypto.TerminalCoreSnapshotFormatName &&
                oldSnapshot.FormatVersion == SnapshotCrypto.TerminalCoreSnapshotFormatVersion &&
                existing.Artifacts.Any(
                    value => value.Kind == ProductionTerminalHistoryArtifactKind.Transcript);
            return allowAuthorityUpgrade && oldSnapshot != null && newSnapshot != null &&
                (oldIsPortable || oldIsHybridCore) &&
                newSnapshot.FormatName == SnapshotCrypto.TerminalCoreSnapshotFormatName &&
                newSnapshot.FormatVersion == SnapshotCrypto.TerminalCoreSnapshotFormatVersion &&
                oldSnapshot.SnapshotSequence == newSnapshot.SnapshotSequence &&
                replacement.Artifacts.Count == 1 &&
                newSnapshot.TranscriptPageCount == 0 &&
                replacement.Deliveries.Count == 0;
        }

        public ProductionTerminalHistoryOutbox LoadOutbox(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), OutboxFileName);
                if (!File.Exists(path)) return null;
                var value = ReadPersistedOutbox(path, sessionId);
                if (value.SessionId != sessionId) throw new InvalidDataException("Terminal outbox session mismatch.");
                return value.Clone();
            }
        }

        // A future Delta producer may compare this exact prior artifact with the
        // current outbox only when its observed current S still matches.
        internal ProductionTerminalHistoryArtifact ReadPreviousSnapshotArtifact(
            Guid sessionId, ulong currentSnapshotSequence)
        {
            ValidateSessionId(sessionId);
            if (currentSnapshotSequence == 0) throw new ArgumentOutOfRangeException(nameof(currentSnapshotSequence));
            lock (SessionLock(sessionId))
            {
                if (File.Exists(DeleteMarkerPath(sessionId))) return null;
                var directory = ResolveSessionDirectory(sessionId);
                var currentPath = Path.Combine(directory, OutboxFileName);
                if (!File.Exists(currentPath)) return null;
                var current = ReadPersistedOutbox(currentPath, sessionId);
                try
                {
                    return current.SnapshotSequence == currentSnapshotSequence
                        ? ReadPreviousSnapshotArtifactFromCurrent(sessionId, current)
                        : null;
                }
                finally
                {
                    foreach (var artifact in current.Artifacts)
                        if (artifact?.Plaintext != null)
                            CryptographicOperations.ZeroMemory(artifact.Plaintext);
                    foreach (var delivery in current.Deliveries.Values)
                        foreach (var sealedObject in delivery.SealedObjects.Values)
                            if (sealedObject != null) CryptographicOperations.ZeroMemory(sealedObject);
                }
            }
        }

        private ProductionTerminalHistoryArtifact ReadPreviousSnapshotArtifactFromCurrent(
            Guid sessionId, ProductionTerminalHistoryOutbox current)
        {
            var directory = ResolveSessionDirectory(sessionId);
            var checkpointPath = Path.Combine(directory, CheckpointFileName);
            var previousPath = Path.Combine(directory, PreviousSnapshotFileName);
            if (!File.Exists(checkpointPath) || !File.Exists(previousPath)) return null;
            if (new FileInfo(previousPath).Length > MaximumPreviousSnapshotProtectedBytes)
                throw new InvalidDataException("Previous terminal snapshot exceeds its protected size limit.");
            var checkpoint = ReadJson<ProductionTerminalHistoryCheckpoint>(
                checkpointPath, sessionId, "checkpoint");
            try
            {
                ValidateCheckpoint(checkpoint);
                var currentSnapshot = current.Artifacts.Single(value =>
                    value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot);
                if (checkpoint.SessionId != sessionId || !checkpoint.SnapshotEligible ||
                    checkpoint.LastSequence != current.SnapshotSequence ||
                    !CryptographicOperations.FixedTimeEquals(
                        checkpoint.SnapshotPlaintext, currentSnapshot.Plaintext))
                    return null;
            }
            finally
            {
                if (checkpoint?.SnapshotPlaintext != null)
                    CryptographicOperations.ZeroMemory(checkpoint.SnapshotPlaintext);
                if (checkpoint?.TranscriptPlaintexts != null)
                    foreach (var transcript in checkpoint.TranscriptPlaintexts)
                        if (transcript != null) CryptographicOperations.ZeroMemory(transcript);
            }
            var retained = ReadJson<ProductionTerminalHistoryPreviousSnapshot>(
                previousPath, sessionId, "previous-snapshot");
            try
            {
                var previous = retained?.Artifact;
                if (retained?.SessionId != sessionId || previous == null ||
                    previous.Kind != ProductionTerminalHistoryArtifactKind.Snapshot ||
                    previous.SnapshotSequence == 0 || previous.UploadOrder != 0 ||
                    string.IsNullOrWhiteSpace(previous.ArtifactId) ||
                    previous.InitialTranscriptBeforeLineId == 0 ||
                    previous.TranscriptPageCount < 0 ||
                    previous.TranscriptPageCount > MaximumTranscriptPublicationPageCount ||
                    previous.Plaintext == null || previous.Plaintext.Length == 0 ||
                    !IsSupportedSnapshotFormat(previous.FormatName, previous.FormatVersion))
                    throw new InvalidDataException("Previous terminal snapshot is invalid.");
                if (previous.SnapshotSequence >= current.SnapshotSequence) return null;
                ValidateSnapshotPlaintext(new ProductionTerminalHistoryOutbox
                { SessionId = sessionId, SnapshotSequence = previous.SnapshotSequence }, previous);
                retained.Artifact = null;
                return previous;
            }
            finally
            {
                if (retained?.Artifact?.Plaintext != null)
                    CryptographicOperations.ZeroMemory(retained.Artifact.Plaintext);
            }
        }

        internal ProductionTerminalHistoryDeltaSnapshotPair ReadDeltaSnapshotPair(
            Guid sessionId, ulong requestedSnapshotSequence)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                if (File.Exists(DeleteMarkerPath(sessionId))) return null;
                if (!RequireSessionProfile(sessionId, false).IsGraphics)
                    return null;
                var path = Path.Combine(ResolveSessionDirectory(sessionId), OutboxFileName);
                if (!File.Exists(path)) return null;
                var outbox = ReadPersistedOutbox(path, sessionId);
                try
                {
                    if (requestedSnapshotSequence != 0 &&
                        requestedSnapshotSequence != outbox.SnapshotSequence) return null;
                    var current = outbox.Artifacts.Single(value =>
                        value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot);
                    if (current.FormatName != SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatName ||
                        current.FormatVersion != SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatVersion)
                        throw new TerminalHistoryProfileException(TerminalSessionProfile.Mismatch);
                    var previous = ReadPreviousSnapshotArtifactFromCurrent(sessionId, outbox);
                    outbox.Artifacts.Remove(current);
                    return new ProductionTerminalHistoryDeltaSnapshotPair
                    { Current = current, Previous = previous };
                }
                finally
                {
                    foreach (var artifact in outbox.Artifacts)
                        if (artifact?.Plaintext != null) CryptographicOperations.ZeroMemory(artifact.Plaintext);
                    foreach (var delivery in outbox.Deliveries.Values)
                        foreach (var bytes in delivery.SealedObjects.Values)
                            if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        // Frozen browsing must never normalize or rewrite the persisted outbox.
        internal ProductionTerminalHistoryArtifact ReadExactGraphicsSnapshot(
            Guid sessionId, ulong snapshotSequence, out ulong availableSequence)
        {
            ValidateSessionId(sessionId);
            availableSequence = 0;
            lock (SessionLock(sessionId))
            {
                if (File.Exists(DeleteMarkerPath(sessionId))) return null;
                if (!RequireSessionProfile(sessionId, false).IsGraphics)
                    throw new TerminalHistoryProfileException(TerminalSessionProfile.Mismatch);
                var path = Path.Combine(ResolveSessionDirectory(sessionId), OutboxFileName);
                if (!File.Exists(path)) return null;
                var outbox = ReadJson<ProductionTerminalHistoryOutbox>(path, sessionId, "outbox");
                try
                {
                    ValidateOutbox(outbox);
                    availableSequence = outbox.SnapshotSequence;
                    if (snapshotSequence != availableSequence) return null;
                    var artifact = outbox.Artifacts.Single(value =>
                        value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot);
                    if (artifact.FormatName != SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatName ||
                        artifact.FormatVersion != SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatVersion)
                        throw new TerminalHistoryProfileException(TerminalSessionProfile.Mismatch);
                    return artifact.Clone();
                }
                finally
                {
                    if (outbox?.Artifacts != null)
                        foreach (var artifact in outbox.Artifacts)
                            if (artifact?.Plaintext != null) CryptographicOperations.ZeroMemory(artifact.Plaintext);
                    if (outbox?.Deliveries != null)
                        foreach (var delivery in outbox.Deliveries.Values)
                            if (delivery?.SealedObjects != null)
                                foreach (var bytes in delivery.SealedObjects.Values)
                                    if (bytes != null) CryptographicOperations.ZeroMemory(bytes);
                }
            }
        }

        public ProductionTerminalHistoryPreparedUpload PrepareNextUpload(
            Guid sessionId,
            Guid recipientDeviceId,
            long keyEpoch,
            Func<ProductionTerminalHistoryArtifact, byte[]> seal)
        {
            ValidateSessionId(sessionId);
            if (recipientDeviceId == Guid.Empty || recipientDeviceId == pcDeviceId)
                throw new ArgumentException("A distinct recipient device is required.", nameof(recipientDeviceId));
            if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            if (seal == null) throw new ArgumentNullException(nameof(seal));
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), OutboxFileName);
                if (!File.Exists(path)) return null;
                var outbox = ReadPersistedOutbox(path, sessionId);
                var deliveryKey = DeliveryKey(recipientDeviceId, keyEpoch);
                if (!outbox.Deliveries.TryGetValue(deliveryKey, out var delivery))
                {
                    delivery = new ProductionTerminalHistoryDelivery
                    {
                        RecipientDeviceId = recipientDeviceId,
                        KeyEpoch = keyEpoch
                    };
                    outbox.Deliveries.Add(deliveryKey, delivery);
                }
                foreach (var artifact in outbox.Artifacts.OrderBy(value => value.UploadOrder))
                {
                    if (delivery.UploadedArtifactIds.Contains(artifact.ArtifactId)) continue;
                    if (!delivery.SealedObjects.TryGetValue(artifact.ArtifactId, out var sealedObject))
                    {
                        sealedObject = seal(artifact.Clone());
                        if (sealedObject == null || sealedObject.Length == 0)
                            throw new InvalidDataException("Terminal artifact sealing returned no ciphertext.");
                        delivery.SealedObjects[artifact.ArtifactId] = (byte[])sealedObject.Clone();
                        WriteJson(path, sessionId, "outbox", outbox);
                    }
                    return new ProductionTerminalHistoryPreparedUpload
                    {
                        SessionId = sessionId,
                        RecipientDeviceId = recipientDeviceId,
                        KeyEpoch = keyEpoch,
                        Artifact = artifact.Clone(),
                        SealedObject = (byte[])sealedObject.Clone()
                    };
                }
                return null;
            }
        }

        public ProductionTerminalHistoryPreparedUpload PrepareArtifact(
            Guid sessionId,
            Guid recipientDeviceId,
            long keyEpoch,
            Func<ProductionTerminalHistoryArtifact, bool> selector,
            Func<ProductionTerminalHistoryArtifact, byte[]> seal)
        {
            ValidateSessionId(sessionId);
            if (recipientDeviceId == Guid.Empty || recipientDeviceId == pcDeviceId)
                throw new ArgumentException("A distinct recipient device is required.", nameof(recipientDeviceId));
            if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            if (selector == null) throw new ArgumentNullException(nameof(selector));
            if (seal == null) throw new ArgumentNullException(nameof(seal));
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), OutboxFileName);
                if (!File.Exists(path)) return null;
                var outbox = ReadPersistedOutbox(path, sessionId);
                var artifact = outbox.Artifacts
                    .OrderBy(value => value.UploadOrder)
                    .FirstOrDefault(value => selector(value.Clone()));
                if (artifact == null) return null;
                return PrepareSealedArtifact(path, sessionId, recipientDeviceId, keyEpoch, artifact, outbox, seal);
            }
        }

        /// <summary>
        /// Prepares the newest snapshot artifact for the recipient. Multiple
        /// production passes legitimately coexist in the outbox (each output
        /// burst produces a newer snapshot); serving the oldest one would hand
        /// the phone a pre-burst screen and permanently break transcript
        /// paging for that restore.
        /// </summary>
        public ProductionTerminalHistoryPreparedUpload PrepareLatestSnapshotArtifact(
            Guid sessionId,
            Guid recipientDeviceId,
            long keyEpoch,
            Func<ProductionTerminalHistoryArtifact, byte[]> seal)
        {
            ValidateSessionId(sessionId);
            if (recipientDeviceId == Guid.Empty || recipientDeviceId == pcDeviceId)
                throw new ArgumentException("A distinct recipient device is required.", nameof(recipientDeviceId));
            if (keyEpoch < 1) throw new ArgumentOutOfRangeException(nameof(keyEpoch));
            if (seal == null) throw new ArgumentNullException(nameof(seal));
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), OutboxFileName);
                if (!File.Exists(path)) return null;
                var outbox = ReadPersistedOutbox(path, sessionId);
                var artifact = outbox.Artifacts
                    .Where(value => value.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
                    .OrderByDescending(value => value.SnapshotSequence)
                    .ThenByDescending(value => value.UploadOrder)
                    .FirstOrDefault();
                if (artifact == null) return null;
                return PrepareSealedArtifact(path, sessionId, recipientDeviceId, keyEpoch, artifact, outbox, seal);
            }
        }

        private ProductionTerminalHistoryPreparedUpload PrepareSealedArtifact(
            string outboxPath,
            Guid sessionId,
            Guid recipientDeviceId,
            long keyEpoch,
            ProductionTerminalHistoryArtifact artifact,
            ProductionTerminalHistoryOutbox outbox,
            Func<ProductionTerminalHistoryArtifact, byte[]> seal)
        {
            var deliveryKey = DeliveryKey(recipientDeviceId, keyEpoch);
            if (!outbox.Deliveries.TryGetValue(deliveryKey, out var delivery))
            {
                delivery = new ProductionTerminalHistoryDelivery
                {
                    RecipientDeviceId = recipientDeviceId,
                    KeyEpoch = keyEpoch
                };
                outbox.Deliveries.Add(deliveryKey, delivery);
            }
            if (!delivery.SealedObjects.TryGetValue(artifact.ArtifactId, out var sealedObject))
            {
                sealedObject = seal(artifact.Clone());
                if (sealedObject == null || sealedObject.Length == 0)
                    throw new InvalidDataException("Terminal artifact sealing returned no ciphertext.");
                delivery.SealedObjects[artifact.ArtifactId] = (byte[])sealedObject.Clone();
                WriteJson(outboxPath, sessionId, "outbox", outbox);
            }
            return new ProductionTerminalHistoryPreparedUpload
            {
                SessionId = sessionId,
                RecipientDeviceId = recipientDeviceId,
                KeyEpoch = keyEpoch,
                Artifact = artifact.Clone(),
                SealedObject = (byte[])sealedObject.Clone()
            };
        }

        /// <summary>
        /// Bounded structural metadata for diagnostics: the transcript page
        /// cursors currently baked for the session. Never includes terminal
        /// text or key material.
        /// </summary>
        public string DescribeTranscriptPageCursors(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), OutboxFileName);
                if (!File.Exists(path)) return "no_outbox";
                var outbox = ReadPersistedOutbox(path, sessionId);
                var pages = outbox.Artifacts
                    .Where(value => value.Kind == ProductionTerminalHistoryArtifactKind.Transcript)
                    .OrderBy(value => value.SnapshotSequence)
                    .ThenBy(value => value.ExclusiveBeforeLineId)
                    .Take(12)
                    .Select(value => "(seq=" + value.SnapshotSequence +
                        " before=" + value.ExclusiveBeforeLineId +
                        " lines=" + value.FirstLineId + ".." + value.LastLineId + ")");
                return "count=" + outbox.Artifacts.Count(value =>
                    value.Kind == ProductionTerminalHistoryArtifactKind.Transcript) +
                    " cursors=" + string.Join(",", pages);
            }
        }

        public void MarkSessionDeleted(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                var marker = DeleteMarkerPath(sessionId);
                if (!File.Exists(marker)) durableWrite(marker, sessionId.ToByteArray());
                if (!File.Exists(marker))
                    throw new IOException("Terminal history deletion marker was not persisted.");
            }
        }

        // Called with the registry's liveness/deletion lock held. Last output time
        // and filesystem mtimes cannot prove when a shell stopped. Legacy history
        // starts its grace period at the first confirmed inactive observation.
        internal bool IsInactiveRetentionExpired(Guid sessionId, bool processAlive, DateTimeOffset now)
        {
            ValidateSessionId(sessionId);
            if (now <= DateTimeOffset.UnixEpoch) throw new ArgumentOutOfRangeException(nameof(now));
            lock (SessionLock(sessionId))
            {
                if (IsSessionDeleted(sessionId)) return false;
                var path = Path.Combine(ResolveSessionDirectory(sessionId), InactiveSinceFileName);
                if (processAlive)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return false;
                }
                if (!File.Exists(path))
                {
                    WriteJson(path, sessionId, "inactive-since", now);
                    return false;
                }
                var since = ReadJson<DateTimeOffset>(path, sessionId, "inactive-since");
                if (since <= DateTimeOffset.UnixEpoch) throw new InvalidDataException("Invalid history retention timestamp.");
                return now > since && now - since > InactiveHistoryRetention;
            }
        }

        public void DeleteSession(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            lock (SessionLock(sessionId))
            {
                MarkSessionDeleted(sessionId);
                var directory = ResolveSessionDirectory(sessionId);
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                validatedProfiles.TryRemove(sessionId, out _);
            }
        }

        public void MarkUploaded(ProductionTerminalHistoryPreparedUpload upload)
        {
            if (upload == null || upload.Artifact == null) throw new ArgumentNullException(nameof(upload));
            ValidateSessionId(upload.SessionId);
            lock (SessionLock(upload.SessionId))
            {
                var path = Path.Combine(SessionDirectory(upload.SessionId), OutboxFileName);
                if (!File.Exists(path)) throw new InvalidOperationException("Terminal outbox no longer exists.");
                var outbox = ReadPersistedOutbox(path, upload.SessionId);
                if (outbox.SnapshotSequence != upload.Artifact.SnapshotSequence ||
                    !outbox.Artifacts.Any(value => value.ArtifactId == upload.Artifact.ArtifactId))
                {
                    throw new InvalidOperationException("Terminal upload belongs to a superseded outbox.");
                }
                var key = DeliveryKey(upload.RecipientDeviceId, upload.KeyEpoch);
                if (!outbox.Deliveries.TryGetValue(key, out var delivery) ||
                    !delivery.SealedObjects.TryGetValue(upload.Artifact.ArtifactId, out var sealedObject) ||
                    !CryptographicOperations.FixedTimeEquals(sealedObject, upload.SealedObject))
                {
                    throw new InvalidOperationException("Terminal upload does not match its persisted sealed object.");
                }
                delivery.UploadedArtifactIds.Add(upload.Artifact.ArtifactId);
                WriteJson(path, upload.SessionId, "outbox", outbox);
            }
        }

        public bool AreAllUploaded(Guid sessionId, ulong snapshotSequence, Guid recipientDeviceId, long keyEpoch)
        {
            var outbox = LoadOutbox(sessionId);
            if (outbox == null || outbox.SnapshotSequence != snapshotSequence) return false;
            if (!outbox.Deliveries.TryGetValue(DeliveryKey(recipientDeviceId, keyEpoch), out var delivery)) return false;
            return outbox.Artifacts.All(value => delivery.UploadedArtifactIds.Contains(value.ArtifactId));
        }

        public void RetainDeliveries(
            Guid sessionId,
            IEnumerable<(Guid RecipientDeviceId, long KeyEpoch)> recipients)
        {
            ValidateSessionId(sessionId);
            if (recipients == null) throw new ArgumentNullException(nameof(recipients));
            var retained = new HashSet<string>(
                recipients.Select(value => DeliveryKey(value.RecipientDeviceId, value.KeyEpoch)),
                StringComparer.Ordinal);
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), OutboxFileName);
                if (!File.Exists(path)) return;
                var outbox = ReadPersistedOutbox(path, sessionId);
                var changed = false;
                foreach (var key in outbox.Deliveries.Keys.ToArray())
                {
                    if (retained.Contains(key)) continue;
                    outbox.Deliveries.Remove(key);
                    changed = true;
                }
                if (changed) WriteJson(path, sessionId, "outbox", outbox);
            }
        }

        public byte[] GetOrCreateSealedChunk(
            Guid sessionId,
            Guid recipientDeviceId,
            long keyEpoch,
            ulong startSequence,
            ulong endSequence,
            byte[] plaintextDigest,
            Func<byte[]> seal)
        {
            ValidateSessionId(sessionId);
            if (recipientDeviceId == Guid.Empty || keyEpoch < 1 || startSequence == 0 || endSequence < startSequence ||
                plaintextDigest == null || plaintextDigest.Length != 32 || seal == null)
                throw new ArgumentException("Chunk outbox identity is invalid.");
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), ChunkOutboxFileName);
                var outbox = File.Exists(path)
                    ? ReadJson<ProductionHistoryChunkOutbox>(path, sessionId, "chunk-outbox")
                    : new ProductionHistoryChunkOutbox { SessionId = sessionId };
                ValidateChunkOutbox(outbox, sessionId);
                var deliveryPrefix = DeliveryKey(recipientDeviceId, keyEpoch) + ":" + startSequence.ToString("D20") + ":";
                var key = deliveryPrefix + endSequence.ToString("D20") + ":" + Convert.ToHexString(plaintextDigest).ToLowerInvariant();
                if (!outbox.SealedObjects.TryGetValue(key, out var sealedObject))
                {
                    foreach (var stale in outbox.SealedObjects.Keys.Where(value =>
                        value.StartsWith(deliveryPrefix, StringComparison.Ordinal) && value != key).ToArray())
                    {
                        CryptographicOperations.ZeroMemory(outbox.SealedObjects[stale]);
                        outbox.SealedObjects.Remove(stale);
                    }
                    sealedObject = seal();
                    if (sealedObject == null || sealedObject.Length == 0)
                        throw new InvalidDataException("History chunk sealing returned no ciphertext.");
                    outbox.SealedObjects[key] = (byte[])sealedObject.Clone();
                    WriteJson(path, sessionId, "chunk-outbox", outbox);
                }
                return (byte[])sealedObject.Clone();
            }
        }

        public void MarkChunkUploaded(
            Guid sessionId,
            Guid recipientDeviceId,
            long keyEpoch,
            ulong startSequence,
            ulong endSequence,
            byte[] plaintextDigest,
            byte[] sealedObject)
        {
            ValidateSessionId(sessionId);
            if (plaintextDigest == null || plaintextDigest.Length != 32 || sealedObject == null)
                throw new ArgumentException("Chunk upload identity is invalid.");
            lock (SessionLock(sessionId))
            {
                var path = Path.Combine(SessionDirectory(sessionId), ChunkOutboxFileName);
                if (!File.Exists(path)) return;
                var outbox = ReadJson<ProductionHistoryChunkOutbox>(path, sessionId, "chunk-outbox");
                ValidateChunkOutbox(outbox, sessionId);
                var key = DeliveryKey(recipientDeviceId, keyEpoch) + ":" + startSequence.ToString("D20") + ":" +
                    endSequence.ToString("D20") + ":" + Convert.ToHexString(plaintextDigest).ToLowerInvariant();
                if (!outbox.SealedObjects.TryGetValue(key, out var stored) ||
                    !CryptographicOperations.FixedTimeEquals(stored, sealedObject))
                    throw new InvalidOperationException("Uploaded history chunk does not match its persisted ciphertext.");
                CryptographicOperations.ZeroMemory(stored);
                outbox.SealedObjects.Remove(key);
                WriteJson(path, sessionId, "chunk-outbox", outbox);
            }
        }

        private object SessionLock(Guid sessionId) => sessionLocks.GetOrAdd(sessionId, _ => new object());

        private TerminalSemanticProfile RequireSessionProfile(Guid sessionId, bool createIfEmpty)
        {
            var expected = profileProvider?.Invoke(sessionId);
            if (validatedProfiles.TryGetValue(sessionId, out var cached))
            {
                if (expected != null && cached != expected) throw new TerminalHistoryProfileException(TerminalSessionProfile.Mismatch);
                return cached;
            }
            var directory = ResolveSessionDirectory(sessionId);
            var path = Path.Combine(directory, TerminalSessionProfile.FileName);
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > TerminalSessionProfile.MaximumProtectedBytes)
                    throw new TerminalHistoryProfileException(TerminalSessionProfile.Invalid);
                byte[] bytes = null;
                try
                {
                    bytes = ReadProtected(path, sessionId, "terminal-profile");
                    var actual = TerminalSessionProfile.Read(bytes, sessionId);
                    if (expected != null && actual != expected) throw new TerminalHistoryProfileException(TerminalSessionProfile.Mismatch);
                    validatedProfiles[sessionId] = actual;
                    return actual;
                }
                catch (InvalidDataException) { throw new TerminalHistoryProfileException(TerminalSessionProfile.Invalid); }
                finally { if (bytes != null) CryptographicOperations.ZeroMemory(bytes); }
            }
            // Absence never proves legacy semantics. Do not stamp or rewrite old payloads.
            if (Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Any(file => !string.Equals(file, Path.Combine(directory, InactiveSinceFileName), StringComparison.OrdinalIgnoreCase)))
                throw new TerminalHistoryProfileException(TerminalSessionProfile.Missing);
            if (profileProvider != null && expected == null) throw new TerminalHistoryProfileException(TerminalSessionProfile.Missing);
            var profile = expected ?? TerminalSemanticProfile.Legacy;
            if (!createIfEmpty) return profile;
            var descriptor = TerminalSessionProfile.Encode(sessionId, profile);
            try
            {
                WriteProtectedAtomic(path, sessionId, "terminal-profile", descriptor);
                validatedProfiles[sessionId] = profile;
            }
            finally { CryptographicOperations.ZeroMemory(descriptor); }
            return profile;
        }

        private string ResolveSessionDirectory(Guid sessionId)
        {
            ValidateSessionId(sessionId);
            var path = Path.GetFullPath(Path.Combine(root, sessionId.ToString("N")));
            if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Terminal history session path escaped its store root.");
            return path;
        }

        private string SessionDirectory(Guid sessionId)
        {
            if (File.Exists(DeleteMarkerPath(sessionId)))
                throw new InvalidOperationException("Terminal history session was permanently deleted.");
            var path = ResolveSessionDirectory(sessionId);
            Directory.CreateDirectory(path);
            return path;
        }

        private string WalDirectory(Guid sessionId) => Path.Combine(SessionDirectory(sessionId), "wal");

        private string ResolveWalDirectory(Guid sessionId) => Path.Combine(ResolveSessionDirectory(sessionId), "wal");

        private bool HasWalSequenceAfter(string directory, ulong missingSequence, ulong throughSequence)
        {
            foreach (var path in Directory.EnumerateFiles(directory, "*" + WalExtension))
            {
                var name = Path.GetFileName(path);
                var sequenceText = name.Substring(0, name.Length - WalExtension.Length);
                if (!ulong.TryParse(sequenceText, out var sequence) || sequence == 0)
                    throw new InvalidDataException("A terminal WAL filename is invalid.");
                if (sequence > missingSequence && sequence <= throughSequence) return true;
            }
            return false;
        }

        private string DeleteMarkerPath(Guid sessionId) =>
            Path.Combine(root, DeleteMarkerPrefix + sessionId.ToString("N") + DeleteMarkerSuffix);

        private void CompleteInterruptedDeletes()
        {
            foreach (var marker in Directory.EnumerateFiles(
                root,
                DeleteMarkerPrefix + "*" + DeleteMarkerSuffix,
                SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(marker);
                var value = name.Substring(
                    DeleteMarkerPrefix.Length,
                    name.Length - DeleteMarkerPrefix.Length - DeleteMarkerSuffix.Length);
                if (!Guid.TryParseExact(value, "N", out var sessionId)) continue;
                var directory = ResolveSessionDirectory(sessionId);
                try
                {
                    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
                }
                catch (Exception error) when (error is IOException ||
                    error is UnauthorizedAccessException ||
                    error is System.Security.SecurityException)
                {
                    // The permanent marker remains authoritative. A locked
                    // directory can be retried on a later clean Agent start.
                }
            }
        }

        internal Guid[] DeletedSessionIds() => Directory.EnumerateFiles(root, DeleteMarkerPrefix + "*" + DeleteMarkerSuffix)
            .Select(Path.GetFileName)
            .Where(name => name.Length == DeleteMarkerPrefix.Length + 32 + DeleteMarkerSuffix.Length)
            .Select(name => name.Substring(DeleteMarkerPrefix.Length, 32))
            .Select(value => Guid.TryParseExact(value, "N", out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty).ToArray();

        private string TranscriptArchiveDirectory(Guid sessionId) =>
            Path.Combine(SessionDirectory(sessionId), TranscriptArchiveDirectoryName);

        private static void ValidateSessionId(Guid sessionId)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
        }

        private static ProductionTerminalTranscriptArchivePage ToArchivePage(
            Guid sessionId,
            IReadOnlyList<PortableTerminalRowV1> rows)
        {
            var page = new ProductionTerminalTranscriptArchivePage
            {
                SessionId = sessionId,
                FirstLineId = rows[0].LineId,
                LastLineId = rows[^1].LineId
            };
            foreach (var row in rows)
            {
                var archived = new ProductionTerminalTranscriptArchiveRow
                {
                    LineId = row.LineId,
                    SealedAtSequence = row.SealedAtSequence,
                    Wrapped = row.Wrapped
                };
                foreach (var cell in row.Cells)
                {
                    var candidate = new ProductionTerminalTranscriptArchiveCell
                    {
                        Repeat = 1,
                        Character = cell.Character,
                        Foreground = cell.Style.Foreground,
                        Background = cell.Style.Background,
                        Attributes = (ushort)cell.Style.Attributes,
                        HyperlinkIndexPlusOne = cell.Style.HyperlinkIndexPlusOne,
                        DisplayWidth = cell.DisplayWidth,
                        WideContinuation = cell.WideContinuation
                    };
                    if (archived.Cells.Count > 0 &&
                        ArchiveCellPresentationEquals(archived.Cells[^1], candidate))
                    {
                        archived.Cells[^1].Repeat++;
                    }
                    else
                    {
                        archived.Cells.Add(candidate);
                    }
                }
                page.Rows.Add(archived);
            }
            return page;
        }

        private static IReadOnlyList<PortableTerminalRowV1> FromArchivePage(
            ProductionTerminalTranscriptArchivePage page)
        {
            var result = new List<PortableTerminalRowV1>(page.Rows.Count);
            foreach (var row in page.Rows)
            {
                var cells = ExpandArchiveCells(row.Cells);
                result.Add(new PortableTerminalRowV1(
                    cells,
                    row.Wrapped,
                    row.LineId,
                    row.SealedAtSequence));
            }
            return result;
        }

        private static void ValidateTranscriptArchivePage(
            ProductionTerminalTranscriptArchivePage page,
            Guid sessionId)
        {
            if (page == null || page.SessionId != sessionId || page.FirstLineId == 0 ||
                page.LastLineId < page.FirstLineId ||
                page.Rows == null || page.Rows.Count != PortableTerminalModelV1.TranscriptArchivePageRows ||
                page.Rows[0]?.LineId != page.FirstLineId || page.Rows[^1]?.LineId != page.LastLineId)
                throw new InvalidDataException("Terminal transcript archive page metadata is invalid.");
            ulong previous = 0;
            foreach (var row in page.Rows)
            {
                if (row == null || row.LineId == 0 || row.LineId <= previous || row.SealedAtSequence == 0 ||
                    row.Cells == null || row.Cells.Count == 0)
                    throw new InvalidDataException("Terminal transcript archive row is invalid.");
                _ = new PortableTerminalRowV1(
                    ExpandArchiveCells(row.Cells),
                    row.Wrapped,
                    row.LineId,
                    row.SealedAtSequence);
                previous = row.LineId;
            }
        }

        private static bool TranscriptArchivePagesEqual(
            ProductionTerminalTranscriptArchivePage left,
            ProductionTerminalTranscriptArchivePage right)
        {
            var leftBytes = JsonSerializer.SerializeToUtf8Bytes(left, JsonOptions);
            var rightBytes = JsonSerializer.SerializeToUtf8Bytes(right, JsonOptions);
            try { return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes); }
            finally
            {
                CryptographicOperations.ZeroMemory(leftBytes);
                CryptographicOperations.ZeroMemory(rightBytes);
            }
        }

        private static IEnumerable<PortableTerminalCellV1> ExpandArchiveCells(
            IEnumerable<ProductionTerminalTranscriptArchiveCell> runs)
        {
            var expanded = 0;
            foreach (var run in runs)
            {
                if (run == null || run.Repeat < 1 || run.Repeat > 512 || expanded > 512 - run.Repeat)
                    throw new InvalidDataException("Terminal transcript archive cell run is invalid.");
                var style = new PortableTerminalStyleV1(
                    run.Foreground,
                    run.Background,
                    (PortableTerminalAttributesV1)run.Attributes,
                    run.HyperlinkIndexPlusOne);
                for (var index = 0; index < run.Repeat; index++)
                    yield return new PortableTerminalCellV1(
                        run.Character,
                        style,
                        run.DisplayWidth,
                        run.WideContinuation);
                expanded += run.Repeat;
            }
        }

        private static bool ArchiveCellPresentationEquals(
            ProductionTerminalTranscriptArchiveCell left,
            ProductionTerminalTranscriptArchiveCell right)
        {
            return left.Character == right.Character &&
                left.Foreground == right.Foreground && left.Background == right.Background &&
                left.Attributes == right.Attributes &&
                left.HyperlinkIndexPlusOne == right.HyperlinkIndexPlusOne &&
                left.DisplayWidth == right.DisplayWidth &&
                left.WideContinuation == right.WideContinuation;
        }

        private static void ValidateCheckpoint(ProductionTerminalHistoryCheckpoint value)
        {
            if (value == null || value.SessionId == Guid.Empty || value.LastSequence == 0 ||
                value.AuthorityVersion < 0 || value.AuthorityVersion > 2 ||
                value.TranscriptPlaintexts == null ||
                (value.AuthorityVersion == 2 && value.TranscriptPlaintexts.Count != 0) ||
                (value.SnapshotEligible && (value.SnapshotPlaintext == null || value.SnapshotPlaintext.Length == 0)) ||
                (!value.SnapshotEligible && string.IsNullOrWhiteSpace(value.IneligibilityCode)))
            {
                throw new InvalidDataException("Terminal history checkpoint is invalid.");
            }
        }

        private void ValidateOutbox(ProductionTerminalHistoryOutbox value)
        {
            if (value == null || value.SessionId == Guid.Empty || value.SnapshotSequence == 0 ||
                value.Artifacts == null || value.Artifacts.Count == 0 || value.Deliveries == null)
                throw new InvalidDataException("Terminal history outbox is invalid.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var snapshotCount = 0;
            var transcriptCount = 0;
            ProductionTerminalHistoryArtifact snapshotArtifact = null;
            var previousOrder = -1;
            foreach (var artifact in value.Artifacts.OrderBy(item => item.UploadOrder))
            {
                if (artifact == null || string.IsNullOrWhiteSpace(artifact.ArtifactId) || !ids.Add(artifact.ArtifactId) ||
                    artifact.SnapshotSequence != value.SnapshotSequence || artifact.UploadOrder <= previousOrder ||
                    artifact.Plaintext == null || artifact.Plaintext.Length == 0 || string.IsNullOrWhiteSpace(artifact.FormatName))
                    throw new InvalidDataException("Terminal history outbox artifact is invalid.");
                if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
                {
                    snapshotCount++;
                    snapshotArtifact = artifact;
                    if (artifact.InitialTranscriptBeforeLineId == 0 || artifact.TranscriptPageCount < 0 ||
                        artifact.TranscriptPageCount > MaximumTranscriptPublicationPageCount ||
                        !IsSupportedSnapshotFormat(artifact.FormatName, artifact.FormatVersion))
                        throw new InvalidDataException("Terminal snapshot publication metadata is invalid.");
                }
                else if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript)
                {
                    transcriptCount++;
                    if (artifact.FirstLineId == 0 || artifact.LastLineId < artifact.FirstLineId ||
                        artifact.ExclusiveBeforeLineId <= artifact.LastLineId ||
                        artifact.InitialTranscriptBeforeLineId != 0 || artifact.ObservedLocalInputEpoch != 0 ||
                        artifact.TranscriptPageCount != 0 ||
                        artifact.FormatName != SnapshotCrypto.TranscriptFormatName ||
                        artifact.FormatVersion != SnapshotCrypto.FormatVersion)
                        throw new InvalidDataException("Terminal transcript outbox cursor is invalid.");
                }
                else
                    throw new InvalidDataException("Terminal history outbox kind is invalid.");
                previousOrder = artifact.UploadOrder;
            }
            if (snapshotCount != 1 || value.Artifacts.OrderBy(item => item.UploadOrder).First().Kind != ProductionTerminalHistoryArtifactKind.Snapshot)
                throw new InvalidDataException("Terminal history outbox must start with one snapshot.");
            if (snapshotArtifact.TranscriptPageCount != transcriptCount)
                throw new InvalidDataException("Terminal snapshot transcript page count is inconsistent with its outbox.");
            ValidateSnapshotPlaintext(value, snapshotArtifact);
            foreach (var delivery in value.Deliveries)
            {
                if (delivery.Value == null || delivery.Key != DeliveryKey(delivery.Value.RecipientDeviceId, delivery.Value.KeyEpoch) ||
                    delivery.Value.SealedObjects == null || delivery.Value.UploadedArtifactIds == null ||
                    delivery.Value.SealedObjects.Keys.Any(id => !ids.Contains(id)) ||
                    delivery.Value.UploadedArtifactIds.Any(id => !delivery.Value.SealedObjects.ContainsKey(id)))
                    throw new InvalidDataException("Terminal history delivery state is invalid.");
            }
        }

        private static bool IsSupportedSnapshotFormat(string formatName, int formatVersion) =>
            formatName == SnapshotCrypto.SnapshotFormatName &&
                formatVersion == SnapshotCrypto.FormatVersion ||
            formatName == SnapshotCrypto.TerminalCoreSnapshotFormatName &&
                formatVersion == SnapshotCrypto.TerminalCoreSnapshotFormatVersion ||
            formatName == SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatName &&
                formatVersion == SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatVersion;

        private void ValidateSnapshotPlaintext(
            ProductionTerminalHistoryOutbox outbox,
            ProductionTerminalHistoryArtifact artifact)
        {
            TerminalSemanticProfile profile;
            lock (SessionLock(outbox.SessionId)) profile = RequireSessionProfile(outbox.SessionId, false);
            bool graphics = profile.IsGraphics;
            bool graphicsFormat = artifact.FormatName == SnapshotCrypto.TerminalCoreGraphicsSnapshotFormatName;
            if (graphics != graphicsFormat)
                throw new TerminalHistoryProfileException(TerminalSessionProfile.Mismatch);
            if (graphicsFormat || artifact.FormatName == SnapshotCrypto.TerminalCoreSnapshotFormatName)
            {
                if (artifact.Plaintext.Length > (graphics ? SnapshotCrypto.MaxTerminalCoreGraphicsSnapshotPlaintextBytes
                    : SnapshotCrypto.MaxTerminalCoreSnapshotPlaintextBytes) ||
                    artifact.ObservedLocalInputEpoch > long.MaxValue)
                    throw new InvalidDataException("Terminal-core snapshot outbox plaintext is out of bounds.");
                try
                {
                    using var terminal = graphics ? NativeTerminalCoreV2.ImportGraphicsSnapshot(artifact.Plaintext)
                        : NativeTerminalCoreV2.ImportSnapshot(artifact.Plaintext);
                    // Only read this exact 2.22 header after the graphics importer has fully validated it.
                    if (graphics && (BinaryPrimitives.ReadUInt64LittleEndian(artifact.Plaintext.AsSpan(
                        GraphicsSnapshotV222RequiredFeaturesOffset, 8)) & GraphicsSnapshotV222StandaloneC1Feature) != 0)
                        throw new InvalidDataException("Terminal-core snapshot output differs from its persisted profile.");
                    var presentation = terminal.GetPresentationInfo();
                    if (presentation.NextSourceSequence != checked(outbox.SnapshotSequence + 1))
                        throw new InvalidDataException(
                            "Terminal-core snapshot source sequence does not match its outbox.");
                    using var checkpoint = terminal.CaptureCheckpoint();
                    var frameBytes = graphics ? checkpoint.ExportRenderFrameV5() : checkpoint.ExportRenderFrameV3();
                    try
                    {
                        int columns, rows;
                        if (graphics)
                        {
                            var frame = NativeTerminalRenderFrameV5Decoder.Decode(frameBytes);
                            if (frame.Text.WidthProfile != (NativeTerminalWidthProfile)profile.WidthProfile)
                                throw new InvalidDataException("Terminal-core snapshot width differs from its persisted profile.");
                            columns = frame.Columns; rows = frame.Rows;
                        }
                        else
                        {
                            var frame = NativeTerminalRenderFrameV3Decoder.Decode(frameBytes);
                            columns = frame.Columns; rows = frame.Rows;
                        }
                        if (columns != artifact.Columns || rows != artifact.Rows)
                            throw new InvalidDataException(
                                "Terminal-core snapshot dimensions do not match its outbox.");
                    }
                    finally { CryptographicOperations.ZeroMemory(frameBytes); }
                }
                catch (InvalidDataException) { throw; }
                catch (Exception error)
                {
                    throw new InvalidDataException(
                        "Terminal-core snapshot outbox plaintext cannot be imported.",
                        error);
                }
                return;
            }

            TerminalSnapshotV1 snapshot;
            try { snapshot = TerminalSnapshotV1.Parser.ParseFrom(artifact.Plaintext); }
            catch (InvalidProtocolBufferException error)
            {
                throw new InvalidDataException("Terminal snapshot outbox plaintext is invalid protobuf.", error);
            }
            SnapshotCrypto.ValidateSnapshotForProducer(snapshot);
            if (snapshot.SnapshotSeq != outbox.SnapshotSequence ||
                checked((int)snapshot.Columns) != artifact.Columns ||
                checked((int)snapshot.Rows) != artifact.Rows ||
                SnapshotCrypto.InitialTranscriptBeforeLineId(snapshot) != artifact.InitialTranscriptBeforeLineId)
            {
                throw new InvalidDataException("Terminal snapshot publication metadata does not match its plaintext.");
            }
        }

        private static bool ArtifactsEqual(
            IList<ProductionTerminalHistoryArtifact> left,
            IList<ProductionTerminalHistoryArtifact> right)
        {
            if (left.Count != right.Count) return false;
            for (var index = 0; index < left.Count; index++)
            {
                var a = left[index];
                var b = right[index];
                if (a.ArtifactId != b.ArtifactId || a.Kind != b.Kind || a.UploadOrder != b.UploadOrder ||
                    a.SnapshotSequence != b.SnapshotSequence ||
                    a.InitialTranscriptBeforeLineId != b.InitialTranscriptBeforeLineId ||
                    a.ObservedLocalInputEpoch != b.ObservedLocalInputEpoch ||
                    a.TranscriptPageCount != b.TranscriptPageCount ||
                    a.Columns != b.Columns || a.Rows != b.Rows ||
                    a.FirstLineId != b.FirstLineId || a.LastLineId != b.LastLineId ||
                    a.ExclusiveBeforeLineId != b.ExclusiveBeforeLineId ||
                    a.HasMoreBefore != b.HasMoreBefore || a.NextBeforeLineId != b.NextBeforeLineId ||
                    a.FormatName != b.FormatName || a.FormatVersion != b.FormatVersion ||
                    !a.Plaintext.AsSpan().SequenceEqual(b.Plaintext)) return false;
            }
            return true;
        }

        private ProductionTerminalHistoryOutbox ReadPersistedOutbox(string path, Guid sessionId)
        {
            var outbox = ReadJson<ProductionTerminalHistoryOutbox>(path, sessionId, "outbox");
            var migrated = NormalizeLegacyTranscriptFirstOutbox(outbox);
            migrated |= NormalizeLegacySnapshotPublicationMetadata(outbox);
            ValidateOutbox(outbox);
            if (migrated) WriteJson(path, sessionId, "outbox", outbox);
            return outbox;
        }

        private static bool NormalizeLegacyTranscriptFirstOutbox(ProductionTerminalHistoryOutbox value)
        {
            if (value?.Artifacts == null || value.Artifacts.Count < 2) return false;
            var ordered = value.Artifacts.OrderBy(item => item?.UploadOrder ?? int.MaxValue).ToList();
            if (ordered.Any(item => item == null) ||
                ordered.Count(item => item.Kind == ProductionTerminalHistoryArtifactKind.Snapshot) != 1 ||
                ordered[^1].Kind != ProductionTerminalHistoryArtifactKind.Snapshot ||
                ordered.Take(ordered.Count - 1).Any(
                    item => item.Kind != ProductionTerminalHistoryArtifactKind.Transcript))
            {
                return false;
            }
            var snapshot = ordered[^1];
            ordered.RemoveAt(ordered.Count - 1);
            ordered.Insert(0, snapshot);
            for (var index = 0; index < ordered.Count; index++)
                ordered[index].UploadOrder = index;
            value.Artifacts = ordered;
            return true;
        }

        private static bool NormalizeLegacySnapshotPublicationMetadata(ProductionTerminalHistoryOutbox value)
        {
            if (value?.Artifacts == null) return false;
            var snapshots = value.Artifacts
                .Where(item => item?.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
                .ToList();
            if (snapshots.Count != 1) return false;
            var snapshotArtifact = snapshots[0];
            if (snapshotArtifact.InitialTranscriptBeforeLineId != 0) return false;
            if (snapshotArtifact.TranscriptPageCount != 0 || snapshotArtifact.Plaintext == null ||
                snapshotArtifact.Plaintext.Length == 0)
            {
                return false;
            }

            TerminalSnapshotV1 snapshot;
            try { snapshot = TerminalSnapshotV1.Parser.ParseFrom(snapshotArtifact.Plaintext); }
            catch (InvalidProtocolBufferException error)
            {
                throw new InvalidDataException("Legacy terminal snapshot metadata cannot be recovered.", error);
            }
            SnapshotCrypto.ValidateSnapshotForProducer(snapshot);
            snapshotArtifact.InitialTranscriptBeforeLineId = SnapshotCrypto.InitialTranscriptBeforeLineId(snapshot);
            snapshotArtifact.TranscriptPageCount = value.Artifacts.Count(
                item => item?.Kind == ProductionTerminalHistoryArtifactKind.Transcript);

            // A pre-metadata Snapshot create could already be marked uploaded.
            // Re-queue the same durable ciphertext so the server observes the
            // explicit publication boundary instead of falsely treating a
            // partial transcript set as complete.
            if (value.Deliveries != null)
            {
                foreach (var delivery in value.Deliveries.Values)
                    delivery?.UploadedArtifactIds?.Remove(snapshotArtifact.ArtifactId);
            }
            return true;
        }

        private static void ValidateChunkOutbox(ProductionHistoryChunkOutbox value, Guid sessionId)
        {
            if (value == null || value.SessionId != sessionId || value.SealedObjects == null ||
                value.SealedObjects.Any(item => string.IsNullOrWhiteSpace(item.Key) || item.Value == null || item.Value.Length == 0))
                throw new InvalidDataException("History chunk outbox is invalid.");
        }

        private static string DeliveryKey(Guid recipientDeviceId, long keyEpoch)
        {
            if (recipientDeviceId == Guid.Empty || keyEpoch < 1)
                throw new InvalidDataException("Terminal history delivery identity is invalid.");
            return recipientDeviceId.ToString("N") + ":" + keyEpoch.ToString("D8");
        }

        private void WriteJson<T>(string path, Guid sessionId, string domain, T value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
            try { WriteProtectedAtomic(path, sessionId, domain, bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private T ReadJson<T>(string path, Guid sessionId, string domain)
        {
            var bytes = ReadProtected(path, sessionId, domain);
            try
            {
                return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                    ?? throw new InvalidDataException("Terminal history protected JSON is empty.");
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("Terminal history protected JSON is invalid.", error);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }

        private void WriteProtectedAtomic(string path, Guid sessionId, string domain, byte[] plaintext)
        {
            if (domain != "terminal-profile" && domain != "inactive-since") RequireSessionProfile(sessionId, true);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var entropy = Entropy(sessionId, domain);
            byte[] protectedBytes = null;
            try
            {
                protectedBytes = ProtectedData.Protect(plaintext, entropy, DataProtectionScope.CurrentUser);
                AtomicWrite(path, protectedBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
                if (protectedBytes != null) CryptographicOperations.ZeroMemory(protectedBytes);
            }
        }

        private byte[] ReadProtected(string path, Guid sessionId, string domain)
        {
            if (domain != "terminal-profile" && domain != "inactive-since") RequireSessionProfile(sessionId, false);
            byte[] stored;
            if (domain == "terminal-profile")
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length == 0 || stream.Length > TerminalSessionProfile.MaximumProtectedBytes)
                    throw new TerminalHistoryProfileException(TerminalSessionProfile.Invalid);
                stored = new byte[checked((int)stream.Length)];
                stream.ReadExactly(stored);
            }
            else stored = File.ReadAllBytes(path);
            var entropy = Entropy(sessionId, domain);
            try { return ProtectedData.Unprotect(stored, entropy, DataProtectionScope.CurrentUser); }
            catch (CryptographicException error)
            {
                throw new InvalidDataException("Terminal history DPAPI state cannot be authenticated.", error);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(stored);
                CryptographicOperations.ZeroMemory(entropy);
            }
        }

        private byte[] Entropy(Guid sessionId, string domain)
        {
            return SHA256.HashData(Encoding.UTF8.GetBytes(
                "TermLink production terminal history " + domain + " V1\n" +
                pcDeviceId.ToString("N") + "\n" + sessionId.ToString("N")));
        }

        private static void AtomicWrite(string path, byte[] bytes)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
