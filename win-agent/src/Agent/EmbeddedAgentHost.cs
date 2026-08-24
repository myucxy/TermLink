using System;
using System.IO;
using System.Threading;

namespace TermLink.Agent
{
    /// <summary>
    /// Hosts the registry, session workers, REST synchronizer, and Relay runtime
    /// inside the interactive TermLink process. The same host is also used by
    /// the legacy Agent entry point, so both launch paths share the single-instance
    /// and shutdown semantics.
    /// </summary>
    public sealed class EmbeddedAgentHost : IDisposable
    {
        private readonly string controlPipeName;
        private readonly string bufferRoot;
        private readonly RegistryServiceOptions options;
        private readonly object sync = new object();
        private SessionRegistryService service;
        private Thread worker;
        private readonly ManualResetEventSlim startupCompleted = new ManualResetEventSlim(false);
        private Exception lastFailure;
        private string startupStage = "not started";
        private bool disposed;

        public EmbeddedAgentHost(string controlPipeName, string bufferRoot)
            : this(CreateOptions(controlPipeName, bufferRoot))
        {
        }

        internal EmbeddedAgentHost(RegistryServiceOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            var controlPipeName = options.ControlPipeName;
            var bufferRoot = options.BufferRoot;
            if (string.IsNullOrWhiteSpace(controlPipeName) || controlPipeName.Length > 200 ||
                controlPipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new ArgumentException("control pipe name is invalid", nameof(controlPipeName));
            }
            if (string.IsNullOrWhiteSpace(bufferRoot))
                throw new ArgumentException("buffer root is required", nameof(bufferRoot));
            this.controlPipeName = controlPipeName;
            this.bufferRoot = Path.GetFullPath(bufferRoot);
            this.options = options;
        }

        private static RegistryServiceOptions CreateOptions(string controlPipeName, string bufferRoot)
        {
            return RegistryServiceOptions.Parse(new[]
            {
                "--registry",
                "--control-pipe", controlPipeName,
                "--buffer-root", bufferRoot
            });
        }

        public bool IsRunning
        {
            get
            {
                lock (sync) return worker != null && worker.IsAlive;
            }
        }

        public Exception LastFailure
        {
            get
            {
                lock (sync) return lastFailure;
            }
        }

        public string StartupStage
        {
            get
            {
                lock (sync) return startupStage;
            }
        }

        /// <summary>
        /// Executes a UI control request directly against the registry hosted
        /// by this process. Named pipes remain available for SessionHost and
        /// external diagnostics, but the main window never waits on itself.
        /// </summary>
        public RegistryResponse Dispatch(RegistryRequest request)
        {
            SessionRegistryService activeService;
            lock (sync)
            {
                ThrowIfDisposed();
                activeService = service;
                if (activeService == null)
                {
                    if (lastFailure != null)
                        throw new InvalidOperationException(
                            "TermLink background runtime is unavailable: " + lastFailure.Message,
                            lastFailure);
                    throw new InvalidOperationException("TermLink background runtime is still starting.");
                }
            }
            return activeService.Dispatch(request);
        }

        /// <summary>
        /// Starts initialization without making the interactive window wait for
        /// DPAPI, session recovery, history discovery, or antivirus scanning.
        /// Callers that require a ready control surface can use Start(), which
        /// waits on the same single initialization attempt.
        /// </summary>
        public void BeginStart()
        {
            lock (sync)
            {
                ThrowIfDisposed();
                if (worker != null && worker.IsAlive) return;
                ReleaseCompletedHostLocked();
                lastFailure = null;
                startupStage = "starting worker";
                startupCompleted.Reset();
                worker = new Thread(RunBackground)
                {
                    IsBackground = true,
                    Name = "TermLink Background Runtime"
                };
                worker.Start();
            }
        }

        public void Start()
        {
            BeginStart();
            if (!startupCompleted.Wait(TimeSpan.FromSeconds(60)))
            {
                throw new TimeoutException(
                    "TermLink background runtime is still initializing after 60 seconds " +
                    "(stage: " + StartupStage + "). The window remains available and will retry automatically.");
            }
            var failure = LastFailure;
            if (failure != null) throw new InvalidOperationException(
                "TermLink background runtime could not start: " + failure.Message,
                failure);
        }

        public bool Stop(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1))
                throw new ArgumentOutOfRangeException(nameof(timeout));
            Thread activeWorker;
            SessionRegistryService activeService;
            lock (sync)
            {
                activeWorker = worker;
                activeService = service;
            }
            if (activeWorker == null) return true;
            activeService?.RequestShutdown();
            var stopped = activeWorker.Join(timeout);
            if (stopped)
            {
                lock (sync) ReleaseCompletedHostLocked();
            }
            return stopped;
        }

        public void Restart(TimeSpan timeout)
        {
            if (!Stop(timeout))
                throw new TimeoutException("TermLink background runtime did not stop in time.");
            Start();
        }

        private void RunBackground()
        {
            Mutex mutex = null;
            var ownsInstance = false;
            try
            {
                SetStartupStage("claiming the local runtime instance");
                mutex = new Mutex(false, "Local\\" + controlPipeName + "-agent");
                try { ownsInstance = mutex.WaitOne(0, false); }
                catch (AbandonedMutexException) { ownsInstance = true; }
                if (!ownsInstance)
                    throw new InvalidOperationException(
                        "Another TermLink background runtime already owns this account control pipe.");

                SetStartupStage("loading local account, session, and history state");
                var candidate = new SessionRegistryService(options);
                lock (sync)
                {
                    service = candidate;
                    startupStage = "ready";
                }
                SignalStartupCompleted();
                candidate.Run();
            }
            catch (Exception error)
            {
                lock (sync)
                {
                    lastFailure = error;
                    startupStage = "failed: " + error.GetType().Name;
                }
                SignalStartupCompleted();
            }
            finally
            {
                SessionRegistryService completed;
                lock (sync)
                {
                    completed = service;
                    service = null;
                }
                if (completed != null)
                {
                    try
                    {
                        completed.Dispose();
                    }
                    catch (Exception error)
                    {
                        // Shutdown is a terminal boundary for the embedded
                        // runtime. A Relay/account failure observed while the
                        // production service is disposing must remain a
                        // diagnostic; letting it escape this background thread
                        // would terminate the unified desktop process during an
                        // otherwise normal tray exit.
                        lock (sync)
                        {
                            if (lastFailure == null) lastFailure = error;
                            startupStage = "stopped with cleanup error: " + error.GetType().Name;
                        }
                    }
                }
                if (ownsInstance)
                {
                    try { mutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                }
                mutex?.Dispose();
            }
        }

        private void SetStartupStage(string value)
        {
            lock (sync) startupStage = value;
        }

        private void SignalStartupCompleted()
        {
            try { startupCompleted.Set(); }
            catch (ObjectDisposedException)
            {
                // Process shutdown may dispose a host whose slow disk/DPAPI
                // initialization is just completing. The process is already
                // exiting, so there is no waiter left to notify.
            }
        }

        private void ReleaseCompletedHostLocked()
        {
            if (worker != null && worker.IsAlive) return;
            service = null;
            worker = null;
        }

        private void ThrowIfDisposed()
        {
            if (disposed) throw new ObjectDisposedException(nameof(EmbeddedAgentHost));
        }

        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
            }
            Stop(TimeSpan.FromSeconds(15));
            lock (sync) ReleaseCompletedHostLocked();
            startupCompleted.Dispose();
        }
    }
}
