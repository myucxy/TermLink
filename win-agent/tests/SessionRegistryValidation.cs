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
                ValidateLocalIpcCapabilities();
                ValidateBoundedLargeBuffer(Path.Combine(bufferRoot, "bounded-large-buffer"));
                ValidateRemoteControlDispatch();
                ValidateActiveCapacityAccounting();
                ValidateCompletedHistoryCapacity(
                    hostExecutable,
                    Path.Combine(bufferRoot, "completed-history-capacity"));
                ValidateCompletedBufferRecovery(Path.Combine(bufferRoot, "completed-recovery"));
                ValidateLiveInterruptedLifecycle(Path.Combine(bufferRoot, "live-interrupted"));
                ValidateControlledInterruptedLifecycle(Path.Combine(bufferRoot, "controlled-interrupted"));
                ValidateRestartPreservesWithoutInterruption(Path.Combine(bufferRoot, "restart-preserves"));
                ValidateRemoteAccessPolicyPersistence(Path.Combine(bufferRoot, "remote-policy"));
                ValidatePolicyPersistenceFailure();
                ValidateStopFailurePaths(Path.Combine(bufferRoot, "failure-paths"));
                ValidateRestartFailurePath(Path.Combine(bufferRoot, "restart-failure"));
                ValidateExplicitSessionTermination(
                    hostExecutable,
                    Path.Combine(bufferRoot, "explicit-termination"));
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
                var stoppedRetry = Send(controlPipe, new RegistryRequest
                {
                    Command = RegistryCommand.Register,
                    Registration = first.CreateRegistration(ManagedShellType.PowerShell7)
                });
                Require(!stoppedRetry.Success && stoppedRetry.Code == "SESSION_STOPPED",
                    "A still-running explicitly stopped Host re-registered after its worker retired.");
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

                var autoRegistered = HostFixture.Start(
                    hostExecutable,
                    "auto-register-stop",
                    38,
                    controlPipe);
                hosts.Add(autoRegistered);
                var autoSessionId = Guid.Empty;
                WaitUntil(delegate
                {
                    var summary = Send(
                        controlPipe,
                        new RegistryRequest { Command = RegistryCommand.List }).Summaries.FirstOrDefault(
                            item => item.HostProcessId == autoRegistered.Process.Id &&
                                item.Lifecycle == SessionAgentLifecycle.Connected &&
                                item.LastSequence >= 2);
                    if (summary == null) return false;
                    autoSessionId = summary.SessionId;
                    return true;
                }, 10000, "Managed SessionHost did not auto-register with the Registry.");
                autoRegistered.AdoptRegisteredSession(autoSessionId);
                var autoStop = Send(controlPipe, new RegistryRequest
                {
                    Command = RegistryCommand.StopSession,
                    SessionId = autoRegistered.SessionId
                });
                Require(autoStop.Success && !autoRegistered.Process.HasExited,
                    "Stopping an auto-registered Session terminated its local Shell.");
                Thread.Sleep(1600);
                Require(!ContainsSession(
                        Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List }).Summaries,
                        autoRegistered.SessionId),
                    "Auto-register loop restored a Session after explicit single-session stop.");
                autoRegistered.CompleteLocally();

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
                    ";single_stop_tombstoned=true" +
                    ";remote_policy_persisted=true" +
                    ";policy_corruption_fail_closed=true" +
                    ";concurrent_stop_winners=1" +
                    ";abnormal_reclaimed=true" +
                    ";lost_host_reclaimed=true" +
                    ";stop_all_sessions=2" +
                    ";registration_blocked=true" +
                    ";registry_close_sessions=2" +
                    ";local_shells_survived=7" +
                    ";auto_registration_stop_stable=true" +
                    ";stop_timeout_stable=true" +
                    ";batch_timeout_reported=true" +
                    ";shutdown_best_effort=true" +
                    ";restart_timeout_reported=true" +
                    ";active_capacity_bounded=true" +
                    ";retiring_capacity_bounded=true" +
                    ";completed_history_capacity_free=true" +
                    ";completed_history_recovery_ordered=true" +
                    ";completed_buffer_recovered=true" +
                    ";live_interrupted_committed=true" +
                    ";external_registry_entry=true" +
                    ";explicit_termination_exact=true" +
                    ";termination_identity_checked=true" +
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
                Require(!duplicateStopping.Success && duplicateStopping.Code == "SESSION_STOPPED",
                    "A timed-out stopped Session bypassed its durable Host tombstone.");

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

        private static void ValidateLocalIpcCapabilities()
        {
            Require(LocalPayloadCodec.ParseCapabilities(LocalPayloadCodec.Capabilities(true)),
                "Local IPC capabilities lost remote-input rejection support.");
            Require(!LocalPayloadCodec.ParseCapabilities(LocalPayloadCodec.Capabilities(false)),
                "Local IPC capabilities enabled an unsupported feature.");
            var invalidRejected = false;
            try { LocalPayloadCodec.ParseCapabilities(new byte[] { 1, 2 }); }
            catch (InvalidDataException) { invalidRejected = true; }
            Require(invalidRejected, "Local IPC capabilities accepted unknown feature bits.");
        }

        private static void ValidateBoundedLargeBuffer(string bufferRoot)
        {
            var sessionId = Guid.NewGuid();
            var directory = Path.Combine(bufferRoot, sessionId.ToString("N"));
            Directory.CreateDirectory(directory);
            const int frameCount = 2048;
            var payload = Enumerable.Repeat((byte)'x', 8193).ToArray();
            using (var buffer = new EncryptedEventBuffer(directory, sessionId, 64L * 1024 * 1024))
            {
                for (var sequence = 1; sequence <= frameCount; sequence++)
                {
                    buffer.Append(new LocalFrame(
                        LocalFrameType.Output,
                        sessionId,
                        (ulong)sequence,
                        payload));
                }

                ulong earliest;
                ulong latest;
                bool hasMore;
                var first = buffer.ReadFrom(
                    0,
                    16,
                    256 * 1024,
                    out earliest,
                    out latest,
                    out hasMore);
                Require(first.Count == 16 && first[0].Sequence == 1 && first[15].Sequence == 16 &&
                    earliest == 1 && latest == frameCount && hasMore,
                    "A >16 MiB/thousands-frame buffer was decrypted beyond its bounded replay page.");
                var tail = buffer.ReadFrom(
                    2040,
                    16,
                    256 * 1024,
                    out earliest,
                    out latest,
                    out hasMore);
                Require(tail.Count == 8 && tail[0].Sequence == 2041 && tail[7].Sequence == 2048 && !hasMore,
                    "Bounded replay did not finish the large buffer in linear S+1 order.");
            }
        }

        private static void ValidateRemoteControlDispatch()
        {
            var factory = new FakeManagedSessionFactory();
            using (var registry = new SessionRegistry(factory, 20))
            {
                var sessionId = Guid.NewGuid();
                string error;
                Require(registry.TryRegister(CreateFakeRegistration(sessionId), out error),
                    "Remote control fixture registration failed: " + error);
                Require(typeof(SessionRegistry).GetMethod("TrySendResize") == null,
                    "Registry still exposes an Android-to-ConPTY resize dispatch surface.");
                var session = factory.Get(sessionId);
                var inputId = Guid.NewGuid();
                var inputBytes = Encoding.UTF8.GetBytes("registry-remote-input");
                Require(registry.TrySendRemoteInput(sessionId, inputId, 17, inputBytes),
                    "Registry did not route the remote input request.");
                Require(session.RemoteInputId == inputId && session.RemoteInputEpoch == 17 &&
                    session.RemoteInputBytes.SequenceEqual(inputBytes),
                    "Registry changed the remote input request.");
                Require(registry.TrySendRemoteInputRejection(
                        sessionId,
                        inputId,
                        LocalRemoteInputResult.ControlLeaseRejected),
                    "Registry did not route the ordered remote input rejection.");
                Require(session.RejectedInputId == inputId &&
                    session.RejectedInputResult == LocalRemoteInputResult.ControlLeaseRejected,
                    "Registry changed the ordered remote input rejection.");
            }
        }

        private static void ValidateActiveCapacityAccounting()
        {
            var factory = new FakeManagedSessionFactory();
            using (var registry = new SessionRegistry(factory, 1))
            {
                var firstId = Guid.NewGuid();
                var nextRegistration = CreateFakeRegistration(Guid.NewGuid());
                factory.SetWaitsBeforeSuccess(firstId, 2);
                string error;
                Require(registry.TryRegister(CreateFakeRegistration(firstId), out error),
                    "Active-capacity fixture registration failed: " + error);
                Require(!registry.TryRegister(nextRegistration, out error) &&
                    error == "SESSION_LIMIT_REACHED",
                    "Registry did not enforce its active Session limit.");

                var stopped = registry.StopSession(firstId);
                Require(!stopped.Success && stopped.TimedOutSessions == 1 && registry.Count == 0,
                    "Retiring-capacity fixture did not enter its timed-out retiring state.");
                Require(!registry.TryRegister(nextRegistration, out error) &&
                    error == "SESSION_LIMIT_REACHED",
                    "A retiring worker stopped consuming an active Session slot too early.");

                Require(factory.Get(firstId).WaitForStop(),
                    "Retiring-capacity fixture could not finish its deferred stop.");
                Require(registry.TryRegister(nextRegistration, out error),
                    "A completed retiring worker did not release its active Session slot: " + error);
            }
        }

        private static void ValidateCompletedHistoryCapacity(string hostExecutable, string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var store = new DpapiRegistryRegistrationStore(
                Path.Combine(bufferRoot, "registrations.dpapi"));
            var completedIds = new List<Guid>();
            for (var index = 0; index < 3; index++)
            {
                completedIds.Add(SeedRecoverableCompletedHistory(bufferRoot, store, index));
            }

            HostFixture recoveredHost = null;
            HostFixture replacementHost = null;
            try
            {
                recoveredHost = HostFixture.Start(hostExecutable, "capacity-recovered-active", 41);
                store.Upsert(recoveredHost.CreateRegistration(ManagedShellType.PowerShell7));

                using (var registry = new SessionRegistry(
                    bufferRoot,
                    1200,
                    4 * 1024 * 1024,
                    1,
                    delegate(ManagedShellType ignoredShell, LocalFrame ignoredFrame) { }))
                {
                    WaitUntil(delegate
                    {
                        return registry.GetSummaries().Any(item =>
                            item.SessionId == recoveredHost.SessionId &&
                            item.Lifecycle == SessionAgentLifecycle.Connected &&
                            item.LastSequence >= 2);
                    }, 10000, "Completed history prevented a live Session from recovering at startup.");
                    Require(registry.RecoveredSessionCount == completedIds.Count + 1 &&
                        registry.Count == 1 && completedIds.All(registry.ContainsSession),
                        "Registry did not recover completed history and the live Session independently.");

                    IList<LocalFrame> frames;
                    ulong earliest;
                    ulong latest;
                    Require(registry.TryReadBufferedEvents(
                            completedIds[0], 0, out frames, out earliest, out latest) &&
                        frames.Count == 4 && earliest == 1 && latest == 4,
                        "Capacity accounting made retained completed history unreadable.");

                    string error;
                    Require(!registry.TryRegister(CreateFakeRegistration(Guid.NewGuid()), out error) &&
                        error == "SESSION_LIMIT_REACHED",
                        "Recovered live Session did not consume the configured active slot.");

                    recoveredHost.CompleteLocally();
                    WaitUntil(delegate
                    {
                        return registry.Count == 0 && registry.ContainsSession(recoveredHost.SessionId);
                    }, 10000, "Completed live Session did not move into retained history.");

                    replacementHost = HostFixture.Start(hostExecutable, "capacity-replacement-active", 42);
                    Require(registry.TryRegister(
                            replacementHost.CreateRegistration(ManagedShellType.CommandPrompt), out error),
                        "Completed history consumed the newly available active slot: " + error);
                    WaitUntil(delegate
                    {
                        return registry.GetSummaries().Any(item =>
                            item.SessionId == replacementHost.SessionId &&
                            item.Lifecycle == SessionAgentLifecycle.Connected &&
                            item.LastSequence >= 2);
                    }, 10000, "Replacement Session did not connect after completed backlog recovery.");
                    replacementHost.CompleteLocally();
                    WaitUntil(delegate { return registry.Count == 0; }, 10000,
                        "Replacement Session did not complete cleanly.");
                }
            }
            finally
            {
                if (replacementHost != null) replacementHost.Dispose();
                if (recoveredHost != null) recoveredHost.Dispose();
            }
        }

        private static Guid SeedRecoverableCompletedHistory(
            string bufferRoot,
            IRegistryRegistrationStore store,
            int ordinal)
        {
            var sessionId = Guid.NewGuid();
            var token = new byte[SessionRegistration.HandshakeTokenBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            store.Upsert(new SessionRegistration(
                sessionId,
                "termlink-capacity-history-" + sessionId.ToString("N"),
                token,
                int.MaxValue - ordinal,
                ManagedShellType.PowerShell7,
                DateTime.UtcNow.AddYears(-1).AddMinutes(ordinal),
                DateTime.UtcNow.AddMinutes(-ordinal)));
            using (var buffer = new EncryptedEventBuffer(
                Path.Combine(bufferRoot, sessionId.ToString("N")),
                sessionId,
                4 * 1024 * 1024))
            {
                buffer.Append(new LocalFrame(
                    LocalFrameType.Lifecycle,
                    sessionId,
                    1,
                    LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0)));
                buffer.Append(new LocalFrame(
                    LocalFrameType.Output,
                    sessionId,
                    2,
                    Encoding.UTF8.GetBytes("completed-capacity-" + ordinal)));
            }
            return sessionId;
        }

        private static void ValidateCompletedBufferRecovery(string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var sessionId = Guid.NewGuid();
            var token = new byte[SessionRegistration.HandshakeTokenBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            var registration = new SessionRegistration(
                sessionId,
                "termlink-recovered-" + sessionId.ToString("N"),
                token,
                int.MaxValue,
                ManagedShellType.PowerShell7,
                DateTime.UtcNow.AddYears(-1),
                DateTime.UtcNow);
            var store = new DpapiRegistryRegistrationStore(Path.Combine(bufferRoot, "registrations.dpapi"));
            store.Upsert(registration);
            var bufferDirectory = Path.Combine(bufferRoot, sessionId.ToString("N"));
            using (var buffer = new EncryptedEventBuffer(bufferDirectory, sessionId, 4 * 1024 * 1024))
            {
                buffer.Append(new LocalFrame(
                    LocalFrameType.Lifecycle,
                    sessionId,
                    1,
                    LocalPayloadCodec.Lifecycle(LocalLifecycleState.Running, 0)));
                buffer.Append(new LocalFrame(
                    LocalFrameType.Output,
                    sessionId,
                    2,
                    Encoding.UTF8.GetBytes("recovery-fixture")));
            }

            LocalFrame committed = null;
            using (var registry = new SessionRegistry(
                bufferRoot,
                1200,
                4 * 1024 * 1024,
                20,
                delegate(ManagedShellType shellType, LocalFrame frame)
                {
                    Require(shellType == ManagedShellType.PowerShell7,
                        "Recovered buffer changed its Shell type.");
                    committed = frame;
                }))
            {
                Require(registry.RecoveredSessionCount == 1 && registry.Count == 0 &&
                    registry.ContainsSession(sessionId),
                    "Completed buffer was not recovered as a retained read-only session.");
                int exitCode;
                Require(committed != null && committed.Sequence == 4 &&
                    committed.Type == LocalFrameType.SessionRemoved,
                    "Dead Host recovery did not append an ordered removal after Interrupted.");
                IList<LocalFrame> frames;
                ulong earliest;
                ulong latest;
                Require(registry.TryReadBufferedEvents(sessionId, 0, out frames, out earliest, out latest) &&
                    earliest == 1 && latest == 4 && frames.Count == 4 &&
                    frames[2].Type == LocalFrameType.Lifecycle &&
                    LocalPayloadCodec.ParseLifecycle(frames[2].Payload, out exitCode) ==
                        LocalLifecycleState.Interrupted &&
                    frames[3].Type == LocalFrameType.SessionRemoved,
                    "Recovered completed buffer did not preserve its committed event stream.");
                bool hasMore;
                Require(registry.TryReadBufferedEvents(
                        sessionId, 0, 2, long.MaxValue,
                        out frames, out earliest, out latest, out hasMore) &&
                    frames.Select(value => value.Sequence).SequenceEqual(new ulong[] { 1, 2 }) &&
                    earliest == 1 && latest == 4 && hasMore,
                    "Bounded Registry replay did not stop after its first two-frame page.");
                Require(registry.TryReadBufferedEvents(
                        sessionId, 2, 2, long.MaxValue,
                        out frames, out earliest, out latest, out hasMore) &&
                    frames.Select(value => value.Sequence).SequenceEqual(new ulong[] { 3, 4 }) &&
                    !hasMore,
                    "Bounded Registry replay did not resume linearly at S+1.");
                Require(!registry.TrySendRemoteInput(sessionId, Guid.NewGuid(), 0, Encoding.UTF8.GetBytes("blocked")) &&
                    !registry.TrySendRemoteInputRejection(
                        sessionId,
                        Guid.NewGuid(),
                        LocalRemoteInputResult.SessionInputUnavailable),
                    "Recovered completed buffer accepted a control operation.");
                registry.ReleaseBufferedSession(sessionId);
                Require(!registry.ContainsSession(sessionId),
                    "Published completed buffer remained retained in Registry memory.");
            }
            var retiredStore = new DpapiRegistryRegistrationStore(
                Path.Combine(bufferRoot, "registrations.dpapi"));
            var retired = retiredStore.Load().Single(item => item.SessionId == sessionId);
            Require(retired.Retired,
                "Published completed Session ID was not durably retired.");
            using (var restarted = new SessionRegistry(new FakeManagedSessionFactory(), 20, retiredStore))
            {
                string reuseError;
                Require(!restarted.TryRegister(CreateFakeRegistration(sessionId), out reuseError) &&
                    reuseError == "SESSION_RETIRED",
                    "A retired Session ID was reusable after Registry restart.");
            }
        }

        private static void ValidateLiveInterruptedLifecycle(string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var committed = new List<LocalFrame>();
            var lost = LostHostFixture.Start();
            try
            {
                using (var registry = new SessionRegistry(
                    bufferRoot,
                    1200,
                    4 * 1024 * 1024,
                    20,
                    delegate(ManagedShellType shellType, LocalFrame frame)
                    {
                        Require(shellType == ManagedShellType.Custom,
                            "Live interrupted callback changed its Shell type.");
                        lock (committed) committed.Add(frame);
                    }))
                {
                    string error;
                    Require(registry.TryRegister(lost.CreateRegistration(), out error),
                        "Live interrupted fixture registration failed: " + error);
                    WaitUntil(delegate
                    {
                        return registry.GetSummaries().Any(item =>
                            item.SessionId == lost.SessionId && item.LastSequence >= 2);
                    }, 5000, "Live interrupted fixture never committed its running state.");
                    WaitUntil(delegate
                    {
                        return registry.GetSummaries().All(item => item.SessionId != lost.SessionId) &&
                            registry.ContainsSession(lost.SessionId);
                    }, 10000, "Live Host loss did not become a retained completed session.");

                    IList<LocalFrame> frames;
                    ulong earliest;
                    ulong latest;
                    Require(registry.TryReadBufferedEvents(
                            lost.SessionId, 0, out frames, out earliest, out latest) &&
                        earliest == 1 && latest == 4 && frames.Count == 4,
                        "Live Host loss did not retain a continuous terminal event stream.");
                    var terminal = frames[frames.Count - 1];
                    int exitCode;
                    Require(terminal.Type == LocalFrameType.SessionRemoved && terminal.Sequence == 4 &&
                        frames[2].Type == LocalFrameType.Lifecycle &&
                        LocalPayloadCodec.ParseLifecycle(frames[2].Payload, out exitCode) ==
                            LocalLifecycleState.Interrupted,
                        "Live Host loss did not append ordered Interrupted and removal events.");
                    lock (committed)
                    {
                        Require(committed.Count == 4 && committed[3].Sequence == terminal.Sequence,
                            "Live Host loss did not notify the production commit callback.");
                    }
                    registry.ReleaseBufferedSession(lost.SessionId);
                    Require(!registry.ContainsSession(lost.SessionId),
                        "Published live Interrupted session remained retained.");
                }
            }
            finally
            {
                lost.Dispose();
            }
        }

        private static void ValidateControlledInterruptedLifecycle(string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var committed = new List<LocalFrame>();
            var fixture = LostHostFixture.Start();
            try
            {
                using (var registry = new SessionRegistry(
                    bufferRoot,
                    1200,
                    4 * 1024 * 1024,
                    20,
                    delegate(ManagedShellType shellType, LocalFrame frame)
                    {
                        Require(shellType == ManagedShellType.Custom,
                            "Controlled stop callback changed its Shell type.");
                        lock (committed) committed.Add(frame);
                    }))
                {
                    string error;
                    Require(registry.TryRegister(fixture.CreateRegistration(), out error),
                        "Controlled stop fixture registration failed: " + error);
                    WaitUntil(delegate
                    {
                        return registry.GetSummaries().Any(item =>
                            item.SessionId == fixture.SessionId && item.LastSequence >= 2);
                    }, 5000, "Controlled stop fixture never committed its running state.");

                    var stop = registry.StopSession(fixture.SessionId);
                    Require(stop.Success && stop.RequestedSessions == 1,
                        "Controlled stop did not finish its managed Session.");
                    Require(registry.ContainsSession(fixture.SessionId),
                        "Controlled stop did not retain its terminal DPAPI buffer.");

                    IList<LocalFrame> frames;
                    ulong earliest;
                    ulong latest;
                    Require(registry.TryReadBufferedEvents(
                            fixture.SessionId, 0, out frames, out earliest, out latest) &&
                        earliest == 1 && latest == 4 && frames.Count == 4,
                        "Controlled stop did not retain a continuous terminal event stream.");
                    var terminal = frames[frames.Count - 1];
                    int exitCode;
                    Require(terminal.Type == LocalFrameType.SessionRemoved && terminal.Sequence == 4 &&
                        frames[2].Type == LocalFrameType.Lifecycle &&
                        LocalPayloadCodec.ParseLifecycle(frames[2].Payload, out exitCode) ==
                            LocalLifecycleState.Interrupted,
                        "Controlled stop did not append Interrupted followed by removal.");
                    lock (committed)
                    {
                        Require(committed.Count == 4 && committed[3].Sequence == terminal.Sequence,
                            "Controlled stop did not notify the production commit callback.");
                    }
                }

                using (var recovered = new SessionRegistry(
                    bufferRoot,
                    1200,
                    4 * 1024 * 1024,
                    20,
                    delegate(ManagedShellType ignoredShell, LocalFrame ignoredFrame) { }))
                {
                    Require(recovered.GetSummaries().All(item => item.SessionId != fixture.SessionId) &&
                        recovered.ContainsSession(fixture.SessionId),
                        "Recovery-only terminal Session reattached to the still-running local Host.");
                    recovered.ReleaseBufferedSession(fixture.SessionId);
                    Require(!recovered.ContainsSession(fixture.SessionId),
                        "Released controlled-stop Session remained retained.");
                }
            }
            finally
            {
                fixture.Dispose();
            }
        }

        private static void ValidateExplicitSessionTermination(string hostExecutable, string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var target = HostFixture.Start(hostExecutable, "terminate-target", 46);
            var survivor = HostFixture.Start(hostExecutable, "terminate-survivor", 47);
            try
            {
                using (var registry = new SessionRegistry(
                    bufferRoot,
                    1200,
                    4 * 1024 * 1024,
                    20))
                {
                    string error;
                    Require(registry.TryRegister(target.CreateRegistration(ManagedShellType.PowerShell7), out error),
                        "Terminate target did not register: " + error);
                    Require(registry.TryRegister(survivor.CreateRegistration(ManagedShellType.CommandPrompt), out error),
                        "Terminate survivor did not register: " + error);
                    WaitUntil(delegate
                    {
                        var summaries = registry.GetSummaries();
                        return summaries.Any(item => item.SessionId == target.SessionId && item.LastSequence >= 2) &&
                            summaries.Any(item => item.SessionId == survivor.SessionId && item.LastSequence >= 2);
                    }, 5000, "Terminate fixtures did not reach Connected state.");
                    var targetChildProcessId = target.WaitForChildProcessId();

                    Require(registry.TerminateSession(target.SessionId),
                        "Explicit Session termination did not accept its registered Host identity.");
                    Require(registry.TerminateSession(target.SessionId),
                        "Explicit Session termination was not idempotent.");
                    Require(target.Process.WaitForExit(10000),
                        "Explicit Session termination did not end the target SessionHost.");
                    WaitUntil(delegate { return !IsProcessRunning(targetChildProcessId); }, 10000,
                        "Explicit Session termination left the target child Shell running.");
                    Require(!survivor.Process.HasExited,
                        "Explicit Session termination killed an unrelated SessionHost.");

                    var stopped = registry.StopSession(survivor.SessionId);
                    Require(stopped.Success && !survivor.Process.HasExited,
                        "StopSession no longer preserves the local Shell after adding explicit termination.");
                    survivor.CompleteLocally();
                }

                var mismatchFactory = new FakeManagedSessionFactory();
                using (var mismatchRegistry = new SessionRegistry(mismatchFactory, 5))
                {
                    var process = Process.GetCurrentProcess();
                    var token = new byte[SessionRegistration.HandshakeTokenBytes];
                    using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
                    var mismatched = new SessionRegistration(
                        Guid.NewGuid(),
                        "termlink-terminate-mismatch-" + Guid.NewGuid().ToString("N"),
                        token,
                        process.Id,
                        ManagedShellType.Custom,
                        process.StartTime.ToUniversalTime().AddTicks(-1),
                        DateTime.UtcNow);
                    string error;
                    Require(mismatchRegistry.TryRegister(mismatched, out error),
                        "Termination identity-mismatch fixture did not register: " + error);
                    Require(!mismatchRegistry.TerminateSession(mismatched.SessionId),
                        "Termination accepted a reused PID with a different process start time.");
                }
            }
            finally
            {
                survivor.Dispose();
                target.Dispose();
            }
        }

        private static void ValidateRestartPreservesWithoutInterruption(string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var committed = new List<LocalFrame>();
            var fixture = LostHostFixture.Start();
            try
            {
                using (var registry = new SessionRegistry(
                    bufferRoot,
                    1200,
                    4 * 1024 * 1024,
                    20,
                    delegate(ManagedShellType shellType, LocalFrame frame)
                    {
                        lock (committed) committed.Add(frame);
                    }))
                {
                    string error;
                    Require(registry.TryRegister(fixture.CreateRegistration(), out error),
                        "Restart preservation fixture registration failed: " + error);
                    WaitUntil(delegate
                    {
                        return registry.GetSummaries().Any(item =>
                            item.SessionId == fixture.SessionId && item.LastSequence >= 2);
                    }, 5000, "Restart preservation fixture never committed its running state.");
                    var restart = registry.ShutdownForRestart();
                    Require(restart.Success && restart.RequestedSessions == 1,
                        "Restart preservation did not stop its managed Session.");
                }

                lock (committed)
                {
                    var interrupted = false;
                    foreach (var frame in committed)
                    {
                        if (frame.Type != LocalFrameType.Lifecycle) continue;
                        int ignoredExit;
                        if (LocalPayloadCodec.ParseLifecycle(frame.Payload, out ignoredExit) ==
                            LocalLifecycleState.Interrupted) interrupted = true;
                    }
                    Require(committed.Count == 2 && !interrupted,
                        "Agent restart emitted an Interrupted lifecycle instead of preserving recovery state.");
                }
                var registrations = new DpapiRegistryRegistrationStore(
                    Path.Combine(bufferRoot, "registrations.dpapi")).Load();
                Require(registrations.Any(item => item.SessionId == fixture.SessionId),
                    "Agent restart removed the preserved recovery registration.");
            }
            finally
            {
                fixture.Dispose();
            }
        }

        private static void ValidateRemoteAccessPolicyPersistence(string bufferRoot)
        {
            Directory.CreateDirectory(bufferRoot);
            var policyPath = Path.Combine(bufferRoot, "remote-access-policy.dpapi");
            var stoppedSessionId = Guid.NewGuid();
            var stoppedRegistration = CreateFakeRegistration(stoppedSessionId);

            using (var registry = new SessionRegistry(
                new FakeManagedSessionFactory(),
                20,
                new NullRegistryRegistrationStore(),
                new DpapiRemoteAccessPolicyStore(policyPath)))
            {
                string error;
                Require(registry.TryRegister(stoppedRegistration, out error),
                    "Persistent stop fixture did not register: " + error);
                var stopped = registry.StopSession(stoppedSessionId);
                Require(stopped.Success && !stopped.PolicyPersistenceFailed,
                    "Single-session stop tombstone was not persisted.");
            }

            using (var recovered = new SessionRegistry(
                new FakeManagedSessionFactory(),
                20,
                new NullRegistryRegistrationStore(),
                new DpapiRemoteAccessPolicyStore(policyPath)))
            {
                string error;
                Require(!recovered.TryRegister(stoppedRegistration, out error) && error == "SESSION_STOPPED",
                    "Agent restart allowed a still-running explicitly stopped Host to re-register.");
                var active = CreateFakeRegistration(Guid.NewGuid());
                Require(recovered.TryRegister(active, out error),
                    "Remote policy fixture could not register an unrelated Session: " + error);
                var stopAll = recovered.StopAllRemoteAccess();
                Require(stopAll.Success && !recovered.RemoteAccessEnabled,
                    "Global remote-access stop was not persisted.");
            }

            using (var disabled = new SessionRegistry(
                new FakeManagedSessionFactory(),
                20,
                new NullRegistryRegistrationStore(),
                new DpapiRemoteAccessPolicyStore(policyPath)))
            {
                string error;
                Require(!disabled.RemoteAccessEnabled &&
                    !disabled.TryRegister(CreateFakeRegistration(Guid.NewGuid()), out error) &&
                    error == "REMOTE_ACCESS_DISABLED",
                    "Agent restart silently re-enabled globally stopped remote access.");
                Require(disabled.EnableRemoteAccess() && disabled.RemoteAccessEnabled,
                    "Explicit Enable did not durably restore remote access.");
            }

            using (var enabled = new SessionRegistry(
                new FakeManagedSessionFactory(),
                20,
                new NullRegistryRegistrationStore(),
                new DpapiRemoteAccessPolicyStore(policyPath)))
            {
                string error;
                Require(enabled.RemoteAccessEnabled,
                    "Explicitly enabled remote access did not survive Agent restart.");
                Require(!enabled.TryRegister(stoppedRegistration, out error) && error == "SESSION_STOPPED",
                    "Global Enable erased a single-session stop tombstone.");
                Require(enabled.TryRegister(CreateFakeRegistration(Guid.NewGuid()), out error),
                    "Enabled policy rejected an unrelated Session: " + error);
            }

            File.WriteAllBytes(policyPath, Encoding.ASCII.GetBytes("truncated-policy"));
            using (var damaged = new SessionRegistry(
                new FakeManagedSessionFactory(),
                20,
                new NullRegistryRegistrationStore(),
                new DpapiRemoteAccessPolicyStore(policyPath)))
            {
                string error;
                Require(!damaged.RemoteAccessEnabled &&
                    !damaged.TryRegister(CreateFakeRegistration(Guid.NewGuid()), out error) &&
                    error == "REMOTE_ACCESS_DISABLED",
                    "Damaged remote-access policy failed open.");
                Require(damaged.EnableRemoteAccess(),
                    "Explicit Enable could not repair a damaged remote-access policy.");
            }
        }

        private static void ValidatePolicyPersistenceFailure()
        {
            var factory = new FakeManagedSessionFactory();
            using (var registry = new SessionRegistry(
                factory,
                20,
                new NullRegistryRegistrationStore(),
                new FailingRemoteAccessPolicyStore()))
            {
                string error;
                var sessionId = Guid.NewGuid();
                var registration = CreateFakeRegistration(sessionId);
                Require(registry.TryRegister(registration, out error),
                    "Policy failure fixture did not register: " + error);
                var stopped = registry.StopSession(sessionId);
                Require(!stopped.Success && stopped.PolicyPersistenceFailed &&
                    stopped.CompletedSessions == 1,
                    "Single-session stop did not report a non-durable policy write.");
                Require(!registry.TryRegister(registration, out error) && error == "SESSION_STOPPED",
                    "A non-durable single stop failed to block re-registration in the current Agent.");
            }

            factory = new FakeManagedSessionFactory();
            using (var registry = new SessionRegistry(
                factory,
                20,
                new NullRegistryRegistrationStore(),
                new FailingRemoteAccessPolicyStore()))
            {
                string error;
                Require(registry.TryRegister(CreateFakeRegistration(Guid.NewGuid()), out error),
                    "Global policy failure fixture did not register: " + error);
                var stopped = registry.StopAllRemoteAccess();
                Require(!stopped.Success && stopped.PolicyPersistenceFailed &&
                    stopped.CompletedSessions == 1 && !registry.RemoteAccessEnabled,
                    "Global stop did not remain effective after its durable policy write failed.");
                Require(!registry.EnableRemoteAccess() && !registry.RemoteAccessEnabled,
                    "Failed durable Enable reopened remote access in memory.");
            }
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

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId)) return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
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

    internal sealed class FailingRemoteAccessPolicyStore : IRemoteAccessPolicyStore
    {
        public RemoteAccessPolicySnapshot Load()
        {
            return new RemoteAccessPolicySnapshot(true, new List<StoppedHostIdentity>());
        }

        public void Save(RemoteAccessPolicySnapshot snapshot)
        {
            throw new IOException("injected remote-access policy write failure");
        }
    }

    internal sealed class FakeManagedSession : IRegistryManagedSession, IRemoteControllableSession,
        IRecoveryRegistrationSession
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
            Registration = registration;
            WaitsBeforeSuccess = waitsBeforeSuccess;
            this.completion = completion;
        }

        public Guid SessionId { get; private set; }
        public SessionRegistration Registration { get; private set; }
        public int WaitsBeforeSuccess { get; private set; }
        public int WaitCalls { get { return Volatile.Read(ref waitCalls); } }
        public Guid RemoteInputId { get; private set; }
        public ulong RemoteInputEpoch { get; private set; }
        public byte[] RemoteInputBytes { get; private set; }
        public Guid RejectedInputId { get; private set; }
        public LocalRemoteInputResult RejectedInputResult { get; private set; }

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

        public bool TrySendRemoteInput(Guid inputId, ulong expectedLocalInputEpoch, byte[] bytes)
        {
            if (inputId == Guid.Empty || bytes == null || bytes.Length == 0) return false;
            RemoteInputId = inputId;
            RemoteInputEpoch = expectedLocalInputEpoch;
            RemoteInputBytes = (byte[])bytes.Clone();
            return true;
        }

        public bool TrySendRemoteInputRejection(Guid inputId, LocalRemoteInputResult result)
        {
            if (inputId == Guid.Empty || result == LocalRemoteInputResult.Accepted) return false;
            RejectedInputId = inputId;
            RejectedInputResult = result;
            return true;
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

        private HostFixture(
            Process process,
            Guid sessionId,
            string pipeName,
            byte[] token,
            string marker,
            int exitCode,
            string childProcessIdPath)
        {
            Process = process;
            SessionId = sessionId;
            PipeName = pipeName;
            Token = token;
            SecretMarker = marker;
            ExitCode = exitCode;
            ChildProcessIdPath = childProcessIdPath;
        }

        public Process Process { get; private set; }
        public Guid SessionId { get; private set; }
        public string PipeName { get; private set; }
        public byte[] Token { get; private set; }
        public string SecretMarker { get; private set; }
        public int ExitCode { get; private set; }
        public string ChildProcessIdPath { get; private set; }

        public static HostFixture Start(
            string hostExecutable,
            string label,
            int exitCode,
            string registryControlPipe = null)
        {
            var sessionId = Guid.NewGuid();
            var pipe = "termlink-registry-session-" + sessionId.ToString("N");
            var token = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            var marker = "SECRET_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            var childProcessIdPath = Path.Combine(
                Path.GetTempPath(),
                "TermLink-Child-" + Guid.NewGuid().ToString("N") + ".pid");
            var script =
                "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);" +
                "[Console]::InputEncoding=[Text.UTF8Encoding]::new($false);" +
                "[IO.File]::WriteAllText('" + childProcessIdPath.Replace("'", "''") + "',[string]$PID);" +
                "[Console]::WriteLine('" + marker + "_READY');" +
                "$v=[Console]::ReadLine();" +
                "[Console]::WriteLine('" + marker + "_LOCAL:'+$v);" +
                "exit " + exitCode;
            var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
            var command = "powershell.exe -NoLogo -NoProfile -NonInteractive -EncodedCommand " + encodedScript;
            var encodedCommand = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
            var arguments = "--command-base64 " + encodedCommand + " --timeout-ms 30000";
            if (!string.IsNullOrEmpty(registryControlPipe))
            {
                arguments += " --managed-shell custom --registry-pipe " + registryControlPipe;
            }
            else
            {
                arguments +=
                    " --agent-pipe " + pipe +
                    " --session-id " + sessionId.ToString("D") +
                    " --handshake-token " + Convert.ToBase64String(token);
            }
            var start = new ProcessStartInfo
            {
                FileName = hostExecutable,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            var process = Process.Start(start);
            return new HostFixture(
                process,
                sessionId,
                pipe,
                token,
                marker,
                exitCode,
                childProcessIdPath);
        }

        public int WaitForChildProcessId()
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    int processId;
                    if (File.Exists(ChildProcessIdPath) &&
                        int.TryParse(File.ReadAllText(ChildProcessIdPath), out processId) && processId > 0)
                    {
                        return processId;
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                Thread.Sleep(50);
            }
            throw new TimeoutException("Child Shell did not publish its process ID.");
        }

        public void AdoptRegisteredSession(Guid sessionId)
        {
            if (sessionId == Guid.Empty) throw new ArgumentException("Registered Session ID is required.");
            SessionId = sessionId;
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
            try
            {
                int childProcessId;
                if (File.Exists(ChildProcessIdPath) &&
                    int.TryParse(File.ReadAllText(ChildProcessIdPath), out childProcessId))
                {
                    using (var child = System.Diagnostics.Process.GetProcessById(childProcessId))
                    {
                        if (!child.HasExited)
                        {
                            child.Kill();
                            child.WaitForExit();
                        }
                    }
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (IOException) { }
            try { if (File.Exists(ChildProcessIdPath)) File.Delete(ChildProcessIdPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
