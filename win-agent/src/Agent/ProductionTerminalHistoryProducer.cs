using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using Google.Protobuf;
using TermLink.LocalIpc;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;
using TermLink.TerminalCore;

namespace TermLink.Agent
{
    internal sealed class ProductionTerminalHistoryProductionResult
    {
        public Guid SessionId { get; set; }
        public ulong LastSequence { get; set; }
        public bool SnapshotEligible { get; set; }
        public bool PermanentlyIneligible { get; set; }
        public string IneligibilityCode { get; set; }
        public int TranscriptPageCount { get; set; }
    }

    internal sealed class ProductionTerminalHistoryProducer : IDisposable
    {
        private const int DefaultColumns = 80;
        private const int DefaultRows = 24;
        private const int MaximumWireColumns = 512;
        private const int MaximumWireRows = 256;
        private const int MaximumTranscriptRowsPerPage = 200;
        private const int MaximumTermuxStyles = 1_024;
        private const int MaximumTermuxCombiningScalars = 15;
        // The sealed-object protobuf adds identities, key metadata, a nonce and the AEAD tag.
        // Reserve a deliberately conservative bound so an artifact accepted here cannot fail
        // later merely because the recipient-specific container crosses its wire limit.
        private const int MaximumSealedContainerOverheadBytes = 1_024;
        private readonly ProductionTerminalHistoryStore store;
        private readonly Guid pcDeviceId;
        private readonly bool terminalCoreShadowEnabled;
        private readonly bool terminalCoreSnapshotAuthorityEnabled;
        private readonly ProductionTerminalCoreShadowDispatcher terminalCoreShadowDispatcher;
        private readonly ConcurrentDictionary<Guid, ProductionState> states =
            new ConcurrentDictionary<Guid, ProductionState>();
        private readonly ConcurrentDictionary<Guid, byte> releasedSessions =
            new ConcurrentDictionary<Guid, byte>();
        private readonly ConcurrentDictionary<Guid, byte> retiredShadowSessions =
            new ConcurrentDictionary<Guid, byte>();
        private readonly ReaderWriterLockSlim lifecycleGate =
            new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private readonly object shadowLifecycleSync = new object();
        private int disposed;

        private sealed class ProductionState : IDisposable
        {
            public readonly object Sync = new object();
            public readonly PortableTerminalModelV1 Model;
            public TerminalLifecycleV1 Lifecycle = TerminalLifecycleV1.Running;
            public bool HasExitCode;
            public int ExitCode;
            public ulong LocalInputEpoch;
            public ulong AppliedSequence;
            public bool HasOutput;
            public readonly SortedDictionary<ulong, LocalFrame> PendingFrames =
                new SortedDictionary<ulong, LocalFrame>();
            public NativeTerminal TerminalCore;
            public string TerminalCoreFailureCode = string.Empty;

            public ProductionState(
                bool terminalCoreSnapshotAuthorityEnabled,
                Action<IReadOnlyList<PortableTerminalRowV1>> transcriptArchiveSink)
            {
                if (!terminalCoreSnapshotAuthorityEnabled)
                {
                    Model = new PortableTerminalModelV1(
                        DefaultColumns,
                        DefaultRows,
                        transcriptArchiveSink);
                    return;
                }
                try
                {
                    TerminalCore = NativeTerminalCoreV2.Create(
                        DefaultColumns,
                        DefaultRows,
                        initialSourceSequence: 1,
                        historyCapacityRows: 20_000,
                        outputProfile: NativeTerminalOutputProfile.Utf8);
                }
                catch (Exception error)
                {
                    TerminalCoreFailureCode = NativeFailureCode(error);
                }
            }

            public void Dispose()
            {
                var terminal = TerminalCore;
                TerminalCore = null;
                try { terminal?.Dispose(); }
                catch { }
            }
        }

        private sealed class ShadowDispatchRequest
        {
            public Guid SessionId;
            public IReadOnlyList<LocalFrame> Frames;
            public PortableTerminalSnapshotV1 Portable;
            public ulong ExpectedSequence;
            public string PortableReasonCode;
        }

        public ProductionTerminalHistoryProducer(
            ProductionTerminalHistoryStore store,
            Guid pcDeviceId)
            : this(store, pcDeviceId, terminalCoreShadowEnabled: false)
        {
        }

        internal ProductionTerminalHistoryProducer(
            ProductionTerminalHistoryStore store,
            Guid pcDeviceId,
            bool terminalCoreShadowEnabled)
            : this(store, pcDeviceId, terminalCoreShadowEnabled, null)
        {
        }

        internal ProductionTerminalHistoryProducer(
            ProductionTerminalHistoryStore store,
            Guid pcDeviceId,
            bool terminalCoreShadowEnabled,
            Action<Guid, ProductionTerminalCoreShadowResult> terminalCoreShadowObserver)
            : this(
                store,
                pcDeviceId,
                terminalCoreShadowEnabled,
                terminalCoreSnapshotAuthorityEnabled: false,
                terminalCoreShadowObserver)
        {
        }

        internal ProductionTerminalHistoryProducer(
            ProductionTerminalHistoryStore store,
            Guid pcDeviceId,
            bool terminalCoreShadowEnabled,
            bool terminalCoreSnapshotAuthorityEnabled,
            Action<Guid, ProductionTerminalCoreShadowResult> terminalCoreShadowObserver)
        {
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            if (pcDeviceId == Guid.Empty) throw new ArgumentException("PC device ID is required.", nameof(pcDeviceId));
            this.pcDeviceId = pcDeviceId;
            // Once terminal-core is the production authority, a second native shadow adds
            // another parser without providing an independent implementation to compare.
            this.terminalCoreShadowEnabled =
                terminalCoreShadowEnabled && !terminalCoreSnapshotAuthorityEnabled;
            this.terminalCoreSnapshotAuthorityEnabled = terminalCoreSnapshotAuthorityEnabled;
            if (this.terminalCoreShadowEnabled)
            {
                terminalCoreShadowDispatcher = new ProductionTerminalCoreShadowDispatcher(
                    terminalCoreShadowObserver);
            }
        }

        public void AppendCommittedFrame(LocalFrame frame) => store.AppendCommittedFrame(frame);

        public IList<LocalFrame> ReadFrames(Guid sessionId) => store.ReadFrames(sessionId);

        public ProductionTerminalHistoryProductionResult ProcessSession(Guid sessionId)
        {
            return ProcessSession(sessionId, persistLegacyArtifacts: true);
        }

        internal ProductionTerminalHistoryProductionResult ProcessSession(
            Guid sessionId,
            bool persistLegacyArtifacts)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            lifecycleGate.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                if (releasedSessions.ContainsKey(sessionId)) return ReleasedResult(sessionId);
                var state = states.GetOrAdd(
                    sessionId,
                    _ => CreateProductionState(sessionId));
                ProductionTerminalHistoryProductionResult result;
                ShadowDispatchRequest shadowDispatch;
                lock (state.Sync)
                {
                    if (releasedSessions.ContainsKey(sessionId))
                    {
                        TryRemoveState(sessionId, state);
                        return ReleasedResult(sessionId);
                    }
                    // Only decrypt WAL entries beyond the in-memory projection.
                    // A fresh process naturally performs one complete recovery;
                    // steady-state production applies each durable frame once.
                    var frames = store.ReadFramesAfter(sessionId, state.AppliedSequence);
                    result = ProcessFrames(
                        sessionId,
                        state,
                        frames,
                        persistLegacyArtifacts,
                        out shadowDispatch);
                }
                QueueTerminalCoreShadow(shadowDispatch);
                return result;
            }
            finally { lifecycleGate.ExitReadLock(); }
        }

        public ProductionTerminalHistoryProductionResult ProcessSession(
            Guid sessionId,
            IList<LocalFrame> committedFrames)
        {
            return ProcessSession(sessionId, committedFrames, persistLegacyArtifacts: true);
        }

        internal ProductionTerminalHistoryProductionResult ProcessSession(
            Guid sessionId,
            IList<LocalFrame> committedFrames,
            bool persistLegacyArtifacts)
        {
            return ProcessSession(
                sessionId,
                committedFrames,
                persistLegacyArtifacts,
                shouldYield: null);
        }

        internal ProductionTerminalHistoryProductionResult ProcessSession(
            Guid sessionId,
            IList<LocalFrame> committedFrames,
            bool persistLegacyArtifacts,
            Func<bool> shouldYield)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("Session ID is required.", nameof(sessionId));
            if (committedFrames == null) throw new ArgumentNullException(nameof(committedFrames));
            lifecycleGate.EnterReadLock();
            try
            {
                ThrowIfDisposed();
                if (releasedSessions.ContainsKey(sessionId)) return ReleasedResult(sessionId);
                var state = states.GetOrAdd(
                    sessionId,
                    _ => CreateProductionState(sessionId));
                ProductionTerminalHistoryProductionResult result;
                ShadowDispatchRequest shadowDispatch;
                lock (state.Sync)
                {
                    if (releasedSessions.ContainsKey(sessionId))
                    {
                        TryRemoveState(sessionId, state);
                        return ReleasedResult(sessionId);
                    }
                    // A late-created producer must recover the prefix once. Once
                    // warm, the history worker supplies only its coalesced append
                    // batch, eliminating repeated WAL directory/decrypt scans.
                    IList<LocalFrame> frames = committedFrames;
                    if (state.AppliedSequence == 0 && state.PendingFrames.Count == 0 &&
                        committedFrames.Count > 0 && committedFrames.Min(value => value.Sequence) > 1)
                    {
                        frames = store.ReadFrames(sessionId);
                    }
                    result = ProcessFrames(
                        sessionId,
                        state,
                        frames,
                        persistLegacyArtifacts,
                        out shadowDispatch,
                        shouldYield);
                }
                QueueTerminalCoreShadow(shadowDispatch);
                return result;
            }
            finally { lifecycleGate.ExitReadLock(); }
        }

        private ProductionTerminalHistoryProductionResult ProcessFrames(
            Guid sessionId,
            ProductionState state,
            IList<LocalFrame> frames,
            bool persistLegacyArtifacts,
            out ShadowDispatchRequest shadowDispatch,
            Func<bool> shouldYield = null)
        {
            shadowDispatch = null;
            foreach (var frame in frames)
            {
                if (frame == null || frame.SessionId != sessionId || frame.Sequence == 0)
                    throw new InvalidDataException("Incremental terminal history frame identity is invalid.");
                if (frame.Sequence > state.AppliedSequence)
                    state.PendingFrames[frame.Sequence] = frame;
            }
            if (state.PendingFrames.Count == 0 && !state.HasOutput)
            {
                return new ProductionTerminalHistoryProductionResult
                {
                    SessionId = sessionId,
                    IneligibilityCode = "NO_DURABLE_FRAMES"
                };
            }

            var outputAnchor = state.PendingFrames.Values
                .Where(value => value.Type == LocalFrameType.Output)
                .Select(value => value.Sequence)
                .DefaultIfEmpty(0UL)
                .Max();
            if (outputAnchor == 0 && !state.HasOutput)
            {
                var terminalWithoutOutput = state.PendingFrames.Values.Any(frame =>
                {
                    if (frame.Type != LocalFrameType.Lifecycle) return false;
                    var lifecycle = LocalPayloadCodec.ParseLifecycle(frame.Payload, out _);
                    return lifecycle == LocalLifecycleState.Ended ||
                        lifecycle == LocalLifecycleState.Interrupted;
                });
                var rejected = new ProductionTerminalHistoryProductionResult
                {
                    SessionId = sessionId,
                    LastSequence = state.PendingFrames.Keys.Last(),
                    // An active session can still produce its first Output, so
                    // NO_OUTPUT_FRAME remains pending until a final lifecycle
                    // marker proves that Snapshot production is impossible.
                    PermanentlyIneligible = terminalWithoutOutput,
                    IneligibilityCode = "NO_OUTPUT_FRAME"
                };
                if (persistLegacyArtifacts) SaveRejectedCheckpoint(rejected);
                return rejected;
            }
            var applied = outputAnchor == 0
                ? Array.Empty<ulong>()
                : state.PendingFrames.Keys.Where(value => value <= outputAnchor).ToArray();
            var shadowFrames = terminalCoreShadowEnabled
                ? new List<LocalFrame>(applied.Length)
                : null;
            foreach (var sequence in applied)
            {
                // Persisted backlog can contain years of terminal output. Keep
                // the already-applied projection in memory and yield between
                // ordered frames when a live session needs its first Snapshot.
                // A later pass ignores <= AppliedSequence and resumes exactly.
                if (shouldYield?.Invoke() == true) return null;
                var frame = state.PendingFrames[sequence];
                if (terminalCoreSnapshotAuthorityEnabled) ApplyTerminalCoreFrame(state, frame);
                else ApplyFrame(state.Model, frame);
                shadowFrames?.Add(frame);
                ApplyProjection(
                    frame,
                    ref state.Lifecycle,
                    ref state.HasExitCode,
                    ref state.ExitCode,
                    ref state.LocalInputEpoch);
                state.AppliedSequence = frame.Sequence;
                if (frame.Type == LocalFrameType.Output) state.HasOutput = true;
                state.PendingFrames.Remove(sequence);
            }

            if (shouldYield?.Invoke() == true) return null;

            return ProduceCheckpoint(
                sessionId,
                state,
                persistLegacyArtifacts,
                shadowFrames,
                out shadowDispatch);
        }

        private ProductionTerminalHistoryProductionResult ProduceCheckpoint(
            Guid sessionId,
            ProductionState state,
            bool persistLegacyArtifacts,
            IReadOnlyList<LocalFrame> shadowFrames,
            out ShadowDispatchRequest shadowDispatch)
        {
            shadowDispatch = null;
            var result = new ProductionTerminalHistoryProductionResult
            {
                SessionId = sessionId,
                // Snapshot S is deliberately the latest Output sequence. REST
                // last_output_seq therefore selects the same state, while any
                // Resize/Title/Lifecycle at S+1 is replayed as a normal suffix.
                LastSequence = state.AppliedSequence
            };
            if (!terminalCoreSnapshotAuthorityEnabled &&
                (state.Model.Columns > MaximumWireColumns || state.Model.Rows > MaximumWireRows))
            {
                shadowDispatch = CreateShadowDispatchRequest(
                    sessionId,
                    shadowFrames,
                    null,
                    state.AppliedSequence,
                    "WIRE_DIMENSIONS_UNSUPPORTED");
                result.PermanentlyIneligible = true;
                result.IneligibilityCode = "WIRE_DIMENSIONS_UNSUPPORTED";
                if (persistLegacyArtifacts) SaveRejectedCheckpoint(result);
                return result;
            }
            PortableTerminalSnapshotV1 portable = null;
            if (!terminalCoreSnapshotAuthorityEnabled &&
                !state.Model.TryCreateSnapshot(out portable))
            {
                result.PermanentlyIneligible = !string.IsNullOrWhiteSpace(
                    state.Model.PermanentRejectionCode);
                result.IneligibilityCode = string.IsNullOrWhiteSpace(state.Model.PermanentRejectionCode)
                    ? state.Model.ParserBoundaryCode
                    : state.Model.PermanentRejectionCode;
                shadowDispatch = CreateShadowDispatchRequest(
                    sessionId,
                    shadowFrames,
                    null,
                    state.AppliedSequence,
                    result.IneligibilityCode);
                if (persistLegacyArtifacts) SaveRejectedCheckpoint(result);
                return result;
            }

            shadowDispatch = CreateShadowDispatchRequest(
                sessionId,
                shadowFrames,
                portable,
                state.AppliedSequence,
                string.Empty);

            if (!persistLegacyArtifacts)
            {
                result.SnapshotEligible = false;
                result.IneligibilityCode = "LEGACY_PUBLICATION_DISABLED";
                return result;
            }

            if (terminalCoreSnapshotAuthorityEnabled)
                return ProduceTerminalCoreCheckpoint(sessionId, state, result);

            var styleTable = BuildStyleTable(portable);
            var profileError = ValidateTermuxProfile(portable, styleTable);
            if (profileError != null) return RejectCheckpoint(result, profileError);
            var palette = CreatePalette(portable);
            var snapshot = BuildSnapshot(
                portable,
                styleTable,
                palette,
                state.Lifecycle,
                state.HasExitCode,
                state.ExitCode,
                state.LocalInputEpoch);
            try
            {
                SnapshotCrypto.ValidateSnapshotForProducer(snapshot);
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
            {
                return RejectCheckpoint(result, "SNAPSHOT_CONTRACT_VALIDATION_FAILED");
            }
            var archivedPages = store.ReadTranscriptArchivePages(sessionId);
            var pages = BuildTranscriptPages(portable, archivedPages, palette).ToList();
            try
            {
                for (var index = 0; index < pages.Count; index++)
                {
                    var exclusiveBefore = index + 1 < pages.Count
                        ? pages[index + 1].FirstLineId
                        : snapshot.PrimaryScreen.VisibleRows[0].LineId;
                    SnapshotCrypto.ValidateTranscriptForRequest(pages[index], snapshot.SnapshotSeq, exclusiveBefore);
                }
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException or ArgumentException)
            {
                return RejectCheckpoint(result, "TRANSCRIPT_CONTRACT_VALIDATION_FAILED");
            }

            byte[] snapshotBytes;
            snapshotBytes = snapshot.ToByteArray();
            var pageBytes = pages.Select(value => value.ToByteArray()).ToList();
            var sizeError = ValidateArtifactSizes(
                snapshotBytes,
                pageBytes,
                terminalCoreSnapshot: false);
            if (sizeError != null)
            {
                CryptographicOperations.ZeroMemory(snapshotBytes);
                foreach (var bytes in pageBytes) CryptographicOperations.ZeroMemory(bytes);
                return RejectCheckpoint(result, sizeError);
            }
            try
            {
                var checkpoint = new ProductionTerminalHistoryCheckpoint
                {
                    SessionId = sessionId,
                    LastSequence = portable.Sequence,
                    AuthorityVersion = 1,
                    SnapshotEligible = true,
                    IneligibilityCode = string.Empty,
                    SnapshotPlaintext = snapshotBytes,
                    TranscriptPlaintexts = pageBytes
                };
                var outbox = BuildOutbox(
                    sessionId,
                    snapshot,
                    pages,
                    snapshotBytes,
                    pageBytes,
                    terminalCoreSnapshot: false);
                store.SaveProduction(checkpoint, outbox);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(snapshotBytes);
                foreach (var bytes in pageBytes) CryptographicOperations.ZeroMemory(bytes);
            }
            result.SnapshotEligible = true;
            result.TranscriptPageCount = pages.Count;
            result.IneligibilityCode = string.Empty;
            return result;
        }

        private ProductionTerminalHistoryProductionResult ProduceTerminalCoreCheckpoint(
            Guid sessionId,
            ProductionState state,
            ProductionTerminalHistoryProductionResult result)
        {
            if (state.TerminalCore == null)
            {
                result.PermanentlyIneligible = true;
                return RejectCheckpoint(
                    result,
                    string.IsNullOrWhiteSpace(state.TerminalCoreFailureCode)
                        ? "NATIVE_UNAVAILABLE"
                        : state.TerminalCoreFailureCode,
                    permanentlyIneligible: true);
            }

            byte[] snapshotBytes = null;
            int snapshotColumns;
            int snapshotRows;
            try
            {
                if (!state.TerminalCore.TryCaptureCheckpoint(out var checkpoint, out var status))
                    return RejectCheckpoint(result, "NATIVE_" + status.ToString().ToUpperInvariant());
                using (checkpoint)
                {
                    if (checkpoint.Info.NextSourceSequence != checked(state.AppliedSequence + 1))
                        throw new InvalidDataException(
                            "terminal-core checkpoint source sequence does not match the durable anchor.");
                    var frameBytes = checkpoint.ExportRenderFrameV3();
                    NativeTerminalRenderFrameV3 frame;
                    try
                    {
                        frame = NativeTerminalRenderFrameV3Decoder.Decode(frameBytes);
                        if (frame.NextSourceSequence != checkpoint.Info.NextSourceSequence ||
                            frame.Columns < 1 || frame.Columns > MaximumWireColumns ||
                            frame.Rows < 1 || frame.Rows > MaximumWireRows)
                        {
                            throw new InvalidDataException(
                                "terminal-core checkpoint presentation metadata is invalid.");
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(frameBytes); }

                    snapshotColumns = frame.Columns;
                    snapshotRows = frame.Rows;
                    snapshotBytes = checkpoint.ExportSnapshot();
                }
            }
            catch (Exception error)
            {
                state.TerminalCoreFailureCode = NativeFailureCode(error);
                state.Dispose();
                return RejectCheckpoint(
                    result,
                    state.TerminalCoreFailureCode,
                    permanentlyIneligible: true);
            }

            try
            {
                var sizeError = ValidateArtifactSizes(
                    snapshotBytes,
                    Array.Empty<byte[]>(),
                    terminalCoreSnapshot: true);
                if (sizeError != null) return RejectCheckpoint(result, sizeError);

                // The canonical native checkpoint owns the bounded scrollback restored by
                // Android. Legacy transcript pages are a compatibility artifact of the
                // retired Portable projection and must not gate or describe native state.
                // Line ID 1 is the protocol's non-zero sentinel for an empty external
                // transcript prefix; Android restores the checkpoint and its live suffix.
                const ulong initialTranscriptBeforeLineId = 1;
                var productionCheckpoint = new ProductionTerminalHistoryCheckpoint
                {
                    SessionId = sessionId,
                    LastSequence = state.AppliedSequence,
                    // V2 marks the terminal-core-only authority whose canonical
                    // checkpoint includes bounded native scrollback.
                    AuthorityVersion = 2,
                    SnapshotEligible = true,
                    IneligibilityCode = string.Empty,
                    SnapshotPlaintext = snapshotBytes,
                    TranscriptPlaintexts = new List<byte[]>()
                };
                var productionOutbox = BuildTerminalCoreOutbox(
                    sessionId,
                    state.AppliedSequence,
                    snapshotColumns,
                    snapshotRows,
                    initialTranscriptBeforeLineId,
                    state.LocalInputEpoch,
                    snapshotBytes);
                store.SaveProduction(productionCheckpoint, productionOutbox);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(snapshotBytes);
            }

            result.SnapshotEligible = true;
            result.TranscriptPageCount = 0;
            result.IneligibilityCode = string.Empty;
            return result;
        }

        public void Dispose()
        {
            lifecycleGate.EnterWriteLock();
            try
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lock (shadowLifecycleSync)
                {
                    terminalCoreShadowDispatcher?.Dispose();
                    retiredShadowSessions.Clear();
                }
                foreach (var state in states.Values)
                {
                    lock (state.Sync) state.Dispose();
                }
                states.Clear();
                releasedSessions.Clear();
            }
            finally { lifecycleGate.ExitWriteLock(); }
        }

        private ProductionState CreateProductionState(Guid sessionId)
        {
            return new ProductionState(
                terminalCoreSnapshotAuthorityEnabled,
                rows => store.AppendTranscriptArchivePage(sessionId, rows));
        }

        internal void ReleaseSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty) return;
            lifecycleGate.EnterReadLock();
            try
            {
                if (Volatile.Read(ref disposed) != 0) return;
                lock (shadowLifecycleSync)
                {
                    // Runtime calls Release only after final history delivery and never reuses a
                    // session ID. Keep a producer-lifetime tombstone so already-started late work
                    // also fails closed; the smaller shadow-retirement tombstone can now go away.
                    releasedSessions[sessionId] = 0;
                    retiredShadowSessions[sessionId] = 0;
                    terminalCoreShadowDispatcher?.RetireSession(sessionId);
                }
                if (states.TryGetValue(sessionId, out var state))
                    lock (state.Sync) TryRemoveState(sessionId, state);
            }
            finally { lifecycleGate.ExitReadLock(); }
            lock (shadowLifecycleSync)
            {
                terminalCoreShadowDispatcher?.ReleaseSession(sessionId);
                retiredShadowSessions.TryRemove(sessionId, out _);
            }
        }

        internal int ActiveProjectionCount => states.Count;

        internal int ActiveShadowSessionCount => terminalCoreShadowDispatcher?.SessionCount ?? 0;

        internal int RetainedShadowSessionCount =>
            terminalCoreShadowDispatcher?.RetainedSessionCount ?? 0;

        internal void RetireShadowSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty) return;
            lifecycleGate.EnterReadLock();
            try
            {
                if (Volatile.Read(ref disposed) != 0) return;
                lock (shadowLifecycleSync)
                {
                    if (releasedSessions.ContainsKey(sessionId)) return;
                    retiredShadowSessions[sessionId] = 0;
                    terminalCoreShadowDispatcher?.RetireSession(sessionId);
                }
            }
            finally { lifecycleGate.ExitReadLock(); }
        }

        private static ProductionTerminalHistoryProductionResult ReleasedResult(Guid sessionId) =>
            new ProductionTerminalHistoryProductionResult
            {
                SessionId = sessionId,
                IneligibilityCode = "SESSION_RELEASED"
            };

        private void ThrowIfDisposed()
        {
            if (System.Threading.Volatile.Read(ref disposed) != 0)
                throw new ObjectDisposedException(nameof(ProductionTerminalHistoryProducer));
        }

        private ShadowDispatchRequest CreateShadowDispatchRequest(
            Guid sessionId,
            IReadOnlyList<LocalFrame> frames,
            PortableTerminalSnapshotV1 portable,
            ulong expectedSequence,
            string portableReasonCode)
        {
            if (!terminalCoreShadowEnabled || frames == null || frames.Count == 0)
                return null;
            return new ShadowDispatchRequest
            {
                SessionId = sessionId,
                Frames = frames,
                Portable = portable,
                ExpectedSequence = expectedSequence,
                PortableReasonCode = portableReasonCode
            };
        }

        private void QueueTerminalCoreShadow(ShadowDispatchRequest request)
        {
            if (request == null) return;
            // The immutable Portable snapshot and committed LocalFrame references may leave the
            // production projection lock here. Dispatcher estimation and payload cloning can be
            // expensive for a large screen and must not extend durable history serialization.
            // Its reject predicate is checked before and after state acquisition so a concurrent
            // retire/release cannot recreate a native generation after its tombstone is removed.
            terminalCoreShadowDispatcher?.Enqueue(
                request.SessionId,
                request.Frames,
                request.Portable,
                request.ExpectedSequence,
                request.PortableReasonCode,
                () => Volatile.Read(ref disposed) != 0 ||
                    releasedSessions.ContainsKey(request.SessionId) ||
                    retiredShadowSessions.ContainsKey(request.SessionId));
        }

        private bool TryRemoveState(Guid sessionId, ProductionState state)
        {
            var removed = ((ICollection<KeyValuePair<Guid, ProductionState>>)states).Remove(
                new KeyValuePair<Guid, ProductionState>(sessionId, state));
            if (removed) state.Dispose();
            return removed;
        }

        private ProductionTerminalHistoryProductionResult RejectCheckpoint(
            ProductionTerminalHistoryProductionResult result,
            string code,
            bool permanentlyIneligible = false)
        {
            result.SnapshotEligible = false;
            result.PermanentlyIneligible = permanentlyIneligible;
            result.IneligibilityCode = code;
            SaveRejectedCheckpoint(result);
            return result;
        }

        public ProductionTerminalHistoryPreparedUpload PrepareNextUpload(
            Guid sessionId,
            ProductionHistoryRecipient recipient)
        {
            if (recipient == null || recipient.PairwiseKey == null || recipient.PairwiseKey.Length != 32 ||
                recipient.DeviceId == Guid.Empty || recipient.KeyEpoch < 1)
                throw new ArgumentException("A valid terminal history recipient is required.", nameof(recipient));
            return store.PrepareNextUpload(
                sessionId,
                recipient.DeviceId,
                recipient.KeyEpoch,
                artifact => SealArtifact(sessionId, recipient, artifact));
        }

        public void MarkUploaded(ProductionTerminalHistoryPreparedUpload upload) => store.MarkUploaded(upload);

        public bool AreAllUploaded(Guid sessionId, ulong snapshotSequence, ProductionHistoryRecipient recipient)
        {
            if (recipient == null) return false;
            return store.AreAllUploaded(sessionId, snapshotSequence, recipient.DeviceId, recipient.KeyEpoch);
        }

        public void RetainDeliveries(
            Guid sessionId,
            IEnumerable<(Guid RecipientDeviceId, long KeyEpoch)> recipients) =>
            store.RetainDeliveries(sessionId, recipients);

        public ProductionTerminalHistoryCheckpoint LoadCheckpoint(Guid sessionId) => store.LoadCheckpoint(sessionId);

        public ProductionTerminalHistoryOutbox LoadOutbox(Guid sessionId) => store.LoadOutbox(sessionId);

        private void SaveRejectedCheckpoint(ProductionTerminalHistoryProductionResult result)
        {
            store.SaveCheckpoint(new ProductionTerminalHistoryCheckpoint
            {
                SessionId = result.SessionId,
                LastSequence = result.LastSequence,
                AuthorityVersion = terminalCoreSnapshotAuthorityEnabled ? 2 : 1,
                SnapshotEligible = false,
                IneligibilityCode = result.IneligibilityCode,
                SnapshotPlaintext = null,
                TranscriptPlaintexts = new List<byte[]>()
            });
        }

        private static void ApplyFrame(PortableTerminalModelV1 model, LocalFrame frame)
        {
            switch (frame.Type)
            {
                case LocalFrameType.Output:
                    model.ApplyOutput(frame.Sequence, frame.Payload);
                    return;
                case LocalFrameType.Resize:
                    LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                    model.ApplyResize(frame.Sequence, columns, rows);
                    return;
                case LocalFrameType.Title:
                    model.ApplyTitle(frame.Sequence, LocalPayloadCodec.ParseTitle(frame.Payload));
                    return;
                case LocalFrameType.Gap:
                    model.MarkSourceGap(frame.Sequence);
                    return;
                default:
                    model.AdvanceNonTerminalEvent(frame.Sequence);
                    return;
            }
        }

        private static void ApplyTerminalCoreFrame(ProductionState state, LocalFrame frame)
        {
            if (state.TerminalCore == null) return;
            try
            {
                switch (frame.Type)
                {
                    case LocalFrameType.Output:
                        state.TerminalCore.ApplyOutput(frame.Sequence, frame.Payload);
                        return;
                    case LocalFrameType.Resize:
                        LocalPayloadCodec.ParseResize(frame.Payload, out var columns, out var rows);
                        state.TerminalCore.ApplyResize(
                            frame.Sequence,
                            checked((uint)columns),
                            checked((uint)rows));
                        return;
                    case LocalFrameType.Gap:
                        state.TerminalCore.ApplyGap(frame.Sequence);
                        return;
                    default:
                        state.TerminalCore.ApplyAdvance(frame.Sequence, 0);
                        return;
                }
            }
            catch (Exception error)
            {
                state.TerminalCoreFailureCode = NativeFailureCode(error);
                state.Dispose();
            }
        }

        private static string NativeFailureCode(Exception error)
        {
            if (error is NativeTerminalException native)
                return "NATIVE_" + native.Status.ToString().ToUpperInvariant();
            if (error is DllNotFoundException) return "NATIVE_DLL_NOT_FOUND";
            if (error is BadImageFormatException) return "NATIVE_ARCHITECTURE";
            if (error is EntryPointNotFoundException) return "NATIVE_ENTRY_POINT";
            if (error is NativeTerminalAbiException) return "NATIVE_ABI";
            if (error is OverflowException) return "INTEGER_OVERFLOW";
            if (error is ArgumentException) return "INVALID_FRAME";
            return "NATIVE_" + error.GetType().Name.ToUpperInvariant();
        }

        private static void ApplyProjection(
            LocalFrame frame,
            ref TerminalLifecycleV1 lifecycle,
            ref bool hasExitCode,
            ref int exitCode,
            ref ulong localInputEpoch)
        {
            if (frame.Type == LocalFrameType.LocalInputObserved)
            {
                localInputEpoch = LocalPayloadCodec.ParseLocalInputEpoch(frame.Payload);
            }
            else if (frame.Type == LocalFrameType.Lifecycle)
            {
                var state = LocalPayloadCodec.ParseLifecycle(frame.Payload, out var value);
                lifecycle = state switch
                {
                    LocalLifecycleState.Running => TerminalLifecycleV1.Running,
                    LocalLifecycleState.Interrupted => TerminalLifecycleV1.Interrupted,
                    LocalLifecycleState.Ended => TerminalLifecycleV1.Ended,
                    _ => throw new InvalidDataException("Terminal lifecycle state is unsupported.")
                };
                hasExitCode = state == LocalLifecycleState.Ended;
                exitCode = value;
            }
        }

        private static List<PortableTerminalStyleV1> BuildStyleTable(PortableTerminalSnapshotV1 snapshot)
        {
            var result = new List<PortableTerminalStyleV1>();
            AddStyle(result, PortableTerminalStyleV1.Default);
            AddStyle(result, snapshot.CurrentStyle);
            AddStyle(result, snapshot.SavedCursorStyle);
            AddStyle(result, snapshot.AlternateCurrentStyle);
            AddStyle(result, snapshot.AlternateSavedCursorStyle);
            foreach (var row in snapshot.ScrollbackRows)
                foreach (var cell in row.Cells) AddStyle(result, cell.Style);
            foreach (var row in snapshot.ScreenRows)
                foreach (var cell in row.Cells) AddStyle(result, cell.Style);
            foreach (var row in snapshot.AlternateScreenRows)
                foreach (var cell in row.Cells) AddStyle(result, cell.Style);
            return result;
        }

        private static void AddStyle(ICollection<PortableTerminalStyleV1> styles, PortableTerminalStyleV1 value)
        {
            if (!styles.Contains(value)) styles.Add(value);
        }

        private static string ValidateTermuxProfile(
            PortableTerminalSnapshotV1 source,
            IList<PortableTerminalStyleV1> styles)
        {
            if (styles.Count > MaximumTermuxStyles) return "TERMUX_STYLE_TABLE_LIMIT_EXCEEDED";
            if (source.Hyperlinks.Count != 0 ||
                styles.Any(value => value.HyperlinkIndexPlusOne != 0))
                return "TERMUX_HYPERLINK_UNSUPPORTED";

            if (source.SavedCursorPendingAutoWrap || source.AlternateSavedCursorPendingAutoWrap)
                return "TERMUX_SAVED_PENDING_WRAP_UNREPRESENTABLE";
            if (source.SavedCursorAutoWrap != source.AutoWrap ||
                source.AlternateSavedCursorAutoWrap != source.AutoWrap)
                return "TERMUX_SAVED_AUTOWRAP_UNREPRESENTABLE";

            // V1 has no field for Termux's CSI ? Pm s/r bank. The portable parser fails
            // closed on save/recall itself, so that bank remains at reset defaults. A
            // checkpoint is safe only while the current representable DEC modes still
            // equal those defaults; otherwise restoring the bank as current would lie.
            if (!source.AutoWrap || source.OriginMode || !source.CursorVisible ||
                source.BracketedPaste || source.FocusReporting)
                return "TERMUX_SAVED_DEC_MODE_BANK_UNREPRESENTABLE";

            if (HasUnrepresentableTabStops(source))
                return "TERMUX_TAB_STOP_STATE_UNREPRESENTABLE";

            if (source.ScrollTop != source.AlternateScrollTop ||
                source.ScrollBottomExclusive != source.AlternateScrollBottomExclusive)
                return "TERMUX_DUAL_SCREEN_MARGINS_UNSUPPORTED";

            if (source.ActiveAlternateScreen && !RetainedCursorEqualsSavedPrimary(source))
                return "TERMUX_DUAL_SCREEN_CURSOR_UNSUPPORTED";

            if (source.PendingAutoWrap &&
                source.CursorColumn != source.Columns - 1)
                return "TERMUX_PENDING_WRAP_CURSOR_UNSUPPORTED";

            foreach (var row in EnumerateAllRows(source))
                foreach (var cell in row.Cells)
                    if (!IsTermuxRestorableCell(cell)) return "TERMUX_GRAPHEME_UNSUPPORTED";
            return null;
        }

        private static bool HasUnrepresentableTabStops(PortableTerminalSnapshotV1 source)
        {
            if (!source.DefaultTabStopsEnabled) return true;
            for (var column = source.Columns; column < source.TabStopCapacity; column++)
            {
                var expandsToDefaultStop = column != 0 && (column & 7) == 0;
                if (source.TabStops.Contains(column) != expandsToDefaultStop) return true;
            }
            return false;
        }

        private static bool RetainedCursorEqualsSavedPrimary(PortableTerminalSnapshotV1 source) =>
            source.CursorColumn == source.SavedCursorColumn &&
            source.CursorRow == TermuxSavedCursorRow(
                source,
                source.SavedCursorRow,
                source.SavedCursorOriginMode) &&
            source.CurrentStyle.Equals(source.SavedCursorStyle) &&
            source.PendingAutoWrap == source.SavedCursorPendingAutoWrap &&
            source.OriginMode == source.SavedCursorOriginMode &&
            source.LineDrawingG0 == source.SavedCursorLineDrawingG0 &&
            source.LineDrawingG1 == source.SavedCursorLineDrawingG1 &&
            source.CharsetGl == source.SavedCursorCharsetGl;

        private static int TermuxSavedCursorRow(
            PortableTerminalSnapshotV1 source,
            int savedRow,
            bool savedOriginMode)
        {
            if (!savedOriginMode) return Math.Max(0, Math.Min(source.Rows - 1, savedRow));
            return Math.Max(
                source.ScrollTop,
                Math.Min(source.ScrollBottomExclusive - 1, savedRow + source.ScrollTop));
        }

        private static IEnumerable<PortableTerminalRowV1> EnumerateAllRows(PortableTerminalSnapshotV1 source)
        {
            foreach (var row in source.ScrollbackRows) yield return row;
            foreach (var row in source.ScreenRows) yield return row;
            foreach (var row in source.AlternateScreenRows) yield return row;
        }

        private static bool IsTermuxRestorableCell(PortableTerminalCellV1 cell)
        {
            if (cell.WideContinuation) return true;
            var text = cell.Character;
            if (string.IsNullOrEmpty(text)) return false;
            var offset = 0;
            var first = char.ConvertToUtf32(text, offset);
            if (TermuxScalarWidth(first) != cell.DisplayWidth) return false;
            offset += char.IsSurrogatePair(text, offset) ? 2 : 1;
            var combining = 0;
            while (offset < text.Length)
            {
                var scalar = char.ConvertToUtf32(text, offset);
                if (TermuxScalarWidth(scalar) != 0 || ++combining > MaximumTermuxCombiningScalars) return false;
                offset += char.IsSurrogatePair(text, offset) ? 2 : 1;
            }
            return true;
        }

        private static int TermuxScalarWidth(int scalar)
        {
            if (scalar == 0 || scalar == 0x034f || scalar is >= 0x200b and <= 0x200f ||
                scalar is 0x2028 or 0x2029 || scalar is >= 0x202a and <= 0x202e ||
                scalar is >= 0x2060 and <= 0x2063 || scalar < 32 || scalar is >= 0x7f and < 0xa0)
                return 0;
            foreach (var range in TermuxZeroWidthRanges)
                if (scalar >= range.Start && scalar <= range.End) return 0;
            foreach (var range in TermuxWideRanges)
                if (scalar >= range.Start && scalar <= range.End) return 2;
            return 1;
        }

        private static string ValidateArtifactSizes(
            byte[] snapshot,
            IList<byte[]> transcriptPages,
            bool terminalCoreSnapshot)
        {
            var maximumPlaintextBytes = terminalCoreSnapshot
                ? SnapshotCrypto.MaxTerminalCoreSnapshotPlaintextBytes
                : SnapshotCrypto.MaxSnapshotPlaintextBytes;
            var maximumObjectBytes = terminalCoreSnapshot
                ? SnapshotCrypto.MaxTerminalCoreSnapshotObjectBytes
                : SnapshotCrypto.MaxSnapshotObjectBytes;
            if (snapshot.Length > maximumPlaintextBytes)
                return terminalCoreSnapshot
                    ? "TERMINAL_CORE_SNAPSHOT_PLAINTEXT_LIMIT_EXCEEDED"
                    : "SNAPSHOT_PLAINTEXT_LIMIT_EXCEEDED";
            if (snapshot.Length > maximumObjectBytes - MaximumSealedContainerOverheadBytes)
                return terminalCoreSnapshot
                    ? "TERMINAL_CORE_SNAPSHOT_OBJECT_LIMIT_EXCEEDED"
                    : "SNAPSHOT_OBJECT_LIMIT_EXCEEDED";
            foreach (var page in transcriptPages)
            {
                if (page.Length > SnapshotCrypto.MaxTranscriptPlaintextBytes)
                    return "TRANSCRIPT_PLAINTEXT_LIMIT_EXCEEDED";
                if (page.Length > SnapshotCrypto.MaxTranscriptObjectBytes - MaximumSealedContainerOverheadBytes)
                    return "TRANSCRIPT_OBJECT_LIMIT_EXCEEDED";
            }
            return null;
        }

        // Exact Unicode 15 tables from the pinned Termux WcWidth implementation.
        private static readonly (int Start, int End)[] TermuxZeroWidthRanges =
        {
            (0x00300, 0x0036f), (0x00483, 0x00489), (0x00591, 0x005bd), (0x005bf, 0x005bf),
            (0x005c1, 0x005c2), (0x005c4, 0x005c5), (0x005c7, 0x005c7), (0x00610, 0x0061a),
            (0x0064b, 0x0065f), (0x00670, 0x00670), (0x006d6, 0x006dc), (0x006df, 0x006e4),
            (0x006e7, 0x006e8), (0x006ea, 0x006ed), (0x00711, 0x00711), (0x00730, 0x0074a),
            (0x007a6, 0x007b0), (0x007eb, 0x007f3), (0x007fd, 0x007fd), (0x00816, 0x00819),
            (0x0081b, 0x00823), (0x00825, 0x00827), (0x00829, 0x0082d), (0x00859, 0x0085b),
            (0x00898, 0x0089f), (0x008ca, 0x008e1), (0x008e3, 0x00902), (0x0093a, 0x0093a),
            (0x0093c, 0x0093c), (0x00941, 0x00948), (0x0094d, 0x0094d), (0x00951, 0x00957),
            (0x00962, 0x00963), (0x00981, 0x00981), (0x009bc, 0x009bc), (0x009c1, 0x009c4),
            (0x009cd, 0x009cd), (0x009e2, 0x009e3), (0x009fe, 0x009fe), (0x00a01, 0x00a02),
            (0x00a3c, 0x00a3c), (0x00a41, 0x00a42), (0x00a47, 0x00a48), (0x00a4b, 0x00a4d),
            (0x00a51, 0x00a51), (0x00a70, 0x00a71), (0x00a75, 0x00a75), (0x00a81, 0x00a82),
            (0x00abc, 0x00abc), (0x00ac1, 0x00ac5), (0x00ac7, 0x00ac8), (0x00acd, 0x00acd),
            (0x00ae2, 0x00ae3), (0x00afa, 0x00aff), (0x00b01, 0x00b01), (0x00b3c, 0x00b3c),
            (0x00b3f, 0x00b3f), (0x00b41, 0x00b44), (0x00b4d, 0x00b4d), (0x00b55, 0x00b56),
            (0x00b62, 0x00b63), (0x00b82, 0x00b82), (0x00bc0, 0x00bc0), (0x00bcd, 0x00bcd),
            (0x00c00, 0x00c00), (0x00c04, 0x00c04), (0x00c3c, 0x00c3c), (0x00c3e, 0x00c40),
            (0x00c46, 0x00c48), (0x00c4a, 0x00c4d), (0x00c55, 0x00c56), (0x00c62, 0x00c63),
            (0x00c81, 0x00c81), (0x00cbc, 0x00cbc), (0x00cbf, 0x00cbf), (0x00cc6, 0x00cc6),
            (0x00ccc, 0x00ccd), (0x00ce2, 0x00ce3), (0x00d00, 0x00d01), (0x00d3b, 0x00d3c),
            (0x00d41, 0x00d44), (0x00d4d, 0x00d4d), (0x00d62, 0x00d63), (0x00d81, 0x00d81),
            (0x00dca, 0x00dca), (0x00dd2, 0x00dd4), (0x00dd6, 0x00dd6), (0x00e31, 0x00e31),
            (0x00e34, 0x00e3a), (0x00e47, 0x00e4e), (0x00eb1, 0x00eb1), (0x00eb4, 0x00ebc),
            (0x00ec8, 0x00ece), (0x00f18, 0x00f19), (0x00f35, 0x00f35), (0x00f37, 0x00f37),
            (0x00f39, 0x00f39), (0x00f71, 0x00f7e), (0x00f80, 0x00f84), (0x00f86, 0x00f87),
            (0x00f8d, 0x00f97), (0x00f99, 0x00fbc), (0x00fc6, 0x00fc6), (0x0102d, 0x01030),
            (0x01032, 0x01037), (0x01039, 0x0103a), (0x0103d, 0x0103e), (0x01058, 0x01059),
            (0x0105e, 0x01060), (0x01071, 0x01074), (0x01082, 0x01082), (0x01085, 0x01086),
            (0x0108d, 0x0108d), (0x0109d, 0x0109d), (0x0135d, 0x0135f), (0x01712, 0x01714),
            (0x01732, 0x01733), (0x01752, 0x01753), (0x01772, 0x01773), (0x017b4, 0x017b5),
            (0x017b7, 0x017bd), (0x017c6, 0x017c6), (0x017c9, 0x017d3), (0x017dd, 0x017dd),
            (0x0180b, 0x0180d), (0x0180f, 0x0180f), (0x01885, 0x01886), (0x018a9, 0x018a9),
            (0x01920, 0x01922), (0x01927, 0x01928), (0x01932, 0x01932), (0x01939, 0x0193b),
            (0x01a17, 0x01a18), (0x01a1b, 0x01a1b), (0x01a56, 0x01a56), (0x01a58, 0x01a5e),
            (0x01a60, 0x01a60), (0x01a62, 0x01a62), (0x01a65, 0x01a6c), (0x01a73, 0x01a7c),
            (0x01a7f, 0x01a7f), (0x01ab0, 0x01ace), (0x01b00, 0x01b03), (0x01b34, 0x01b34),
            (0x01b36, 0x01b3a), (0x01b3c, 0x01b3c), (0x01b42, 0x01b42), (0x01b6b, 0x01b73),
            (0x01b80, 0x01b81), (0x01ba2, 0x01ba5), (0x01ba8, 0x01ba9), (0x01bab, 0x01bad),
            (0x01be6, 0x01be6), (0x01be8, 0x01be9), (0x01bed, 0x01bed), (0x01bef, 0x01bf1),
            (0x01c2c, 0x01c33), (0x01c36, 0x01c37), (0x01cd0, 0x01cd2), (0x01cd4, 0x01ce0),
            (0x01ce2, 0x01ce8), (0x01ced, 0x01ced), (0x01cf4, 0x01cf4), (0x01cf8, 0x01cf9),
            (0x01dc0, 0x01dff), (0x020d0, 0x020f0), (0x02cef, 0x02cf1), (0x02d7f, 0x02d7f),
            (0x02de0, 0x02dff), (0x0302a, 0x0302d), (0x03099, 0x0309a), (0x0a66f, 0x0a672),
            (0x0a674, 0x0a67d), (0x0a69e, 0x0a69f), (0x0a6f0, 0x0a6f1), (0x0a802, 0x0a802),
            (0x0a806, 0x0a806), (0x0a80b, 0x0a80b), (0x0a825, 0x0a826), (0x0a82c, 0x0a82c),
            (0x0a8c4, 0x0a8c5), (0x0a8e0, 0x0a8f1), (0x0a8ff, 0x0a8ff), (0x0a926, 0x0a92d),
            (0x0a947, 0x0a951), (0x0a980, 0x0a982), (0x0a9b3, 0x0a9b3), (0x0a9b6, 0x0a9b9),
            (0x0a9bc, 0x0a9bd), (0x0a9e5, 0x0a9e5), (0x0aa29, 0x0aa2e), (0x0aa31, 0x0aa32),
            (0x0aa35, 0x0aa36), (0x0aa43, 0x0aa43), (0x0aa4c, 0x0aa4c), (0x0aa7c, 0x0aa7c),
            (0x0aab0, 0x0aab0), (0x0aab2, 0x0aab4), (0x0aab7, 0x0aab8), (0x0aabe, 0x0aabf),
            (0x0aac1, 0x0aac1), (0x0aaec, 0x0aaed), (0x0aaf6, 0x0aaf6), (0x0abe5, 0x0abe5),
            (0x0abe8, 0x0abe8), (0x0abed, 0x0abed), (0x0fb1e, 0x0fb1e), (0x0fe00, 0x0fe0f),
            (0x0fe20, 0x0fe2f), (0x101fd, 0x101fd), (0x102e0, 0x102e0), (0x10376, 0x1037a),
            (0x10a01, 0x10a03), (0x10a05, 0x10a06), (0x10a0c, 0x10a0f), (0x10a38, 0x10a3a),
            (0x10a3f, 0x10a3f), (0x10ae5, 0x10ae6), (0x10d24, 0x10d27), (0x10eab, 0x10eac),
            (0x10efd, 0x10eff), (0x10f46, 0x10f50), (0x10f82, 0x10f85), (0x11001, 0x11001),
            (0x11038, 0x11046), (0x11070, 0x11070), (0x11073, 0x11074), (0x1107f, 0x11081),
            (0x110b3, 0x110b6), (0x110b9, 0x110ba), (0x110c2, 0x110c2), (0x11100, 0x11102),
            (0x11127, 0x1112b), (0x1112d, 0x11134), (0x11173, 0x11173), (0x11180, 0x11181),
            (0x111b6, 0x111be), (0x111c9, 0x111cc), (0x111cf, 0x111cf), (0x1122f, 0x11231),
            (0x11234, 0x11234), (0x11236, 0x11237), (0x1123e, 0x1123e), (0x11241, 0x11241),
            (0x112df, 0x112df), (0x112e3, 0x112ea), (0x11300, 0x11301), (0x1133b, 0x1133c),
            (0x11340, 0x11340), (0x11366, 0x1136c), (0x11370, 0x11374), (0x11438, 0x1143f),
            (0x11442, 0x11444), (0x11446, 0x11446), (0x1145e, 0x1145e), (0x114b3, 0x114b8),
            (0x114ba, 0x114ba), (0x114bf, 0x114c0), (0x114c2, 0x114c3), (0x115b2, 0x115b5),
            (0x115bc, 0x115bd), (0x115bf, 0x115c0), (0x115dc, 0x115dd), (0x11633, 0x1163a),
            (0x1163d, 0x1163d), (0x1163f, 0x11640), (0x116ab, 0x116ab), (0x116ad, 0x116ad),
            (0x116b0, 0x116b5), (0x116b7, 0x116b7), (0x1171d, 0x1171f), (0x11722, 0x11725),
            (0x11727, 0x1172b), (0x1182f, 0x11837), (0x11839, 0x1183a), (0x1193b, 0x1193c),
            (0x1193e, 0x1193e), (0x11943, 0x11943), (0x119d4, 0x119d7), (0x119da, 0x119db),
            (0x119e0, 0x119e0), (0x11a01, 0x11a0a), (0x11a33, 0x11a38), (0x11a3b, 0x11a3e),
            (0x11a47, 0x11a47), (0x11a51, 0x11a56), (0x11a59, 0x11a5b), (0x11a8a, 0x11a96),
            (0x11a98, 0x11a99), (0x11c30, 0x11c36), (0x11c38, 0x11c3d), (0x11c3f, 0x11c3f),
            (0x11c92, 0x11ca7), (0x11caa, 0x11cb0), (0x11cb2, 0x11cb3), (0x11cb5, 0x11cb6),
            (0x11d31, 0x11d36), (0x11d3a, 0x11d3a), (0x11d3c, 0x11d3d), (0x11d3f, 0x11d45),
            (0x11d47, 0x11d47), (0x11d90, 0x11d91), (0x11d95, 0x11d95), (0x11d97, 0x11d97),
            (0x11ef3, 0x11ef4), (0x11f00, 0x11f01), (0x11f36, 0x11f3a), (0x11f40, 0x11f40),
            (0x11f42, 0x11f42), (0x13440, 0x13440), (0x13447, 0x13455), (0x16af0, 0x16af4),
            (0x16b30, 0x16b36), (0x16f4f, 0x16f4f), (0x16f8f, 0x16f92), (0x16fe4, 0x16fe4),
            (0x1bc9d, 0x1bc9e), (0x1cf00, 0x1cf2d), (0x1cf30, 0x1cf46), (0x1d167, 0x1d169),
            (0x1d17b, 0x1d182), (0x1d185, 0x1d18b), (0x1d1aa, 0x1d1ad), (0x1d242, 0x1d244),
            (0x1da00, 0x1da36), (0x1da3b, 0x1da6c), (0x1da75, 0x1da75), (0x1da84, 0x1da84),
            (0x1da9b, 0x1da9f), (0x1daa1, 0x1daaf), (0x1e000, 0x1e006), (0x1e008, 0x1e018),
            (0x1e01b, 0x1e021), (0x1e023, 0x1e024), (0x1e026, 0x1e02a), (0x1e08f, 0x1e08f),
            (0x1e130, 0x1e136), (0x1e2ae, 0x1e2ae), (0x1e2ec, 0x1e2ef), (0x1e4ec, 0x1e4ef),
            (0x1e8d0, 0x1e8d6), (0x1e944, 0x1e94a), (0xe0100, 0xe01ef)
        };

        private static readonly (int Start, int End)[] TermuxWideRanges =
        {
            (0x1100, 0x115f), (0x231a, 0x231b), (0x2329, 0x232a), (0x23e9, 0x23ec),
            (0x23f0, 0x23f0), (0x23f3, 0x23f3), (0x25fd, 0x25fe), (0x2614, 0x2615),
            (0x2648, 0x2653), (0x267f, 0x267f), (0x2693, 0x2693), (0x26a1, 0x26a1),
            (0x26aa, 0x26ab), (0x26bd, 0x26be), (0x26c4, 0x26c5), (0x26ce, 0x26ce),
            (0x26d4, 0x26d4), (0x26ea, 0x26ea), (0x26f2, 0x26f3), (0x26f5, 0x26f5),
            (0x26fa, 0x26fa), (0x26fd, 0x26fd), (0x2705, 0x2705), (0x270a, 0x270b),
            (0x2728, 0x2728), (0x274c, 0x274c), (0x274e, 0x274e), (0x2753, 0x2755),
            (0x2757, 0x2757), (0x2795, 0x2797), (0x27b0, 0x27b0), (0x27bf, 0x27bf),
            (0x2b1b, 0x2b1c), (0x2b50, 0x2b50), (0x2b55, 0x2b55), (0x2e80, 0x2e99),
            (0x2e9b, 0x2ef3), (0x2f00, 0x2fd5), (0x2ff0, 0x2ffb), (0x3000, 0x303e),
            (0x3041, 0x3096), (0x3099, 0x30ff), (0x3105, 0x312f), (0x3131, 0x318e),
            (0x3190, 0x31e3), (0x31f0, 0x321e), (0x3220, 0x3247), (0x3250, 0x4dbf),
            (0x4e00, 0xa48c), (0xa490, 0xa4c6), (0xa960, 0xa97c), (0xac00, 0xd7a3),
            (0xf900, 0xfaff), (0xfe10, 0xfe19), (0xfe30, 0xfe52), (0xfe54, 0xfe66),
            (0xfe68, 0xfe6b), (0xff01, 0xff60), (0xffe0, 0xffe6), (0x16fe0, 0x16fe4),
            (0x16ff0, 0x16ff1), (0x17000, 0x187f7), (0x18800, 0x18cd5), (0x18d00, 0x18d08),
            (0x1aff0, 0x1aff3), (0x1aff5, 0x1affb), (0x1affd, 0x1affe), (0x1b000, 0x1b122),
            (0x1b132, 0x1b132), (0x1b150, 0x1b152), (0x1b155, 0x1b155), (0x1b164, 0x1b167),
            (0x1b170, 0x1b2fb), (0x1f004, 0x1f004), (0x1f0cf, 0x1f0cf), (0x1f18e, 0x1f18e),
            (0x1f191, 0x1f19a), (0x1f200, 0x1f202), (0x1f210, 0x1f23b), (0x1f240, 0x1f248),
            (0x1f250, 0x1f251), (0x1f260, 0x1f265), (0x1f300, 0x1f320), (0x1f32d, 0x1f335),
            (0x1f337, 0x1f37c), (0x1f37e, 0x1f393), (0x1f3a0, 0x1f3ca), (0x1f3cf, 0x1f3d3),
            (0x1f3e0, 0x1f3f0), (0x1f3f4, 0x1f3f4), (0x1f3f8, 0x1f43e), (0x1f440, 0x1f440),
            (0x1f442, 0x1f4fc), (0x1f4ff, 0x1f53d), (0x1f54b, 0x1f54e), (0x1f550, 0x1f567),
            (0x1f57a, 0x1f57a), (0x1f595, 0x1f596), (0x1f5a4, 0x1f5a4), (0x1f5fb, 0x1f64f),
            (0x1f680, 0x1f6c5), (0x1f6cc, 0x1f6cc), (0x1f6d0, 0x1f6d2), (0x1f6d5, 0x1f6d7),
            (0x1f6dc, 0x1f6df), (0x1f6eb, 0x1f6ec), (0x1f6f4, 0x1f6fc), (0x1f7e0, 0x1f7eb),
            (0x1f7f0, 0x1f7f0), (0x1f90c, 0x1f93a), (0x1f93c, 0x1f945), (0x1f947, 0x1f9ff),
            (0x1fa70, 0x1fa7c), (0x1fa80, 0x1fa88), (0x1fa90, 0x1fabd), (0x1fabf, 0x1fac5),
            (0x1face, 0x1fadb), (0x1fae0, 0x1fae8), (0x1faf0, 0x1faf8), (0x20000, 0x2fffd),
            (0x30000, 0x3fffd)
        };

        private static TerminalSnapshotV1 BuildSnapshot(
            PortableTerminalSnapshotV1 source,
            IList<PortableTerminalStyleV1> styles,
            TerminalPaletteV1 palette,
            TerminalLifecycleV1 lifecycle,
            bool hasExitCode,
            int exitCode,
            ulong localInputEpoch)
        {
            var charset = CreateCharset(source);
            var snapshot = new TerminalSnapshotV1
            {
                FormatVersion = SnapshotCrypto.FormatVersion,
                SnapshotSeq = source.Sequence,
                Columns = checked((uint)source.Columns),
                Rows = checked((uint)source.Rows),
                ActiveScreen = source.ActiveAlternateScreen
                    ? TerminalScreenKindV1.Alternate
                    : TerminalScreenKindV1.Primary,
                PrimaryScreen = CreatePrimaryScreen(source, styles, charset),
                AlternateScreen = CreateAlternateScreen(source, styles, charset),
                Modes = new TerminalModesV1
                {
                    AutoWrap = source.AutoWrap,
                    Origin = source.OriginMode,
                    Insert = source.InsertMode,
                    BracketedPaste = source.BracketedPaste,
                    FocusReporting = source.FocusReporting,
                    MouseTracking = TerminalMouseTrackingV1.None,
                    MouseEncoding = TerminalMouseEncodingV1.Default
                },
                Palette = palette.Clone(),
                ScrollbackFirstLineId = 0,
                NextTranscriptLineId = source.NextLineId,
                ScrollbackTruncated = false,
                Title = source.Title,
                Projection = new TerminalSessionProjectionV1
                {
                    Lifecycle = lifecycle,
                    ObservedLocalInputEpoch = localInputEpoch
                },
                ParserState = SnapshotParserStateV1.Ground
            };
            if (hasExitCode) snapshot.Projection.ExitCode = exitCode;
            foreach (var style in styles) snapshot.StyleTable.Add(ToWireStyle(style));
            foreach (var column in source.TabStops)
                if (column < source.Columns) snapshot.TabStops.Add(checked((uint)column));
            return snapshot;
        }

        private static TerminalScreenV1 CreatePrimaryScreen(
            PortableTerminalSnapshotV1 source,
            IList<PortableTerminalStyleV1> styles,
            TerminalCharsetStateV1 charset)
        {
            var screen = new TerminalScreenV1
            {
                Cursor = CreateCursor(
                    checked((uint)source.CursorColumn),
                    checked((uint)source.CursorRow),
                    source.CursorVisible,
                    source.CursorStyle,
                    source.CursorBlink,
                    checked((uint)styles.IndexOf(source.CurrentStyle)),
                    source.PendingAutoWrap,
                    charset,
                    source.OriginMode),
                SavedCursor = CreateCursor(
                    checked((uint)source.SavedCursorColumn),
                    checked((uint)TermuxSavedCursorRow(
                        source,
                        source.SavedCursorRow,
                        source.SavedCursorOriginMode)),
                    source.CursorVisible,
                    source.CursorStyle,
                    source.CursorBlink,
                    checked((uint)styles.IndexOf(source.SavedCursorStyle)),
                    source.SavedCursorPendingAutoWrap,
                    CreateCharset(
                        source.SavedCursorLineDrawingG0,
                        source.SavedCursorLineDrawingG1,
                        source.SavedCursorCharsetGl),
                    source.SavedCursorOriginMode),
                ScrollTop = checked((uint)source.ScrollTop),
                ScrollBottom = checked((uint)(source.ScrollBottomExclusive - 1)),
                MarginLeft = 0,
                MarginRight = checked((uint)(source.Columns - 1)),
                CurrentStyleIndex = checked((uint)styles.IndexOf(source.CurrentStyle)),
                PendingWrap = source.PendingAutoWrap,
                LastPrintedGrapheme = string.Empty,
                Charset = charset.Clone()
            };
            foreach (var row in source.ScreenRows) screen.VisibleRows.Add(ToWireRow(row, styles));
            return screen;
        }

        private static TerminalScreenV1 CreateAlternateScreen(
            PortableTerminalSnapshotV1 source,
            IList<PortableTerminalStyleV1> styles,
            TerminalCharsetStateV1 charset)
        {
            // While primary is active Termux has no retained alternate current cursor: entering
            // the alternate buffer carries the primary current state. The inactive wire cursor
            // is therefore deliberately that retained state, not Portable's disposable cursor.
            var currentStyle = source.ActiveAlternateScreen
                ? source.AlternateCurrentStyle
                : source.CurrentStyle;
            var cursorColumn = source.ActiveAlternateScreen
                ? source.AlternateCursorColumn
                : source.CursorColumn;
            var cursorRow = source.ActiveAlternateScreen
                ? source.AlternateCursorRow
                : source.CursorRow;
            var pendingWrap = source.ActiveAlternateScreen
                ? source.AlternatePendingAutoWrap
                : source.PendingAutoWrap;
            var currentStyleIndex = checked((uint)styles.IndexOf(currentStyle));
            var screen = new TerminalScreenV1
            {
                Cursor = CreateCursor(
                    checked((uint)cursorColumn),
                    checked((uint)cursorRow),
                    source.CursorVisible,
                    source.CursorStyle,
                    source.CursorBlink,
                    currentStyleIndex,
                    pendingWrap,
                    charset,
                    source.OriginMode),
                SavedCursor = CreateCursor(
                    checked((uint)source.AlternateSavedCursorColumn),
                    checked((uint)TermuxSavedCursorRow(
                        source,
                        source.AlternateSavedCursorRow,
                        source.AlternateSavedCursorOriginMode)),
                    source.CursorVisible,
                    source.CursorStyle,
                    source.CursorBlink,
                    checked((uint)styles.IndexOf(source.AlternateSavedCursorStyle)),
                    source.AlternateSavedCursorPendingAutoWrap,
                    CreateCharset(
                        source.AlternateSavedCursorLineDrawingG0,
                        source.AlternateSavedCursorLineDrawingG1,
                        source.AlternateSavedCursorCharsetGl),
                    source.AlternateSavedCursorOriginMode),
                ScrollTop = checked((uint)source.AlternateScrollTop),
                ScrollBottom = checked((uint)(source.AlternateScrollBottomExclusive - 1)),
                MarginLeft = 0,
                MarginRight = checked((uint)(source.Columns - 1)),
                CurrentStyleIndex = currentStyleIndex,
                PendingWrap = pendingWrap,
                LastPrintedGrapheme = string.Empty,
                Charset = charset.Clone()
            };
            foreach (var row in source.AlternateScreenRows)
                screen.VisibleRows.Add(ToWireRow(row, styles));
            return screen;
        }

        private static TerminalCursorStateV1 CreateCursor(
            uint column,
            uint row,
            bool visible,
            PortableTerminalCursorStyleV1 cursorStyle,
            bool cursorBlink,
            uint styleIndex,
            bool pendingWrap,
            TerminalCharsetStateV1 charset,
            bool originMode = false)
        {
            return new TerminalCursorStateV1
            {
                Column = column,
                Row = row,
                Visible = visible,
                Style = cursorStyle switch
                {
                    PortableTerminalCursorStyleV1.Block => TerminalCursorStyleV1.Block,
                    PortableTerminalCursorStyleV1.Underline => TerminalCursorStyleV1.Underline,
                    PortableTerminalCursorStyleV1.Bar => TerminalCursorStyleV1.Bar,
                    _ => throw new InvalidDataException("Terminal cursor style is unsupported.")
                },
                Blink = cursorBlink,
                RenditionStyleIndex = styleIndex,
                Charset = charset.Clone(),
                OriginMode = originMode,
                PendingWrap = pendingWrap
            };
        }

        private static IEnumerable<TerminalTranscriptPageV1> BuildTranscriptPages(
            PortableTerminalSnapshotV1 source,
            IList<IReadOnlyList<PortableTerminalRowV1>> archivedPages,
            TerminalPaletteV1 palette)
        {
            if (archivedPages == null) throw new ArgumentNullException(nameof(archivedPages));
            var pageRows = new List<IReadOnlyList<PortableTerminalRowV1>>(archivedPages.Count +
                (source.ScrollbackRows.Count + MaximumTranscriptRowsPerPage - 1) /
                MaximumTranscriptRowsPerPage);
            pageRows.AddRange(archivedPages);
            for (var offset = 0; offset < source.ScrollbackRows.Count; offset += MaximumTranscriptRowsPerPage)
            {
                var count = Math.Min(MaximumTranscriptRowsPerPage, source.ScrollbackRows.Count - offset);
                pageRows.Add(source.ScrollbackRows.Skip(offset).Take(count).ToArray());
            }
            for (var pageIndex = 0; pageIndex < pageRows.Count; pageIndex++)
            {
                var rows = pageRows[pageIndex];
                if (rows.Count == 0 || rows.Count > MaximumTranscriptRowsPerPage)
                    throw new InvalidDataException("A transcript archive page has an invalid row count.");
                var styles = BuildStyleTable(rows);
                if (styles.Count > MaximumTermuxStyles)
                    throw new InvalidDataException("A transcript archive page exceeds the style-table limit.");
                var first = rows[0];
                var last = rows[^1];
                var page = new TerminalTranscriptPageV1
                {
                    FormatVersion = SnapshotCrypto.FormatVersion,
                    AsOfSnapshotSeq = source.Sequence,
                    FirstLineId = first.LineId,
                    LastLineId = last.LineId,
                    HasMoreBefore = pageIndex > 0,
                    NextBeforeLineId = pageIndex > 0 ? first.LineId : 0,
                    Palette = palette.Clone()
                };
                foreach (var style in styles) page.StyleTable.Add(ToWireStyle(style));
                foreach (var row in rows) page.Rows.Add(ToWireRow(row, styles));
                yield return page;
            }
        }

        private static List<PortableTerminalStyleV1> BuildStyleTable(
            IEnumerable<PortableTerminalRowV1> rows)
        {
            var result = new List<PortableTerminalStyleV1>();
            AddStyle(result, PortableTerminalStyleV1.Default);
            foreach (var row in rows)
                foreach (var cell in row.Cells) AddStyle(result, cell.Style);
            return result;
        }

        private static TerminalRowV1 ToWireRow(
            PortableTerminalRowV1 source,
            IList<PortableTerminalStyleV1> styles)
        {
            var row = new TerminalRowV1
            {
                LineId = source.LineId,
                SealedAtSeq = source.SealedAtSequence,
                SoftWrapped = source.Wrapped
            };
            for (var index = 0; index < source.Cells.Count; index++)
            {
                var cell = source.Cells[index];
                var styleIndex = styles.IndexOf(cell.Style);
                if (styleIndex < 0) throw new InvalidDataException("Terminal row style is absent from its style table.");
                var repeat = 1;
                var grapheme = cell.Character;
                if (!cell.WideContinuation && cell.DisplayWidth == 1 && cell.Character == " ")
                {
                    while (index + repeat < source.Cells.Count)
                    {
                        var candidate = source.Cells[index + repeat];
                        if (candidate.WideContinuation || candidate.DisplayWidth != 1 || candidate.Character != " " ||
                            !candidate.Style.Equals(cell.Style)) break;
                        repeat++;
                    }
                    // Empty width-one graphemes are the protocol's canonical RLE blank.
                    grapheme = string.Empty;
                }
                row.Runs.Add(new TerminalCellRunV1
                {
                    Repeat = checked((uint)repeat),
                    Grapheme = grapheme,
                    DisplayWidth = checked((uint)cell.DisplayWidth),
                    StyleIndex = checked((uint)styleIndex),
                    WideContinuation = cell.WideContinuation
                });
                index += repeat - 1;
            }
            return row;
        }

        private static TerminalStyleV1 ToWireStyle(PortableTerminalStyleV1 source)
        {
            return new TerminalStyleV1
            {
                Foreground = ToWireColor(source.Foreground),
                Background = ToWireColor(source.Background),
                UnderlineColor = new TerminalColorRefV1 { DefaultColor = true },
                Attributes = (uint)source.Attributes,
                UnderlineStyle = (source.Attributes & PortableTerminalAttributesV1.Underline) != 0
                    ? TerminalUnderlineStyleV1.Single
                    : TerminalUnderlineStyleV1.None,
                HyperlinkIndexPlusOne = checked((uint)source.HyperlinkIndexPlusOne)
            };
        }

        private static TerminalColorRefV1 ToWireColor(int color)
        {
            if (color == PortableTerminalStyleV1.DefaultColor)
                return new TerminalColorRefV1 { DefaultColor = true };
            if (PortableTerminalStyleV1.TryGetRgb(color, out var rgb))
                return new TerminalColorRefV1 { Rgb = checked((uint)rgb) };
            return new TerminalColorRefV1 { PaletteIndex = checked((uint)color) };
        }

        private static TerminalCharsetStateV1 CreateCharset(PortableTerminalSnapshotV1 source)
            => CreateCharset(source.LineDrawingG0, source.LineDrawingG1, source.CharsetGl);

        private static TerminalCharsetStateV1 CreateCharset(
            bool lineDrawingG0,
            bool lineDrawingG1,
            int charsetGl)
        {
            return new TerminalCharsetStateV1
            {
                G0 = lineDrawingG0
                    ? TerminalCharsetV1.DecSpecialGraphics
                    : TerminalCharsetV1.Ascii,
                G1 = lineDrawingG1
                    ? TerminalCharsetV1.DecSpecialGraphics
                    : TerminalCharsetV1.Ascii,
                G2 = TerminalCharsetV1.Ascii,
                G3 = TerminalCharsetV1.Ascii,
                Gl = checked((uint)charsetGl),
                // The Android Termux renderer does not expose an independent GR selector.
                // Emit the portable subset that can be restored without changing semantics.
                Gr = 0
            };
        }

        private static TerminalPaletteV1 CreatePalette(PortableTerminalSnapshotV1 source)
        {
            var palette = new TerminalPaletteV1
            {
                DefaultForegroundRgb = checked((uint)source.DefaultForegroundRgb),
                DefaultBackgroundRgb = checked((uint)source.DefaultBackgroundRgb),
                DefaultCursorRgb = checked((uint)source.DefaultCursorRgb)
            };
            foreach (var color in source.PaletteRgb)
                palette.ColorsRgb.Add(checked((uint)color));
            return palette;
        }

        private static ProductionTerminalHistoryOutbox BuildOutbox(
            Guid sessionId,
            TerminalSnapshotV1 snapshot,
            IList<TerminalTranscriptPageV1> pages,
            byte[] snapshotBytes,
            IList<byte[]> pageBytes,
            bool terminalCoreSnapshot)
        {
            var outbox = new ProductionTerminalHistoryOutbox
            {
                SessionId = sessionId,
                SnapshotSequence = snapshot.SnapshotSeq
            };
            var initialTranscriptBeforeLineId = SnapshotCrypto.InitialTranscriptBeforeLineId(snapshot);
            var order = 0;
            // Android can render the tail immediately after this anchor lands;
            // older transcript pages are background backfill and must never
            // delay the usable snapshot.
            outbox.Artifacts.Add(new ProductionTerminalHistoryArtifact
            {
                ArtifactId = "snapshot-" + snapshot.SnapshotSeq.ToString("D20"),
                Kind = ProductionTerminalHistoryArtifactKind.Snapshot,
                UploadOrder = order++,
                SnapshotSequence = snapshot.SnapshotSeq,
                InitialTranscriptBeforeLineId = initialTranscriptBeforeLineId,
                ObservedLocalInputEpoch = terminalCoreSnapshot
                    ? snapshot.Projection.ObservedLocalInputEpoch
                    : 0,
                TranscriptPageCount = pages.Count,
                Columns = checked((int)snapshot.Columns),
                Rows = checked((int)snapshot.Rows),
                FormatName = terminalCoreSnapshot
                    ? SnapshotCrypto.TerminalCoreSnapshotFormatName
                    : SnapshotCrypto.SnapshotFormatName,
                FormatVersion = terminalCoreSnapshot
                    ? checked((int)SnapshotCrypto.TerminalCoreSnapshotFormatVersion)
                    : checked((int)SnapshotCrypto.FormatVersion),
                Plaintext = (byte[])snapshotBytes.Clone()
            });
            for (var index = 0; index < pages.Count; index++)
            {
                var page = pages[index];
                outbox.Artifacts.Add(new ProductionTerminalHistoryArtifact
                {
                    ArtifactId = "transcript-" + snapshot.SnapshotSeq.ToString("D20") + "-" +
                        page.FirstLineId.ToString("D20") + "-" + page.LastLineId.ToString("D20"),
                    Kind = ProductionTerminalHistoryArtifactKind.Transcript,
                    UploadOrder = order++,
                    SnapshotSequence = snapshot.SnapshotSeq,
                    FirstLineId = page.FirstLineId,
                    LastLineId = page.LastLineId,
                    ExclusiveBeforeLineId = index + 1 < pages.Count
                        ? pages[index + 1].FirstLineId
                        : snapshot.PrimaryScreen.VisibleRows[0].LineId,
                    HasMoreBefore = page.HasMoreBefore,
                    NextBeforeLineId = page.NextBeforeLineId,
                    FormatName = SnapshotCrypto.TranscriptFormatName,
                    FormatVersion = checked((int)SnapshotCrypto.FormatVersion),
                    Plaintext = (byte[])pageBytes[index].Clone()
                });
            }
            return outbox;
        }

        private static ProductionTerminalHistoryOutbox BuildTerminalCoreOutbox(
            Guid sessionId,
            ulong snapshotSequence,
            int columns,
            int rows,
            ulong initialTranscriptBeforeLineId,
            ulong observedLocalInputEpoch,
            byte[] snapshotBytes)
        {
            return new ProductionTerminalHistoryOutbox
            {
                SessionId = sessionId,
                SnapshotSequence = snapshotSequence,
                Artifacts = new List<ProductionTerminalHistoryArtifact>
                {
                    new ProductionTerminalHistoryArtifact
                    {
                        ArtifactId = "snapshot-" + snapshotSequence.ToString("D20"),
                        Kind = ProductionTerminalHistoryArtifactKind.Snapshot,
                        UploadOrder = 0,
                        SnapshotSequence = snapshotSequence,
                        InitialTranscriptBeforeLineId = initialTranscriptBeforeLineId,
                        ObservedLocalInputEpoch = observedLocalInputEpoch,
                        TranscriptPageCount = 0,
                        Columns = columns,
                        Rows = rows,
                        FormatName = SnapshotCrypto.TerminalCoreSnapshotFormatName,
                        FormatVersion = checked((int)SnapshotCrypto.TerminalCoreSnapshotFormatVersion),
                        Plaintext = (byte[])snapshotBytes.Clone()
                    }
                }
            };
        }

        private byte[] SealArtifact(
            Guid sessionId,
            ProductionHistoryRecipient recipient,
            ProductionTerminalHistoryArtifact artifact)
        {
            if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Transcript)
            {
                var page = TerminalTranscriptPageV1.Parser.ParseFrom(artifact.Plaintext);
                return SnapshotCrypto.SealTranscript(
                    recipient.PairwiseKey,
                    sessionId,
                    pcDeviceId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    page,
                    artifact.ExclusiveBeforeLineId);
            }
            if (artifact.Kind == ProductionTerminalHistoryArtifactKind.Snapshot)
            {
                if (artifact.FormatName == SnapshotCrypto.TerminalCoreSnapshotFormatName &&
                    artifact.FormatVersion == SnapshotCrypto.TerminalCoreSnapshotFormatVersion)
                {
                    return SnapshotCrypto.SealTerminalCoreSnapshot(
                        recipient.PairwiseKey,
                        sessionId,
                        pcDeviceId,
                        recipient.DeviceId,
                        recipient.KeyEpoch,
                        artifact.SnapshotSequence,
                        checked((uint)artifact.Columns),
                        checked((uint)artifact.Rows),
                        artifact.InitialTranscriptBeforeLineId,
                        artifact.ObservedLocalInputEpoch,
                        artifact.Plaintext);
                }
                var snapshot = TerminalSnapshotV1.Parser.ParseFrom(artifact.Plaintext);
                return SnapshotCrypto.SealSnapshot(
                    recipient.PairwiseKey,
                    sessionId,
                    pcDeviceId,
                    recipient.DeviceId,
                    recipient.KeyEpoch,
                    snapshot);
            }
            throw new InvalidDataException("Terminal history artifact kind is unsupported.");
        }
    }
}
