using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;

namespace TermLink.Agent
{
    /// <summary>
    /// Runs diagnostic terminal-core replay away from the production history worker. Work is
    /// bounded per session; overload retires only that session's shadow instead of applying
    /// backpressure to durable history publication.
    /// </summary>
    internal sealed class ProductionTerminalCoreShadowDispatcher : IDisposable
    {
        internal const int MaximumPendingWorkItemsPerSession = 8;
        internal const long MaximumPendingRetainedBytesPerSession = 16L * 1024L * 1024L;

        private readonly ConcurrentDictionary<Guid, SessionState> sessions =
            new ConcurrentDictionary<Guid, SessionState>();
        private readonly Action<Guid, ProductionTerminalCoreShadowResult> observer;
        private int disposed;

        private sealed class SessionState
        {
            public readonly object Sync = new object();
            public Task Tail = Task.CompletedTask;
            public ProductionTerminalCoreShadow Shadow;
            public int PendingWorkItems;
            public long PendingRetainedBytes;
            public bool Accepting = true;
            public bool ReleaseScheduled;
        }

        private sealed class WorkItem
        {
            public Guid SessionId;
            public LocalFrame[] Frames;
            public PortableTerminalSnapshotV1 Portable;
            public ulong ExpectedSequence;
            public string PortableReasonCode;
            public long RetainedBytes;
        }

        public ProductionTerminalCoreShadowDispatcher(
            Action<Guid, ProductionTerminalCoreShadowResult> observer)
        {
            this.observer = observer;
        }

        public void Enqueue(
            Guid sessionId,
            IReadOnlyList<LocalFrame> frames,
            PortableTerminalSnapshotV1 portable,
            ulong expectedSequence,
            string portableReasonCode,
            Func<bool> rejectPredicate = null)
        {
            if (sessionId == Guid.Empty || frames == null || frames.Count == 0 ||
                Volatile.Read(ref disposed) != 0 || ShouldReject(rejectPredicate))
                return;

            // A retained tombstone is the common late-retry path. Avoid walking the Portable
            // graph (and avoid diagnostic noise from otherwise-invalid ignored frames) once the
            // session is known to be permanently closed. A concurrent retirement immediately
            // after this check may waste one estimate, but the locked admission check below still
            // prevents cloning or scheduling that work.
            if (sessions.TryGetValue(sessionId, out var retainedState))
            {
                lock (retainedState.Sync)
                    if (!retainedState.Accepting || retainedState.ReleaseScheduled) return;
            }

            SessionState state = null;
            try
            {
                var retainedBytes = EstimateRetainedBytes(
                    frames,
                    portable,
                    sessionId,
                    portableReasonCode);
                state = sessions.GetOrAdd(sessionId, _ => new SessionState());
                lock (state.Sync)
                {
                    // The caller's persistent release/retire predicate closes the window between
                    // the cheap preflight above and GetOrAdd.  If Release removed the old
                    // dispatcher tombstone in that interval, fail this newly-created generation
                    // closed and remove it by key+value identity.
                    if (Volatile.Read(ref disposed) != 0 || ShouldReject(rejectPredicate))
                    {
                        state.Accepting = false;
                        TryRemoveSession(sessionId, state);
                        ScheduleReleaseLocked(state, sessionId, null);
                        return;
                    }
                    if (!state.Accepting || state.ReleaseScheduled)
                        return;
                    if (state.PendingWorkItems >= MaximumPendingWorkItemsPerSession ||
                        retainedBytes > MaximumPendingRetainedBytesPerSession ||
                        state.PendingRetainedBytes >
                            MaximumPendingRetainedBytesPerSession - retainedBytes)
                    {
                        RetireLocked(state, sessionId, "DISPATCH_QUEUE_OVERFLOW");
                        return;
                    }

                    var copies = new LocalFrame[frames.Count];
                    for (var index = 0; index < frames.Count; index++)
                    {
                        var frame = frames[index];
                        copies[index] = new LocalFrame(
                            frame.Type,
                            frame.SessionId,
                            frame.Sequence,
                            (byte[])frame.Payload.Clone());
                    }
                    var work = new WorkItem
                    {
                        SessionId = sessionId,
                        Frames = copies,
                        Portable = portable,
                        ExpectedSequence = expectedSequence,
                        PortableReasonCode = portableReasonCode ?? string.Empty,
                        RetainedBytes = retainedBytes
                    };
                    state.PendingWorkItems++;
                    state.PendingRetainedBytes += retainedBytes;
                    state.Tail = state.Tail.ContinueWith(
                        _ => Execute(state, work),
                        CancellationToken.None,
                        TaskContinuationOptions.None,
                        TaskScheduler.Default);
                }
            }
            catch (Exception error)
            {
                var reasonCode = "DISPATCH_" + error.GetType().Name.ToUpperInvariant();
                if (state == null)
                {
                    if (Volatile.Read(ref disposed) != 0 || ShouldReject(rejectPredicate)) return;
                    state = sessions.GetOrAdd(sessionId, _ => new SessionState());
                }
                lock (state.Sync)
                {
                    // Estimation and cloning failures are permanent shadow failures too. Install
                    // the tombstone before the diagnostic callback, which is untrusted and may
                    // block. Recheck the caller lifecycle gate when this catch had to create state
                    // so a concurrent final release cannot be followed by a new generation.
                    if (!state.Accepting || state.ReleaseScheduled) return;
                    if (Volatile.Read(ref disposed) != 0 || ShouldReject(rejectPredicate))
                    {
                        state.Accepting = false;
                        TryRemoveSession(sessionId, state);
                        ScheduleReleaseLocked(state, sessionId, null);
                        return;
                    }
                    RetireLocked(state, sessionId, null);
                }
                Notify(sessionId, new ProductionTerminalCoreShadowResult
                {
                    Status = "SHADOW_RETIRED",
                    ReasonCode = reasonCode,
                    LastAppliedSequence = expectedSequence
                });
            }
        }

        /// <summary>
        /// Stops a session permanently while retaining its dictionary tombstone. This is used
        /// when history retries may still arrive and must not create a second native generation.
        /// </summary>
        public void RetireSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty || Volatile.Read(ref disposed) != 0) return;
            var state = sessions.GetOrAdd(sessionId, _ => new SessionState());
            lock (state.Sync)
            {
                if (Volatile.Read(ref disposed) != 0)
                {
                    state.Accepting = false;
                    TryRemoveSession(sessionId, state);
                    ScheduleReleaseLocked(state, sessionId, null);
                    return;
                }
                RetireLocked(state, sessionId, null);
            }
        }

        /// <summary>
        /// Removes a session after the producer has established that no later Enqueue can occur.
        /// The key/value removal prevents an old state from deleting a newer state (ABA).
        /// </summary>
        public void ReleaseSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty || !sessions.TryGetValue(sessionId, out var state)) return;
            lock (state.Sync)
            {
                state.Accepting = false;
                TryRemoveSession(sessionId, state);
                ScheduleReleaseLocked(state, sessionId, null);
            }
        }

        internal int SessionCount
        {
            get
            {
                var count = 0;
                foreach (var state in sessions.Values)
                {
                    lock (state.Sync)
                        if (state.Accepting && !state.ReleaseScheduled) count++;
                }
                return count;
            }
        }

        internal int RetainedSessionCount => sessions.Count;

        private void Execute(SessionState state, WorkItem work)
        {
            try
            {
                lock (state.Sync)
                    if (!state.Accepting) return;

                if (state.Shadow == null)
                    state.Shadow = new ProductionTerminalCoreShadow(80, 24);
                foreach (var frame in work.Frames) state.Shadow.Apply(frame);
                var result = state.Shadow.ObserveCheckpoint(
                    work.Portable,
                    work.ExpectedSequence,
                    work.PortableReasonCode);
                if (string.Equals(result?.Status, "SHADOW_RETIRED", StringComparison.Ordinal))
                {
                    // Retire before observer notification: observers are diagnostic and may
                    // block, while this tombstone must immediately reject later work.
                    lock (state.Sync) RetireLocked(state, work.SessionId, null);
                }
                Notify(work.SessionId, result);
            }
            catch (Exception error)
            {
                var result = new ProductionTerminalCoreShadowResult
                {
                    Status = "SHADOW_RETIRED",
                    ReasonCode = "DISPATCH_" + error.GetType().Name.ToUpperInvariant(),
                    LastAppliedSequence = work.ExpectedSequence
                };
                // Exceptions also fail closed before a potentially blocking diagnostic callback.
                lock (state.Sync) RetireLocked(state, work.SessionId, null);
                Notify(work.SessionId, result);
            }
            finally
            {
                lock (state.Sync)
                {
                    state.PendingWorkItems--;
                    state.PendingRetainedBytes -= work.RetainedBytes;
                }
            }
        }

        private void RetireLocked(SessionState state, Guid sessionId, string reasonCode)
        {
            state.Accepting = false;
            // Deliberately retain this state in the dictionary. History can retry after terminal
            // completion; the tombstone makes the queue limit apply to the whole session rather
            // than to a succession of detached generations.
            ScheduleReleaseLocked(state, sessionId, reasonCode);
        }

        private void ScheduleReleaseLocked(SessionState state, Guid sessionId, string reasonCode)
        {
            if (state.ReleaseScheduled) return;
            state.ReleaseScheduled = true;
            state.Tail = state.Tail.ContinueWith(
                _ =>
                {
                    if (!string.IsNullOrWhiteSpace(reasonCode))
                    {
                        Notify(sessionId, new ProductionTerminalCoreShadowResult
                        {
                            Status = "SHADOW_RETIRED",
                            ReasonCode = reasonCode
                        });
                    }
                    try { state.Shadow?.Dispose(); }
                    catch { }
                    state.Shadow = null;
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }

        internal static long EstimateRetainedBytes(
            IReadOnlyList<LocalFrame> frames,
            PortableTerminalSnapshotV1 portable,
            Guid sessionId,
            string portableReasonCode)
        {
            // This is intentionally a conservative managed-retention estimate rather than a
            // claim about exact CLR heap size. It includes cloned payloads, frame/array objects,
            // and the immutable Portable snapshot graph retained by the queued continuation.
            long bytes = checked(256L + 24L + (8L * frames.Count) +
                StringBytes(portableReasonCode));
            for (var index = 0; index < frames.Count; index++)
            {
                var frame = frames[index];
                if (frame == null || frame.SessionId != sessionId)
                    throw new ArgumentException("Shadow work contains an invalid frame.", nameof(frames));
                bytes = checked(bytes + 72L + 24L + frame.Payload.Length);
            }
            if (portable == null) return bytes;

            bytes = checked(bytes + 768L + StringBytes(portable.Title));
            bytes = checked(bytes + 24L + (4L * portable.PaletteRgb.Count));
            bytes = checked(bytes + 24L + (4L * portable.TabStops.Count));
            bytes = checked(bytes + 24L + (8L * portable.Hyperlinks.Count));
            foreach (var hyperlink in portable.Hyperlinks)
            {
                bytes = checked(bytes + 48L + StringBytes(hyperlink.Id) +
                    StringBytes(hyperlink.Uri) + StringBytes(hyperlink.Parameters));
            }
            bytes = checked(bytes + EstimateRows(portable.ScrollbackRows));
            bytes = checked(bytes + EstimateRows(portable.ScreenRows));
            bytes = checked(bytes + EstimateRows(portable.AlternateScreenRows));
            return bytes;
        }

        private static long EstimateRows(IReadOnlyList<PortableTerminalRowV1> rows)
        {
            long bytes = 56L + (8L * rows.Count);
            foreach (var row in rows)
            {
                bytes = checked(bytes + 96L + (8L * row.Cells.Count));
                foreach (var cell in row.Cells)
                {
                    bytes = checked(bytes + 64L + StringBytes(cell.Character));
                }
            }
            return bytes;
        }

        private static long StringBytes(string value) =>
            value == null ? 0L : checked(24L + (2L * value.Length));

        private static bool ShouldReject(Func<bool> rejectPredicate)
        {
            if (rejectPredicate == null) return false;
            try { return rejectPredicate(); }
            catch
            {
                // Shadow admission is diagnostic. A caller-gate failure must never escape into
                // production history or permit work whose lifecycle cannot be proven active.
                return true;
            }
        }

        private bool TryRemoveSession(Guid sessionId, SessionState state) =>
            ((ICollection<KeyValuePair<Guid, SessionState>>)sessions).Remove(
                new KeyValuePair<Guid, SessionState>(sessionId, state));

        private void Notify(Guid sessionId, ProductionTerminalCoreShadowResult result)
        {
            if (observer == null || result == null || Volatile.Read(ref disposed) != 0) return;
            try { observer(sessionId, result); }
            catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            foreach (var pair in sessions)
            {
                lock (pair.Value.Sync)
                {
                    pair.Value.Accepting = false;
                    TryRemoveSession(pair.Key, pair.Value);
                    ScheduleReleaseLocked(pair.Value, pair.Key, null);
                }
            }
        }
    }
}
