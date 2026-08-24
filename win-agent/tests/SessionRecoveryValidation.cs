using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using TermLink.Agent;

namespace TermLink.AgentRecoveryValidation
{
    internal static class Program
    {
        private const int SessionCount = 20;

        public static int Main(string[] args)
        {
            if (args.Length != 6)
            {
                Console.Error.WriteLine(
                    "Usage: SessionRecoveryValidation <agent.exe> <tray.exe> <session-host.exe> <buffer-root> <isolated-local-app-data> <isolation-marker>");
                return 2;
            }

            var hosts = new List<HostFixture>();
            AgentFixture firstAgent = null;
            AgentFixture recoveredAgent = null;
            AgentFixture finalAgent = null;
            try
            {
                var agentExecutable = Path.GetFullPath(args[0]);
                var trayExecutable = Path.GetFullPath(args[1]);
                var hostExecutable = Path.GetFullPath(args[2]);
                var bufferRoot = Path.GetFullPath(args[3]);
                ValidationIsolation.Initialize(args[4], args[5]);
                Directory.CreateDirectory(bufferRoot);
                var controlPipe = "termlink-recovery-control-" + Guid.NewGuid().ToString("N");
                var indexPath = Path.Combine(bufferRoot, "registrations.dpapi");

                firstAgent = AgentFixture.Start(agentExecutable, controlPipe, bufferRoot);
                WaitForAgent(controlPipe, 15000);
                AssertProductionRuntimeAbsent(controlPipe);
                for (var index = 0; index < SessionCount; index++)
                {
                    var host = HostFixture.Start(hostExecutable, index);
                    hosts.Add(host);
                    var response = Send(controlPipe, new RegistryRequest
                    {
                        Command = RegistryCommand.Register,
                        Registration = host.CreateRegistration(index)
                    });
                    Require(response.Success && response.Code == "REGISTERED",
                        "Session " + index + " registration failed: " + response.Code);
                }

                var before = WaitForConnectedSessions(controlPipe, hosts, 30000);
                Require(before.Count == SessionCount, "Initial Registry session count was not 20.");
                var registeredTimes = before.ToDictionary(item => item.SessionId, item => item.RegisteredAtUtc);
                Require(File.Exists(indexPath), "DPAPI registration index was not created.");
                AssertIndexCiphertext(indexPath, hosts);
                ValidateTrayStatus(trayExecutable, controlPipe, bufferRoot, SessionCount, "before");

                firstAgent.Kill();
                firstAgent.Dispose();
                firstAgent = null;
                recoveredAgent = AgentFixture.Start(agentExecutable, controlPipe, bufferRoot);
                var recovered = WaitForConnectedSessions(controlPipe, hosts, 45000);
                AssertProductionRuntimeAbsent(controlPipe);
                Require(recovered.Count == SessionCount, "Recovered Registry session count was not 20.");
                Require(recovered.All(item => registeredTimes[item.SessionId].Ticks == item.RegisteredAtUtc.Ticks),
                    "A recovered registration lost its original registration time.");
                AssertIndexCiphertext(indexPath, hosts);
                ValidateTrayStatus(trayExecutable, controlPipe, bufferRoot, SessionCount, "after");

                var restart = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.RestartAgent });
                Require(restart.Success && restart.Code == "AGENT_RESTART_READY" &&
                    restart.AffectedSessions == SessionCount,
                    "Controlled Agent restart did not preserve all recovered sessions.");
                Require(recoveredAgent.WaitForExit(15000), "Agent did not exit for controlled restart.");
                var restartDiagnostics = recoveredAgent.ReadOutput();
                Require(restartDiagnostics.IndexOf(";recovered=20;", StringComparison.Ordinal) >= 0,
                    "Controlled restart diagnostics did not report 20 recovered sessions.");
                recoveredAgent.Dispose();
                recoveredAgent = null;

                finalAgent = AgentFixture.Start(agentExecutable, controlPipe, bufferRoot);
                var controlledRecovery = WaitForConnectedSessions(controlPipe, hosts, 45000);
                AssertProductionRuntimeAbsent(controlPipe);
                Require(controlledRecovery.Count == SessionCount,
                    "Controlled restart did not recover all 20 sessions.");
                Require(controlledRecovery.All(item =>
                    registeredTimes[item.SessionId].Ticks == item.RegisteredAtUtc.Ticks),
                    "Controlled restart lost an original registration time.");
                ValidateTrayStatus(trayExecutable, controlPipe, bufferRoot, SessionCount, "controlled");

                var stopped = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.StopAllRemoteAccess });
                Require(stopped.Success && stopped.Code == "REMOTE_ACCESS_STOPPED" &&
                    stopped.AffectedSessions == SessionCount,
                    "Stopping remote access did not cover all recovered sessions.");
                Require(hosts.All(host => !host.Process.HasExited),
                    "Stopping remote access terminated a local Session Host.");
                Require(File.Exists(indexPath),
                    "Stopping remote access did not retain the encrypted terminal-recovery index.");
                AssertIndexCiphertext(indexPath, hosts);

                foreach (var host in hosts) host.BeginLocalCompletion();
                foreach (var host in hosts) host.VerifyLocalCompletion();

                ShutdownWithTray(trayExecutable, controlPipe, bufferRoot);
                Require(finalAgent.WaitForExit(15000), "Final Agent did not exit after Tray shutdown command.");
                var diagnostics = finalAgent.ReadOutput();
                Require(diagnostics.IndexOf(";recovered=20;", StringComparison.Ordinal) >= 0,
                    "Final Agent diagnostics did not report 20 restored sessions: " + diagnostics);

                Console.WriteLine(
                    "__RECOVERY_TEST_RESULT__:sessions=20" +
                    ";recovered=20" +
                    ";controlled_recovered=20" +
                    ";registration_times_preserved=true" +
                    ";dpapi_index_plaintext=false" +
                    ";tray_status_sessions=20" +
                    ";local_shells_survived=20" +
                    ";terminal_index_retained=true" +
                    ";isolated_local_app_data=true" +
                    ";production_runtime_absent=true" +
                    ";production_configured=false");
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                return 1;
            }
            finally
            {
                if (firstAgent != null) firstAgent.Dispose();
                if (recoveredAgent != null) recoveredAgent.Dispose();
                if (finalAgent != null) finalAgent.Dispose();
                foreach (var host in hosts) host.Dispose();
            }
        }

        private static IList<SessionSummary> WaitForConnectedSessions(
            string controlPipe,
            IList<HostFixture> hosts,
            int timeoutMilliseconds)
        {
            IList<SessionSummary> result = null;
            WaitUntil(delegate
            {
                var response = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List });
                result = response.Summaries;
                return response.Success && result.Count == hosts.Count && hosts.All(host => result.Any(item =>
                    item.SessionId == host.SessionId &&
                    item.Lifecycle == SessionAgentLifecycle.Connected &&
                    item.LastSequence >= 2));
            }, timeoutMilliseconds, "Sessions did not all reach Connected after Agent start.");
            return result;
        }

        private static void WaitForAgent(string controlPipe, int timeoutMilliseconds)
        {
            WaitUntil(delegate
            {
                var response = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List });
                return response.Success;
            }, timeoutMilliseconds, "Agent control pipe did not become ready.");
        }

        private static void AssertProductionRuntimeAbsent(string controlPipe)
        {
            var status = Send(controlPipe, new RegistryRequest { Command = RegistryCommand.List });
            Require(status.Success && status.Production != null,
                "Isolated Agent did not return a production status contract.");
            Require(!status.Production.Configured && !status.Production.AccountLinked &&
                status.Production.RelayState == ProductionRelayState.NotConfigured,
                "Isolated Agent loaded a production account or started a production runtime.");
            var retry = Send(controlPipe, new RegistryRequest
            {
                Command = RegistryCommand.RetryProductionConnection
            });
            Require(!retry.Success && retry.Code == "PRODUCTION_NOT_CONFIGURED",
                "Isolated Agent unexpectedly accepted a production Relay reconnect request.");
        }

        private static void ValidateTrayStatus(
            string trayExecutable,
            string controlPipe,
            string bufferRoot,
            int expectedSessions,
            string label)
        {
            var destination = Path.Combine(bufferRoot, "tray-status-" + label + ".json");
            var process = StartTray(
                trayExecutable,
                controlPipe,
                bufferRoot,
                "--status-json",
                destination);
            using (process)
            {
                Require(process.WaitForExit(15000), "Tray status command timed out.");
                Require(process.ExitCode == 0, "Tray status command returned " + process.ExitCode + ".");
            }
            var json = File.ReadAllText(destination, Encoding.UTF8);
            var summariesStart = json.IndexOf("\"Summaries\":", StringComparison.Ordinal);
            var productionStart = json.IndexOf("\"Production\":", StringComparison.Ordinal);
            Require(summariesStart >= 0 && productionStart > summariesStart,
                "Tray status did not contain ordered local and production sections.");
            var summariesJson = json.Substring(summariesStart, productionStart - summariesStart);
            Require(CountOccurrences(summariesJson, "\"SessionId\"") == expectedSessions,
                "Tray status did not report " + expectedSessions + " sessions.");
            Require(json.IndexOf("HandshakeToken", StringComparison.OrdinalIgnoreCase) < 0 &&
                json.IndexOf("PipeName", StringComparison.OrdinalIgnoreCase) < 0 &&
                json.IndexOf("BufferRoot", StringComparison.OrdinalIgnoreCase) < 0,
                "Tray status exposed a sensitive registration field.");
        }

        private static void ShutdownWithTray(
            string trayExecutable,
            string controlPipe,
            string bufferRoot)
        {
            var process = StartTray(
                trayExecutable,
                controlPipe,
                bufferRoot,
                "--shutdown-agent",
                null);
            using (process)
            {
                Require(process.WaitForExit(15000), "Tray shutdown command timed out.");
                Require(process.ExitCode == 0, "Tray shutdown command returned " + process.ExitCode + ".");
            }
        }

        private static Process StartTray(
            string trayExecutable,
            string controlPipe,
            string bufferRoot,
            string command,
            string value)
        {
            var arguments = new StringBuilder();
            AppendArgument(arguments, "--control-pipe");
            AppendArgument(arguments, controlPipe);
            AppendArgument(arguments, "--buffer-root");
            AppendArgument(arguments, bufferRoot);
            AppendArgument(arguments, command);
            if (value != null) AppendArgument(arguments, value);
            var start = new ProcessStartInfo
            {
                FileName = trayExecutable,
                Arguments = arguments.ToString(),
                UseShellExecute = false,
                CreateNoWindow = true
            };
            ValidationIsolation.Apply(start);
            return Process.Start(start);
        }

        private static void AssertIndexCiphertext(string indexPath, IEnumerable<HostFixture> hosts)
        {
            var bytes = File.ReadAllBytes(indexPath);
            Require(bytes.Length > 5 && bytes[0] == (byte)'T' && bytes[1] == (byte)'L' &&
                bytes[2] == (byte)'R' && bytes[3] == (byte)'I' &&
                (bytes[4] == 1 || bytes[4] == 2 || bytes[4] == 3),
                "Registration index header is invalid.");
            foreach (var host in hosts)
            {
                Require(!Contains(bytes, Encoding.UTF8.GetBytes(host.PipeName)),
                    "Registration index exposed a plaintext pipe name.");
                Require(!Contains(bytes, host.Token), "Registration index exposed a plaintext handshake token.");
                Require(!Contains(bytes, Encoding.UTF8.GetBytes(host.SecretMarker)),
                    "Registration index exposed terminal content.");
            }
        }

        private static bool Contains(byte[] source, byte[] value)
        {
            if (value.Length == 0 || value.Length > source.Length) return false;
            for (var offset = 0; offset <= source.Length - value.Length; offset++)
            {
                var match = true;
                for (var index = 0; index < value.Length; index++)
                {
                    if (source[offset + index] == value[index]) continue;
                    match = false;
                    break;
                }
                if (match) return true;
            }
            return false;
        }

        private static int CountOccurrences(string text, string value)
        {
            var count = 0;
            var offset = 0;
            while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += value.Length;
            }
            return count;
        }

        private static RegistryResponse Send(string controlPipe, RegistryRequest request)
        {
            return RegistryControlClient.Send(controlPipe, request, 5000);
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

        private static void AppendArgument(StringBuilder arguments, string value)
        {
            if (arguments.Length > 0) arguments.Append(' ');
            arguments.Append('"').Append(value.Replace("\"", "\\\"")).Append('"');
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    internal sealed class AgentFixture : IDisposable
    {
        private AgentFixture(Process process)
        {
            Process = process;
        }

        public Process Process { get; private set; }

        public static AgentFixture Start(string executable, string controlPipe, string bufferRoot)
        {
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments =
                    "--registry --control-pipe " + controlPipe +
                    " --buffer-root \"" + bufferRoot + "\"" +
                    " --host-disconnect-timeout-ms 30000" +
                    " --max-buffer-bytes 4194304 --max-sessions 20 --diagnostics",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            ValidationIsolation.Apply(start);
            var process = Process.Start(start);
            if (process == null) throw new InvalidOperationException("Agent process could not be started.");
            return new AgentFixture(process);
        }

        public void Kill()
        {
            if (!Process.HasExited)
            {
                Process.Kill();
                Process.WaitForExit();
            }
        }

        public bool WaitForExit(int timeoutMilliseconds)
        {
            return Process.WaitForExit(timeoutMilliseconds);
        }

        public string ReadOutput()
        {
            if (!Process.HasExited) throw new InvalidOperationException("Agent is still running.");
            return Process.StandardOutput.ReadToEnd() + "\n" + Process.StandardError.ReadToEnd();
        }

        public void Dispose()
        {
            if (Process == null) return;
            Kill();
            Process.Dispose();
            Process = null;
        }
    }

    internal static class ValidationIsolation
    {
        private const string LocalAppDataEnvironment = "LOCALAPPDATA";
        private const string AcceptanceRootEnvironment = "TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT";
        private const string AcceptanceCAEnvironment = "TERMLINK_ACCEPTANCE_CA_FILE";
        private static string isolatedRoot;
        private static string isolationMarker;

        public static void Initialize(string expectedRoot, string expectedMarker)
        {
            isolatedRoot = Path.GetFullPath(expectedRoot);
            isolationMarker = Path.GetFullPath(expectedMarker);
            RequireEnvironment(LocalAppDataEnvironment, isolatedRoot);
            RequireEnvironment(AcceptanceRootEnvironment, isolatedRoot);
            RequireEnvironment(AcceptanceCAEnvironment, isolationMarker);
            if (!Directory.Exists(isolatedRoot))
                throw new InvalidOperationException("The isolated LocalAppData root does not exist.");
            var termLinkRoot = Path.Combine(isolatedRoot, "TermLink");
            if (File.Exists(Path.Combine(termLinkRoot, "production-config.json")) ||
                File.Exists(Path.Combine(termLinkRoot, "production-credentials.dpapi")))
            {
                throw new InvalidOperationException(
                    "The isolated LocalAppData root already contains production account files.");
            }
        }

        public static void Apply(ProcessStartInfo start)
        {
            if (start == null) throw new ArgumentNullException("start");
            if (string.IsNullOrWhiteSpace(isolatedRoot) || string.IsNullOrWhiteSpace(isolationMarker))
                throw new InvalidOperationException("Validation isolation was not initialized.");
            start.EnvironmentVariables[LocalAppDataEnvironment] = isolatedRoot;
            start.EnvironmentVariables[AcceptanceRootEnvironment] = isolatedRoot;
            start.EnvironmentVariables[AcceptanceCAEnvironment] = isolationMarker;
        }

        private static void RequireEnvironment(string name, string expected)
        {
            var actual = Environment.GetEnvironmentVariable(name);
            if (string.IsNullOrWhiteSpace(actual) ||
                !string.Equals(Path.GetFullPath(actual), expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Validation child process did not receive isolated environment variable " + name + ".");
            }
        }
    }

    internal sealed class HostFixture : IDisposable
    {
        private readonly int exitCode;
        private bool completionStarted;
        private bool completionVerified;

        private HostFixture(
            Process process,
            Guid sessionId,
            string pipeName,
            byte[] token,
            string secretMarker,
            int exitCode)
        {
            Process = process;
            SessionId = sessionId;
            PipeName = pipeName;
            Token = token;
            SecretMarker = secretMarker;
            this.exitCode = exitCode;
        }

        public Process Process { get; private set; }
        public Guid SessionId { get; private set; }
        public string PipeName { get; private set; }
        public byte[] Token { get; private set; }
        public string SecretMarker { get; private set; }

        public static HostFixture Start(string hostExecutable, int index)
        {
            var sessionId = Guid.NewGuid();
            var pipeName = "termlink-recovery-session-" + sessionId.ToString("N");
            var token = new byte[SessionRegistration.HandshakeTokenBytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(token);
            var marker = "RECOVERY_SECRET_" + index.ToString("D2") + "_" +
                Guid.NewGuid().ToString("N").Substring(0, 8);
            var exitCode = 40 + index;
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
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = hostExecutable,
                Arguments =
                    "--command-base64 " + encodedCommand +
                    " --timeout-ms 120000" +
                    " --agent-pipe " + pipeName +
                    " --session-id " + sessionId.ToString("D") +
                    " --handshake-token " + Convert.ToBase64String(token),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            });
            if (process == null) throw new InvalidOperationException("Session Host process could not be started.");
            return new HostFixture(process, sessionId, pipeName, token, marker, exitCode);
        }

        public SessionRegistration CreateRegistration(int index)
        {
            return new SessionRegistration(
                SessionId,
                PipeName,
                Token,
                Process.Id,
                (ManagedShellType)((index % 4) + 1));
        }

        public void BeginLocalCompletion()
        {
            if (completionStarted) return;
            completionStarted = true;
            Require(!Process.HasExited, "Session Host exited before local completion.");
            Process.StandardInput.WriteLine("local-user-input");
            Process.StandardInput.Flush();
            Process.StandardInput.Close();
        }

        public void VerifyLocalCompletion()
        {
            if (completionVerified) return;
            if (!completionStarted) throw new InvalidOperationException("Local completion was not started.");
            Require(Process.WaitForExit(20000), "Session Host did not finish after local input.");
            var output = Process.StandardOutput.ReadToEnd();
            var error = Process.StandardError.ReadToEnd();
            Require(Process.ExitCode == exitCode,
                "Session Host returned " + Process.ExitCode + ", expected " + exitCode + ": " + error);
            Require(output.IndexOf(SecretMarker + "_LOCAL:local-user-input", StringComparison.Ordinal) >= 0,
                "Local shell did not receive input after Agent recovery and remote stop.");
            completionVerified = true;
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

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
