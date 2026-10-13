using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using TermLink.Agent;
using TermLink.Protocol;
using TermLink.Protocol.Realtime.V1;

namespace TermLink.ProductionConnectionLiveness.Validation;

internal static class Program
{
    private const string AcceptanceCaEnvironment = "TERMLINK_ACCEPTANCE_CA_FILE";
    private const string AcceptanceRootEnvironment = "TERMLINK_ACCEPTANCE_LOCAL_APP_DATA_ROOT";
    private const string TerminalCoreShadowEnvironment = "TERMLINK_ENABLE_TERMINAL_CORE_SHADOW";
    private const string TerminalSnapshotEnvironment = "TERMLINK_ENABLE_TERMINAL_SNAPSHOT_V2";
    private const string CredentialEntropy = "TermLink Windows production credentials V1";
    private const string AcceptanceHost = "termlink.127.0.0.1.sslip.io";

    private static async Task<int> Main()
    {
        var testRoot = Path.Combine(
            Path.GetTempPath(),
            "TermLink-Production-Connection-Liveness-" + Guid.NewGuid().ToString("N"));
        Process agent = null;
        Process sessionHost = null;
        Process peerSessionHost = null;
        Task<string> agentError = null;
        Task<string> hostError = null;
        Task<string> peerHostError = null;
        LoopbackRelay relay = null;
        CertificatePair certificates = null;
        DeviceIdentity pcIdentity = null;
        DeviceIdentity androidIdentity = null;
        string controlPipe = null;
        var previousEnvironment = CaptureEnvironment();
        try
        {
            Directory.CreateDirectory(testRoot);
            var winAgentRoot = FindWinAgentRoot();
            var agentSource = Path.Combine(
                winAgentRoot, "src", "Agent", "bin", "Release", "net8.0-windows");
            var sessionHostPath = Path.Combine(
                winAgentRoot, "src", "SessionHost", "bin", "Release", "net8.0-windows",
                "TermLink.SessionHost.exe");
            Require(File.Exists(Path.Combine(agentSource, "TermLink.Agent.exe")),
                "Release TermLink.Agent.exe is unavailable.");
            Require(File.Exists(sessionHostPath),
                "Release TermLink.SessionHost.exe is unavailable.");

            var certificateRoot = Path.Combine(testRoot, "certificates");
            Directory.CreateDirectory(certificateRoot);
            certificates = CreateCertificates(AcceptanceHost);
            var caPath = Path.Combine(certificateRoot, "acceptance-root.cer");
            File.WriteAllBytes(caPath, certificates.Root.Export(X509ContentType.Cert));

            var pcDeviceId = Guid.NewGuid();
            var androidDeviceId = Guid.NewGuid();
            pcIdentity = DeviceIdentity.Generate();
            androidIdentity = DeviceIdentity.Generate();

            var port = ReserveLoopbackPort();
            relay = await LoopbackRelay.StartAsync(
                port,
                certificates.Server,
                androidDeviceId,
                androidIdentity.Exchange.PublicKey,
                androidIdentity.SigningPublicKey).ConfigureAwait(false);
            var serverUrl = "https://" + AcceptanceHost + ":" + port;
            await ProbeLoopbackTlsAsync(serverUrl, certificates.Root).ConfigureAwait(false);
            var acceptanceDataRoot = Path.Combine(testRoot, "local-data");
            WriteIsolatedAccount(
                acceptanceDataRoot,
                serverUrl,
                pcDeviceId,
                pcIdentity);
            SetEnvironment(caPath, acceptanceDataRoot);

            // Deliberately omit the native core DLL from this private Agent copy.
            // A real managed SessionHost output frame then produces a deterministic
            // SHADOW_RETIRED/NATIVE_UNAVAILABLE soft terminal-core failure.
            var isolatedAgentRoot = Path.Combine(testRoot, "agent-no-native-core");
            CopyDirectoryWithoutNativeCore(agentSource, isolatedAgentRoot);
            var agentPath = Path.Combine(isolatedAgentRoot, "TermLink.Agent.exe");
            var bufferRoot = Path.Combine(testRoot, "buffer");
            controlPipe = "termlink-connection-liveness-" + Guid.NewGuid().ToString("N");
            agent = StartProcess(
                agentPath,
                new[] { "--registry", "--control-pipe", controlPipe, "--buffer-root", bufferRoot },
                testRoot,
                caPath,
                acceptanceDataRoot,
                out agentError);

            await WaitForRegistryAsync(controlPipe, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            await relay.WaitForAcceptCountAsync(1, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            await WaitForProductionConnectedAsync(controlPipe, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var initialGeneration = relay.CurrentGeneration;
            var initialAcceptCount = relay.AcceptCount;
            var agentPid = agent.Id;

            var sessionId = Guid.NewGuid();
            var peerSessionId = Guid.NewGuid();
            var command = "\"" + Environment.GetEnvironmentVariable("ComSpec") +
                "\" /D /Q /K \"ping -n 2 127.0.0.1 >nul & " +
                "echo __TERMLINK_LIVENESS_READY__ & ping -t 127.0.0.1\"";
            var commandBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
            sessionHost = StartProcess(
                sessionHostPath,
                new[]
                {
                    "--command-base64", commandBase64,
                    "--managed-shell", "command-prompt",
                    "--managed-session-id", sessionId.ToString("D"),
                    "--registry-pipe", controlPipe,
                    "--registration-timeout-ms", "15000",
                    "--timeout-ms", "120000", "--diagnostics"
                },
                testRoot,
                caPath,
                acceptanceDataRoot,
                out hostError);
            var sessionHostPid = sessionHost.Id;
            var peerCommand = "\"" + Environment.GetEnvironmentVariable("ComSpec") +
                "\" /D /Q /K echo __TERMLINK_PEER_READY__";
            var peerCommandBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(peerCommand));
            peerSessionHost = StartProcess(
                sessionHostPath,
                new[]
                {
                    "--command-base64", peerCommandBase64,
                    "--managed-shell", "command-prompt",
                    "--managed-session-id", peerSessionId.ToString("D"),
                    "--registry-pipe", controlPipe,
                    "--registration-timeout-ms", "15000",
                    "--timeout-ms", "120000", "--diagnostics"
                },
                testRoot,
                caPath,
                acceptanceDataRoot,
                out peerHostError);
            var peerSessionHostPid = peerSessionHost.Id;

            // A headless launcher may close stdin while the managed session
            // remains alive. Protocol replies must keep their ConPTY pipe.
            sessionHost.StandardInput.Close();
            peerSessionHost.StandardInput.Close();

            await WaitForSessionRegistrationAsync(
                controlPipe, sessionId, sessionHostPid, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            await WaitForSessionRegistrationAsync(
                controlPipe,
                peerSessionId,
                peerSessionHostPid,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            var pairwiseKey = SessionCrypto.DerivePairwiseKey(
                androidIdentity.Exchange,
                pcIdentity.Exchange.PublicKey,
                pcDeviceId,
                androidDeviceId,
                1);
            try
            {
                await EstablishHealthyRouteAsync(
                    relay,
                    initialGeneration,
                    pairwiseKey,
                    androidIdentity.Exchange.PublicKey,
                    pcDeviceId,
                    androidDeviceId,
                    sessionId).ConfigureAwait(false);
                await EstablishHealthyRouteAsync(
                    relay,
                    initialGeneration,
                    pairwiseKey,
                    androidIdentity.Exchange.PublicKey,
                    pcDeviceId,
                    androidDeviceId,
                    peerSessionId).ConfigureAwait(false);

                await relay.WaitForSessionEnvelopeQuiescenceAsync(
                    peerSessionId,
                    TimeSpan.FromMilliseconds(350),
                    TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                var peerEnvelopeCount = relay.GetSessionEnvelopeCount(peerSessionId);
                // A syntactically invalid protobuf cannot be assigned to any
                // session. It is still an application-frame fault and must not
                // replace the healthy physical WSS generation.
                await relay.SendBinaryPayloadAsync(
                    new byte[] { 0 },
                    initialGeneration).ConfigureAwait(false);
                await relay.SendEnvelopeAsync(
                    BuildTamperedAndroidEnvelope(
                        sessionId,
                        pcDeviceId,
                        androidDeviceId,
                        MessageType.FlowControl,
                        sequence: 3,
                        acknowledgement: 0),
                    initialGeneration).ConfigureAwait(false);

                // The WebSocket reader handles frames serially. If this valid
                // peer SUBSCRIBE is accepted, the preceding tampered route has
                // already crossed the exact-route retirement boundary.
                await relay.SendEnvelopeAsync(
                    BuildAndroidEnvelope(
                        pairwiseKey,
                        peerSessionId,
                        pcDeviceId,
                        androidDeviceId,
                        MessageType.Subscribe,
                        sequence: 3,
                        acknowledgement: 0,
                        new SubscribePayload
                        {
                            ResumeFromSeq = 0,
                    RequestId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                    SubscriberCapabilities = { "terminal_state_2_21_v2" },
                            SubscriberExchangePublicKey = ByteString.CopyFrom(
                                androidIdentity.Exchange.PublicKey)
                        }.ToByteArray()),
                    initialGeneration).ConfigureAwait(false);
                await relay.SendEnvelopeAsync(
                    BuildAndroidEnvelope(
                        pairwiseKey,
                        peerSessionId,
                        pcDeviceId,
                        androidDeviceId,
                        MessageType.FlowControl,
                        sequence: 4,
                        acknowledgement: 0,
                        BuildFlowControlPayload(controlEpoch: 1)),
                    initialGeneration).ConfigureAwait(false);
                await relay.WaitForSessionEnvelopeCountAsync(
                    peerSessionId,
                    peerEnvelopeCount + 1,
                    TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pairwiseKey);
            }
            await WaitForAnyShadowRetiredAsync(
                acceptanceDataRoot,
                new[] { sessionId, peerSessionId },
                TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            RequireAlive(agent, agentPid, "Agent exited or was replaced after terminal-core soft failure.");
            RequireAlive(sessionHost, sessionHostPid,
                "SessionHost exited or was replaced after terminal-core soft failure.");
            RequireAlive(peerSessionHost, peerSessionHostPid,
                "Peer SessionHost exited or was replaced after a located session fault.");
            Require(relay.AcceptCount == initialAcceptCount &&
                    relay.CurrentGeneration == initialGeneration,
                "A terminal-core or located session fault replaced the shared physical WebSocket generation.");

            await relay.DropCurrentGenerationAsync().ConfigureAwait(false);
            await relay.WaitForAcceptCountAsync(initialAcceptCount + 1, TimeSpan.FromSeconds(30))
                .ConfigureAwait(false);
            var recoveredGeneration = relay.CurrentGeneration;
            Require(recoveredGeneration == initialGeneration + 1,
                "Relay recovery did not create exactly the next WebSocket generation.");
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Require(relay.AcceptCount == initialAcceptCount + 1,
                "Relay recovery created more than one replacement WebSocket generation.");
            RequireAlive(agent, agentPid, "Agent PID changed across Relay recovery.");
            RequireAlive(sessionHost, sessionHostPid, "SessionHost PID changed across Relay recovery.");
            RequireAlive(peerSessionHost, peerSessionHostPid,
                "Peer SessionHost PID changed across Relay recovery.");
            await WaitForProductionConnectedAsync(controlPipe, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            Console.WriteLine(
                "Production connection liveness validation passed: Agent PID {0}, SessionHost PIDs {1}/{2}, " +
                "isolated generation {3}, recovered generation {4}.",
                agentPid,
                sessionHostPid,
                peerSessionHostPid,
                initialGeneration,
                recoveredGeneration);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            if (agentError?.IsCompleted == true)
                Console.Error.WriteLine("Agent stderr: " + await agentError.ConfigureAwait(false));
            if (agent != null)
                Console.Error.WriteLine("Agent state: pid=" + agent.Id + "; exited=" + agent.HasExited +
                    (agent.HasExited ? "; code=" + agent.ExitCode : string.Empty));
            if (!string.IsNullOrWhiteSpace(controlPipe) && agent?.HasExited == false)
            {
                try
                {
                    var status = RegistryControlClient.Send(
                        controlPipe,
                        new RegistryRequest { Command = RegistryCommand.List },
                        2000).Production;
                    Console.Error.WriteLine("Relay state: " + status?.RelayState +
                        "; error=" + status?.LastErrorCode + "; server=" + status?.ServerOrigin);
                }
                catch (Exception statusError)
                {
                    Console.Error.WriteLine("Status probe failed: " + statusError.Message);
                }
            }
            if (!string.IsNullOrWhiteSpace(relay?.LastFailure))
                Console.Error.WriteLine("Loopback Relay failure: " + relay.LastFailure);
            if (sessionHost?.HasExited == true) Console.Error.WriteLine("SessionHost exit code: " + sessionHost.ExitCode);
            if (peerSessionHost?.HasExited == true) Console.Error.WriteLine("Peer SessionHost exit code: " + peerSessionHost.ExitCode);
            if (hostError?.IsCompleted == true)
                Console.Error.WriteLine("SessionHost stderr: " + await hostError.ConfigureAwait(false));
            if (peerHostError?.IsCompleted == true)
                Console.Error.WriteLine("Peer SessionHost stderr: " +
                    await peerHostError.ConfigureAwait(false));
            return 1;
        }
        finally
        {
            TryTerminate(peerSessionHost);
            TryTerminate(sessionHost);
            TryTerminate(agent);
            if (relay != null) await relay.DisposeAsync().ConfigureAwait(false);
            androidIdentity?.Dispose();
            pcIdentity?.Dispose();
            certificates?.Dispose();
            RestoreEnvironment(previousEnvironment);
            TryDeleteDirectory(testRoot);
        }
    }

    private static Process StartProcess(
        string executable,
        string[] arguments,
        string workingDirectory,
        string caPath,
        string acceptanceDataRoot,
        out Task<string> stderr)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment[AcceptanceCaEnvironment] = caPath;
        start.Environment[AcceptanceRootEnvironment] = acceptanceDataRoot;
        // Exercise terminal-core as the non-authoritative shadow. Enabling the
        // native Snapshot authority intentionally disables the duplicate
        // shadow parser in ProductionTerminalHistoryProducer, which would make
        // the NATIVE_UNAVAILABLE soft-failure assertion impossible to reach.
        start.Environment[TerminalCoreShadowEnvironment] = "1";
        start.Environment[TerminalSnapshotEnvironment] = "0";
        var process = Process.Start(start) ??
            throw new InvalidOperationException("Process could not be started: " + executable);
        _ = process.StandardOutput.ReadToEndAsync();
        stderr = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static async Task WaitForProductionConnectedAsync(
        string controlPipe,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        Exception lastError = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var response = RegistryControlClient.Send(
                    controlPipe,
                    new RegistryRequest { Command = RegistryCommand.List },
                    1000);
                if (response.Production?.RelayState == ProductionRelayState.Connected) return;
            }
            catch (Exception error) when (
                error is IOException || error is TimeoutException || error is InvalidDataException)
            {
                lastError = error;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        throw new TimeoutException("Production Agent did not reach Connected.", lastError);
    }

    private static async Task WaitForRegistryAsync(string controlPipe, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                RegistryControlClient.Send(
                    controlPipe,
                    new RegistryRequest { Command = RegistryCommand.List },
                    500);
                return;
            }
            catch (Exception error) when (
                error is IOException || error is TimeoutException || error is InvalidDataException)
            {
            }
            await Task.Delay(50).ConfigureAwait(false);
        }
        throw new TimeoutException("Agent Registry control pipe was unavailable.");
    }

    private static async Task WaitForSessionRegistrationAsync(
        string controlPipe,
        Guid sessionId,
        int hostPid,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow.Add(timeout);
        while (DateTime.UtcNow < deadline)
        {
            var response = RegistryControlClient.Send(
                controlPipe,
                new RegistryRequest { Command = RegistryCommand.List },
                2000);
            var session = response.Summaries?.SingleOrDefault(item => item.SessionId == sessionId);
            if (session != null)
            {
                if (session.HostProcessId <= 0)
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    continue;
                }
                Require(session.HostProcessId == hostPid,
                    "Registry recorded SessionHost PID " + session.HostProcessId +
                    " instead of launched PID " + hostPid + " for session " +
                    sessionId.ToString("D") + ".");
                return;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
        throw new TimeoutException("Managed SessionHost did not register with the Agent.");
    }

    private static async Task WaitForAnyShadowRetiredAsync(
        string localDataRoot,
        Guid[] sessionIds,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(localDataRoot) || !Path.IsPathFullyQualified(localDataRoot))
            throw new ArgumentException("An absolute isolated LocalAppData root is required.", nameof(localDataRoot));
        if (sessionIds == null || sessionIds.Length == 0 || sessionIds.Any(value => value == Guid.Empty))
            throw new ArgumentException("At least one non-empty session ID is required.", nameof(sessionIds));
        var logPath = Path.Combine(
            localDataRoot,
            "TermLink", "logs", "TermLink-terminal-core-shadow.jsonl");
        var deadline = DateTime.UtcNow.Add(timeout);
        var sessionTexts = sessionIds
            .Select(value => "\"session_id\":\"" + value.ToString("D") + "\"")
            .ToArray();
        var lastContent = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(logPath))
                {
                    using var stream = new FileStream(
                        logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    var content = await reader.ReadToEndAsync().ConfigureAwait(false);
                    lastContent = content;
                    if (content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Any(line => sessionTexts.Any(value =>
                                line.Contains(value, StringComparison.Ordinal)) &&
                            line.Contains("\"status\":\"SHADOW_RETIRED\"", StringComparison.Ordinal)))
                    {
                        return;
                    }
                }
            }
            catch (IOException) { }
            await Task.Delay(100).ConfigureAwait(false);
        }
        if (lastContent.Length > 4096)
            lastContent = lastContent.Substring(lastContent.Length - 4096);
        throw new TimeoutException(
            "No controlled SessionHost produced the deterministic terminal-core " +
            "SHADOW_RETIRED diagnostic. " +
            "Observed log tail: " + lastContent);
    }

    private static async Task EstablishHealthyRouteAsync(
        LoopbackRelay relay,
        long generation,
        byte[] pairwiseKey,
        byte[] androidExchangePublicKey,
        Guid pcDeviceId,
        Guid androidDeviceId,
        Guid sessionId)
    {
        var initialCount = relay.GetSessionEnvelopeCount(sessionId);
        await relay.SendEnvelopeAsync(
            BuildAndroidEnvelope(
                pairwiseKey,
                sessionId,
                pcDeviceId,
                androidDeviceId,
                MessageType.Subscribe,
                sequence: 1,
                acknowledgement: 0,
                new SubscribePayload
                {
                    ResumeFromSeq = 0,
                    RequestId = ByteString.CopyFrom(GuidBytes(Guid.NewGuid())),
                    SubscriberCapabilities = { "terminal_state_2_21_v2" },
                    SubscriberExchangePublicKey = ByteString.CopyFrom(androidExchangePublicKey)
                }.ToByteArray()),
            generation).ConfigureAwait(false);
        await relay.SendEnvelopeAsync(
            BuildAndroidEnvelope(
                pairwiseKey,
                sessionId,
                pcDeviceId,
                androidDeviceId,
                MessageType.FlowControl,
                sequence: 2,
                acknowledgement: 0,
                BuildFlowControlPayload(controlEpoch: 1)),
            generation).ConfigureAwait(false);
        await relay.WaitForSessionEnvelopeCountAsync(
            sessionId,
            initialCount + 1,
            TimeSpan.FromSeconds(20)).ConfigureAwait(false);
    }

    private static byte[] BuildFlowControlPayload(ulong controlEpoch)
    {
        return new FlowControlPayload
        {
            HighestContiguousSeq = 0,
            MaxUnackedEvents = 128,
            MaxUnackedWireBytes = 3UL << 20,
            Paused = false,
            ReasonCode = "ACTIVE",
            ControlEpoch = controlEpoch
        }.ToByteArray();
    }

    private static Envelope BuildAndroidEnvelope(
        byte[] pairwiseKey,
        Guid sessionId,
        Guid pcDeviceId,
        Guid androidDeviceId,
        MessageType messageType,
        ulong sequence,
        ulong acknowledgement,
        byte[] plaintext)
    {
        var keyId = Encoding.UTF8.GetBytes("epoch-00000001");
        var nonce = SessionCrypto.Nonce(new byte[] { 0x41, 0x4E, 0x44, 0x52 }, sequence);
        var sessionKey = SessionCrypto.DeriveSessionKey(
            pairwiseKey,
            sessionId,
            CryptoDirection.AndroidToPc,
            keyId);
        byte[] associatedData = null;
        byte[] ciphertext = null;
        try
        {
            var envelope = new Envelope
            {
                ProtocolVersion = 1,
                MessageType = messageType,
                SessionId = ByteString.CopyFrom(GuidBytes(sessionId)),
                SenderDeviceId = ByteString.CopyFrom(GuidBytes(androidDeviceId)),
                RecipientDeviceId = ByteString.CopyFrom(GuidBytes(pcDeviceId)),
                Direction = Direction.AndroidToPc,
                Seq = sequence,
                Ack = acknowledgement,
                SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                KeyId = ByteString.CopyFrom(keyId),
                Nonce = ByteString.CopyFrom(nonce)
            };
            associatedData = SessionCrypto.AssociatedData(envelope);
            ciphertext = SessionCrypto.Encrypt(
                sessionKey,
                nonce,
                associatedData,
                plaintext);
            envelope.Ciphertext = ByteString.CopyFrom(ciphertext);
            return envelope;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyId);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(sessionKey);
            if (associatedData != null) CryptographicOperations.ZeroMemory(associatedData);
            if (ciphertext != null) CryptographicOperations.ZeroMemory(ciphertext);
        }
    }

    private static Envelope BuildTamperedAndroidEnvelope(
        Guid sessionId,
        Guid pcDeviceId,
        Guid androidDeviceId,
        MessageType messageType,
        ulong sequence,
        ulong acknowledgement)
    {
        return new Envelope
        {
            ProtocolVersion = 1,
            MessageType = messageType,
            SessionId = ByteString.CopyFrom(GuidBytes(sessionId)),
            SenderDeviceId = ByteString.CopyFrom(GuidBytes(androidDeviceId)),
            RecipientDeviceId = ByteString.CopyFrom(GuidBytes(pcDeviceId)),
            Direction = Direction.AndroidToPc,
            Seq = sequence,
            Ack = acknowledgement,
            SentAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            KeyId = ByteString.CopyFromUtf8("epoch-00000001"),
            Nonce = ByteString.CopyFrom(
                SessionCrypto.Nonce(new byte[] { 0x42, 0x41, 0x44, 0x21 }, sequence)),
            Ciphertext = ByteString.CopyFrom(Enumerable.Repeat((byte)0xA5, 32).ToArray())
        };
    }

    private static byte[] GuidBytes(Guid value) =>
        Convert.FromHexString(value.ToString("N"));

    private static Guid GuidFromBytes(ReadOnlySpan<byte> value)
    {
        if (value.Length != 16)
            throw new InvalidDataException("UUID must contain 16 bytes.");
        return Guid.ParseExact(Convert.ToHexString(value), "N");
    }

    private static void WriteIsolatedAccount(
        string localDataRoot,
        string serverUrl,
        Guid deviceId,
        DeviceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var termLinkRoot = Path.Combine(localDataRoot, "TermLink");
        Directory.CreateDirectory(termLinkRoot);
        var pcId = Guid.NewGuid();
        var config = new
        {
            ServerUrl = serverUrl,
            PcId = pcId,
            PcName = "LIVENESS-PC",
            AccountKey = (string)null,
            AccountUsername = "liveness",
            AccountNickname = "Liveness",
            AccountEmail = "",
            AgentVersion = "0.0.0-liveness",
            KeyEpoch = 1,
            OIDCIssuer = (string)null,
            OIDCClientId = (string)null
        };
        File.WriteAllBytes(
            Path.Combine(termLinkRoot, "production-config.json"),
            JsonSerializer.SerializeToUtf8Bytes(config));

        var material = identity.ExportPrivateMaterial();
        var plain = JsonSerializer.SerializeToUtf8Bytes(new
        {
            DeviceId = deviceId,
            PrivateMaterial = Convert.ToBase64String(material),
            AccountKey = (string)null,
            AccessToken = "liveness-token",
            RefreshToken = (string)null,
            AccessTokenExpiresAtUnixSeconds = 0
        });
        var entropy = Encoding.UTF8.GetBytes(CredentialEntropy);
        try
        {
            var protectedBytes = ProtectedData.Protect(
                plain, entropy, DataProtectionScope.CurrentUser);
            try
            {
                File.WriteAllBytes(
                    Path.Combine(termLinkRoot, "production-credentials.dpapi"),
                    protectedBytes);
            }
            finally { CryptographicOperations.ZeroMemory(protectedBytes); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(material);
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(entropy);
        }
    }

    private static CertificatePair CreateCertificates(string host)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=" + host, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment |
            X509KeyUsageFlags.KeyCertSign, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(host);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var ephemeral = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(12));
        var password = Guid.NewGuid().ToString("N");
        var pfx = ephemeral.Export(X509ContentType.Pfx, password);
        var server = new X509Certificate2(
            pfx,
            password,
            X509KeyStorageFlags.UserKeySet |
                X509KeyStorageFlags.PersistKeySet |
                X509KeyStorageFlags.Exportable);
        CryptographicOperations.ZeroMemory(pfx);
        var root = new X509Certificate2(ephemeral.Export(X509ContentType.Cert));
        return new CertificatePair(root, server);
    }

    private static int ReserveLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task ProbeLoopbackTlsAsync(string serverUrl, X509Certificate2 root)
    {
        using var handler = new HttpClientHandler { UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, certificate, providedChain, errors) =>
        {
            if (certificate == null ||
                (errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != 0)
                return false;
            using var leaf = new X509Certificate2(certificate);
            using var validation = new X509Chain();
            validation.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            validation.ChainPolicy.CustomTrustStore.Add(root);
            if (providedChain != null)
            {
                for (var index = 1; index < providedChain.ChainElements.Count; index++)
                    validation.ChainPolicy.ExtraStore.Add(providedChain.ChainElements[index].Certificate);
            }
            validation.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return validation.Build(leaf);
        };
        // This acceptance process also starts two SessionHosts and can run
        // beside Android/Rust compiler workers. Keep the self-probe bounded,
        // but do not let a short ThreadPool scheduling stall masquerade as a
        // Relay/TLS failure before the actual liveness scenario starts.
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        using var response = await client.GetAsync(serverUrl + "/health").ConfigureAwait(false);
        Require(response.IsSuccessStatusCode, "Loopback Relay TLS self-probe failed.");
    }

    private static void CopyDirectoryWithoutNativeCore(string source, string destination)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (string.Equals(
                Path.GetFileName(sourcePath), "termlink_terminal_ffi.dll",
                StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Path.GetRelativePath(source, sourcePath);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sourcePath, target, overwrite: true);
        }
    }

    private static string FindWinAgentRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory != null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TermLink.Windows.sln")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException("win-agent root was not found from the validation output.");
    }

    private static void RequireAlive(Process process, int expectedPid, string message)
    {
        Require(process != null && process.Id == expectedPid && !process.HasExited, message);
        using var observed = Process.GetProcessById(expectedPid);
        Require(!observed.HasExited, message);
    }

    private static void TryTerminate(Process process)
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException) { }
        finally { process.Dispose(); }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var full = Path.GetFullPath(path);
        var temporary = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase)) return;
        try { Directory.Delete(full, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static EnvironmentSnapshot CaptureEnvironment() => new(
        Environment.GetEnvironmentVariable(AcceptanceCaEnvironment),
        Environment.GetEnvironmentVariable(AcceptanceRootEnvironment),
        Environment.GetEnvironmentVariable(TerminalCoreShadowEnvironment),
        Environment.GetEnvironmentVariable(TerminalSnapshotEnvironment));

    private static void SetEnvironment(string caPath, string acceptanceDataRoot)
    {
        Environment.SetEnvironmentVariable(AcceptanceCaEnvironment, caPath);
        Environment.SetEnvironmentVariable(AcceptanceRootEnvironment, acceptanceDataRoot);
        Environment.SetEnvironmentVariable(TerminalCoreShadowEnvironment, "1");
        Environment.SetEnvironmentVariable(TerminalSnapshotEnvironment, "1");
    }

    private static void RestoreEnvironment(EnvironmentSnapshot snapshot)
    {
        Environment.SetEnvironmentVariable(AcceptanceCaEnvironment, snapshot.AcceptanceCa);
        Environment.SetEnvironmentVariable(AcceptanceRootEnvironment, snapshot.AcceptanceRoot);
        Environment.SetEnvironmentVariable(TerminalCoreShadowEnvironment, snapshot.TerminalCoreShadow);
        Environment.SetEnvironmentVariable(TerminalSnapshotEnvironment, snapshot.TerminalSnapshot);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record EnvironmentSnapshot(
        string AcceptanceCa,
        string AcceptanceRoot,
        string TerminalCoreShadow,
        string TerminalSnapshot);

    private sealed class CertificatePair : IDisposable
    {
        public CertificatePair(X509Certificate2 root, X509Certificate2 server)
        {
            Root = root;
            Server = server;
        }

        public X509Certificate2 Root { get; }
        public X509Certificate2 Server { get; }

        public void Dispose()
        {
            Server.Dispose();
            Root.Dispose();
        }
    }

    private sealed class LoopbackRelay : IAsyncDisposable
    {
        private readonly object sync = new();
        private readonly TcpListener listener;
        private readonly X509Certificate2 certificate;
        private readonly byte[] grantsBody;
        private readonly CancellationTokenSource cancellation = new();
        private readonly SemaphoreSlim socketWriteGate = new(1, 1);
        private readonly ConcurrentDictionary<Guid, int> sessionEnvelopeCounts = new();
        private readonly Task acceptWorker;
        private TcpClient currentSocketClient;
        private Stream currentSocketStream;
        private long generation;
        private int acceptCount;
        private string lastFailure;

        private LoopbackRelay(
            TcpListener listener,
            X509Certificate2 certificate,
            Guid androidDeviceId,
            byte[] androidExchangePublicKey,
            byte[] androidSigningPublicKey)
        {
            this.listener = listener;
            this.certificate = certificate;
            grantsBody = JsonSerializer.SerializeToUtf8Bytes(new
            {
                key_epoch = 1,
                items = new[]
                {
                    new
                    {
                        device_id = androidDeviceId,
                        device_type = "ANDROID",
                        exchange_public_key = Convert.ToBase64String(androidExchangePublicKey),
                        signing_public_key = Convert.ToBase64String(androidSigningPublicKey)
                    }
                }
            });
            acceptWorker = Task.Run(AcceptLoopAsync);
        }

        public int AcceptCount => Volatile.Read(ref acceptCount);
        public long CurrentGeneration => Interlocked.Read(ref generation);
        public string LastFailure => Volatile.Read(ref lastFailure);

        public static Task<LoopbackRelay> StartAsync(
            int port,
            X509Certificate2 certificate,
            Guid androidDeviceId,
            byte[] androidExchangePublicKey,
            byte[] androidSigningPublicKey)
        {
            if (androidDeviceId == Guid.Empty)
                throw new ArgumentException("Android device ID is required.", nameof(androidDeviceId));
            if (androidExchangePublicKey == null || androidExchangePublicKey.Length != 32)
                throw new ArgumentException(
                    "Android exchange public key must contain 32 bytes.",
                    nameof(androidExchangePublicKey));
            if (androidSigningPublicKey == null || androidSigningPublicKey.Length != 32)
                throw new ArgumentException(
                    "Android signing public key must contain 32 bytes.",
                    nameof(androidSigningPublicKey));
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start(32);
            return Task.FromResult(new LoopbackRelay(
                listener,
                certificate,
                androidDeviceId,
                androidExchangePublicKey,
                androidSigningPublicKey));
        }

        public int GetSessionEnvelopeCount(Guid sessionId) =>
            sessionEnvelopeCounts.TryGetValue(sessionId, out var count) ? count : 0;

        public async Task WaitForSessionEnvelopeCountAsync(
            Guid sessionId,
            int expected,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                if (GetSessionEnvelopeCount(sessionId) >= expected) return;
                await Task.Delay(50).ConfigureAwait(false);
            }
            throw new TimeoutException(
                "Loopback Relay did not observe the expected healthy output for session " +
                sessionId.ToString("D") + ".");
        }

        public async Task WaitForSessionEnvelopeQuiescenceAsync(
            Guid sessionId,
            TimeSpan quietPeriod,
            TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            var stableSince = DateTime.UtcNow;
            var lastCount = GetSessionEnvelopeCount(sessionId);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(50).ConfigureAwait(false);
                var currentCount = GetSessionEnvelopeCount(sessionId);
                if (currentCount != lastCount)
                {
                    lastCount = currentCount;
                    stableSince = DateTime.UtcNow;
                    continue;
                }
                if (DateTime.UtcNow - stableSince >= quietPeriod) return;
            }
            throw new TimeoutException(
                "Loopback Relay output did not quiesce for session " +
                sessionId.ToString("D") + ".");
        }

        public async Task SendEnvelopeAsync(Envelope envelope, long expectedGeneration)
        {
            ArgumentNullException.ThrowIfNull(envelope);
            var payload = envelope.ToByteArray();
            if (payload.Length < 1 || payload.Length >= 1024 * 1024)
                throw new InvalidDataException("Injected Envelope exceeds the realtime limit.");
            Stream stream;
            lock (sync)
            {
                if (expectedGeneration != generation || currentSocketStream == null)
                    throw new InvalidOperationException(
                        "The requested WebSocket generation is no longer active.");
                stream = currentSocketStream;
            }
            await socketWriteGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                lock (sync)
                {
                    if (expectedGeneration != generation ||
                        !ReferenceEquals(stream, currentSocketStream))
                    {
                        throw new InvalidOperationException(
                            "The WebSocket generation changed before the test Envelope was sent.");
                    }
                }
                await WriteWebSocketFrameAsync(
                    stream,
                    opcode: 2,
                    payload,
                    cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
                socketWriteGate.Release();
            }
        }

        public async Task SendBinaryPayloadAsync(byte[] payload, long expectedGeneration)
        {
            ArgumentNullException.ThrowIfNull(payload);
            if (payload.Length < 1 || payload.Length >= 1024 * 1024)
                throw new InvalidDataException("Injected binary frame exceeds the realtime limit.");
            Stream stream;
            lock (sync)
            {
                if (expectedGeneration != generation || currentSocketStream == null)
                    throw new InvalidOperationException(
                        "The requested WebSocket generation is no longer active.");
                stream = currentSocketStream;
            }
            await socketWriteGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            try
            {
                lock (sync)
                {
                    if (expectedGeneration != generation ||
                        !ReferenceEquals(stream, currentSocketStream))
                    {
                        throw new InvalidOperationException(
                            "The WebSocket generation changed before the test frame was sent.");
                    }
                }
                await WriteWebSocketFrameAsync(
                    stream,
                    opcode: 2,
                    payload,
                    cancellation.Token).ConfigureAwait(false);
            }
            finally { socketWriteGate.Release(); }
        }

        public async Task WaitForAcceptCountAsync(int expected, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline)
            {
                if (AcceptCount >= expected) return;
                await Task.Delay(50).ConfigureAwait(false);
            }
            throw new TimeoutException("Loopback Relay did not accept WebSocket generation " + expected + ".");
        }

        public Task DropCurrentGenerationAsync()
        {
            TcpClient client;
            lock (sync) client = currentSocketClient;
            if (client == null) throw new InvalidOperationException("No live WebSocket generation is available.");
            client.Dispose();
            return Task.CompletedTask;
        }

        private async Task AcceptLoopAsync()
        {
            while (!cancellation.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(cancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (SocketException) when (cancellation.IsCancellationRequested) { return; }
                _ = Task.Run(() => HandleClientAsync(client));
            }
        }

        private async Task HandleClientAsync(TcpClient client)
        {
            using (client)
            using (var tls = new SslStream(client.GetStream(), leaveInnerStreamOpen: false))
            {
                try
                {
                    await tls.AuthenticateAsServerAsync(
                        certificate,
                        clientCertificateRequired: false,
                        enabledSslProtocols: System.Security.Authentication.SslProtocols.Tls12 |
                            System.Security.Authentication.SslProtocols.Tls13,
                        checkCertificateRevocation: false).ConfigureAwait(false);
                    var requestText = await ReadHttpHeadersAsync(tls, cancellation.Token).ConfigureAwait(false);
                    var firstLine = requestText.Split(new[] { "\r\n" }, StringSplitOptions.None)[0];
                    var parts = firstLine.Split(' ');
                    if (parts.Length < 2) throw new InvalidDataException("Loopback HTTP request line is invalid.");
                    var method = parts[0];
                    var path = parts[1];
                    if (path == "/v1/realtime" &&
                        HeaderValue(requestText, "Upgrade").Equals("websocket", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleWebSocketAsync(client, tls, requestText).ConfigureAwait(false);
                        return;
                    }
                    await WriteRestResponseAsync(tls, method, path, cancellation.Token).ConfigureAwait(false);
                }
                catch (Exception error) when (
                    error is OperationCanceledException || error is IOException ||
                    error is SocketException || error is InvalidDataException ||
                    error is System.Security.Authentication.AuthenticationException)
                {
                    Volatile.Write(ref lastFailure, error.ToString());
                }
            }
        }

        private async Task WriteRestResponseAsync(
            Stream stream,
            string method,
            string path,
            CancellationToken token)
        {
            var status = "200 OK";
            var body = "{}";
            if (method == "POST" && path == "/v1/devices/register") status = "201 Created";
            else if (method == "POST" &&
                (path.EndsWith("/file-operations/claim", StringComparison.Ordinal) ||
                 path.EndsWith("/agent-updates/claim", StringComparison.Ordinal)))
            {
                status = "204 No Content";
                body = string.Empty;
            }
            else if (method == "GET" && path == "/v1/devices") body = "{\"items\":[]}";
            else if (method == "GET" && path.EndsWith("/grants", StringComparison.Ordinal))
                body = Encoding.UTF8.GetString(grantsBody);
            else if (method == "GET" && path.EndsWith("/lease", StringComparison.Ordinal))
            {
                status = "404 Not Found";
            }
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var headers = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n\r\n");
            await stream.WriteAsync(headers, token).ConfigureAwait(false);
            if (bodyBytes.Length > 0) await stream.WriteAsync(bodyBytes, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private async Task HandleWebSocketAsync(
            TcpClient client,
            Stream stream,
            string requestText)
        {
            var key = HeaderValue(requestText, "Sec-WebSocket-Key");
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("WebSocket key is missing.");
            var accept = Convert.ToBase64String(SHA1.HashData(
                Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 101 Switching Protocols\r\n" +
                "Upgrade: websocket\r\n" +
                "Connection: Upgrade\r\n" +
                "Sec-WebSocket-Accept: " + accept + "\r\n" +
                "Sec-WebSocket-Protocol: termlink.realtime.v1\r\n\r\n");
            await stream.WriteAsync(response, cancellation.Token).ConfigureAwait(false);
            await stream.FlushAsync(cancellation.Token).ConfigureAwait(false);
            lock (sync)
            {
                currentSocketClient = client;
                currentSocketStream = stream;
            }
            Interlocked.Increment(ref generation);
            Interlocked.Increment(ref acceptCount);
            try
            {
                var hello = await ReadWebSocketFrameAsync(stream, cancellation.Token).ConfigureAwait(false);
                if (hello.Opcode != 1)
                    throw new InvalidDataException("Agent hello frame was not a bounded text message.");
                await socketWriteGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                try
                {
                    var welcome = JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        type = "welcome",
                        protocol_version = 1,
                        heartbeat_seconds = 15,
                        connection_id = Guid.NewGuid().ToString("D"),
                        capabilities = new[]
                        {
                            "cumulative_ack",
                            "e2ee_x25519_chacha20poly1305",
                            "flow_control_v1",
                            "lease_v1", "terminal_attach_v1", "terminal_host_geometry_v1", "terminal_host_theme_v1", "terminal_host_resize_v1"
                        }
                    });
                    await WriteWebSocketFrameAsync(
                        stream,
                        opcode: 1,
                        welcome,
                        cancellation.Token).ConfigureAwait(false);
                }
                finally { socketWriteGate.Release(); }
                while (!cancellation.IsCancellationRequested)
                {
                    var frame = await ReadWebSocketFrameAsync(stream, cancellation.Token).ConfigureAwait(false);
                    if (frame.Opcode == 8) break;
                    if (frame.Opcode == 9)
                    {
                        await socketWriteGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                        try
                        {
                            await WriteWebSocketFrameAsync(
                                stream,
                                10,
                                frame.Payload,
                                cancellation.Token).ConfigureAwait(false);
                        }
                        finally { socketWriteGate.Release(); }
                    }
                    else if (frame.Opcode == 2)
                    {
                        var envelope = Envelope.Parser.ParseFrom(frame.Payload);
                        var sessionId = GuidFromBytes(envelope.SessionId.Span);
                        if (envelope.MessageType != MessageType.TerminalAttachResult)
                            sessionEnvelopeCounts.AddOrUpdate(sessionId, 1, (_, count) => count + 1);
                    }
                }
            }
            catch (Exception error) when (
                error is OperationCanceledException || error is IOException ||
                error is SocketException || error is InvalidDataException)
            {
            }
            finally
            {
                lock (sync)
                {
                    if (ReferenceEquals(currentSocketClient, client))
                    {
                        currentSocketClient = null;
                        currentSocketStream = null;
                    }
                }
            }
        }

        private static async Task<string> ReadHttpHeadersAsync(Stream stream, CancellationToken token)
        {
            using var bytes = new MemoryStream();
            var one = new byte[1];
            var matched = 0;
            var marker = new byte[] { 13, 10, 13, 10 };
            while (bytes.Length < 64 * 1024)
            {
                await ReadExactAsync(stream, one, token).ConfigureAwait(false);
                bytes.WriteByte(one[0]);
                matched = one[0] == marker[matched] ? matched + 1 : one[0] == marker[0] ? 1 : 0;
                if (matched == marker.Length) return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new InvalidDataException("Loopback HTTP headers exceed 64 KiB.");
        }

        private static string HeaderValue(string requestText, string name)
        {
            foreach (var line in requestText.Split(new[] { "\r\n" }, StringSplitOptions.None))
            {
                var separator = line.IndexOf(':');
                if (separator > 0 && string.Equals(
                    line.Substring(0, separator), name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(separator + 1).Trim();
            }
            return string.Empty;
        }

        private static async Task<WebSocketFrame> ReadWebSocketFrameAsync(
            Stream stream,
            CancellationToken token)
        {
            var header = new byte[2];
            await ReadExactAsync(stream, header, token).ConfigureAwait(false);
            var opcode = header[0] & 0x0f;
            var masked = (header[1] & 0x80) != 0;
            ulong length = (uint)(header[1] & 0x7f);
            if (length == 126)
            {
                var extended = new byte[2];
                await ReadExactAsync(stream, extended, token).ConfigureAwait(false);
                length = (uint)((extended[0] << 8) | extended[1]);
            }
            else if (length == 127)
            {
                var extended = new byte[8];
                await ReadExactAsync(stream, extended, token).ConfigureAwait(false);
                if (BitConverter.IsLittleEndian) Array.Reverse(extended);
                length = BitConverter.ToUInt64(extended, 0);
            }
            if (length > 1024 * 1024) throw new InvalidDataException("WebSocket frame exceeds 1 MiB.");
            var mask = masked ? new byte[4] : Array.Empty<byte>();
            if (masked) await ReadExactAsync(stream, mask, token).ConfigureAwait(false);
            var payload = new byte[(int)length];
            if (payload.Length > 0) await ReadExactAsync(stream, payload, token).ConfigureAwait(false);
            if (masked)
                for (var index = 0; index < payload.Length; index++) payload[index] ^= mask[index % 4];
            return new WebSocketFrame(opcode, payload);
        }

        private static async Task WriteWebSocketFrameAsync(
            Stream stream,
            int opcode,
            byte[] payload,
            CancellationToken token)
        {
            using var frame = new MemoryStream();
            frame.WriteByte((byte)(0x80 | opcode));
            if (payload.Length < 126) frame.WriteByte((byte)payload.Length);
            else if (payload.Length <= ushort.MaxValue)
            {
                frame.WriteByte(126);
                frame.WriteByte((byte)(payload.Length >> 8));
                frame.WriteByte((byte)payload.Length);
            }
            else
            {
                frame.WriteByte(127);
                var length = BitConverter.GetBytes((ulong)payload.Length);
                if (BitConverter.IsLittleEndian) Array.Reverse(length);
                frame.Write(length, 0, length.Length);
            }
            frame.Write(payload, 0, payload.Length);
            await stream.WriteAsync(frame.ToArray(), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken token)
        {
            var offset = 0;
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(offset), token).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                offset += read;
            }
        }

        public async ValueTask DisposeAsync()
        {
            cancellation.Cancel();
            TcpClient client;
            lock (sync) client = currentSocketClient;
            client?.Dispose();
            listener.Stop();
            try { await acceptWorker.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            cancellation.Dispose();
        }

        private sealed record WebSocketFrame(int Opcode, byte[] Payload);
    }
}
