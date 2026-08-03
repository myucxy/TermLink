using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;

namespace TermLink.Agent
{
    internal static class RegistryValidationProgram
    {
        private static int mainThreadId;

        public static int Main(string[] args)
        {
            if (args.Length == 5 && args[0] == "--fake-lost-host")
            {
                return RunFakeLostHost(args[1], Guid.Parse(args[2]), Convert.FromBase64String(args[3]), int.Parse(args[4]));
            }
            if (args.Length != 3)
            {
                Console.Error.WriteLine(
                    "Usage: SessionRegistryValidation <agent.exe> <session-host.exe> <buffer-root>");
                return 2;
            }

            mainThreadId = Thread.CurrentThread.ManagedThreadId;
            var hosts = new List<HostFixture>();
            SessionRegistryService service = null;
            Task serviceTask = null;
            try
            {
                var agentExecutable = Path.GetFullPath(args[0]);
                var hostExecutable = Path.GetFullPath(args[1]);
                var bufferRoot = Path.GetFullPath(args[2]);
                Directory.CreateDirectory(bufferRoot);
                ValidateActualAgentEntry(agentExecutable, Path.Combine(bufferRoot, "external-entry"));
                ValidateStopFailurePaths(Path.Combine(bufferRoot, "failure-paths"));
                ValidateRestartFailurePath(Path.Combine(bufferRoot, "restart-failure"));
                var controlPipe = "termlink-registry-control-" + Guid.NewGuid().ToString("N");
                var options = RegistryServiceOptions.Parse(new[]
                {
                    "--registry",
                    "--control-pipe", controlPipe,
                    "--buffer-root", bufferRoot,
                    "--host-disconnect-timeout-ms", "1200",
                    "--max-buffer-bytes", (4 * 1024 * 1024).ToString(),
                    "--max-sessions", "20"
                });
                service = new SessionRegistryService(options);
                serviceTask = Task.Factory.StartNew(
                    new Func<int>(service.Run),
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default);

                var first = HostFixture.Start(hostExecutable, "single-stop", 31);
                var abnormal = HostFixture.Start(hostExecutable, "abnormal-host", 32);
                var concurrent = HostFixture.Start(hostExecutable, "concurrent-stop", 33);
                hosts.Add(first);
                hosts.Add(abnormal);
                hosts.Add(concurrent);

                Register(controlPipe, first, ManagedShellType.PowerShell7);
                Register(controlPipe, abnormal, ManagedShellType.CommandPrompt);
                Register(controlPipe, concurrent, ManagedShellType.Wsl);
                WaitForSessions(controlPipe, new[] { first.SessionId, abnormal.SessionId, concurrent.SessionId }, 10000);

                var summaries = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List }).Summaries;
                Require(summaries.Count == 3, "Registry did not expose all three concurrent sessions.");
                Require(summaries.All(item => item.Lifecycle == SessionAgentLifecycle.Connected),
                    "A registered Host did not reach Connected state.");
                Require(summaries.All(item => item.LastSequence >= 2),
                    "A connected Host did not publish lifecycle and resize events.");
                AssertSummaryRedaction(summaries, hosts, bufferRoot);

                var stopFirst = Send(controlPipe, new RegistryRequest
                {
                    Command = RegistryCommand.StopSession,
                    SessionId = first.SessionId
                });
                Require(stopFirst.Success && stopFirst.AffectedSessions == 1, "Single-session stop failed.");
                Require(!first.Process.HasExited, "Stopping remote access terminated the local Shell Host.");
                first.CompleteLocally();

                abnormal.Process.Kill();
                abnormal.Process.WaitForExit();
                WaitUntil(delegate
                {
                    return !ContainsSession(
                        Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List }).Summaries,
                        abnormal.SessionId);
                }, 10000, "Abnormal Host remained in Registry state.");

                var lost = LostHostFixture.Start();
                try
                {
                    var lostRegistered = Send(controlPipe, new RegistryRequest
                    {
                        Command = RegistryCommand.Register,
                        Registration = lost.CreateRegistration()
                    });
                    Require(lostRegistered.Success, "Lost-Host fixture registration failed.");
                    WaitUntil(delegate
                    {
                        return ContainsSession(
                            Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List }).Summaries,
                            lost.SessionId);
                    }, 5000, "Lost-Host fixture never entered Registry state.");
                    WaitUntil(delegate
                    {
                        return !ContainsSession(
                            Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List }).Summaries,
                            lost.SessionId);
                    }, 10000, "Disconnected live Host remained in Registry state.");
                    Require(!lost.Process.HasExited, "Registry killed the disconnected but still-live Host process.");
                }
                finally
                {
                    lost.Dispose();
                }

                var concurrentStops = new Task<RegistryResponse>[8];
                for (var index = 0; index < concurrentStops.Length; index++)
                {
                    concurrentStops[index] = Task.Factory.StartNew(delegate
                    {
                        return Send(controlPipe, new RegistryRequest
                        {
                            Command = RegistryCommand.StopSession,
                            SessionId = concurrent.SessionId
                        });
                    });
                }
                Task.WaitAll(concurrentStops);
                Require(concurrentStops.Count(task => task.Result.Success) == 1,
                    "Concurrent duplicate stop did not have exactly one winner.");
                Require(!concurrent.Process.HasExited, "Concurrent remote stop terminated the local Shell Host.");
                concurrent.CompleteLocally();

                var stopAllOne = HostFixture.Start(hostExecutable, "stop-all-one", 34);
                var stopAllTwo = HostFixture.Start(hostExecutable, "stop-all-two", 35);
                hosts.Add(stopAllOne);
                hosts.Add(stopAllTwo);
                Register(controlPipe, stopAllOne, ManagedShellType.WindowsPowerShell);
                Register(controlPipe, stopAllTwo, ManagedShellType.Custom);
                WaitForSessions(controlPipe, new[] { stopAllOne.SessionId, stopAllTwo.SessionId }, 10000);
                var stopAll = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.StopAllRemoteAccess });
                Require(stopAll.Success && stopAll.AffectedSessions == 2, "Global remote stop did not stop both sessions.");
                Require(!stopAll.RemoteAccessEnabled, "Global remote stop did not disable future registrations.");
                Require(!stopAllOne.Process.HasExited && !stopAllTwo.Process.HasExited,
                    "Global remote stop terminated a local Shell Host.");
                var disabledRegistration = HostFixture.CreateRegistrationForExistingProcess(stopAllOne.Process.Id);
                var disabled = Send(controlPipe, new RegistryRequest
                {
                    Command = RegistryCommand.Register,
                    Registration = disabledRegistration
                });
                Require(!disabled.Success && disabled.Code == "REMOTE_ACCESS_DISABLED",
                    "Registry accepted a new session while remote access was stopped.");
                stopAllOne.CompleteLocally();
                stopAllTwo.CompleteLocally();

                var enabled = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.EnableRemoteAccess });
                Require(enabled.Success && enabled.RemoteAccessEnabled, "Remote access could not be explicitly re-enabled.");

                var closeOne = HostFixture.Start(hostExecutable, "registry-close-one", 36);
                var closeTwo = HostFixture.Start(hostExecutable, "registry-close-two", 37);
                hosts.Add(closeOne);
                hosts.Add(closeTwo);
                Register(controlPipe, closeOne, ManagedShellType.PowerShell7);
                Register(controlPipe, closeTwo, ManagedShellType.CommandPrompt);
                WaitForSessions(controlPipe, new[] { closeOne.SessionId, closeTwo.SessionId }, 10000);
                var shutdown = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.Shutdown });
                Require(shutdown.Success && shutdown.Code == "REGISTRY_SHUTDOWN", "Registry shutdown command failed.");
                Require(serviceTask.Wait(10000), "Registry service did not stop after shutdown.");
                Require(!closeOne.Process.HasExited && !closeTwo.Process.HasExited,
                    "Registry shutdown terminated a local Shell Host.");
                closeOne.CompleteLocally();
                closeTwo.CompleteLocally();

                Console.WriteLine(
                    "__REGISTRY_TEST_RESULT__:multi_sessions=3" +
                    ";summary_redacted=true" +
                    ";single_stop_local_alive=true" +
                    ";concurrent_stop_winners=1" +
                    ";abnormal_reclaimed=true" +
                    ";lost_host_reclaimed=true" +
                    ";stop_all_sessions=2" +
                    ";registration_blocked=true" +
                    ";registry_close_sessions=2" +
                    ";local_shells_survived=6" +
                    ";stop_timeout_stable=true" +
                    ";batch_timeout_reported=true" +
                    ";shutdown_best_effort=true" +
                    ";restart_timeout_reported=true" +
                    ";external_registry_entry=true" +
                    ";test_thread=" + mainThreadId);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
            finally
            {
                if (service != null) service.Dispose();
                foreach (var host in hosts) host.Dispose();
            }
        }

        private static void Register(string controlPipe, HostFixture host, ManagedShellType shellType)
        {
            var response = Send(controlPipe, new RegistryRequest
            {
                Command = RegistryCommand.Register,
                Registration = host.CreateRegistration(shellType)
            });
            Require(response.Success && response.Code == "REGISTERED",
                "Registry rejected session " + host.SessionId + ": " + response.Code);
        }

        private static void ValidateActualAgentEntry(string agentExecutable, string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var controlPipe = "termlink-registry-external-" + Guid.NewGuid().ToString("N");
            Process process = null;
            try
            {
                var start = new ProcessStartInfo
                {
                    FileName = agentExecutable,
                    Arguments =
                        "--registry --control-pipe " + controlPipe +
                        " --buffer-root \"" + bufferRoot + "\" --diagnostics",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false),
                    StandardErrorEncoding = new UTF8Encoding(false)
                };
                process = Process.Start(start);
                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                var list = RegistryControlClient.Send(
                    controlPipe,
                    new RegistryRequest { Command = RegistryCommand.List },
                    10000);
                Require(list.Success && list.Code == "OK" && list.Summaries.Count == 0,
                    "External Registry entry did not serve an empty list.");
                var shutdown = RegistryControlClient.Send(
                    controlPipe,
                    new RegistryRequest { Command = RegistryCommand.Shutdown },
                    10000);
                Require(shutdown.Success && shutdown.Code == "REGISTRY_SHUTDOWN",
                    "External Registry entry did not accept graceful shutdown.");
                Require(process.WaitForExit(10000), "External Registry process did not exit after shutdown.");
                var output = outputTask.GetAwaiter().GetResult();
                var error = errorTask.GetAwaiter().GetResult();
                Require(process.ExitCode == 0,
                    "External Registry process returned " + process.ExitCode + ": " + error);
                Require(output.IndexOf("__REGISTRY_RESULT__:", StringComparison.Ordinal) >= 0,
                    "External Registry diagnostics were not emitted.");
            }
            finally
            {
                if (process != null && !process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit();
                }
                if (process != null) process.Dispose();
            }
        }

        private static void ValidateStopFailurePaths(string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var controlPipe = "termlink-registry-failure-" + Guid.NewGuid().ToString("N");
            var options = RegistryServiceOptions.Parse(new[]
            {
                "--registry",
                "--control-pipe", controlPipe,
                "--buffer-root", bufferRoot,
                "--host-disconnect-timeout-ms", "1200"
            });
            var factory = new FakeManagedSessionFactory();
            var registry = new SessionRegistry(factory, 20);
            var service = new SessionRegistryService(options, registry);
            var serviceTask = Task.Factory.StartNew(
                new Func<int>(service.Run),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            try
            {
                var single = Guid.NewGuid();
                factory.SetWaitsBeforeSuccess(single, 2);
                RegisterFake(controlPipe, single);
                var singleStop = Send(controlPipe, new RegistryRequest
                {
                    Command = RegistryCommand.StopSession,
                    SessionId = single
                });
                Require(!singleStop.Success && singleStop.Code == "STOP_TIMEOUT" &&
                    singleStop.AffectedSessions == 1 && singleStop.TimedOutSessions == 1,
                    "Single-session timeout did not return its stable structured result.");
                var afterSingle = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List });
                Require(afterSingle.Success && afterSingle.Code == "OK",
                    "Registry control loop stopped after a single-session timeout.");
                var duplicateStopping = Send(controlPipe, new RegistryRequest
                {
                    Command = RegistryCommand.Register,
                    Registration = CreateFakeRegistration(single)
                });
                Require(!duplicateStopping.Success && duplicateStopping.Code == "SESSION_STOPPING",
                    "Registry accepted a duplicate ID while the old worker was still retiring.");

                var batchSlow = Guid.NewGuid();
                var batchFast = Guid.NewGuid();
                factory.SetWaitsBeforeSuccess(batchSlow, 2);
                factory.SetWaitsBeforeSuccess(batchFast, 1);
                RegisterFake(controlPipe, batchSlow);
                RegisterFake(controlPipe, batchFast);
                var batch = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.StopAllRemoteAccess });
                Require(!batch.Success && batch.Code == "REMOTE_ACCESS_STOP_TIMEOUT" &&
                    batch.AffectedSessions == 2 && batch.TimedOutSessions == 1,
                    "Batch timeout result did not report all requested and timed-out workers.");
                Require(factory.Get(batchSlow).WaitCalls == 1 && factory.Get(batchFast).WaitCalls == 1,
                    "Batch stop skipped a worker after another worker timed out.");
                var enabled = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.EnableRemoteAccess });
                Require(enabled.Success, "Registry did not continue after a batch stop timeout.");

                var shutdownSlow = Guid.NewGuid();
                var shutdownFast = Guid.NewGuid();
                factory.SetWaitsBeforeSuccess(shutdownSlow, 2);
                factory.SetWaitsBeforeSuccess(shutdownFast, 1);
                RegisterFake(controlPipe, shutdownSlow);
                RegisterFake(controlPipe, shutdownFast);
                var shutdown = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.Shutdown });
                Require(!shutdown.Success && shutdown.Code == "REGISTRY_SHUTDOWN_WITH_TIMEOUTS" &&
                    shutdown.TimedOutSessions == 1,
                    "Registry shutdown did not return a structured timeout result.");
                Require(serviceTask.Wait(10000), "Registry service did not exit after structured shutdown failure.");
                Require(factory.Get(shutdownFast).WaitCalls == 1,
                    "Registry shutdown skipped a worker after another worker timed out.");
            }
            finally
            {
                service.Dispose();
            }

            Require(factory.AllSessions.All(item => item.WaitCalls >= item.WaitsBeforeSuccess),
                "Registry Dispose did not retry every retiring worker during best-effort cleanup.");
        }

        private static void ValidateRestartFailurePath(string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var controlPipe = "termlink-registry-restart-failure-" + Guid.NewGuid().ToString("N");
            var options = RegistryServiceOptions.Parse(new[]
            {
                "--registry",
                "--control-pipe", controlPipe,
                "--buffer-root", bufferRoot,
                "--host-disconnect-timeout-ms", "1200"
            });
            var factory = new FakeManagedSessionFactory();
            var registry = new SessionRegistry(factory, 20);
            var service = new SessionRegistryService(options, registry);
            var serviceTask = Task.Factory.StartNew(
                new Func<int>(service.Run),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);
            var slow = Guid.NewGuid();
            var fast = Guid.NewGuid();
            try
            {
                factory.SetWaitsBeforeSuccess(slow, 2);
                factory.SetWaitsBeforeSuccess(fast, 1);
                RegisterFake(controlPipe, slow);
                RegisterFake(controlPipe, fast);
                var restart = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.RestartAgent });
                Require(!restart.Success && restart.Code == "AGENT_RESTART_WITH_TIMEOUTS" &&
                    restart.AffectedSessions == 2 && restart.TimedOutSessions == 1,
                    "Controlled restart timeout did not return its stable structured result.");
                Require(serviceTask.Wait(10000), "Registry service did not exit after restart timeout.");
                Require(factory.Get(slow).WaitCalls == 1 && factory.Get(fast).WaitCalls == 1,
                    "Controlled restart skipped a worker after another worker timed out.");
            }
            finally
            {
                service.Dispose();
            }
            Require(factory.AllSessions.All(item => item.WaitCalls >= item.WaitsBeforeSuccess),
                "Registry Dispose did not retry a worker after restart timeout.");
        }

        private static void RegisterFake(string controlPipe, Guid sessionId)
        {
            var response = Send(controlPipe, new RegistryRequest
            {
                Command = RegistryCommand.Register,
                Registration = CreateFakeRegistration(sessionId)
            });
            Require(response.Success, "Fake worker registration failed: " + response.Code);
        }

        private static SessionRegistration CreateFakeRegistration(Guid sessionId)
        {
            var token = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            return new SessionRegistration(
                sessionId,
                "termlink-fake-worker-" + sessionId.ToString("N"),
                token,
                Process.GetCurrentProcess().Id,
                ManagedShellType.Custom);
        }

        private static int RunFakeLostHost(string pipeName, Guid sessionId, byte[] token, int holdMilliseconds)
        {
            try
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None))
                {
                    pipe.Connect(10000);
                    LocalFrameCodec.Write(pipe, new LocalFrame(
                        LocalFrameType.HostHello,
                        sessionId,
                        0,
                        LocalPayloadCodec.Hello(Process.GetCurrentProcess().Id, token)));
                    var response = LocalFrameCodec.Read(pipe);
                    Require(response != null && response.Type == LocalFrameType.AgentHello &&
                        response.SessionId == sessionId && response.Sequence == 0,
                        "Fake Host received an invalid Agent handshake.");
                    LocalPayloadCodec.ParseHello(response.Payload, token);
                    LocalFrameCodec.Write(pipe, new LocalFrame(
                        LocalFrameType.Lifecycle,
                        sessionId,
                        1,
                        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0)));
                    Require(LocalFrameCodec.Read(pipe).Type == LocalFrameType.Ack, "Fake Host lifecycle was not ACKed.");
                    LocalFrameCodec.Write(pipe, new LocalFrame(
                        LocalFrameType.Resize,
                        sessionId,
                        2,
                        LocalPayloadCodec.Resize(80, 25)));
                    Require(LocalFrameCodec.Read(pipe).Type == LocalFrameType.Ack, "Fake Host resize was not ACKed.");
                }
                Console.WriteLine("__FAKE_HOST_DISCONNECTED__");
                Thread.Sleep(holdMilliseconds);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
        }

        private static RegistryResponse Send(string pipe, RegistryRequest request)
        {
            return RegistryControlClient.Send(pipe, request, 10000);
        }

        private static void WaitForSessions(string pipe, Guid[] expected, int timeoutMilliseconds)
        {
            WaitUntil(delegate
            {
                var summaries = Send(pipe, new RegistryRequest { Command = RegistryCommand.List }).Summaries;
                return expected.All(id => summaries.Any(item =>
                    item.SessionId == id && item.Lifecycle == SessionAgentLifecycle.Connected &&
                    item.LastSequence >= 2));
            }, timeoutMilliseconds, "Registered sessions did not all become connected.");
        }

        private static void AssertSummaryRedaction(
            IList<SessionSummary> summaries,
            IList<HostFixture> hosts,
            string bufferRoot)
        {
            var text = string.Join("\n", summaries.Select(item => item.ToDiagnosticRecord()).ToArray());
            Require(text.IndexOf(bufferRoot, StringComparison.OrdinalIgnoreCase) < 0,
                "Session summary exposed the encrypted buffer path.");
            foreach (var host in hosts)
            {
                Require(text.IndexOf(host.PipeName, StringComparison.Ordinal) < 0,
                    "Session summary exposed a per-session pipe name.");
                Require(text.IndexOf(Convert.ToBase64String(host.Token), StringComparison.Ordinal) < 0,
                    "Session summary exposed a handshake token.");
                Require(text.IndexOf(host.SecretMarker, StringComparison.Ordinal) < 0,
                    "Session summary exposed terminal content.");
            }
        }

        private static bool ContainsSession(IList<SessionSummary> summaries, Guid sessionId)
        {
            return summaries.Any(item => item.SessionId == sessionId);
        }

        private static void WaitUntil(Func<bool> condition, int timeoutMilliseconds, string failure)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMilliseconds);
            Exception last = null;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    if (condition()) return;
                    last = null;
                }
                catch (Exception error)
                {
                    last = error;
                }
                Thread.Sleep(50);
            }
            if (last != null) throw new InvalidOperationException(failure, last);
            throw new InvalidOperationException(failure);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    internal sealed class FakeManagedSessionFactory : IRegistryManagedSessionFactory
    {
        private readonly object sync = new object();
        private readonly Dictionary<Guid, int> waitsBeforeSuccess = new Dictionary<Guid, int>();
        private readonly Dictionary<Guid, FakeManagedSession> sessions = new Dictionary<Guid, FakeManagedSession>();

        public IEnumerable<FakeManagedSession> AllSessions
        {
            get { lock (sync) return sessions.Values.ToArray(); }
        }

        public void SetWaitsBeforeSuccess(Guid sessionId, int waits)
        {
            lock (sync) waitsBeforeSuccess[sessionId] = waits;
        }

        public FakeManagedSession Get(Guid sessionId)
        {
            lock (sync) return sessions[sessionId];
        }

        public IRegistryManagedSession Create(
            SessionRegistration registration,
            Action<IRegistryManagedSession> completion)
        {
            lock (sync)
            {
                int waits;
                if (!waitsBeforeSuccess.TryGetValue(registration.SessionId, out waits)) waits = 1;
                var session = new FakeManagedSession(registration, waits, completion);
                sessions.Add(registration.SessionId, session);
                return session;
            }
        }
    }

    internal sealed class FakeManagedSession : IRegistryManagedSession
    {
        private readonly Action<IRegistryManagedSession> completion;
        private int waitCalls;
        private int completionSent;

        public FakeManagedSession(
            SessionRegistration registration,
            int waitsBeforeSuccess,
            Action<IRegistryManagedSession> completion)
        {
            SessionId = registration.SessionId;
            WaitsBeforeSuccess = waitsBeforeSuccess;
            this.completion = completion;
        }

        public Guid SessionId { get; private set; }
        public int WaitsBeforeSuccess { get; private set; }
        public int WaitCalls { get { return Volatile.Read(ref waitCalls); } }

        public void Start()
        {
        }

        public SessionSummary GetSummary()
        {
            return new SessionSummary
            {
                SessionId = SessionId,
                ShellType = ManagedShellType.Custom,
                Lifecycle = SessionAgentLifecycle.Connected,
                HostProcessId = Process.GetCurrentProcess().Id,
                LastSequence = 2,
                RegisteredAtUtc = DateTime.UtcNow,
                LastActivityUtc = DateTime.UtcNow
            };
        }

        public void BeginStop()
        {
        }

        public bool WaitForStop()
        {
            var calls = Interlocked.Increment(ref waitCalls);
            if (calls < WaitsBeforeSuccess) return false;
            if (Interlocked.Exchange(ref completionSent, 1) == 0) completion(this);
            return true;
        }

        public void Dispose()
        {
            BeginStop();
            WaitForStop();
        }
    }

    internal sealed class LostHostFixture : IDisposable
    {
        private LostHostFixture(Process process, Guid sessionId, string pipeName, byte[] token)
        {
            Process = process;
            SessionId = sessionId;
            PipeName = pipeName;
            Token = token;
        }

        public Process Process { get; private set; }
        public Guid SessionId { get; private set; }
        public string PipeName { get; private set; }
        public byte[] Token { get; private set; }

        public static LostHostFixture Start()
        {
            var sessionId = Guid.NewGuid();
            var pipeName = "termlink-registry-lost-" + sessionId.ToString("N");
            var token = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            var start = new ProcessStartInfo
            {
                FileName = Process.GetCurrentProcess().MainModule.FileName,
                Arguments =
                    "--fake-lost-host " + pipeName + " " + sessionId.ToString("D") + " " +
                    Convert.ToBase64String(token) + " 10000",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            return new LostHostFixture(System.Diagnostics.Process.Start(start), sessionId, pipeName, token);
        }

        public SessionRegistration CreateRegistration()
        {
            return new SessionRegistration(SessionId, PipeName, Token, Process.Id, ManagedShellType.Custom);
        }

        public void Dispose()
        {
            if (Process == null) return;
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit();
            }
            Process.Dispose();
            Process = null;
        }
    }

    internal sealed class HostFixture : IDisposable
    {
        private bool completed;

        private HostFixture(Process process, Guid sessionId, string pipeName, byte[] token, string marker, int exitCode)
        {
            Process = process;
            SessionId = sessionId;
            PipeName = pipeName;
            Token = token;
            SecretMarker = marker;
            ExitCode = exitCode;
        }

        public Process Process { get; private set; }
        public Guid SessionId { get; private set; }
        public string PipeName { get; private set; }
        public byte[] Token { get; private set; }
        public string SecretMarker { get; private set; }
        public int ExitCode { get; private set; }

        public static HostFixture Start(string hostExecutable, string label, int exitCode)
        {
            var sessionId = Guid.NewGuid();
            var pipe = "termlink-registry-session-" + sessionId.ToString("N");
            var token = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            var marker = "SECRET_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var script =
                "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" +
                "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false);" +
                "[Console]::WriteLine('" + marker + "_READY');" +
                "$v=[Console]::ReadLine();" +
                "[Console]::WriteLine('" + marker + "_LOCAL:'+$v);" +
                "exit " + exitCode;
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var command = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encodedScript;
            var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
            var start = new ProcessStartInfo
            {
                FileName = hostExecutable,
                Arguments =
                    "--command-base64 " + encodedCommand +
                    " --timeout-ms 30000" +
                    " --agent-pipe " + pipe +
                    " --session-id " + sessionId.ToString("D") +
                    " --handshake-token " + Convert.ToBase64String(token),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            var process = Process.Start(start);
            return new HostFixture(process, sessionId, pipe, token, marker, exitCode);
        }

        public SessionRegistration CreateRegistration(ManagedShellType shellType)
        {
            return new SessionRegistration(SessionId, PipeName, Token, Process.Id, shellType);
        }

        public static SessionRegistration CreateRegistrationForExistingProcess(int processId)
        {
            var token = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            return new SessionRegistration(
                Guid.NewGuid(),
                "termlink-disabled-registration-" + Guid.NewGuid().ToString("N"),
                token,
                processId,
                ManagedShellType.Custom);
        }

        public void CompleteLocally()
        {
            if (completed) return;
            completed = true;
            if (Process.HasExited) throw new InvalidOperationException("Local Shell Host exited before local completion.");
            Process.StandardInput.WriteLine("local-user-input");
            Process.StandardInput.Flush();
            Process.StandardInput.Close();
            if (!Process.WaitForExit(10000)) throw new TimeoutException("Local Shell Host did not finish.");
            var output = Process.StandardOutput.ReadToEnd();
            var error = Process.StandardError.ReadToEnd();
            if (Process.ExitCode != ExitCode)
            {
                throw new InvalidOperationException(
                    "Local Shell Host returned " + Process.ExitCode + ", expected " + ExitCode + ": " + error);
            }
            if (output.IndexOf(SecretMarker + "_LOCAL:local-user-input", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "Local Shell did not receive input after remote access stopped. output=" +
                    output.Replace("\r", "<CR>").Replace("\n", "<LF>") + "; error=" + error);
            }
        }

        public void Dispose()
        {
            if (Process == null) return;
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit();
            }
            Process.Dispose();
            Process = null;
        }
    }
}
