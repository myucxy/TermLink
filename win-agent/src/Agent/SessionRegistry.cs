using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
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
        public bool Success { get { return TimedOutSessions == 0; } }
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

    internal interface IRegistryManagedSessionFactory
    {
        IRegistryManagedSession Create(
            SessionRegistration registration,
            Action<IRegistryManagedSession> completion);
    }

    internal sealed class SessionRegistry : IDisposable
    {
        private readonly object sync = new object();
        private readonly Dictionary<Guid, IRegistryManagedSession> sessions =
            new Dictionary<Guid, IRegistryManagedSession>();
        private readonly HashSet<IRegistryManagedSession> retiring = new HashSet<IRegistryManagedSession>();
        private readonly IRegistryManagedSessionFactory sessionFactory;
        private readonly IRegistryRegistrationStore registrationStore;
        private readonly int maximumSessions;
        private int recoveredSessionCount;
        private bool remoteAccessEnabled = true;
        private bool preserveRegistrationsOnShutdown;
        private bool disposed;

        public SessionRegistry(
            string bufferRoot,
            int disconnectedTimeoutMilliseconds,
            long maximumBufferBytes,
            int maximumSessions)
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
            Directory.CreateDirectory(resolvedBufferRoot);
            sessionFactory = new ManagedSessionFactory(
                resolvedBufferRoot,
                disconnectedTimeoutMilliseconds,
                maximumBufferBytes);
            registrationStore = new DpapiRegistryRegistrationStore(
                Path.Combine(resolvedBufferRoot, "registrations.dpapi"));
            RecoverRegistrations();
        }

        internal SessionRegistry(IRegistryManagedSessionFactory sessionFactory, int maximumSessions)
            : this(sessionFactory, maximumSessions, new NullRegistryRegistrationStore())
        {
        }

        internal SessionRegistry(
            IRegistryManagedSessionFactory sessionFactory,
            int maximumSessions,
            IRegistryRegistrationStore registrationStore)
        {
            if (sessionFactory == null) throw new ArgumentNullException("sessionFactory");
            if (registrationStore == null) throw new ArgumentNullException("registrationStore");
            if (maximumSessions < 1 || maximumSessions > 1000) throw new ArgumentOutOfRangeException("maximumSessions");
            this.sessionFactory = sessionFactory;
            this.registrationStore = registrationStore;
            this.maximumSessions = maximumSessions;
            RecoverRegistrations();
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

        private void RecoverRegistrations()
        {
            foreach (var registration in registrationStore.Load())
            {
                if (!registration.MatchesRunningHost())
                {
                    registrationStore.Remove(registration.SessionId);
                    continue;
                }
                string error;
                if (!TryRegister(registration, out error))
                {
                    throw new InvalidOperationException(
                        "Unable to recover Registry session " + registration.SessionId.ToString("D") + ": " + error);
                }
                recoveredSessionCount++;
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
                if (!remoteAccessEnabled)
                {
                    error = "REMOTE_ACCESS_DISABLED";
                    return false;
                }
                if (sessions.ContainsKey(registration.SessionId))
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
                if (sessions.Count + retiring.Count >= maximumSessions)
                {
                    error = "SESSION_LIMIT_REACHED";
                    return false;
                }

                managed = sessionFactory.Create(registration, OnSessionCompleted);
                registrationStore.Upsert(registration);
                sessions.Add(registration.SessionId, managed);
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
                    }
                    registrationStore.Remove(registration.SessionId);
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

        public SessionStopResult StopSession(Guid sessionId)
        {
            IRegistryManagedSession managed;
            lock (sync)
            {
                if (!sessions.TryGetValue(sessionId, out managed)) return new SessionStopResult();
                registrationStore.Remove(sessionId);
                sessions.Remove(sessionId);
                retiring.Add(managed);
            }
            managed.BeginStop();
            var completed = managed.WaitForStop();
            if (completed)
            {
                lock (sync) retiring.Remove(managed);
            }
            return new SessionStopResult
            {
                RequestedSessions = 1,
                CompletedSessions = completed ? 1 : 0,
                TimedOutSessions = completed ? 0 : 1
            };
        }

        public SessionStopResult StopAllRemoteAccess()
        {
            List<IRegistryManagedSession> stopped;
            lock (sync)
            {
                registrationStore.Clear();
                remoteAccessEnabled = false;
                stopped = new List<IRegistryManagedSession>(sessions.Values);
                sessions.Clear();
                foreach (var session in stopped) retiring.Add(session);
            }
            return StopSessions(stopped);
        }

        public bool EnableRemoteAccess()
        {
            lock (sync)
            {
                if (disposed) return false;
                var changed = !remoteAccessEnabled;
                remoteAccessEnabled = true;
                return changed;
            }
        }

        private void OnSessionCompleted(IRegistryManagedSession completed)
        {
            lock (sync)
            {
                IRegistryManagedSession current;
                if (sessions.TryGetValue(completed.SessionId, out current) &&
                    ReferenceEquals(current, completed))
                {
                    if (!preserveRegistrationsOnShutdown)
                    {
                        registrationStore.Remove(completed.SessionId);
                    }
                    sessions.Remove(completed.SessionId);
                }
                retiring.Remove(completed);
            }
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
                if (!preserveRegistrationsOnShutdown) registrationStore.Clear();
                disposed = true;
                remoteAccessEnabled = false;
                stopped = new List<IRegistryManagedSession>(retiring);
                foreach (var session in sessions.Values)
                {
                    if (!stopped.Contains(session)) stopped.Add(session);
                    retiring.Add(session);
                }
                sessions.Clear();
            }
            return StopSessions(stopped);
        }

        private SessionStopResult StopSessions(IList<IRegistryManagedSession> stopped)
        {
            foreach (var session in stopped) session.BeginStop();
            var completed = 0;
            var timedOut = 0;
            foreach (var session in stopped)
            {
                if (session.WaitForStop())
                {
                    completed++;
                    lock (sync) retiring.Remove(session);
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

        public ManagedSessionFactory(string bufferRoot, int disconnectedTimeoutMilliseconds, long maximumBufferBytes)
        {
            this.bufferRoot = bufferRoot;
            this.disconnectedTimeoutMilliseconds = disconnectedTimeoutMilliseconds;
            this.maximumBufferBytes = maximumBufferBytes;
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
                completion);
        }
    }

    internal sealed class ManagedSession : IRegistryManagedSession
    {
        private readonly SessionAgent agent;
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
            Action<IRegistryManagedSession> completion)
        {
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
            agent = new SessionAgent(options);
        }

        public Guid SessionId { get; private set; }

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
            var request = false;
            lock (sync)
            {
                if (!disposed)
                {
                    disposed = true;
                    request = true;
                }
            }
            if (request) agent.RequestRemoteStop();
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
        private readonly object sync = new object();
        private NamedPipeServerStream activeControlPipe;
        private volatile bool shutdownRequested;
        private int requests;
        private bool disposed;

        public SessionRegistryService(RegistryServiceOptions options)
        {
            if (options == null) throw new ArgumentNullException("options");
            this.options = options;
            registry = new SessionRegistry(
                options.BufferRoot,
                options.DisconnectedTimeoutMilliseconds,
                options.MaximumBufferBytes,
                options.MaximumSessions);
        }

        internal SessionRegistryService(RegistryServiceOptions options, SessionRegistry registry)
        {
            if (options == null) throw new ArgumentNullException("options");
            if (registry == null) throw new ArgumentNullException("registry");
            this.options = options;
            this.registry = registry;
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
                while (!shutdownRequested)
                {
                    using (var pipe = CreateControlPipe(options.ControlPipeName))
                    {
                        lock (sync) activeControlPipe = pipe;
                        try
                        {
                            pipe.WaitForConnection();
                            RegistryResponse response;
                            try
                            {
                                response = Handle(RegistryControlCodec.ReadRequest(pipe));
                            }
                            catch (InvalidDataException)
                            {
                                response = Response(false, "INVALID_REQUEST", 0);
                            }
                            catch (ArgumentException)
                            {
                                response = Response(false, "INVALID_REQUEST", 0);
                            }
                            catch (InvalidOperationException)
                            {
                                response = Response(false, "OPERATION_FAILED", 0);
                            }
                            catch (TimeoutException)
                            {
                                response = Response(false, "OPERATION_TIMEOUT", 0);
                            }
                            RegistryControlCodec.WriteResponse(pipe, response);
                            requests++;
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
                            lock (sync)
                            {
                                if (ReferenceEquals(activeControlPipe, pipe)) activeControlPipe = null;
                            }
                        }
                    }
                }
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
                        AffectedSessions = 0
                    };
                case RegistryCommand.StopSession:
                    var stopped = registry.StopSession(request.SessionId);
                    if (stopped.RequestedSessions == 0) return Response(false, "SESSION_NOT_FOUND", 0);
                    return StopResponse(stopped, "STOPPED", "STOP_TIMEOUT");
                case RegistryCommand.StopAllRemoteAccess:
                    var stoppedAll = registry.StopAllRemoteAccess();
                    return StopResponse(
                        stoppedAll,
                        "REMOTE_ACCESS_STOPPED",
                        "REMOTE_ACCESS_STOP_TIMEOUT");
                case RegistryCommand.EnableRemoteAccess:
                    registry.EnableRemoteAccess();
                    return Response(true, "REMOTE_ACCESS_ENABLED", 0);
                case RegistryCommand.Shutdown:
                    shutdownRequested = true;
                    var shutdown = registry.Shutdown();
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
                TimedOutSessions = 0
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
                TimedOutSessions = result.TimedOutSessions
            };
        }

        private static NamedPipeServerStream CreateControlPipe(string pipeName)
        {
            return CurrentUserPipe.Create(pipeName, 1, PipeOptions.None, 16 * 1024, 16 * 1024);
        }

        public void RequestShutdown()
        {
            NamedPipeServerStream pipe;
            shutdownRequested = true;
            lock (sync) pipe = activeControlPipe;
            if (pipe != null)
            {
                try { pipe.Dispose(); }
                catch (ObjectDisposedException) { }
            }
            registry.Dispose();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            RequestShutdown();
        }
    }

}
