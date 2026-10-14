using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;

namespace TermLink.Agent
{
    internal sealed class SessionStopResult
    {
        public int RequestedSessions { get; set; }
        public int CompletedSessions { get; set; }
        public int TimedOutSessions { get; set; }
        public bool PolicyPersistenceFailed { get; set; }
        public bool Success { get { return TimedOutSessions == 0 && !PolicyPersistenceFailed; } }
    }

    internal interface IRegistryManagedSession
    {
        Guid SessionId { get; }
        void Start();
        SessionSummary GetSummary();
        void BeginStop();
        bool WaitForStop();
        void Dispose();
    }

    internal interface IInterruptibleRegistryManagedSession
    {
        void BeginStop(bool commitInterruptedLifecycle);
    }

    internal interface IRecoveryRegistrationSession
    {
        SessionRegistration Registration { get; }
    }

    internal interface IRegistryManagedSessionFactory
    {
        IRegistryManagedSession Create(
            SessionRegistration registration,
            Action<IRegistryManagedSession> completion);
    }

    internal interface IRemoteControllableSession
    {
        bool TrySendRemoteInput(Guid inputId, ulong expectedLocalInputEpoch, byte[] bytes);
        bool TrySendRemoteInputRejection(Guid inputId, LocalRemoteInputResult result);
    }

    internal enum RemoteInputDispatchOutcome
    {
        Unavailable = 0,
        Enqueued = 1,
        Duplicate = 2
    }

    /// <summary>
    /// Optional richer remote-input boundary for make-before-break transports.
    /// A duplicate is not a rejection: the result from the first carrier copy
    /// remains authoritative.
    /// </summary>
    internal interface IIdempotentRemoteControllableSession : IRemoteControllableSession
    {
        RemoteInputDispatchOutcome DispatchRemoteInput(
            Guid inputId,
            ulong expectedLocalInputEpoch,
            byte[] bytes);
    }

    internal interface IRemoteInputReplayDispatchSession : IIdempotentRemoteControllableSession
    {
        RemoteInputDispatchOutcome DispatchRemoteInput(
            Guid inputId,
            ulong expectedLocalInputEpoch,
            byte[] bytes,
            Guid senderDeviceId,
            byte[] envelopeSha256);
        bool TrySendRemoteInputRejection(
            Guid inputId,
            LocalRemoteInputResult result,
            Guid senderDeviceId,
            byte[] envelopeSha256);
    }

    // Kept separate from IRegistryManagedSession so existing registry test
    // doubles do not need to implement the production replay surface.
    internal interface IBufferedRegistryManagedSession
    {
        ManagedShellType ShellType { get; }
        IList<LocalFrame> ReadBufferedEvents(
            ulong resumeFromSequence,
            out ulong earliestRetainedSequence,
            out ulong latestSequence);
        IList<LocalFrame> ReadBufferedEvents(
            ulong resumeFromSequence,
            int maximumEvents,
            long maximumStoredBytes,
            out ulong earliestRetainedSequence,
            out ulong latestSequence,
            out bool hasMore);
        void DeleteBufferedEvents();
    }

    // The production Relay uses this optional surface to recover an input
    // replay fence after the Agent process is rebuilt. It is deliberately
    // separate from the existing registry doubles and control interfaces.
    internal interface IRemoteInputReplaySession
    {
        bool TryGetRemoteInputIdentity(
            Guid inputId,
            out Guid senderDeviceId,
            out byte[] envelopeSha256);
        bool TryFindRemoteInputResult(Guid inputId, out LocalFrame resultFrame);
    }

    internal sealed class SessionRegistry : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<Guid, IRegistryManagedSession> sessions =
            new Dictionary<Guid, IRegistryManagedSession>();
        private readonly Dictionary<Guid, IRegistryManagedSession> completedSessions =
            new Dictionary<Guid, IRegistryManagedSession>();
        // History reads profiles while holding its own lock; never reacquire registry sync there.
        private readonly ConcurrentDictionary<Guid, IRegistryManagedSession> profileSessions = new();
        private readonly HashSet<Guid> releasedCompletedSessions = new HashSet<Guid>();
        private readonly HashSet<IRegistryManagedSession> retiring = new HashSet<IRegistryManagedSession>();
        private readonly HashSet<Guid> retainOnCompletion = new HashSet<Guid>();
        private readonly HashSet<Guid> terminatingSessionIds = new HashSet<Guid>();
        private readonly IRegistryManagedSessionFactory sessionFactory;
        private readonly IRegistryRegistrationStore registrationStore;
        private readonly IRemoteAccessPolicyStore remoteAccessPolicyStore;
        private readonly Dictionary<Guid, StoppedHostIdentity> stoppedHosts =
            new Dictionary<Guid, StoppedHostIdentity>();
        private readonly HashSet<Guid> stoppedSessionIds = new HashSet<Guid>();
        private readonly Dictionary<Guid, SessionRegistration> knownRegistrations =
            new Dictionary<Guid, SessionRegistration>();
        private readonly int maximumSessions;
        private readonly bool retainCompletedSessions;
        private readonly string bufferRoot;
        private readonly long maximumBufferBytes;
        private readonly Action<ManagedShellType, LocalFrame> committedFrame;
        private readonly Action<string> completionDiagnostic;
        private readonly Action<Guid> explicitlyClosed;
        private int recoveredSessionCount;
        private bool remoteAccessEnabled = true;
        private bool preserveRegistrationsOnShutdown;
        private bool recoveryStarted;
        private bool disposed;

        public SessionRegistry(
            string bufferRoot,
            int disconnectedTimeoutMilliseconds,
            long maximumBufferBytes,
            int maximumSessions,
            Action<ManagedShellType, LocalFrame> committedFrame = null,
            Action<Guid> explicitlyClosed = null,
            Func<Guid, CancellationToken, Task<bool>> authorizeClipboard = null,
            Func<string> brokerCaptureRootProvider = null,
            bool deferRecovery = false)
        {
            if (string.IsNullOrWhiteSpace(bufferRoot)) throw new ArgumentException("buffer root is required", "bufferRoot");
            if (disconnectedTimeoutMilliseconds < 250 || disconnectedTimeoutMilliseconds > 300000)
            {
                throw new ArgumentOutOfRangeException("disconnectedTimeoutMilliseconds");
            }
            if (maximumBufferBytes < 1024 || maximumBufferBytes > 1024L * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException("maximumBufferBytes");
            }
            if (maximumSessions < 1 || maximumSessions > 1000) throw new ArgumentOutOfRangeException("maximumSessions");

            var resolvedBufferRoot = Path.GetFullPath(bufferRoot);
            this.maximumSessions = maximumSessions;
            this.bufferRoot = resolvedBufferRoot;
            this.maximumBufferBytes = maximumBufferBytes;
            this.committedFrame = committedFrame;
            this.explicitlyClosed = explicitlyClosed;
            Directory.CreateDirectory(resolvedBufferRoot);
            sessionFactory = new ManagedSessionFactory(
                resolvedBufferRoot,
                disconnectedTimeoutMilliseconds,
                maximumBufferBytes,
                committedFrame, authorizeClipboard, brokerCaptureRootProvider);
            retainCompletedSessions = committedFrame != null;
            registrationStore = new DpapiRegistryRegistrationStore(
                Path.Combine(resolvedBufferRoot, "registrations.dpapi"));
            remoteAccessPolicyStore = new DpapiRemoteAccessPolicyStore(
                Path.Combine(resolvedBufferRoot, "remote-access-policy.dpapi"));
            LoadRemoteAccessPolicy();
            if (!deferRecovery) RecoverRetainedSessions();
        }

        internal SessionRegistry(IRegistryManagedSessionFactory sessionFactory, int maximumSessions)
            : this(
                sessionFactory,
                maximumSessions,
                new NullRegistryRegistrationStore(),
                new NullRemoteAccessPolicyStore())
        {
        }

        internal SessionRegistry(
            IRegistryManagedSessionFactory sessionFactory,
            int maximumSessions,
            IRegistryRegistrationStore registrationStore)
            : this(
                sessionFactory,
                maximumSessions,
                registrationStore,
                new NullRemoteAccessPolicyStore())
        {
        }

        internal SessionRegistry(
            IRegistryManagedSessionFactory sessionFactory,
            int maximumSessions,
            IRegistryRegistrationStore registrationStore,
            IRemoteAccessPolicyStore remoteAccessPolicyStore,
            Action<ManagedShellType, LocalFrame> committedFrame = null,
            Action<string> completionDiagnostic = null)
        {
            if (sessionFactory == null) throw new ArgumentNullException("sessionFactory");
            if (registrationStore == null) throw new ArgumentNullException("registrationStore");
            if (remoteAccessPolicyStore == null) throw new ArgumentNullException("remoteAccessPolicyStore");
            if (maximumSessions < 1 || maximumSessions > 1000) throw new ArgumentOutOfRangeException("maximumSessions");
            this.sessionFactory = sessionFactory;
            this.registrationStore = registrationStore;
            this.remoteAccessPolicyStore = remoteAccessPolicyStore;
            this.maximumSessions = maximumSessions;
            this.committedFrame = committedFrame;
            this.completionDiagnostic = completionDiagnostic;
            retainCompletedSessions = committedFrame != null;
            LoadRemoteAccessPolicy();
            RecoverRetainedSessions();
        }

        public bool RemoteAccessEnabled
        {
            get { lock (sync) return remoteAccessEnabled && !disposed; }
        }

        public int Count
        {
            get { lock (sync) return sessions.Count; }
        }

        public int RecoveredSessionCount
        {
            get { lock (sync) return recoveredSessionCount; }
        }

        internal bool ContainsSession(Guid sessionId)
        {
            lock (sync) return sessions.ContainsKey(sessionId) || completedSessions.ContainsKey(sessionId);
        }

        // Cleanup must recognize the same durable registrations as the catalog,
        // even after their managed session and account history have gone away.
        // ContainsSession remains the operational wrapper check for routing/recovery.
        internal bool ContainsSessionRecord(Guid sessionId)
        {
            lock (sync) return knownRegistrations.ContainsKey(sessionId) || sessions.ContainsKey(sessionId) ||
                completedSessions.ContainsKey(sessionId) || retiring.Any(item => item.SessionId == sessionId);
        }

        internal bool ContainsActiveSession(Guid sessionId)
        {
            lock (sync) return sessions.ContainsKey(sessionId);
        }

        private void LoadRemoteAccessPolicy()
        {
            var snapshot = remoteAccessPolicyStore.Load();
            if (snapshot == null) throw new InvalidDataException("Remote-access policy store returned no state.");
            remoteAccessEnabled = snapshot.RemoteAccessEnabled && snapshot.Valid;
            stoppedHosts.Clear();
            stoppedSessionIds.Clear();
            foreach (var stopped in snapshot.StoppedHosts)
            {
                if (stopped == null || stoppedHosts.ContainsKey(stopped.SessionId))
                    throw new InvalidDataException("Remote-access policy contains duplicate stopped Hosts.");
                stoppedHosts.Add(stopped.SessionId, stopped);
                stoppedSessionIds.Add(stopped.SessionId);
            }
        }

        private RemoteAccessPolicySnapshot PolicySnapshot(
            bool enabled,
            IDictionary<Guid, StoppedHostIdentity> stopped)
        {
            return new RemoteAccessPolicySnapshot(
                enabled,
                new List<StoppedHostIdentity>(stopped.Values));
        }

        private bool IsSessionStoppedByPolicyLocked(Guid sessionId)
        {
            if (!stoppedSessionIds.Contains(sessionId)) return false;
            StoppedHostIdentity stopped;
            if (!stoppedHosts.TryGetValue(sessionId, out stopped)) return true;
            if (stopped.MatchesRunningHost()) return true;

            var retained = new Dictionary<Guid, StoppedHostIdentity>(stoppedHosts);
            retained.Remove(sessionId);
            try
            {
                remoteAccessPolicyStore.Save(PolicySnapshot(remoteAccessEnabled, retained));
            }
            catch (Exception error)
            {
                if (!IsPolicyStorageError(error)) throw;
                // Keeping a stale tombstone over-blocks one Session but is the
                // fail-closed result when its durable removal cannot be saved.
                return true;
            }
            stoppedHosts.Remove(sessionId);
            stoppedSessionIds.Remove(sessionId);
            return false;
        }

        private bool AddStoppedHostPolicyLocked(IRegistryManagedSession managed)
        {
            stoppedSessionIds.Add(managed.SessionId);
            var recoverable = managed as IRecoveryRegistrationSession;
            if (recoverable == null) return true;
            var next = new Dictionary<Guid, StoppedHostIdentity>();
            foreach (var item in stoppedHosts)
            {
                if (item.Value.MatchesRunningHost()) next[item.Key] = item.Value;
            }
            var stopped = StoppedHostIdentity.FromRegistration(recoverable.Registration);
            next[managed.SessionId] = stopped;
            try
            {
                remoteAccessPolicyStore.Save(PolicySnapshot(remoteAccessEnabled, next));
            }
            catch (Exception error)
            {
                if (!IsPolicyStorageError(error)) throw;
                stoppedHosts[managed.SessionId] = stopped;
                return false;
            }
            stoppedHosts.Clear();
            stoppedSessionIds.Clear();
            foreach (var item in next)
            {
                stoppedHosts[item.Key] = item.Value;
                stoppedSessionIds.Add(item.Key);
            }
            return true;
        }

        private bool SaveRemoteAccessEnabledLocked(bool enabled)
        {
            try
            {
                remoteAccessPolicyStore.Save(PolicySnapshot(enabled, stoppedHosts));
                return true;
            }
            catch (Exception error)
            {
                if (!IsPolicyStorageError(error)) throw;
                return false;
            }
        }

        private static bool IsPolicyStorageError(Exception error)
        {
            return error is IOException ||
                error is UnauthorizedAccessException ||
                error is InvalidDataException ||
                error is CryptographicException;
        }

        internal void RecoverRetainedSessions()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException(nameof(SessionRegistry));
                if (recoveryStarted)
                {
                    // A later login can supply the runtime that was unavailable at startup.
                    foreach (var recovered in completedSessions.Values.OfType<RecoveredBufferedSession>().ToArray())
                    {
                        if (recovered.RecoveryError.Length == 0 || deletedSessions.Contains(recovered.SessionId)) continue;
                        try
                        {
                            var terminalFrame = recovered.EnsureTerminalFrame(frame => committedFrame(recovered.ShellType, frame));
                            if (terminalFrame != null) committedFrame(recovered.ShellType, terminalFrame);
                        }
                        catch when (recovered.RecoveryError.Length != 0) { }
                    }
                    return;
                }
                recoveryStarted = true;
                RecoverRegistrations();
            }
        }

        private void RecoverRegistrations()
        {
            foreach (var registration in registrationStore.Load())
            {
                if (knownRegistrations.ContainsKey(registration.SessionId))
                    throw new InvalidDataException("Registry registration store returned duplicate Session IDs.");
                knownRegistrations.Add(registration.SessionId, registration);
                if (registration.Retired) continue;
                if (deletedSessions.Contains(registration.SessionId))
                {
                    RetireRegistration(registration.SessionId);
                    continue;
                }
                var blockedByStoppedHost = false;
                lock (sync)
                {
                    if (remoteAccessEnabled)
                        blockedByStoppedHost = IsSessionStoppedByPolicyLocked(registration.SessionId);
                }
                if (!remoteAccessEnabled || blockedByStoppedHost)
                {
                    var recoveryOnly = registration.AsRecoveryOnly();
                    registrationStore.Upsert(recoveryOnly);
                    knownRegistrations[recoveryOnly.SessionId] = recoveryOnly;
                    if (RecoverCompletedSession(recoveryOnly)) recoveredSessionCount++;
                    else if (!recoveryOnly.MatchesRunningHost()) RetireRegistration(recoveryOnly.SessionId);
                    continue;
                }
                if (!registration.MatchesRunningHost())
                {
                    if (RecoverCompletedSession(registration))
                    {
                        recoveredSessionCount++;
                        continue;
                    }
                    RetireRegistration(registration.SessionId);
                    continue;
                }
                string error;
                if (!TryRegister(registration.AsActive(), out error))
                {
                    throw new InvalidOperationException(
                        "Unable to recover Registry session " + registration.SessionId.ToString("D") + ": " + error);
                }
                recoveredSessionCount++;
            }
        }

        private bool RecoverCompletedSession(SessionRegistration registration)
        {
            if (!retainCompletedSessions || committedFrame == null) return false;
            var recovered = new RecoveredBufferedSession(
                registration,
                Path.Combine(bufferRoot, registration.SessionId.ToString("N")),
                maximumBufferBytes);
            try
            {
                completedSessions.Add(registration.SessionId, recovered);
                profileSessions[registration.SessionId] = recovered;
                var terminalFrame = recovered.EnsureTerminalFrame(frame => committedFrame(registration.ShellType, frame));
                if (terminalFrame != null) committedFrame(registration.ShellType, terminalFrame);
                return true;
            }
            catch when (recovered.RecoveryError.Length != 0)
            {
                // Keep one unavailable history isolated without stopping unrelated sessions.
                return true;
            }
            catch
            {
                completedSessions.Remove(registration.SessionId);
                profileSessions.TryRemove(registration.SessionId, out _);
                recovered.Dispose();
                throw;
            }
        }

        public bool TryRegister(SessionRegistration registration, out string error)
        {
            if (registration == null) throw new ArgumentNullException("registration");
            IRegistryManagedSession managed;
            lock (sync)
            {
                if (disposed)
                {
                    error = "REGISTRY_CLOSED";
                    return false;
                }
                if (deletedSessions.Contains(registration.SessionId))
                {
                    error = "SESSION_RETIRED";
                    return false;
                }
                if (!remoteAccessEnabled)
                {
                    error = "REMOTE_ACCESS_DISABLED";
                    return false;
                }
                if (IsSessionStoppedByPolicyLocked(registration.SessionId))
                {
                    error = "SESSION_STOPPED";
                    return false;
                }
                SessionRegistration known;
                if (knownRegistrations.TryGetValue(registration.SessionId, out known))
                {
                    if (known.Retired)
                    {
                        error = "SESSION_RETIRED";
                        return false;
                    }
                    if (!known.HasSameHostIdentity(registration))
                    {
                        error = "SESSION_ID_REUSED";
                        return false;
                    }
                }
                if (sessions.ContainsKey(registration.SessionId) || completedSessions.ContainsKey(registration.SessionId))
                {
                    error = "SESSION_ALREADY_REGISTERED";
                    return false;
                }
                foreach (var retiringSession in retiring)
                {
                    if (retiringSession.SessionId == registration.SessionId)
                    {
                        error = "SESSION_STOPPING";
                        return false;
                    }
                }
                // The limit bounds workers that can still consume live Host/IPC
                // resources. Retained completed sessions are read-only history;
                // counting that backlog here eventually prevents every new
                // terminal from registering until its history is uploaded.
                // Retiring workers remain live until their stop completes and
                // therefore continue to consume a slot.
                if (sessions.Count + retiring.Count >= maximumSessions)
                {
                    error = "SESSION_LIMIT_REACHED";
                    return false;
                }

                var activeRegistration = registration.AsActive();
                managed = sessionFactory.Create(activeRegistration, OnSessionCompleted);
                registrationStore.Upsert(activeRegistration);
                knownRegistrations[activeRegistration.SessionId] = activeRegistration;
                sessions.Add(activeRegistration.SessionId, managed);
                profileSessions[activeRegistration.SessionId] = managed;
            }

            try
            {
                managed.Start();
                error = null;
                return true;
            }
            catch
            {
                lock (sync)
                {
                    IRegistryManagedSession current;
                    if (sessions.TryGetValue(registration.SessionId, out current) &&
                        ReferenceEquals(current, managed))
                    {
                        sessions.Remove(registration.SessionId);
                        ForgetReleasedProfileLocked(managed);
                    }
                    var recoveryOnly = registration.AsRecoveryOnly();
                    registrationStore.Upsert(recoveryOnly);
                    knownRegistrations[recoveryOnly.SessionId] = recoveryOnly;
                }
                managed.Dispose();
                throw;
            }
        }

        public IList<SessionSummary> GetSummaries()
        {
            var result = new List<SessionSummary>();
            lock (sync)
            {
                foreach (var session in sessions.Values) result.Add(session.GetSummary());
            }
            result.Sort(delegate(SessionSummary left, SessionSummary right)
            {
                return left.RegisteredAtUtc.CompareTo(right.RegisteredAtUtc);
            });
            return result;
        }

        internal IList<SessionSummary> GetCatalogSummaries()
        {
            lock (sync)
            {
                return knownRegistrations.Values.Select(registration =>
                {
                    IRegistryManagedSession active;
                    if (!registration.Retired && registration.MatchesRunningHost() &&
                        sessions.TryGetValue(registration.SessionId, out active))
                        return active.GetSummary();
                    return new SessionSummary
                    {
                        SessionId = registration.SessionId, ShellType = registration.ShellType,
                        RegisteredAtUtc = registration.RegisteredAtUtc, Lifecycle = SessionAgentLifecycle.HostLost
                    };
                }).ToList();
            }
        }

        public Task<LocalResizeResult> RequestResizeAsync(Guid sessionId, LocalResizeRequest request)
        {
            ManagedSession managed;
            lock (sync)
            {
                IRegistryManagedSession session;
                managed = remoteAccessEnabled && sessions.TryGetValue(sessionId, out session)
                    ? session as ManagedSession : null;
            }
            return managed != null ? managed.RequestResizeAsync(request) : Task.FromResult(
                new LocalResizeResult { Id = request.Id, Error = LocalResizeError.SESSION_UNAVAILABLE });
        }

        public bool TrySendRemoteInput(Guid sessionId, Guid inputId, ulong expectedLocalInputEpoch, byte[] bytes)
        {
            return DispatchRemoteInput(sessionId, inputId, expectedLocalInputEpoch, bytes) ==
                RemoteInputDispatchOutcome.Enqueued;
        }

        public RemoteInputDispatchOutcome DispatchRemoteInput(
            Guid sessionId,
            Guid inputId,
            ulong expectedLocalInputEpoch,
            byte[] bytes)
        {
            return DispatchRemoteInput(
                sessionId,
                inputId,
                expectedLocalInputEpoch,
                bytes,
                Guid.Empty,
                null);
        }

        public RemoteInputDispatchOutcome DispatchRemoteInput(
            Guid sessionId,
            Guid inputId,
            ulong expectedLocalInputEpoch,
            byte[] bytes,
            Guid senderDeviceId,
            byte[] envelopeSha256)
        {
            IRemoteControllableSession controllable;
            lock (sync)
            {
                IRegistryManagedSession managed;
                if (!remoteAccessEnabled || !sessions.TryGetValue(sessionId, out managed))
                    return RemoteInputDispatchOutcome.Unavailable;
                controllable = managed as IRemoteControllableSession;
            }
            var replayAware = controllable as IRemoteInputReplayDispatchSession;
            if (replayAware != null)
            {
                return replayAware.DispatchRemoteInput(
                    inputId,
                    expectedLocalInputEpoch,
                    bytes,
                    senderDeviceId,
                    envelopeSha256);
            }
            var idempotent = controllable as IIdempotentRemoteControllableSession;
            if (idempotent != null)
                return idempotent.DispatchRemoteInput(inputId, expectedLocalInputEpoch, bytes);
            return controllable != null &&
                controllable.TrySendRemoteInput(inputId, expectedLocalInputEpoch, bytes)
                    ? RemoteInputDispatchOutcome.Enqueued
                    : RemoteInputDispatchOutcome.Unavailable;
        }

        internal bool TrySendRemoteInputRejection(
            Guid sessionId,
            Guid inputId,
            LocalRemoteInputResult result,
            Guid senderDeviceId,
            byte[] envelopeSha256)
        {
            IRemoteControllableSession controllable;
            lock (sync)
            {
                IRegistryManagedSession managed;
                if (!remoteAccessEnabled || !sessions.TryGetValue(sessionId, out managed)) return false;
                controllable = managed as IRemoteControllableSession;
            }
            var replayAware = controllable as IRemoteInputReplayDispatchSession;
            return replayAware != null && replayAware.TrySendRemoteInputRejection(
                inputId,
                result,
                senderDeviceId,
                envelopeSha256);
        }

        public bool TrySendRemoteInputRejection(Guid sessionId, Guid inputId, LocalRemoteInputResult result)
        {
            IRemoteControllableSession controllable;
            lock (sync)
            {
                IRegistryManagedSession managed;
                if (!remoteAccessEnabled || !sessions.TryGetValue(sessionId, out managed)) return false;
                controllable = managed as IRemoteControllableSession;
            }
            return controllable != null && controllable.TrySendRemoteInputRejection(inputId, result);
        }

        internal bool TryReadRemoteInputIdentity(
            Guid sessionId,
            Guid inputId,
            out Guid senderDeviceId,
            out byte[] envelopeSha256)
        {
            senderDeviceId = Guid.Empty;
            envelopeSha256 = null;
            IRemoteInputReplaySession replay;
            lock (sync)
            {
                IRegistryManagedSession managed;
                if (!sessions.TryGetValue(sessionId, out managed) &&
                    !completedSessions.TryGetValue(sessionId, out managed))
                {
                    managed = null;
                    foreach (var candidate in retiring)
                    {
                        if (candidate.SessionId == sessionId)
                        {
                            managed = candidate;
                            break;
                        }
                    }
                }
                replay = managed as IRemoteInputReplaySession;
            }
            return replay != null && replay.TryGetRemoteInputIdentity(
                inputId,
                out senderDeviceId,
                out envelopeSha256);
        }

        internal bool TryFindRemoteInputIdentity(
            Guid inputId,
            out Guid sessionId,
            out Guid senderDeviceId,
            out byte[] envelopeSha256)
        {
            sessionId = Guid.Empty;
            senderDeviceId = Guid.Empty;
            envelopeSha256 = null;
            foreach (var candidate in SnapshotAllSessionIds())
            {
                if (!TryReadRemoteInputIdentity(
                    candidate,
                    inputId,
                    out senderDeviceId,
                    out envelopeSha256))
                {
                    continue;
                }
                sessionId = candidate;
                return true;
            }
            return false;
        }

        internal bool TryReadRemoteInputResult(
            Guid sessionId,
            Guid inputId,
            out LocalFrame resultFrame)
        {
            resultFrame = null;
            IRemoteInputReplaySession replay;
            lock (sync)
            {
                IRegistryManagedSession managed;
                if (!sessions.TryGetValue(sessionId, out managed) &&
                    !completedSessions.TryGetValue(sessionId, out managed))
                {
                    managed = null;
                    foreach (var candidate in retiring)
                    {
                        if (candidate.SessionId == sessionId)
                        {
                            managed = candidate;
                            break;
                        }
                    }
                }
                replay = managed as IRemoteInputReplaySession;
            }
            return replay != null && replay.TryFindRemoteInputResult(inputId, out resultFrame);
        }

        internal bool TryFindRemoteInputResult(
            Guid inputId,
            out Guid sessionId,
            out LocalFrame resultFrame)
        {
            sessionId = Guid.Empty;
            resultFrame = null;
            foreach (var candidate in SnapshotAllSessionIds())
            {
                if (!TryReadRemoteInputResult(candidate, inputId, out resultFrame)) continue;
                sessionId = candidate;
                return true;
            }
            return false;
        }

        private IList<Guid> SnapshotAllSessionIds()
        {
            lock (sync)
            {
                var result = new HashSet<Guid>(sessions.Keys);
                foreach (var sessionId in completedSessions.Keys) result.Add(sessionId);
                foreach (var session in retiring) result.Add(session.SessionId);
                return result.ToList();
            }
        }

        internal string TerminalCompatibilityError(Guid sessionId)
        {
            lock (sync)
            {
                IRegistryManagedSession session;
                if (!sessions.TryGetValue(sessionId, out session) && !completedSessions.TryGetValue(sessionId, out session))
                    session = retiring.FirstOrDefault(candidate => candidate.SessionId == sessionId);
                var managed = session as ManagedSession;
                if (managed != null) return managed.TerminalCompatibilityError;
                var recovered = session as RecoveredBufferedSession;
                return recovered == null ? string.Empty : recovered.TerminalCompatibilityError;
            }
        }

        internal TerminalSemanticProfile GetSessionProfile(Guid sessionId)
        {
            if (!profileSessions.TryGetValue(sessionId, out var session)) return null;
            if (session is ManagedSession managed) return managed.BoundProfile;
            if (session is RecoveredBufferedSession recovered) return recovered.BoundProfile;
            return null;
        }

        private void ForgetReleasedProfileLocked(IRegistryManagedSession session)
        {
            if (!sessions.ContainsKey(session.SessionId) && !completedSessions.ContainsKey(session.SessionId) &&
                !retiring.Any(candidate => candidate.SessionId == session.SessionId))
                profileSessions.TryRemove(session.SessionId, out _);
        }

        internal bool TryReadBufferedEvents(
            Guid sessionId,
            ulong resumeFromSequence,
            out IList<LocalFrame> frames,
            out ulong earliestRetainedSequence,
            out ulong latestSequence)
        {
            frames = null;
            earliestRetainedSequence = 0;
            latestSequence = 0;
            IBufferedRegistryManagedSession buffered;
            lock (sync)
            {
                IRegistryManagedSession managed;
                if (!sessions.TryGetValue(sessionId, out managed) &&
                    !completedSessions.TryGetValue(sessionId, out managed))
                {
                    managed = null;
                    foreach (var candidate in retiring)
                    {
                        if (candidate.SessionId == sessionId)
                        {
                            managed = candidate;
                            break;
                        }
                    }
                    if (managed == null) return false;
                }
                buffered = managed as IBufferedRegistryManagedSession;
            }
            if (buffered == null) return false;
            frames = buffered.ReadBufferedEvents(
                resumeFromSequence,
                out earliestRetainedSequence,
                out latestSequence);
            return true;
        }

        internal bool TryReadBufferedEvents(
            Guid sessionId,
            ulong resumeFromSequence,
            int maximumEvents,
            long maximumStoredBytes,
            out IList<LocalFrame> frames,
            out ulong earliestRetainedSequence,
            out ulong latestSequence,
            out bool hasMore)
        {
            frames = null;
            earliestRetainedSequence = 0;
            latestSequence = 0;
            hasMore = false;
            IBufferedRegistryManagedSession buffered;
            lock (sync)
            {
                IRegistryManagedSession managed;
                if (!sessions.TryGetValue(sessionId, out managed) &&
                    !completedSessions.TryGetValue(sessionId, out managed))
                {
                    managed = null;
                    foreach (var candidate in retiring)
                    {
                        if (candidate.SessionId == sessionId)
                        {
                            managed = candidate;
                            break;
                        }
                    }
                    if (managed == null) return false;
                }
                buffered = managed as IBufferedRegistryManagedSession;
            }
            if (buffered == null) return false;
            frames = buffered.ReadBufferedEvents(
                resumeFromSequence,
                maximumEvents,
                maximumStoredBytes,
                out earliestRetainedSequence,
                out latestSequence,
                out hasMore);
            return true;
        }

        internal void ReleaseBufferedSession(Guid sessionId)
        {
            IRegistryManagedSession completed = null;
            lock (sync)
            {
                if (!completedSessions.TryGetValue(sessionId, out completed))
                {
                    if (sessions.ContainsKey(sessionId)) releasedCompletedSessions.Add(sessionId);
                    else
                    {
                        foreach (var candidate in retiring)
                        {
                            if (candidate.SessionId == sessionId)
                            {
                                releasedCompletedSessions.Add(sessionId);
                                break;
                            }
                        }
                    }
                    return;
                }
                FinalizeRegistration(completed);
                completedSessions.Remove(sessionId);
                ForgetReleasedProfileLocked(completed);
            }
            DeleteBufferedEvents(completed);
            completed.Dispose();
        }

        public SessionStopResult StopSession(Guid sessionId)
        {
            IRegistryManagedSession managed;
            var policyDurable = true;
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out managed)) return new SessionStopResult();
                policyDurable = AddStoppedHostPolicyLocked(managed);
                if (retainCompletedSessions)
                {
                    retainOnCompletion.Add(sessionId);
                }
                else MarkRecoveryOnly(managed);
                sessions.Remove(sessionId);
                retiring.Add(managed);
            }
            BeginStop(managed, true);
            var completed = managed.WaitForStop();
            if (explicitlyClosed != null) explicitlyClosed(sessionId);
            if (completed)
            {
                lock (sync)
                {
                    retiring.Remove(managed);
                    ForgetReleasedProfileLocked(managed);
                }
            }
            return new SessionStopResult
            {
                RequestedSessions = 1,
                CompletedSessions = completed ? 1 : 0,
                TimedOutSessions = completed ? 0 : 1,
                PolicyPersistenceFailed = !policyDurable
            };
        }

        public bool TerminateSession(Guid sessionId)
        {
            return TerminateSession(sessionId, false);
        }

        internal bool TerminateSession(Guid sessionId, bool explicitUserClose)
        {
            if (sessionId == Guid.Empty) return false;
            IRegistryManagedSession managed;
            SessionRegistration registration;
            lock (sync)
            {
                if (terminatingSessionIds.Contains(sessionId)) return true;
                if (!sessions.TryGetValue(sessionId, out managed)) return false;
                var recoverable = managed as IRecoveryRegistrationSession;
                if (recoverable == null) return false;
                registration = recoverable.Registration;
            }

            var terminated = false;
            try
            {
                using (var process = Process.GetProcessById(registration.ExpectedHostProcessId))
                {
                    // PID alone is unsafe because Windows can reuse it. Verify
                    // the durable registration's process start time again while
                    // holding a Process handle, then recheck Registry ownership
                    // immediately before terminating the tree.
                    if (process.StartTime.ToUniversalTime().Ticks !=
                        registration.ExpectedHostStartTimeUtc.Ticks)
                    {
                        return false;
                    }
                    lock (sync)
                    {
                        if (terminatingSessionIds.Contains(sessionId)) return true;
                        IRegistryManagedSession current;
                        if (!sessions.TryGetValue(sessionId, out current) ||
                            !ReferenceEquals(current, managed))
                        {
                            return false;
                        }
                        var currentRegistration = current as IRecoveryRegistrationSession;
                        if (currentRegistration == null ||
                            !registration.HasSameHostIdentity(currentRegistration.Registration))
                        {
                            return false;
                        }
                        terminatingSessionIds.Add(sessionId);
                    }
#if TERMLINK_PRODUCTION_RUNTIME
                    process.Kill(entireProcessTree: true);
#else
                    // The isolated .NET Framework validation build lacks the
                    // Process.Kill(bool) API. Production always compiles the
                    // entire-tree overload above.
                    process.Kill();
#endif
                    terminated = process.WaitForExit(5000);
                    if (terminated && explicitUserClose && explicitlyClosed != null)
                        explicitlyClosed(sessionId);
                    return terminated;
                }
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            finally
            {
                if (!terminated)
                {
                    lock (sync) terminatingSessionIds.Remove(sessionId);
                }
            }
        }

        public SessionStopResult StopAllRemoteAccess()
        {
            List<IRegistryManagedSession> stopped;
            var policyDurable = true;
            lock (sync)
            {
                remoteAccessEnabled = false;
                policyDurable = SaveRemoteAccessEnabledLocked(false);
                stopped = new List<IRegistryManagedSession>(sessions.Values);
                if (retainCompletedSessions)
                {
                    foreach (var session in stopped)
                    {
                        retainOnCompletion.Add(session.SessionId);
                    }
                }
                else
                {
                    registrationStore.Clear();
                    foreach (var completed in completedSessions.Values)
                    {
                        if (!stopped.Contains(completed)) stopped.Add(completed);
                    }
                    completedSessions.Clear();
                    releasedCompletedSessions.Clear();
                }
                sessions.Clear();
                foreach (var session in stopped) retiring.Add(session);
            }
            var result = StopSessions(stopped, true);
            result.PolicyPersistenceFailed = !policyDurable;
            return result;
        }

        public bool EnableRemoteAccess()
        {
            lock (sync)
            {
                if (disposed) return false;
                if (remoteAccessEnabled) return true;
                if (!SaveRemoteAccessEnabledLocked(true)) return false;
                remoteAccessEnabled = true;
                return true;
            }
        }

        private void OnSessionCompleted(IRegistryManagedSession completed)
        {
            var deleteReleasedBuffer = false;
            Exception persistenceError = null;
            lock (sync)
            {
                IRegistryManagedSession current;
                var wasActive = sessions.TryGetValue(completed.SessionId, out current) &&
                    ReferenceEquals(current, completed);
                var wasRetainedStop = retainOnCompletion.Remove(completed.SessionId);
                try
                {
                    if (wasActive || wasRetainedStop)
                    {
                        var alreadyReleased = releasedCompletedSessions.Remove(completed.SessionId);
                        deleteReleasedBuffer = alreadyReleased;
                        if (retainCompletedSessions && !alreadyReleased && !preserveRegistrationsOnShutdown)
                        {
                            try { MarkRecoveryOnly(completed); }
                            catch (Exception error) when (IsCompletionPersistenceError(error))
                            { persistenceError = error; }
                            completedSessions[completed.SessionId] = completed;
                        }
                        else if (!preserveRegistrationsOnShutdown)
                        {
                            try { FinalizeRegistration(completed); }
                            catch (Exception error) when (IsCompletionPersistenceError(error))
                            {
                                deleteReleasedBuffer = false;
                                if (retainCompletedSessions && alreadyReleased)
                                    completedSessions[completed.SessionId] = completed;
                                persistenceError = error;
                            }
                        }
                    }
                }
                finally
                {
                    if (wasActive) sessions.Remove(completed.SessionId);
                    retiring.Remove(completed);
                    ForgetReleasedProfileLocked(completed);
                }
            }
            if (persistenceError != null) ObserveCompletionPersistenceFailure(completed.SessionId, persistenceError);
            if (deleteReleasedBuffer) DeleteBufferedEvents(completed);
        }

        private static bool IsCompletionPersistenceError(Exception error) =>
            error is IOException or UnauthorizedAccessException or CryptographicException;

        private void ObserveCompletionPersistenceFailure(Guid sessionId, Exception error)
        {
            var line = DateTime.UtcNow.ToString("O") + " session-completion-persistence session=" +
                sessionId.ToString("N") + " failure=" + error.GetType().Name + Environment.NewLine;
            try
            {
                if (completionDiagnostic != null) { completionDiagnostic(line); return; }
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TermLink", "logs");
                Directory.CreateDirectory(root);
                File.AppendAllText(Path.Combine(root, "TermLink-control-runtime.log"), line, new UTF8Encoding(false));
            }
            catch
            {
                try { Console.Error.Write(line); }
                catch { }
            }
        }

        private static void DeleteBufferedEvents(IRegistryManagedSession session)
        {
            var buffered = session as IBufferedRegistryManagedSession;
            if (buffered != null) buffered.DeleteBufferedEvents();
        }

        public SessionStopResult Shutdown()
        {
            return Shutdown(false);
        }

        public SessionStopResult ShutdownForRestart()
        {
            return Shutdown(true);
        }

        private SessionStopResult Shutdown(bool preserveRegistrations)
        {
            List<IRegistryManagedSession> stopped;
            lock (sync)
            {
                if (preserveRegistrations) preserveRegistrationsOnShutdown = true;
                disposed = true;
                remoteAccessEnabled = false;
                stopped = new List<IRegistryManagedSession>(retiring);
                foreach (var session in sessions.Values)
                {
                    if (!stopped.Contains(session)) stopped.Add(session);
                    retiring.Add(session);
                }
                if (preserveRegistrationsOnShutdown || !retainCompletedSessions)
                {
                    foreach (var completed in completedSessions.Values)
                    {
                        if (!stopped.Contains(completed)) stopped.Add(completed);
                        retiring.Add(completed);
                    }
                    completedSessions.Clear();
                    releasedCompletedSessions.Clear();
                }
                if (!preserveRegistrationsOnShutdown && retainCompletedSessions)
                {
                    foreach (var session in stopped)
                    {
                        MarkRecoveryOnly(session);
                        retainOnCompletion.Add(session.SessionId);
                    }
                }
                else
                {
                    retainOnCompletion.Clear();
                    if (!preserveRegistrationsOnShutdown) registrationStore.Clear();
                }
                sessions.Clear();
            }
            return StopSessions(stopped, !preserveRegistrationsOnShutdown);
        }

        private static void BeginStop(IRegistryManagedSession session, bool commitInterruptedLifecycle)
        {
            var interruptible = session as IInterruptibleRegistryManagedSession;
            if (interruptible != null) interruptible.BeginStop(commitInterruptedLifecycle);
            else session.BeginStop();
        }

        private void MarkRecoveryOnly(IRegistryManagedSession session)
        {
            var recoverable = session as IRecoveryRegistrationSession;
            if (recoverable == null) return;
            var recoveryOnly = recoverable.Registration.AsRecoveryOnly();
            registrationStore.Upsert(recoveryOnly);
            knownRegistrations[recoveryOnly.SessionId] = recoveryOnly;
        }

        private void RetireRegistration(Guid sessionId)
        {
            SessionRegistration registration;
            if (!knownRegistrations.TryGetValue(sessionId, out registration)) return;
            registrationStore.Retire(sessionId);
            knownRegistrations[sessionId] = registration.AsRetired();
        }

        private readonly HashSet<Guid> deletedSessions = new();

        internal void RestoreDeletedSessions(IEnumerable<Guid> ids)
        {
            lock (sync) deletedSessions.UnionWith(ids);
        }

        internal bool HasSessionBuffer(Guid sessionId) => SessionBufferPath(sessionId) is string path && Directory.Exists(path);

        private string SessionBufferPath(Guid sessionId)
        {
            if (sessionId == Guid.Empty || string.IsNullOrEmpty(bufferRoot)) return null;
            var path = Path.GetFullPath(Path.Combine(bufferRoot, sessionId.ToString("N")));
            if (!string.Equals(Path.GetDirectoryName(path), bufferRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Session buffer escaped its root.");
            return path;
        }

        internal void DeleteRetiredSessionBuffer(Guid sessionId)
        {
            lock (sync)
            {
                if (!deletedSessions.Contains(sessionId) || sessions.ContainsKey(sessionId) ||
                    completedSessions.ContainsKey(sessionId) || retiring.Any(item => item.SessionId == sessionId)) return;
            }
            // The durable tombstone fences new registrations. Legacy retired
            // records can have buffers without a live/completed wrapper to release.
            var path = SessionBufferPath(sessionId);
            if (path == null || !Directory.Exists(path)) return;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Session buffer is a reparse point.");
            foreach (var file in Directory.EnumerateFiles(path)) File.Delete(file);
            Directory.Delete(path, recursive: false);
        }

        // Shares the registration lock: a delayed reconnect cannot revive an ID while it is deleted.
        internal bool DeleteInactiveSession(Guid sessionId, Action persistDeletion, Func<bool, bool> retentionAllowsDeletion = null)
        {
            lock (sync)
            {
                bool processAlive;
                if (knownRegistrations.TryGetValue(sessionId, out var registration))
                {
                    try
                    {
                        using (var host = Process.GetProcessById(registration.ExpectedHostProcessId))
                            processAlive = !host.HasExited && host.StartTime.ToUniversalTime().Ticks == registration.ExpectedHostStartTimeUtc.Ticks;
                    }
                    catch (ArgumentException) { processAlive = false; } // PID no longer exists.
                    catch (Exception error) when (error is System.ComponentModel.Win32Exception || error is InvalidOperationException)
                    {
                        return false; // Unreadable process state is not proof of an inactive host.
                    }
                }
                else processAlive = sessions.TryGetValue(sessionId, out var session) &&
                    session.GetSummary().Lifecycle == SessionAgentLifecycle.Connected;
                // Recovered/live hosts reset their retention clock under the same
                // lock that excludes registration and durable deletion.
                var allowed = retentionAllowsDeletion?.Invoke(processAlive) ?? true;
                if (processAlive || !allowed) return false;
                persistDeletion();
                deletedSessions.Add(sessionId);
                RetireRegistration(sessionId);
                return true;
            }
        }

        private void FinalizeRegistration(IRegistryManagedSession session)
        {
            var recoverable = session as IRecoveryRegistrationSession;
            if (recoverable == null) return;
            var summary = session.GetSummary();
            if (summary.Lifecycle == SessionAgentLifecycle.Ended ||
                !recoverable.Registration.MatchesRunningHost())
            {
                RetireRegistration(session.SessionId);
                return;
            }
            MarkRecoveryOnly(session);
        }

        private SessionStopResult StopSessions(IList<IRegistryManagedSession> stopped, bool commitInterruptedLifecycle)
        {
            foreach (var session in stopped) BeginStop(session, commitInterruptedLifecycle);
            var completed = 0;
            var timedOut = 0;
            foreach (var session in stopped)
            {
                if (session.WaitForStop())
                {
                    completed++;
                    lock (sync)
                    {
                        retiring.Remove(session);
                        ForgetReleasedProfileLocked(session);
                    }
                }
                else
                {
                    timedOut++;
                }
            }
            return new SessionStopResult
            {
                RequestedSessions = stopped.Count,
                CompletedSessions = completed,
                TimedOutSessions = timedOut
            };
        }

        public void Dispose()
        {
            Shutdown();
        }
    }

    internal sealed class ManagedSessionFactory : IRegistryManagedSessionFactory
    {
        private readonly string bufferRoot;
        private readonly int disconnectedTimeoutMilliseconds;
        private readonly long maximumBufferBytes;
        private readonly Action<ManagedShellType, LocalFrame> committedFrame;
        private readonly Func<Guid, CancellationToken, Task<bool>> authorizeClipboard;
        private readonly Func<string> brokerCaptureRootProvider;

        public ManagedSessionFactory(string bufferRoot, int disconnectedTimeoutMilliseconds, long maximumBufferBytes, Action<ManagedShellType, LocalFrame> committedFrame = null,
            Func<Guid, CancellationToken, Task<bool>> authorizeClipboard = null,
            Func<string> brokerCaptureRootProvider = null)
        {
            this.bufferRoot = bufferRoot;
            this.disconnectedTimeoutMilliseconds = disconnectedTimeoutMilliseconds;
            this.maximumBufferBytes = maximumBufferBytes;
            this.committedFrame = committedFrame;
            this.authorizeClipboard = authorizeClipboard;
            this.brokerCaptureRootProvider = brokerCaptureRootProvider;
        }

        public IRegistryManagedSession Create(
            SessionRegistration registration,
            Action<IRegistryManagedSession> completion)
        {
            return new ManagedSession(
                registration,
                Path.Combine(bufferRoot, registration.SessionId.ToString("N")),
                disconnectedTimeoutMilliseconds,
                maximumBufferBytes,
                completion,
                committedFrame, authorizeClipboard, brokerCaptureRootProvider);
        }
    }

    internal sealed class RecoveredBufferedSession : IRegistryManagedSession, IRecoveryRegistrationSession, IBufferedRegistryManagedSession, IRemoteInputReplaySession
    {
        internal TerminalSemanticProfile BoundProfile => buffer.BoundProfile;
        private readonly EncryptedEventBuffer buffer;
        private readonly string bufferDirectory;
        private readonly SessionRegistration registration;
        private readonly ManagedShellType shellType;
        private readonly DateTime registeredAtUtc;
        private LocalLifecycleState lifecycle;
        private int exitCode;
        internal string RecoveryError { get; private set; } = string.Empty;

        public RecoveredBufferedSession(
            SessionRegistration registration,
            string bufferDirectory,
            long maximumBufferBytes)
        {
            if (registration == null) throw new ArgumentNullException("registration");
            this.registration = registration;
            SessionId = registration.SessionId;
            shellType = registration.ShellType;
            registeredAtUtc = registration.RegisteredAtUtc;
            this.bufferDirectory = bufferDirectory;
            buffer = new EncryptedEventBuffer(bufferDirectory, SessionId, maximumBufferBytes);
        }

        public Guid SessionId { get; private set; }
        public ManagedShellType ShellType { get { return shellType; } }
        public SessionRegistration Registration { get { return registration; } }

        public LocalFrame EnsureTerminalFrame(Action<LocalFrame> verifyBrokerCapture = null)
        {
            if (buffer.TerminalCompatibilityError.Length != 0 || !buffer.HasBoundProfile)
            {
                lifecycle = LocalLifecycleState.Interrupted;
                return null;
            }
            ulong earliest;
            ulong latest;
            var frames = buffer.ReadFrom(0, out earliest, out latest);
            foreach (var frame in frames)
            {
                if (frame.Type != LocalFrameType.BrokerOutput) continue;
                try
                {
                    if (verifyBrokerCapture == null)
                        throw new InvalidOperationException("BROKER_CAPTURE_RUNTIME_UNAVAILABLE");
                    verifyBrokerCapture(frame);
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException ||
                    error is UnauthorizedAccessException || error is CryptographicException ||
                    (error is InvalidOperationException && error.Message == "BROKER_CAPTURE_RUNTIME_UNAVAILABLE"))
                {
                    RecoveryError = "BROKER_CAPTURE_RECOVERY_UNAVAILABLE";
                    throw;
                }
            }
            RecoveryError = string.Empty;
            LocalFrame terminalLifecycle = null;
            if (frames.Count > 0)
            {
                var last = frames[frames.Count - 1];
                if (last.Type == LocalFrameType.SessionRemoved)
                {
                    LocalPayloadCodec.ParseSessionRemoved(last.Payload);
                    for (var index = frames.Count - 2; index >= 0; index--)
                    {
                        if (frames[index].Type != LocalFrameType.Lifecycle) continue;
                        terminalLifecycle = frames[index];
                        break;
                    }
                    if (terminalLifecycle == null)
                        throw new InvalidDataException("Recovered removal has no preceding terminal lifecycle.");
                    lifecycle = LocalPayloadCodec.ParseLifecycle(terminalLifecycle.Payload, out exitCode);
                    if (lifecycle != LocalLifecycleState.Ended &&
                        lifecycle != LocalLifecycleState.Interrupted)
                        throw new InvalidDataException("Recovered removal follows a non-terminal lifecycle.");
                    return last;
                }
                if (last.Type == LocalFrameType.Lifecycle)
                {
                    var state = LocalPayloadCodec.ParseLifecycle(last.Payload, out exitCode);
                    if (state == LocalLifecycleState.Ended || state == LocalLifecycleState.Interrupted)
                    {
                        lifecycle = state;
                        terminalLifecycle = last;
                    }
                }
            }
            if (latest == ulong.MaxValue) throw new InvalidDataException("Recovered event sequence is exhausted.");
            if (terminalLifecycle == null)
            {
                lifecycle = LocalLifecycleState.Interrupted;
                exitCode = 0;
                terminalLifecycle = new LocalFrame(
                    LocalFrameType.Lifecycle,
                    SessionId,
                    latest + 1,
                    LocalPayloadCodec.Lifecycle(lifecycle, exitCode));
                buffer.Append(terminalLifecycle);
                latest = terminalLifecycle.Sequence;
                if (latest == ulong.MaxValue) throw new InvalidDataException("Recovered event sequence is exhausted.");
            }
            var removal = new LocalFrame(
                LocalFrameType.SessionRemoved,
                SessionId,
                latest + 1,
                LocalPayloadCodec.SessionRemoved());
            buffer.Append(removal);
            return removal;
        }

        public void Start() { }

        internal string TerminalCompatibilityError
        {
            get
            {
                var error = buffer.TerminalCompatibilityError;
                return error.Length != 0 ? error : (!buffer.HasBoundProfile ? TerminalSessionProfile.Missing : RecoveryError);
            }
        }

        public SessionSummary GetSummary()
        {
            return new SessionSummary
            {
                SessionId = SessionId,
                ShellType = shellType,
                Lifecycle = RecoveryError.Length != 0 ? SessionAgentLifecycle.Faulted : lifecycle == LocalLifecycleState.Ended
                    ? SessionAgentLifecycle.Ended
                    : SessionAgentLifecycle.HostLost,
                LastSequence = buffer.LastSequence,
                RegisteredAtUtc = registeredAtUtc,
                LastActivityUtc = DateTime.UtcNow,
                ExitCode = lifecycle == LocalLifecycleState.Ended ? (int?)exitCode : null
            };
        }

        public void BeginStop() { }
        public bool WaitForStop() { return true; }

        public IList<LocalFrame> ReadBufferedEvents(
            ulong resumeFromSequence,
            out ulong earliestRetainedSequence,
            out ulong latestSequence)
        {
            return buffer.ReadFrom(
                resumeFromSequence,
                out earliestRetainedSequence,
                out latestSequence);
        }

        public IList<LocalFrame> ReadBufferedEvents(
            ulong resumeFromSequence,
            int maximumEvents,
            long maximumStoredBytes,
            out ulong earliestRetainedSequence,
            out ulong latestSequence,
            out bool hasMore)
        {
            return buffer.ReadFrom(
                resumeFromSequence,
                maximumEvents,
                maximumStoredBytes,
                out earliestRetainedSequence,
                out latestSequence,
                out hasMore);
        }

        public void DeleteBufferedEvents()
        {
            buffer.DeleteAll();
            TryDeletePersistentState(bufferDirectory, "input-dedupe.dat");
            TryDeletePersistentState(bufferDirectory, "command-sequence.dat");
        }

        private static void TryDeletePersistentState(string directory, string name)
        {
            try
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        public bool TryGetRemoteInputIdentity(
            Guid inputId,
            out Guid senderDeviceId,
            out byte[] envelopeSha256)
        {
            // A completed recovery buffer has no live SessionAgent command
            // fence. Its result stream remains searchable below, but there is
            // no authenticated request identity to claim for a new retry.
            senderDeviceId = Guid.Empty;
            envelopeSha256 = null;
            return false;
        }

        public bool TryFindRemoteInputResult(Guid inputId, out LocalFrame resultFrame)
        {
            return buffer.TryFindRemoteInputResult(inputId, out resultFrame);
        }

        public void Dispose()
        {
            buffer.Dispose();
        }
    }

    internal sealed class ManagedSession : IRegistryManagedSession, IInterruptibleRegistryManagedSession, IRecoveryRegistrationSession, IRemoteInputReplayDispatchSession, IBufferedRegistryManagedSession, IRemoteInputReplaySession
    {
        internal string TerminalCompatibilityError { get { return agent.TerminalCompatibilityError; } }
        internal TerminalSemanticProfile BoundProfile => agent.BoundProfile;
        private readonly SessionAgent agent;
        private readonly SessionRegistration registration;
        private readonly ManagedShellType shellType;
        private readonly DateTime registeredAtUtc;
        private readonly Action<IRegistryManagedSession> completion;
        private readonly object sync = new object();
        private Task worker;
        private bool disposed;

        public ManagedSession(
            SessionRegistration registration,
            string bufferDirectory,
            int disconnectedTimeoutMilliseconds,
            long maximumBufferBytes,
            Action<IRegistryManagedSession> completion,
            Action<ManagedShellType, LocalFrame> committedFrame = null,
            Func<Guid, CancellationToken, Task<bool>> authorizeClipboard = null,
            Func<string> brokerCaptureRootProvider = null)
        {
            this.registration = registration;
            SessionId = registration.SessionId;
            shellType = registration.ShellType;
            registeredAtUtc = registration.RegisteredAtUtc;
            this.completion = completion;
            var options = AgentOptions.CreateManaged(
                registration.PipeName,
                registration.SessionId,
                registration.HandshakeToken,
                bufferDirectory,
                registration.ExpectedHostProcessId,
                disconnectedTimeoutMilliseconds,
                maximumBufferBytes);
            agent = new SessionAgent(options, committedFrame == null ? (Action<LocalFrame>)null : frame => committedFrame(shellType, frame),
                authorizeClipboard, brokerCaptureRootProvider);
        }

        public Guid SessionId { get; private set; }
        public ManagedShellType ShellType { get { return shellType; } }
        public SessionRegistration Registration { get { return registration; } }

        public void Start()
        {
            lock (sync)
            {
                if (disposed) throw new ObjectDisposedException("ManagedSession");
                if (worker != null) throw new InvalidOperationException("Managed session already started.");
                worker = Task.Factory.StartNew(
                    Run,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);
            }
        }

        private void Run()
        {
            try
            {
                agent.RunManaged();
            }
            catch
            {
                // The state is exposed as Faulted; Registry cleanup must not depend on a consumer observing it.
            }
            finally
            {
                agent.Dispose();
                completion(this);
            }
        }

        public SessionSummary GetSummary()
        {
            var snapshot = agent.Snapshot;
            return new SessionSummary
            {
                SessionId = SessionId,
                ShellType = shellType,
                Lifecycle = snapshot.Lifecycle,
                HostProcessId = snapshot.HostProcessId,
                LastSequence = snapshot.LastSequence,
                RegisteredAtUtc = registeredAtUtc,
                LastActivityUtc = snapshot.LastActivityUtc,
                ExitCode = snapshot.ExitCode
            };
        }

        public void BeginStop()
        {
            BeginStop(true);
        }

        public void BeginStop(bool commitInterruptedLifecycle)
        {
            var request = false;
            lock (sync)
            {
                if (!disposed)
                {
                    disposed = true;
                    request = true;
                }
            }
            if (request) agent.RequestRemoteStop(commitInterruptedLifecycle);
        }

        public Task<LocalResizeResult> RequestResizeAsync(LocalResizeRequest request) => agent.RequestResizeAsync(request);

        public bool TrySendRemoteInput(Guid inputId, ulong expectedLocalInputEpoch, byte[] bytes)
        {
            return agent.TrySendRemoteInput(inputId, expectedLocalInputEpoch, bytes);
        }

        public RemoteInputDispatchOutcome DispatchRemoteInput(
            Guid inputId,
            ulong expectedLocalInputEpoch,
            byte[] bytes)
        {
            return agent.DispatchRemoteInput(inputId, expectedLocalInputEpoch, bytes);
        }

        public RemoteInputDispatchOutcome DispatchRemoteInput(
            Guid inputId,
            ulong expectedLocalInputEpoch,
            byte[] bytes,
            Guid senderDeviceId,
            byte[] envelopeSha256)
        {
            return agent.DispatchRemoteInput(
                inputId,
                expectedLocalInputEpoch,
                bytes,
                senderDeviceId,
                envelopeSha256);
        }

        public bool TrySendRemoteInputRejection(Guid inputId, LocalRemoteInputResult result)
        {
            return agent.TrySendRemoteInputRejection(inputId, result);
        }

        public bool TrySendRemoteInputRejection(
            Guid inputId,
            LocalRemoteInputResult result,
            Guid senderDeviceId,
            byte[] envelopeSha256)
        {
            return agent.TrySendRemoteInputRejection(
                inputId,
                result,
                senderDeviceId,
                envelopeSha256);
        }

        public IList<LocalFrame> ReadBufferedEvents(
            ulong resumeFromSequence,
            out ulong earliestRetainedSequence,
            out ulong latestSequence)
        {
            return agent.ReadBufferedEvents(
                resumeFromSequence,
                out earliestRetainedSequence,
                out latestSequence);
        }

        public IList<LocalFrame> ReadBufferedEvents(
            ulong resumeFromSequence,
            int maximumEvents,
            long maximumStoredBytes,
            out ulong earliestRetainedSequence,
            out ulong latestSequence,
            out bool hasMore)
        {
            return agent.ReadBufferedEvents(
                resumeFromSequence,
                maximumEvents,
                maximumStoredBytes,
                out earliestRetainedSequence,
                out latestSequence,
                out hasMore);
        }

        public void DeleteBufferedEvents()
        {
            agent.DeleteBufferedEvents();
        }

        public bool TryGetRemoteInputIdentity(
            Guid inputId,
            out Guid senderDeviceId,
            out byte[] envelopeSha256)
        {
            return agent.TryGetRemoteInputIdentity(
                inputId,
                out senderDeviceId,
                out envelopeSha256);
        }

        public bool TryFindRemoteInputResult(Guid inputId, out LocalFrame resultFrame)
        {
            return agent.TryFindRemoteInputResult(inputId, out resultFrame);
        }

        public bool WaitForStop()
        {
            Task task;
            lock (sync)
            {
                task = worker;
            }
            if (task != null && !task.Wait(5000))
            {
                return false;
            }
            if (task == null) agent.Dispose();
            return true;
        }

        public void Dispose()
        {
            BeginStop();
            WaitForStop();
        }
    }

    internal sealed class RegistryServiceOptions
    {
        public string ControlPipeName { get; private set; }
        public string BufferRoot { get; private set; }
        public int DisconnectedTimeoutMilliseconds { get; private set; }
        public long MaximumBufferBytes { get; private set; }
        public int MaximumSessions { get; private set; }
        public bool Diagnostics { get; private set; }

        public static bool IsRegistryMode(string[] args)
        {
            return args != null && args.Length > 0 && args[0] == "--registry";
        }

        public static RegistryServiceOptions Parse(string[] args)
        {
            if (!IsRegistryMode(args)) throw new ArgumentException("--registry must be the first argument.");
            string pipe = null;
            string bufferRoot = null;
            var timeout = 5000;
            long maximumBuffer = 16 * 1024 * 1024;
            var maximumSessions = 20;
            var diagnostics = false;
            for (var index = 1; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--control-pipe": pipe = Required(args, ref index, "--control-pipe"); break;
                    case "--buffer-root": bufferRoot = Required(args, ref index, "--buffer-root"); break;
                    case "--host-disconnect-timeout-ms":
                        timeout = ParseInt(Required(args, ref index, "--host-disconnect-timeout-ms"), 250, 300000, "host disconnect timeout");
                        break;
                    case "--max-buffer-bytes":
                        maximumBuffer = ParseLong(Required(args, ref index, "--max-buffer-bytes"), 1024, 1024L * 1024 * 1024, "maximum buffer bytes");
                        break;
                    case "--max-sessions":
                        maximumSessions = ParseInt(Required(args, ref index, "--max-sessions"), 1, 1000, "maximum sessions");
                        break;
                    case "--diagnostics": diagnostics = true; break;
                    default: throw new ArgumentException("Unknown Registry argument: " + args[index]);
                }
            }
            if (string.IsNullOrWhiteSpace(pipe) || pipe.Length > 200 || pipe.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new ArgumentException("--control-pipe is invalid.");
            }
            if (string.IsNullOrWhiteSpace(bufferRoot)) throw new ArgumentException("--buffer-root is required.");
            return new RegistryServiceOptions
            {
                ControlPipeName = pipe,
                BufferRoot = Path.GetFullPath(bufferRoot),
                DisconnectedTimeoutMilliseconds = timeout,
                MaximumBufferBytes = maximumBuffer,
                MaximumSessions = maximumSessions,
                Diagnostics = diagnostics
            };
        }

        private static string Required(string[] args, ref int index, string name)
        {
            if (++index >= args.Length || string.IsNullOrEmpty(args[index])) throw new ArgumentException(name + " requires a value.");
            return args[index];
        }

        private static int ParseInt(string value, int minimum, int maximum, string name)
        {
            int parsed;
            if (!int.TryParse(value, out parsed) || parsed < minimum || parsed > maximum)
            {
                throw new ArgumentException(name + " must be from " + minimum + " to " + maximum + ".");
            }
            return parsed;
        }

        private static long ParseLong(string value, long minimum, long maximum, string name)
        {
            long parsed;
            if (!long.TryParse(value, out parsed) || parsed < minimum || parsed > maximum)
            {
                throw new ArgumentException(name + " must be from " + minimum + " to " + maximum + ".");
            }
            return parsed;
        }
    }

    internal sealed class SessionRegistryService : IDisposable
    {
        private readonly RegistryServiceOptions options;
        private readonly SessionRegistry registry;
#if TERMLINK_PRODUCTION_RUNTIME
        private ProductionAgentRuntime productionRuntime;
        private readonly object productionRuntimeMutationSync = new object();
#else
        // The legacy csc validation build intentionally excludes production sources.
        private readonly IDisposable productionRuntime;
#endif
        private readonly object sync = new object();
        private readonly HashSet<NamedPipeServerStream> activeControlPipes =
            new HashSet<NamedPipeServerStream>();
        private readonly byte[] controlSecret;
        private readonly RegistryControlReplayWindow controlReplay = new RegistryControlReplayWindow();
        private volatile bool shutdownRequested;
        private int requests;
        private bool disposed;

        public SessionRegistryService(RegistryServiceOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            this.options = options;
            controlSecret = RegistryControlSecretStore.GetOrCreateDefault();
#if TERMLINK_PRODUCTION_RUNTIME
            var runtime = ProductionAgentRuntime.TryCreate(options.ControlPipeName);
            try
            {
                registry = new SessionRegistry(
                    options.BufferRoot,
                    options.DisconnectedTimeoutMilliseconds,
                    options.MaximumBufferBytes,
                    options.MaximumSessions,
                    ForwardCommittedFrame,
                    sessionId => CurrentProductionRuntime()?.DestroySessionHistory(sessionId),
                    (sessionId, token) => CurrentProductionRuntime()?.AuthorizeLocalClipboardAsync(sessionId, token) ?? Task.FromResult(false),
                    brokerCaptureRootProvider: () => CurrentProductionRuntime()?.BrokerCaptureRootPath,
                    deferRecovery: true);
                if (runtime != null)
                {
                    Volatile.Write(ref productionRuntime, runtime);
                    runtime.Attach(registry);
                }
                else registry.RecoverRetainedSessions();
            }
            catch
            {
                Volatile.Write(ref productionRuntime, null);
                if (runtime != null) runtime.Dispose();
                if (registry != null) registry.Dispose();
                Array.Clear(controlSecret, 0, controlSecret.Length);
                throw;
            }
#else
            registry = new SessionRegistry(
                options.BufferRoot,
                options.DisconnectedTimeoutMilliseconds,
                options.MaximumBufferBytes,
                options.MaximumSessions);
            productionRuntime = null;
#endif
        }

        internal SessionRegistryService(RegistryServiceOptions options, SessionRegistry registry)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (registry == null) throw new ArgumentNullException("registry");
            this.options = options;
            this.registry = registry;
            controlSecret = RegistryControlSecretStore.GetOrCreateDefault();
            productionRuntime = null;
        }

        public int Run()
        {
            ConsoleCancelEventHandler cancelHandler = delegate(object sender, ConsoleCancelEventArgs eventArgs)
            {
                eventArgs.Cancel = true;
                RequestShutdown();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                // Control requests are short and independent, while SessionHost
                // registration can arrive in bursts when Terminal restores many
                // tabs. Keep enough listeners posted so clients do not mistake
                // normal load for a missing runtime.
                const int workerCount = 32;
                var workers = new Thread[workerCount];
                for (var index = 0; index < workers.Length; index++)
                {
                    workers[index] = new Thread(RunControlPipeWorker)
                    {
                        IsBackground = true,
                        Name = "TermLink Control " + (index + 1)
                    };
                    workers[index].Start();
                }
                foreach (var worker in workers) worker.Join();
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
            if (options.Diagnostics)
            {
                Console.WriteLine(
                    "__REGISTRY_RESULT__:requests=" + requests +
                    ";sessions=" + registry.Count +
                    ";recovered=" + registry.RecoveredSessionCount +
                    ";remote_enabled=" + registry.RemoteAccessEnabled.ToString().ToLowerInvariant());
            }
            return 0;
        }

        private void RunControlPipeWorker()
        {
            while (true)
            {
                NamedPipeServerStream pipe;
                lock (sync)
                {
                    if (shutdownRequested) return;
                    pipe = CreateControlPipe(options.ControlPipeName);
                    activeControlPipes.Add(pipe);
                }
                using (pipe)
                {
                    try
                    {
                        pipe.WaitForConnection();
                        RegistryResponse response;
                        RegistryRequest controlRequest = null;
                        var responseVersion = (byte)6;
                        try
                        {
                            byte requestVersion;
                            var request = RegistryControlCodec.ReadRequest(pipe, out requestVersion);
                            controlRequest = request;
                            responseVersion = requestVersion;
                            string authenticationCode;
                            response = IsAuthorized(request, requestVersion, out authenticationCode)
                                ? Handle(request)
                                : Response(false, authenticationCode, 0);
                        }
                        catch (InvalidDataException)
                        {
                            response = Response(false, "INVALID_REQUEST", 0);
                        }
                        catch (ArgumentException)
                        {
                            response = Response(false, "INVALID_REQUEST", 0);
                        }
#if TERMLINK_PRODUCTION_RUNTIME
                        catch (ProductionEnrollmentException error)
                        {
                            ObserveControlRequestFailure(controlRequest, error);
                            response = Response(false, error.Code, 0);
                        }
#endif
                        catch (InvalidOperationException error)
                        {
                            ObserveControlRequestFailure(controlRequest, error);
                            response = Response(false, "OPERATION_FAILED", 0);
                        }
                        catch (TimeoutException)
                        {
                            response = Response(false, "OPERATION_TIMEOUT", 0);
                        }
#if TERMLINK_PRODUCTION_RUNTIME
                        catch (ProductionReauthenticationRequiredException)
                        {
                            response = Response(false, "ACCOUNT_REAUTH_REQUIRED", 0);
                        }
                        catch (System.Net.Http.HttpRequestException)
                        {
                            response = Response(false, "PRODUCTION_REQUEST_FAILED", 0);
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            response = Response(false, "PRODUCTION_RESPONSE_INVALID", 0);
                        }
                        catch (OperationCanceledException)
                        {
                            response = Response(false, "PRODUCTION_REQUEST_CANCELLED", 0);
                        }
                        catch (Exception error)
                        {
                            // A browser callback, token exchange, DPAPI write,
                            // or runtime replacement failure is scoped to this
                            // authenticated request. It must fail closed without
                            // terminating every local session and the Agent.
                            ObserveControlRequestFailure(controlRequest, error);
                            response = Response(false, "OPERATION_FAILED", 0);
                        }
#endif
                        if (responseVersion >= 7 && controlRequest != null)
                            RegistryControlAuthentication.PrepareResponse(
                                response,
                                controlRequest,
                                controlSecret);
                        RegistryControlCodec.WriteResponse(pipe, response, responseVersion);
                        Interlocked.Increment(ref requests);
                        if (shutdownRequested) DisposeOtherControlPipes(pipe);
                    }
                    catch (IOException)
                    {
                        if (!shutdownRequested) continue;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (!shutdownRequested) throw;
                    }
                    finally
                    {
                        lock (sync) activeControlPipes.Remove(pipe);
                    }
                }
            }
        }

        private void DisposeOtherControlPipes(NamedPipeServerStream current)
        {
            NamedPipeServerStream[] pipes;
            lock (sync) pipes = activeControlPipes.Where(pipe => !ReferenceEquals(pipe, current)).ToArray();
            foreach (var pipe in pipes)
            {
                try { pipe.Dispose(); }
                catch (ObjectDisposedException) { }
            }
        }

        private void ObserveControlRequestFailure(RegistryRequest request, Exception error)
        {
            var line = string.Empty;
            try
            {
                var command = request == null ? "unknown" : request.Command.ToString();
                var frame = new StackTrace(error, true).GetFrame(0);
                var method = frame == null ? null : frame.GetMethod();
                var declaringType = method == null ? null : method.DeclaringType;
                var failureSite = method == null
                    ? "unknown"
                    : (declaringType == null ? "unknown" : declaringType.Name) + "." + method.Name;
                var sourceLine = frame == null ? 0 : frame.GetFileLineNumber();
                line = DateTime.UtcNow.ToString("O") +
                    " control-request command=" + command +
                    " failure=" + error.GetType().Name +
                    " site=" + failureSite +
                    (sourceLine > 0 ? " line=" + sourceLine : string.Empty);
                // Enrollment exceptions carry fixed, credential-free diagnostic
                // text (status codes and server error codes) that field logs
                // need to distinguish unrecognized server rejections. Every
                // other failure keeps the type+site-only record.
                if (error is ProductionEnrollmentException enrollmentError)
                {
                    var message = (enrollmentError.Message ?? string.Empty)
                        .Replace('\r', ' ').Replace('\n', ' ');
                    if (message.Length > 300) message = message[..300];
                    line += " message=" + message;
                }
                line += Environment.NewLine;
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TermLink",
                    "logs");
                Directory.CreateDirectory(root);
                File.AppendAllText(
                    Path.Combine(root, "TermLink-control-runtime.log"),
                    line,
                    new UTF8Encoding(false));
                if (options.Diagnostics) Console.Error.Write(line);
            }
            catch
            {
                // A bounded diagnostic containing only type and source location
                // must never affect control flow or record credentials/messages.
                try
                {
                    if (!string.IsNullOrEmpty(line))
                        File.AppendAllText(
                            Path.Combine(options.BufferRoot, "control-runtime.log"),
                            line,
                            new UTF8Encoding(false));
                }
                catch
                {
                }
            }
        }

        private bool IsAuthorized(RegistryRequest request, byte requestVersion, out string code)
        {
            if (requestVersion < 7)
            {
                // V5/V6 remains accepted only for SessionHost registration so
                // terminals that survived an in-place upgrade can reconnect.
                // Every UI/diagnostic/lifecycle command requires V7 authentication.
                code = request.Command == RegistryCommand.Register
                    ? "LEGACY_SESSION_REGISTRATION"
                    : "AUTHENTICATION_REQUIRED";
                return request.Command == RegistryCommand.Register;
            }
            var now = DateTime.UtcNow;
            if (!RegistryControlAuthentication.Verify(request, controlSecret, now))
            {
                code = "AUTHENTICATION_FAILED";
                return false;
            }
            if (!controlReplay.TryAccept(request, now))
            {
                code = "AUTHENTICATION_REPLAYED";
                return false;
            }
            code = "AUTHENTICATED";
            return true;
        }

        /// <summary>
        /// Dispatches a control request inside the Agent process. This entry point
        /// is retained for Agent-owned hosting and validation; external UI,
        /// SessionHost, diagnostics, and lifecycle clients use the authenticated
        /// per-user pipe.
        /// </summary>
        internal RegistryResponse Dispatch(RegistryRequest request)
        {
            if (request == null) throw new ArgumentNullException("request");
            var response = Handle(request);
            if (shutdownRequested) DisposeOtherControlPipes(null);
            return response;
        }

#if TERMLINK_PRODUCTION_RUNTIME
        private void ForwardCommittedFrame(ManagedShellType shellType, LocalFrame frame)
        {
            var runtime = Volatile.Read(ref productionRuntime);
            if (runtime == null && frame.Type == LocalFrameType.BrokerOutput)
                throw new InvalidOperationException("BROKER_CAPTURE_RUNTIME_UNAVAILABLE");
            if (runtime != null) runtime.OnCommitted(shellType, frame);
        }

        private ProductionAgentRuntime CurrentProductionRuntime()
        {
            return Volatile.Read(ref productionRuntime);
        }

        private void ReplaceProductionAccount(Action enrollment)
        {
            if (enrollment == null) throw new ArgumentNullException(nameof(enrollment));
            lock (productionRuntimeMutationSync)
            {
                enrollment();
                ReplaceProductionRuntimeLocked(ProductionAgentRuntime.TryCreate(options.ControlPipeName));
            }
        }

        private void ForgetProductionAccount()
        {
            lock (productionRuntimeMutationSync)
            {
                // First remove the live runtime from the publication route and
                // cancel its Relay/HTTP work. Durable account removal follows
                // only after old-account synchronization is already stopped.
                var previous = Interlocked.Exchange(ref productionRuntime, null);
                RetireProductionRuntime(previous);
                ProductionEnrollment.ForgetLocalAccount();
            }
        }

        private void ConfigureProductionServer(string serverUrl)
        {
            var normalized = ProductionEnrollment.NormalizeServerOrigin(serverUrl);
            lock (productionRuntimeMutationSync)
            {
                // Persist only after validation. The old runtime is retired
                // before its account files are removed; local sessions remain
                // owned by SessionRegistry throughout the switch.
                ProductionEnrollment.ConfigureServer(normalized);
                var previous = Interlocked.Exchange(ref productionRuntime, null);
                RetireProductionRuntime(previous);
                ProductionEnrollment.ForgetLocalAccount();
            }
        }

        private void ReplaceProductionRuntimeLocked(ProductionAgentRuntime next)
        {
            var previous = Interlocked.Exchange(ref productionRuntime, null);
            RetireProductionRuntime(previous);
            if (next == null) return;
            try
            {
                // SessionRegistry remains alive across account changes. Attach
                // queues every still-running session for replay, while its
                // durable local buffers cover frames committed during the swap.
                next.Attach(registry);
                Volatile.Write(ref productionRuntime, next);
                next.ReconcileAttachedRegistryAfterPublication();
            }
            catch
            {
                Interlocked.CompareExchange(ref productionRuntime, null, next);
                RetireProductionRuntime(next);
                throw;
            }
        }

        private static void RetireProductionRuntime(ProductionAgentRuntime runtime)
        {
            if (runtime == null) return;
            // Stop old-account publication before returning a new account state.
            // Worker joins and HTTP disposal are deliberately off the control
            // request so a slow network cannot hold the sign-out button open.
            runtime.StopAccountSynchronization();
            Task.Run(delegate
            {
                try { runtime.Dispose(); }
                catch (Exception) { }
            });
        }
#endif

        private RegistryResponse Handle(RegistryRequest request)
        {
            switch (request.Command)
            {
                case RegistryCommand.Register:
                    string error;
                    var registered = registry.TryRegister(request.Registration, out error);
                    return Response(registered, registered ? "REGISTERED" : error, 0);
                case RegistryCommand.List:
                    return new RegistryResponse
                    {
                        Success = true,
                        Code = "OK",
                        RemoteAccessEnabled = registry.RemoteAccessEnabled,
                        Summaries = registry.GetSummaries(),
                        AffectedSessions = 0,
                        Production = ProductionSnapshot(false)
                    };
                case RegistryCommand.StopSession:
                    var stopped = registry.StopSession(request.SessionId);
                    if (stopped.RequestedSessions == 0) return Response(false, "SESSION_NOT_FOUND", 0);
                    var stopFailure = stopped.PolicyPersistenceFailed
                        ? (stopped.TimedOutSessions > 0 ? "STOP_TIMEOUT_NOT_DURABLE" : "STOPPED_NOT_DURABLE")
                        : "STOP_TIMEOUT";
                    return StopResponse(stopped, "STOPPED", stopFailure);
                case RegistryCommand.StopAllRemoteAccess:
                    var stoppedAll = registry.StopAllRemoteAccess();
                    var stopAllFailure = stoppedAll.PolicyPersistenceFailed
                        ? (stoppedAll.TimedOutSessions > 0
                            ? "REMOTE_ACCESS_STOP_TIMEOUT_NOT_DURABLE"
                            : "REMOTE_ACCESS_STOPPED_NOT_DURABLE")
                        : "REMOTE_ACCESS_STOP_TIMEOUT";
                    return StopResponse(
                        stoppedAll,
                        "REMOTE_ACCESS_STOPPED",
                        stopAllFailure);
                case RegistryCommand.EnableRemoteAccess:
                    var enabled = registry.EnableRemoteAccess();
                    return Response(
                        enabled,
                        enabled ? "REMOTE_ACCESS_ENABLED" : "REMOTE_ACCESS_ENABLE_NOT_DURABLE",
                        0);
                case RegistryCommand.Shutdown:
                    shutdownRequested = true;
                    var shutdown = registry.Shutdown();
#if TERMLINK_PRODUCTION_RUNTIME
                    var shutdownRuntime = CurrentProductionRuntime();
                    if (shutdownRuntime != null)
                        shutdownRuntime.DrainTerminalPublications(TimeSpan.FromSeconds(10));
#endif
                    return StopResponse(
                        shutdown,
                        "REGISTRY_SHUTDOWN",
                        "REGISTRY_SHUTDOWN_WITH_TIMEOUTS");
                case RegistryCommand.RestartAgent:
                    shutdownRequested = true;
                    var restart = registry.ShutdownForRestart();
                    return StopResponse(
                        restart,
                        "AGENT_RESTART_READY",
                        "AGENT_RESTART_WITH_TIMEOUTS");
                case RegistryCommand.DetectAiAgents:
#if TERMLINK_PRODUCTION_RUNTIME
                    var detected = TermLink.Ai.AiAgentDiscovery.CatalogAsync(true).GetAwaiter().GetResult();
                    var detectedResponse = Response(true, "AI_AGENTS_DETECTED", detected.Length);
                    detectedResponse.Production.AiAgentsJson = System.Text.Json.JsonSerializer.Serialize(detected);
                    return detectedResponse;
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.RefreshProductionStatus:
#if TERMLINK_PRODUCTION_RUNTIME
                    var refreshRuntime = CurrentProductionRuntime();
                    if (refreshRuntime == null) return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
                    try
                    {
                        refreshRuntime.GetStatus(true);
                    }
                    catch (ProductionReauthenticationRequiredException)
                    {
                        return Response(false, "ACCOUNT_REAUTH_REQUIRED", 0);
                    }
                    return Response(true, "PRODUCTION_STATUS_REFRESHED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.RetryProductionConnection:
#if TERMLINK_PRODUCTION_RUNTIME
                    var retryRuntime = CurrentProductionRuntime();
                    if (retryRuntime == null) return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
                    retryRuntime.RequestReconnect();
                    return Response(true, "PRODUCTION_RECONNECT_REQUESTED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.CreatePairingChallenge:
#if TERMLINK_PRODUCTION_RUNTIME
                    var pairingRuntime = CurrentProductionRuntime();
                    if (pairingRuntime == null) return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
                    pairingRuntime.CreatePairingChallenge(request.DeviceId, request.PairingPermission);
                    return Response(true, "PAIRING_CHALLENGE_CREATED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.GrantDevice:
#if TERMLINK_PRODUCTION_RUNTIME
                    var grantRuntime = CurrentProductionRuntime();
                    if (grantRuntime == null) return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
                    grantRuntime.GrantDevice(request.DeviceId, request.GrantPermission);
                    return Response(true, "DEVICE_GRANT_UPDATED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.RevokeDevice:
#if TERMLINK_PRODUCTION_RUNTIME
                    var revokeRuntime = CurrentProductionRuntime();
                    if (revokeRuntime == null) return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
                    revokeRuntime.RevokeDevice(request.DeviceId);
                    return Response(true, "DEVICE_REVOKED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.LoginAccount:
#if TERMLINK_PRODUCTION_RUNTIME
                    ReplaceProductionAccount(delegate
                    {
                        ProductionEnrollment.LoginDirect(
                            ProductionEnrollment.ConfiguredServerUrl(),
                            request.PcName,
                            request.AccountUsername,
                            request.AccountPassword);
                    });
                    return Response(true, "ACCOUNT_LOGIN_ACCEPTED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.RegisterAccount:
#if TERMLINK_PRODUCTION_RUNTIME
                    ReplaceProductionAccount(delegate
                    {
                        ProductionEnrollment.RegisterDirect(
                            ProductionEnrollment.ConfiguredServerUrl(),
                            request.PcName,
                            request.AccountUsername,
                            request.AccountNickname,
                            request.AccountEmail,
                            request.AccountPassword,
                            request.AccountPasswordConfirmation);
                    });
                    return Response(true, "ACCOUNT_REGISTRATION_ACCEPTED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.CancelAccountAuthorization:
#if TERMLINK_PRODUCTION_RUNTIME
                    ProductionEnrollment.CancelBrowserAuthorization();
                    return Response(true, "ACCOUNT_AUTHORIZATION_CANCELLED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.ReopenAccountAuthorization:
#if TERMLINK_PRODUCTION_RUNTIME
                    ProductionEnrollment.ReopenBrowserAuthorization();
                    return Response(true, "ACCOUNT_AUTHORIZATION_REOPENED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.BeginAccountAuthorization:
#if TERMLINK_PRODUCTION_RUNTIME
                    var configuredServerUrl = ProductionEnrollment.ConfiguredServerUrl();
                    if (ProductionEnrollment.TryContinueActiveBrowserAuthorization(
                            configuredServerUrl))
                    {
                        // The owning request performs the runtime swap while
                        // holding this lock. Do not acknowledge the joined
                        // request until that new-account runtime is visible.
                        lock (productionRuntimeMutationSync) { }
                        return Response(true, "ACCOUNT_AUTHORIZATION_ACCEPTED", 0);
                    }
                    ReplaceProductionAccount(delegate
                    {
                        ProductionEnrollment.LinkAccount(
                            configuredServerUrl,
                            request.PcName);
                    });
                    return Response(true, "ACCOUNT_AUTHORIZATION_ACCEPTED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.ConfigureServer:
#if TERMLINK_PRODUCTION_RUNTIME
                    ConfigureProductionServer(request.ServerUrl);
                    return Response(true, "PRODUCTION_SERVER_CONFIGURED", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.SignOutAccount:
#if TERMLINK_PRODUCTION_RUNTIME
                    ForgetProductionAccount();
                    return Response(true, "ACCOUNT_SIGNED_OUT", 0);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                case RegistryCommand.SaveTerminalCommands:
#if TERMLINK_PRODUCTION_RUNTIME
                    var configuredCommands = (request.TerminalCommands ??
                            new List<ProductionTerminalCommandSummary>())
                        .Where(command => command.Detected || command.Enabled)
                        .Select(command => TerminalCommandProfileStore.CreateConfiguredProfile(
                            command.Id,
                            command.DisplayName,
                            command.Executable,
                            command.Arguments,
                            command.ShellType,
                            command.Detected,
                            command.Enabled,
                            command.RunAsAdministrator))
                        .ToArray();
                    var submittedIds = new HashSet<string>(
                        configuredCommands.Select(command => command.Id),
                        StringComparer.Ordinal);
                    var retainedDisabledDetected = TerminalCommandProfileStore.LoadConfiguredProfiles()
                        .Where(command =>
                            string.Equals(command.Source, "detected", StringComparison.Ordinal) &&
                            !command.Enabled &&
                            !submittedIds.Contains(command.Id));
                    TerminalCommandProfileStore.SaveConfiguredProfiles(
                        configuredCommands.Concat(retainedDisabledDetected));
                    var commandRuntime = CurrentProductionRuntime();
                    if (commandRuntime != null) commandRuntime.RequestReconnect();
                    return Response(true, "TERMINAL_COMMANDS_SAVED", configuredCommands.Length);
#else
                    return Response(false, "PRODUCTION_NOT_CONFIGURED", 0);
#endif
                default:
                    return Response(false, "UNKNOWN_COMMAND", 0);
            }
        }

        private RegistryResponse Response(bool success, string code, int affected)
        {
            return new RegistryResponse
            {
                Success = success,
                Code = code,
                RemoteAccessEnabled = registry.RemoteAccessEnabled,
                Summaries = new List<SessionSummary>(),
                AffectedSessions = affected,
                TimedOutSessions = 0,
                Production = ProductionSnapshot(false)
            };
        }

        private RegistryResponse StopResponse(SessionStopResult result, string successCode, string timeoutCode)
        {
            return new RegistryResponse
            {
                Success = result.Success,
                Code = result.Success ? successCode : timeoutCode,
                RemoteAccessEnabled = registry.RemoteAccessEnabled,
                Summaries = new List<SessionSummary>(),
                AffectedSessions = result.RequestedSessions,
                TimedOutSessions = result.TimedOutSessions,
                Production = ProductionSnapshot(false)
            };
        }

        private ProductionStatus ProductionSnapshot(bool forceRefresh)
        {
#if TERMLINK_PRODUCTION_RUNTIME
            var runtime = CurrentProductionRuntime();
            if (runtime != null) return runtime.GetStatus(forceRefresh);
#endif
            return new ProductionStatus
            {
                Configured = false,
                AccountLinked = false,
                ServerOrigin = ProductionAccountStorage.LoadServerUrl(),
                TerminalSnapshotEnabled = false,
                AgentVersion = string.Empty,
                RelayState = ProductionRelayState.NotConfigured,
                Devices = new List<ProductionDeviceSummary>(),
                Sessions = new List<ProductionSessionSummary>(),
                TerminalCommands = AvailableTerminalCommands()
            };
        }

        private static List<ProductionTerminalCommandSummary> AvailableTerminalCommands()
        {
#if TERMLINK_PRODUCTION_RUNTIME
            return ManagedTerminalLauncher.LoadAvailableTerminalCommandProfiles()
                    .Select(profile => new ProductionTerminalCommandSummary
                    {
                        Id = profile.Id,
                        DisplayName = profile.DisplayName,
                        Executable = profile.ExecutablePath,
                        Arguments = profile.Arguments,
                        ShellType = profile.ManagedShell,
                        Detected = string.Equals(profile.Source, "detected", StringComparison.Ordinal),
                        Enabled = profile.Enabled,
                        RunAsAdministrator = profile.RunAsAdministrator
                    })
                    .ToList();
#else
            // The portable recovery harness compiles SessionRegistry without
            // WindowsFileOperations. Command discovery is a production runtime
            // capability and must not pull platform launchers into that oracle.
            return new List<ProductionTerminalCommandSummary>();
#endif
        }

        private static NamedPipeServerStream CreateControlPipe(string pipeName)
        {
            return CurrentUserPipe.Create(pipeName, 64, PipeOptions.Asynchronous, 16 * 1024, 16 * 1024);
        }

        public void RequestShutdown()
        {
            NamedPipeServerStream[] pipes;
            shutdownRequested = true;
            lock (sync) pipes = activeControlPipes.ToArray();
            foreach (var pipe in pipes)
            {
                try { pipe.Dispose(); }
                catch (ObjectDisposedException) { }
            }
            registry.Shutdown();
#if TERMLINK_PRODUCTION_RUNTIME
            ProductionAgentRuntime runtime;
            lock (productionRuntimeMutationSync)
                runtime = Interlocked.Exchange(ref productionRuntime, null);
            if (runtime != null)
            {
                runtime.DrainTerminalPublications(TimeSpan.FromSeconds(10));
                runtime.Dispose();
            }
#else
            if (productionRuntime != null) productionRuntime.Dispose();
#endif
            registry.Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try
            {
                RequestShutdown();
            }
            finally
            {
                Array.Clear(controlSecret, 0, controlSecret.Length);
            }
        }
    }

}
