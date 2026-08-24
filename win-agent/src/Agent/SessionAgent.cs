using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using TermLink.LocalIpc;

namespace TermLink.Agent
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
#if TERMLINK_PRODUCTION_RUNTIME
                if (ProductionEnrollmentOptions.IsEnrollment(args))
                {
                    return ProductionEnrollment.Run(ProductionEnrollmentOptions.Parse(args));
                }
#endif
                if (RegistryServiceOptions.IsRegistryMode(args))
                {
                    var registryOptions = RegistryServiceOptions.Parse(args);
#if TERMLINK_PRODUCTION_RUNTIME
                    using (var host = new EmbeddedAgentHost(registryOptions))
                    {
                        host.Start();
                        while (host.IsRunning) Thread.Sleep(100);
                        if (host.LastFailure != null) throw host.LastFailure;
                        return 0;
                    }
#else
                    using (var service = new SessionRegistryService(registryOptions))
                    {
                        return service.Run();
                    }
#endif
                }
                var options = AgentOptions.Parse(args);
                if (options.InspectBuffer)
                {
                    Console.WriteLine(EncryptedEventBuffer.Inspect(options.BufferDirectory, options.SessionId));
                    return 0;
                }
                using (var agent = new SessionAgent(options)) return agent.Run();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("TermLink Agent failed: " + error.Message);
                return 125;
            }
        }
    }

    internal sealed class AgentOptions
    {
        public string PipeName { get; private set; }
        public Guid SessionId { get; private set; }
        public byte[] HandshakeToken { get; private set; }
        public string BufferDirectory { get; private set; }
        public byte[] InputBytes { get; private set; }
        public Guid InputId { get; private set; }
        public int DuplicateInputAttempts { get; private set; }
        public bool UseRemoteInputV2 { get; private set; }
        public short ResizeColumns { get; private set; }
        public short ResizeRows { get; private set; }
        public int WaitMilliseconds { get; private set; }
        public long MaximumBufferBytes { get; private set; }
        public bool Diagnostics { get; private set; }
        public bool InspectBuffer { get; private set; }
        public int TestDisconnectAfterFrames { get; private set; }
        public int ExpectedHostProcessId { get; private set; }

        public static AgentOptions CreateManaged(
            string pipeName,
            Guid sessionId,
            byte[] handshakeToken,
            string bufferDirectory,
            int expectedHostProcessId,
            int disconnectedTimeoutMilliseconds,
            long maximumBufferBytes)
        {
            if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 ||
                pipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new ArgumentException("pipe name is invalid", "pipeName");
            }
            if (sessionId == Guid.Empty) throw new ArgumentException("session ID is required", "sessionId");
            if (handshakeToken == null || handshakeToken.Length != LocalPayloadCodec.HandshakeTokenBytes)
            {
                throw new ArgumentException("handshake token must contain 32 bytes", "handshakeToken");
            }
            if (string.IsNullOrWhiteSpace(bufferDirectory))
            {
                throw new ArgumentException("buffer directory is required", "bufferDirectory");
            }
            if (expectedHostProcessId <= 0)
            {
                throw new ArgumentOutOfRangeException("expectedHostProcessId");
            }
            if (disconnectedTimeoutMilliseconds < 250 || disconnectedTimeoutMilliseconds > 300000)
            {
                throw new ArgumentOutOfRangeException("disconnectedTimeoutMilliseconds");
            }
            if (maximumBufferBytes < 1024 || maximumBufferBytes > 1024L * 1024 * 1024)
            {
                throw new ArgumentOutOfRangeException("maximumBufferBytes");
            }
            return new AgentOptions
            {
                PipeName = pipeName,
                SessionId = sessionId,
                HandshakeToken = (byte[])handshakeToken.Clone(),
                BufferDirectory = Path.GetFullPath(bufferDirectory),
                DuplicateInputAttempts = 1,
                WaitMilliseconds = disconnectedTimeoutMilliseconds,
                MaximumBufferBytes = maximumBufferBytes,
                ExpectedHostProcessId = expectedHostProcessId
            };
        }

        public static AgentOptions Parse(string[] args)
        {
            string pipeName = null;
            string sessionText = null;
            string tokenText = null;
            string bufferDirectory = null;
            string inputText = null;
            string inputIdText = null;
            short columns = 0;
            short rows = 0;
            var duplicateAttempts = 1;
            var waitMilliseconds = 30000;
            long maximumBufferBytes = 16 * 1024 * 1024;
            var diagnostics = false;
            var inspect = false;
            var testDisconnectAfterFrames = 0;
            var useRemoteInputV2 = false;

            for (var index = 0; index < args.Length; index++)
            {
                switch (args[index])
                {
                    case "--pipe-name": pipeName = RequiredValue(args, ref index, "--pipe-name"); break;
                    case "--session-id": sessionText = RequiredValue(args, ref index, "--session-id"); break;
                    case "--handshake-token": tokenText = RequiredValue(args, ref index, "--handshake-token"); break;
                    case "--buffer-directory": bufferDirectory = RequiredValue(args, ref index, "--buffer-directory"); break;
                    case "--input-base64": inputText = RequiredValue(args, ref index, "--input-base64"); break;
                    case "--input-id": inputIdText = RequiredValue(args, ref index, "--input-id"); break;
                    case "--remote-input-v2": useRemoteInputV2 = true; break;
                    case "--duplicate-input-attempts":
                        duplicateAttempts = ParseInteger(RequiredValue(args, ref index, "--duplicate-input-attempts"), 1, 100, "duplicate input attempts");
                        break;
                    case "--resize-columns":
                        columns = checked((short)ParseInteger(RequiredValue(args, ref index, "--resize-columns"), 2, 1000, "resize columns"));
                        break;
                    case "--resize-rows":
                        rows = checked((short)ParseInteger(RequiredValue(args, ref index, "--resize-rows"), 2, 1000, "resize rows"));
                        break;
                    case "--wait-ms":
                        waitMilliseconds = ParseInteger(RequiredValue(args, ref index, "--wait-ms"), 1000, 300000, "wait timeout");
                        break;
                    case "--max-buffer-bytes":
                        maximumBufferBytes = ParseLong(RequiredValue(args, ref index, "--max-buffer-bytes"), 1024, 1024L * 1024 * 1024, "maximum buffer bytes");
                        break;
                    case "--diagnostics": diagnostics = true; break;
                    case "--inspect-buffer": inspect = true; break;
                    case "--test-disconnect-after-frames":
                        testDisconnectAfterFrames = ParseInteger(
                            RequiredValue(args, ref index, "--test-disconnect-after-frames"),
                            1,
                            1000000,
                            "test disconnect frame count");
                        break;
                    default: throw new ArgumentException("Unknown argument: " + args[index]);
                }
            }

            Guid sessionId;
            if (!Guid.TryParse(sessionText, out sessionId) || sessionId == Guid.Empty)
            {
                throw new ArgumentException("--session-id must be a non-empty GUID.");
            }
            if (string.IsNullOrWhiteSpace(bufferDirectory))
            {
                throw new ArgumentException("--buffer-directory is required.");
            }

            if (inspect)
            {
                return new AgentOptions
                {
                    SessionId = sessionId,
                    BufferDirectory = Path.GetFullPath(bufferDirectory),
                    InspectBuffer = true
                };
            }
            if (string.IsNullOrWhiteSpace(pipeName) || pipeName.Length > 200 ||
                pipeName.IndexOfAny(new[] { '\\', '/' }) >= 0)
            {
                throw new ArgumentException("--pipe-name is invalid.");
            }
            if (string.IsNullOrWhiteSpace(tokenText)) throw new ArgumentException("--handshake-token is required.");
            if (testDisconnectAfterFrames > 0 && !diagnostics)
            {
                throw new ArgumentException("--test-disconnect-after-frames requires --diagnostics.");
            }
            if (useRemoteInputV2 && string.IsNullOrWhiteSpace(inputText))
            {
                throw new ArgumentException("--remote-input-v2 requires --input-base64.");
            }
            if ((columns == 0) != (rows == 0))
            {
                throw new ArgumentException("resize columns and rows must be supplied together.");
            }

            byte[] inputBytes = null;
            Guid inputId = Guid.Empty;
            if (!string.IsNullOrWhiteSpace(inputText))
            {
                inputBytes = Convert.FromBase64String(inputText);
                if (inputBytes.Length == 0 || inputBytes.Length > LocalFrameCodec.MaximumFrameBytes - 64)
                {
                    throw new ArgumentException("input bytes are empty or too large.");
                }
                if (!Guid.TryParse(inputIdText, out inputId) || inputId == Guid.Empty)
                {
                    throw new ArgumentException("--input-id is required with --input-base64.");
                }
            }

            return new AgentOptions
            {
                PipeName = pipeName,
                SessionId = sessionId,
                HandshakeToken = LocalPayloadCodec.ParseToken(tokenText),
                BufferDirectory = Path.GetFullPath(bufferDirectory),
                InputBytes = inputBytes,
                InputId = inputId,
                DuplicateInputAttempts = duplicateAttempts,
                UseRemoteInputV2 = useRemoteInputV2,
                ResizeColumns = columns,
                ResizeRows = rows,
                WaitMilliseconds = waitMilliseconds,
                MaximumBufferBytes = maximumBufferBytes,
                Diagnostics = diagnostics,
                TestDisconnectAfterFrames = testDisconnectAfterFrames
            };
        }

        private static string RequiredValue(string[] args, ref int index, string option)
        {
            if (++index >= args.Length || string.IsNullOrEmpty(args[index]))
            {
                throw new ArgumentException(option + " requires a value.");
            }
            return args[index];
        }

        private static int ParseInteger(string value, int minimum, int maximum, string label)
        {
            int parsed;
            if (!int.TryParse(value, out parsed) || parsed < minimum || parsed > maximum)
            {
                throw new ArgumentException(label + " must be from " + minimum + " to " + maximum + ".");
            }
            return parsed;
        }

        private static long ParseLong(string value, long minimum, long maximum, string label)
        {
            long parsed;
            if (!long.TryParse(value, out parsed) || parsed < minimum || parsed > maximum)
            {
                throw new ArgumentException(label + " must be from " + minimum + " to " + maximum + ".");
            }
            return parsed;
        }
    }

    internal sealed class SessionAgentSnapshot
    {
        public SessionAgentLifecycle Lifecycle { get; set; }
        public int HostProcessId { get; set; }
        public ulong LastSequence { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public int? ExitCode { get; set; }
    }

    internal sealed class SessionAgent : IDisposable
    {
        private readonly AgentOptions options;
        private readonly EncryptedEventBuffer eventBuffer;
        private readonly InputDeduplicator inputDeduplicator;
        private readonly CommandSequenceStore commandSequenceStore;
        private readonly Action<LocalFrame> committedFrame;
        private readonly object statusSync = new object();
        private readonly object commandSync = new object();
        private NamedPipeServerStream waitingPipe;
        private NamedPipeServerStream connectedPipe;
        private SessionAgentLifecycle lifecycle = SessionAgentLifecycle.Starting;
        private DateTime lastActivityUtc = DateTime.UtcNow;
        private int actualHostProcessId;
        private volatile bool stopRequested;
        private bool commitInterruptedOnStop;
        private ulong lastHostSequence;
        private ulong nextCommandSequence;
        private long outputBytes;
        private int outputFrames;
        private int persistedFrames;
        private int acceptedInputs;
        private int duplicateInputs;
        private int remoteInputAcceptedResults;
        private int remoteInputRejectedResults;
        private int reconnects;
        private int resizeApplied;
        private bool commandsSent;
        private bool testDisconnectTriggered;
        private volatile bool remoteInputRejectSupported;
        private bool ended;
        private bool removed;
        private bool interruptedCommitted;
        private int exitCode;
        private bool disposed;

        public SessionAgent(AgentOptions options, Action<LocalFrame> committedFrame = null)
        {
            this.options = options;
            this.committedFrame = committedFrame;
            eventBuffer = new EncryptedEventBuffer(options.BufferDirectory, options.SessionId, options.MaximumBufferBytes);
            lastHostSequence = eventBuffer.LastSequence;
            inputDeduplicator = new InputDeduplicator(options.BufferDirectory, options.SessionId);
            commandSequenceStore = new CommandSequenceStore(options.BufferDirectory, options.SessionId);
            nextCommandSequence = commandSequenceStore.Value;
        }

        public int Run()
        {
            RunCore();
            CommitInterruptedLifecycle();

            if (!ended)
            {
                throw new InvalidOperationException(
                    "Session Agent stopped before the Host published its ended lifecycle event (" +
                    Snapshot.Lifecycle + ").");
            }
            if (options.Diagnostics)
            {
                WriteDiagnostics();
            }
            return exitCode;
        }

        public void RunManaged()
        {
            try
            {
                RunCore();
            }
            catch
            {
                Transition(SessionAgentLifecycle.Faulted);
                try { CommitInterruptedLifecycle(); }
                catch { }
                throw;
            }
            CommitInterruptedLifecycle();
        }

        private void CommitInterruptedLifecycle()
        {
            var terminalFrames = new List<LocalFrame>(2);
            lock (statusSync)
            {
                var stoppedWithInterruption = lifecycle == SessionAgentLifecycle.RemoteStopped && commitInterruptedOnStop;
                if (removed || interruptedCommitted ||
                    (!ended && lifecycle != SessionAgentLifecycle.HostLost &&
                     lifecycle != SessionAgentLifecycle.Faulted && !stoppedWithInterruption))
                {
                    return;
                }
                if (!ended)
                {
                    if (lastHostSequence == ulong.MaxValue)
                        throw new InvalidDataException("Session event sequence is exhausted.");
                    var interrupted = new LocalFrame(
                        LocalFrameType.Lifecycle,
                        options.SessionId,
                        lastHostSequence + 1,
                        LocalPayloadCodec.Lifecycle(LocalLifecycleState.Interrupted, 0));
                    eventBuffer.Append(interrupted);
                    terminalFrames.Add(interrupted);
                    lastHostSequence = interrupted.Sequence;
                    persistedFrames++;
                }
                if (lastHostSequence == ulong.MaxValue)
                    throw new InvalidDataException("Session event sequence is exhausted.");
                var removal = new LocalFrame(
                    LocalFrameType.SessionRemoved,
                    options.SessionId,
                    lastHostSequence + 1,
                    LocalPayloadCodec.SessionRemoved());
                eventBuffer.Append(removal);
                terminalFrames.Add(removal);
                lastHostSequence = removal.Sequence;
                lastActivityUtc = DateTime.UtcNow;
                persistedFrames += 1;
                removed = true;
                interruptedCommitted = true;
            }

            if (committedFrame != null)
            {
                foreach (var terminal in terminalFrames)
                {
                    try { committedFrame(terminal); }
                    catch { }
                }
            }
        }

        public SessionAgentSnapshot Snapshot
        {
            get
            {
                lock (statusSync)
                {
                    return new SessionAgentSnapshot
                    {
                        Lifecycle = lifecycle,
                        HostProcessId = actualHostProcessId,
                        LastSequence = lastHostSequence,
                        LastActivityUtc = lastActivityUtc,
                        ExitCode = ended ? (int?)exitCode : null
                    };
                }
            }
        }

        internal IList<LocalFrame> ReadBufferedEvents(
            ulong resumeFromSequence,
            out ulong earliestRetainedSequence,
            out ulong latestSequence)
        {
            return eventBuffer.ReadFrom(
                resumeFromSequence,
                out earliestRetainedSequence,
                out latestSequence);
        }

        internal IList<LocalFrame> ReadBufferedEvents(
            ulong resumeFromSequence,
            int maximumEvents,
            long maximumStoredBytes,
            out ulong earliestRetainedSequence,
            out ulong latestSequence,
            out bool hasMore)
        {
            return eventBuffer.ReadFrom(
                resumeFromSequence,
                maximumEvents,
                maximumStoredBytes,
                out earliestRetainedSequence,
                out latestSequence,
                out hasMore);
        }

        internal void DeleteBufferedEvents()
        {
            eventBuffer.DeleteAll();
        }

        public void RequestRemoteStop()
        {
            RequestRemoteStop(false);
        }

        public void RequestRemoteStop(bool commitInterruptedLifecycle)
        {
            NamedPipeServerStream wait;
            NamedPipeServerStream connected;
            lock (statusSync)
            {
                if (stopRequested)
                {
                    if (commitInterruptedLifecycle) commitInterruptedOnStop = true;
                    return;
                }
                commitInterruptedOnStop = commitInterruptedLifecycle;
                stopRequested = true;
                lifecycle = SessionAgentLifecycle.RemoteStopped;
                lastActivityUtc = DateTime.UtcNow;
                wait = waitingPipe;
                connected = connectedPipe;
            }
            DisposePipe(connected);
            if (!ReferenceEquals(wait, connected)) DisposePipe(wait);
        }

        private void RunCore()
        {
            var disconnectedDeadline = DateTime.UtcNow.AddMilliseconds(options.WaitMilliseconds);
            while (!removed && !stopRequested)
            {
                if (actualHostProcessId > 0 && !IsProcessRunning(actualHostProcessId))
                {
                    Transition(SessionAgentLifecycle.HostLost);
                    return;
                }

                using (var pipe = CreatePipe(options.PipeName))
                {
                    SetWaitingPipe(pipe);
                    if (!WaitForConnection(pipe, disconnectedDeadline))
                    {
                        SetWaitingPipe(null);
                        if (stopRequested) return;
                        Transition(SessionAgentLifecycle.HostLost);
                        return;
                    }
                    SetWaitingPipe(null);
                    try
                    {
                        remoteInputRejectSupported = false;
                        Authenticate(pipe);
                        SetConnectedPipe(pipe);
                        if (lastHostSequence > 0) reconnects++;
                        ProcessConnection(pipe);
                    }
                    catch (EndOfStreamException)
                    {
                    }
                    catch (IOException)
                    {
                    }
                    catch (ObjectDisposedException)
                    {
                        if (!stopRequested) throw;
                    }
                    finally
                    {
                        SetConnectedPipe(null);
                    }
                }

                if (!removed && !stopRequested)
                {
                    if (actualHostProcessId > 0 && !IsProcessRunning(actualHostProcessId))
                    {
                        Transition(SessionAgentLifecycle.HostLost);
                        return;
                    }
                    Transition(SessionAgentLifecycle.Disconnected);
                    disconnectedDeadline = DateTime.UtcNow.AddMilliseconds(options.WaitMilliseconds);
                }
            }
        }

        private void WriteDiagnostics()
        {
            Console.WriteLine(
                "__AGENT_RESULT__:session=" + options.SessionId.ToString("D") +
                ";last_seq=" + lastHostSequence +
                ";frames=" + persistedFrames +
                ";output_frames=" + outputFrames +
                ";output_bytes=" + outputBytes +
                ";inputs_accepted=" + acceptedInputs +
                ";inputs_duplicate=" + duplicateInputs +
                ";remote_input_results_accepted=" + remoteInputAcceptedResults +
                ";remote_input_results_rejected=" + remoteInputRejectedResults +
                ";resize_applied=" + resizeApplied +
                ";reconnects=" + reconnects +
                ";exit=" + exitCode);
        }

        private void ProcessConnection(NamedPipeServerStream pipe)
        {
            while (!removed && pipe.IsConnected)
            {
                var frame = LocalFrameCodec.Read(pipe);
                if (frame == null) break;
                if (frame.SessionId != options.SessionId)
                {
                    throw new InvalidDataException("Host sent a frame for another session.");
                }
                if (frame.Type == LocalFrameType.HostHello || frame.Type == LocalFrameType.AgentHello ||
                    frame.Type == LocalFrameType.Input || frame.Type == LocalFrameType.ResizeRequest ||
                    frame.Type == LocalFrameType.RemoteInputV2 || frame.Type == LocalFrameType.RemoteInputReject)
                {
                    throw new InvalidDataException("Host sent an invalid frame type: " + frame.Type + ".");
                }
                if (frame.Type == LocalFrameType.Capabilities)
                {
                    if (frame.Sequence != 0) throw new InvalidDataException("Host capabilities must use sequence zero.");
                    remoteInputRejectSupported = LocalPayloadCodec.ParseCapabilities(frame.Payload);
                    continue;
                }
                if (frame.Sequence <= lastHostSequence)
                {
                    SendAck(pipe, frame.Sequence);
                    continue;
                }
                if (frame.Type == LocalFrameType.Gap)
                {
                    ulong gapStart;
                    ulong gapEnd;
                    LocalPayloadCodec.ParseGap(frame.Payload, out gapStart, out gapEnd);
                    if (gapStart > lastHostSequence + 1 ||
                        frame.Sequence == 0 || gapEnd != frame.Sequence - 1)
                    {
                        throw new InvalidDataException(
                            "Invalid Host gap for next sequence " + (lastHostSequence + 1) +
                            " through " + (frame.Sequence - 1) + ".");
                    }
                }
                else if (frame.Sequence != lastHostSequence + 1)
                {
                    throw new InvalidDataException(
                        "Host sequence gap: expected " + (lastHostSequence + 1) + ", received " + frame.Sequence + ".");
                }

                if (frame.Type != LocalFrameType.Ack)
                {
                    eventBuffer.Append(frame);
                    persistedFrames++;
                    if (committedFrame != null)
                    {
                        try { committedFrame(frame); }
                        catch { }
                    }
                }
                lock (statusSync)
                {
                    lastHostSequence = frame.Sequence;
                    lastActivityUtc = DateTime.UtcNow;
                }

                switch (frame.Type)
                {
                    case LocalFrameType.Output:
                        outputFrames++;
                        outputBytes += frame.Payload.Length;
                        break;
                    case LocalFrameType.Resize:
                        short columns;
                        short rows;
                        LocalPayloadCodec.ParseResize(frame.Payload, out columns, out rows);
                        resizeApplied++;
                        break;
                    case LocalFrameType.Lifecycle:
                        int lifecycleExit;
                        var state = LocalPayloadCodec.ParseLifecycle(frame.Payload, out lifecycleExit);
                        if (state == LocalLifecycleState.Running) SendInitialCommands(pipe);
                        if (state == LocalLifecycleState.Ended)
                        {
                            ended = true;
                            exitCode = lifecycleExit;
                            Transition(SessionAgentLifecycle.Ended);
                        }
                        break;
                    case LocalFrameType.SessionRemoved:
                        LocalPayloadCodec.ParseSessionRemoved(frame.Payload);
                        if (!ended)
                            throw new InvalidDataException("Host removed a session before publishing its ended lifecycle.");
                        removed = true;
                        break;
                    case LocalFrameType.Gap:
                        break;
                    case LocalFrameType.RemoteInputResult:
                        LocalRemoteInputResult remoteInputResult;
                        LocalPayloadCodec.ParseRemoteInputResult(frame.Payload, out remoteInputResult);
                        if (remoteInputResult == LocalRemoteInputResult.Accepted) remoteInputAcceptedResults++;
                        else remoteInputRejectedResults++;
                        break;
                    case LocalFrameType.Ack:
                        break;
                }
                if (!testDisconnectTriggered && options.TestDisconnectAfterFrames > 0 &&
                    persistedFrames >= options.TestDisconnectAfterFrames && !ended)
                {
                    testDisconnectTriggered = true;
                    throw new IOException("Test-only Agent disconnect after persisted frame threshold.");
                }
                SendAck(pipe, frame.Sequence);
            }
        }

        private void SendInitialCommands(NamedPipeServerStream pipe)
        {
            if (commandsSent) return;
            commandsSent = true;
            if (options.InputBytes != null)
            {
                for (var attempt = 0; attempt < options.DuplicateInputAttempts; attempt++)
                {
                    if (inputDeduplicator.TryAccept(options.InputId))
                    {
                        if (options.UseRemoteInputV2)
                        {
                            Send(pipe, LocalFrameType.RemoteInputV2,
                                LocalPayloadCodec.RemoteInput(options.InputId, 0, options.InputBytes));
                        }
                        else
                        {
                            Send(pipe, LocalFrameType.Input,
                                LocalPayloadCodec.Input(options.InputId, options.InputBytes));
                        }
                        acceptedInputs++;
                    }
                    else
                    {
                        duplicateInputs++;
                        if (options.UseRemoteInputV2)
                        {
                            TrySendRemoteInputRejection(
                                options.InputId,
                                LocalRemoteInputResult.InputSequenceRejected);
                        }
                    }
                }
            }
        }

        private void SendAck(NamedPipeServerStream pipe, ulong sequence)
        {
            lock (commandSync)
            {
                LocalFrameCodec.Write(pipe,
                    new LocalFrame(LocalFrameType.Ack, options.SessionId, sequence, new byte[0]));
            }
        }

        private void Send(NamedPipeServerStream pipe, LocalFrameType type, byte[] payload)
        {
            lock (commandSync)
            {
                nextCommandSequence = checked(nextCommandSequence + 1);
                commandSequenceStore.Save(nextCommandSequence);
                LocalFrameCodec.Write(pipe,
                    new LocalFrame(type, options.SessionId, nextCommandSequence, payload));
            }
        }

        public bool TrySendRemoteInput(Guid inputId, ulong expectedLocalInputEpoch, byte[] bytes)
        {
            if (inputId == Guid.Empty || bytes == null || bytes.Length == 0) return false;
            lock (commandSync)
            {
                NamedPipeServerStream pipe;
                lock (statusSync) pipe = connectedPipe;
                if (pipe == null || !pipe.IsConnected || !inputDeduplicator.TryAccept(inputId)) return false;
                nextCommandSequence = checked(nextCommandSequence + 1);
                commandSequenceStore.Save(nextCommandSequence);
                try
                {
                    LocalFrameCodec.Write(pipe, new LocalFrame(
                        LocalFrameType.RemoteInputV2,
                        options.SessionId,
                        nextCommandSequence,
                        LocalPayloadCodec.RemoteInput(inputId, expectedLocalInputEpoch, bytes)));
                    acceptedInputs++;
                    return true;
                }
                catch (IOException) { return false; }
                catch (ObjectDisposedException) { return false; }
            }
        }

        public bool TrySendRemoteInputRejection(Guid inputId, LocalRemoteInputResult result)
        {
            if (inputId == Guid.Empty || result == LocalRemoteInputResult.Accepted ||
                !Enum.IsDefined(typeof(LocalRemoteInputResult), result) || !remoteInputRejectSupported) return false;
            lock (commandSync)
            {
                NamedPipeServerStream pipe;
                lock (statusSync) pipe = connectedPipe;
                if (pipe == null || !pipe.IsConnected) return false;
                nextCommandSequence = checked(nextCommandSequence + 1);
                commandSequenceStore.Save(nextCommandSequence);
                try
                {
                    LocalFrameCodec.Write(pipe, new LocalFrame(
                        LocalFrameType.RemoteInputReject,
                        options.SessionId,
                        nextCommandSequence,
                        LocalPayloadCodec.RemoteInputResult(inputId, result)));
                    return true;
                }
                catch (IOException) { return false; }
                catch (ObjectDisposedException) { return false; }
            }
        }

        private void Authenticate(NamedPipeServerStream pipe)
        {
            var hello = LocalFrameCodec.Read(pipe);
            if (hello == null || hello.Type != LocalFrameType.HostHello ||
                hello.SessionId != options.SessionId || hello.Sequence != 0)
            {
                throw new InvalidDataException("Invalid Session Host handshake.");
            }
            var claimedProcessId = LocalPayloadCodec.ParseHello(hello.Payload, options.HandshakeToken);
            uint actualProcessId;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out actualProcessId) ||
                actualProcessId != claimedProcessId)
            {
                throw new InvalidDataException("Session Host process identity mismatch.");
            }
            if (options.ExpectedHostProcessId > 0 && actualProcessId != options.ExpectedHostProcessId)
            {
                throw new InvalidDataException("Session Host process does not match the Registry registration.");
            }
            lock (statusSync)
            {
                actualHostProcessId = checked((int)actualProcessId);
                lifecycle = SessionAgentLifecycle.Connected;
                lastActivityUtc = DateTime.UtcNow;
            }
            LocalFrameCodec.Write(pipe, new LocalFrame(
                LocalFrameType.AgentHello,
                options.SessionId,
                0,
                LocalPayloadCodec.Hello(Process.GetCurrentProcess().Id, options.HandshakeToken)));
        }

        private static NamedPipeServerStream CreatePipe(string pipeName)
        {
            return CurrentUserPipe.Create(
                pipeName,
                1,
                PipeOptions.Asynchronous,
                64 * 1024,
                64 * 1024);
        }

        private bool WaitForConnection(NamedPipeServerStream pipe, DateTime deadline)
        {
            var asyncResult = pipe.BeginWaitForConnection(null, null);
            while (!stopRequested)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return false;
                var slice = remaining.TotalMilliseconds > 100 ? 100 : Math.Max(1, (int)remaining.TotalMilliseconds);
                if (asyncResult.AsyncWaitHandle.WaitOne((int)slice))
                {
                    try
                    {
                        pipe.EndWaitForConnection(asyncResult);
                        return true;
                    }
                    catch (ObjectDisposedException)
                    {
                        if (stopRequested) return false;
                        throw;
                    }
                }
                if (actualHostProcessId > 0 && !IsProcessRunning(actualHostProcessId)) return false;
            }
            return false;
        }

        private void SetWaitingPipe(NamedPipeServerStream pipe)
        {
            lock (statusSync) waitingPipe = pipe;
        }

        private void SetConnectedPipe(NamedPipeServerStream pipe)
        {
            lock (statusSync) connectedPipe = pipe;
        }

        private void Transition(SessionAgentLifecycle value)
        {
            lock (statusSync)
            {
                lifecycle = value;
                lastActivityUtc = DateTime.UtcNow;
            }
        }

        private static bool IsProcessRunning(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId)) return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }

        private static void DisposePipe(NamedPipeServerStream pipe)
        {
            if (pipe == null) return;
            try { pipe.Dispose(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            RequestRemoteStop();
            eventBuffer.Dispose();
            inputDeduplicator.Dispose();
            commandSequenceStore.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
    }

    internal sealed class InputDeduplicator : IDisposable
    {
        private const int MaximumEntries = 4096;
        private readonly string path;
        private readonly byte[] entropy;
        private readonly List<Guid> ordered = new List<Guid>();
        private readonly HashSet<Guid> known = new HashSet<Guid>();

        public InputDeduplicator(string bufferDirectory, Guid sessionId)
        {
            Directory.CreateDirectory(bufferDirectory);
            path = Path.Combine(bufferDirectory, "input-dedupe.dat");
            entropy = sessionId.ToByteArray();
            Load();
        }

        public bool TryAccept(Guid inputId)
        {
            if (inputId == Guid.Empty) throw new ArgumentException("input ID is required", "inputId");
            if (known.Contains(inputId)) return false;
            known.Add(inputId);
            ordered.Add(inputId);
            while (ordered.Count > MaximumEntries)
            {
                known.Remove(ordered[0]);
                ordered.RemoveAt(0);
            }
            Save();
            return true;
        }

        private void Load()
        {
            if (!File.Exists(path)) return;
            var protectedBytes = File.ReadAllBytes(path);
            var bytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
            if (bytes.Length < 4) throw new InvalidDataException("Invalid input dedupe file.");
            var count = checked((int)LocalFrameCodec.ReadUInt32(bytes, 0));
            if (count < 0 || count > MaximumEntries || bytes.Length != 4 + count * 16)
            {
                throw new InvalidDataException("Invalid input dedupe entry count.");
            }
            for (var index = 0; index < count; index++)
            {
                var guid = new byte[16];
                Buffer.BlockCopy(bytes, 4 + index * 16, guid, 0, 16);
                var value = new Guid(guid);
                if (value == Guid.Empty || !known.Add(value))
                {
                    throw new InvalidDataException("Invalid input dedupe entry.");
                }
                ordered.Add(value);
            }
        }

        private void Save()
        {
            var bytes = new byte[4 + ordered.Count * 16];
            LocalFrameCodec.WriteUInt32(bytes, 0, checked((uint)ordered.Count));
            for (var index = 0; index < ordered.Count; index++)
            {
                Buffer.BlockCopy(ordered[index].ToByteArray(), 0, bytes, 4 + index * 16, 16);
            }
            var protectedBytes = ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser);
            AtomicWrite(path, protectedBytes);
        }

        public void Dispose()
        {
        }

        internal static void AtomicWrite(string path, byte[] bytes)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
    }

    internal sealed class CommandSequenceStore : IDisposable
    {
        private readonly string path;
        private readonly byte[] entropy;
        public ulong Value { get; private set; }

        public CommandSequenceStore(string bufferDirectory, Guid sessionId)
        {
            Directory.CreateDirectory(bufferDirectory);
            path = Path.Combine(bufferDirectory, "command-sequence.dat");
            entropy = sessionId.ToByteArray();
            if (!File.Exists(path)) return;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), entropy, DataProtectionScope.CurrentUser);
            if (bytes.Length != 8) throw new InvalidDataException("Invalid command sequence state.");
            Value = LocalFrameCodec.ReadUInt64(bytes, 0);
        }

        public void Save(ulong value)
        {
            if (value == 0 || value <= Value) throw new InvalidOperationException("Command sequence must increase.");
            var bytes = new byte[8];
            LocalFrameCodec.WriteUInt64(bytes, 0, value);
            InputDeduplicator.AtomicWrite(path, ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser));
            Value = value;
        }

        public void Dispose() { }
    }

    internal sealed class EncryptedEventBuffer : IDisposable
    {
        private readonly string directory;
        private readonly byte[] entropy;
        private readonly long maximumBytes;
        private readonly object sync = new object();
        private readonly LinkedList<FileInfo> chunks = new LinkedList<FileInfo>();
        private long storedBytes;
        private ulong lastSequence;

        public ulong LastSequence
        {
            get { lock (sync) return lastSequence; }
        }

        public EncryptedEventBuffer(string directory, Guid sessionId, long maximumBytes)
        {
            this.directory = directory;
            entropy = sessionId.ToByteArray();
            this.maximumBytes = maximumBytes;
            Directory.CreateDirectory(directory);
            var existing = new List<FileInfo>(new DirectoryInfo(directory).GetFiles("*.chunk"));
            existing.Sort(delegate(FileInfo left, FileInfo right)
            {
                return string.CompareOrdinal(left.Name, right.Name);
            });
            foreach (var file in existing)
            {
                ulong sequence;
                if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file.Name), out sequence) ||
                    sequence == 0 || sequence <= lastSequence)
                {
                    throw new InvalidDataException("Invalid encrypted event chunk name: " + file.Name);
                }
                chunks.AddLast(file);
                storedBytes += file.Length;
                lastSequence = sequence;
            }
            Trim();
        }

        public void Append(LocalFrame frame)
        {
            if (frame.Type == LocalFrameType.Input || frame.Type == LocalFrameType.ResizeRequest ||
                frame.Type == LocalFrameType.RemoteInputV2 || frame.Type == LocalFrameType.RemoteInputReject ||
                frame.Type == LocalFrameType.HostHello || frame.Type == LocalFrameType.Capabilities ||
                frame.Type == LocalFrameType.AgentHello)
            {
                throw new InvalidOperationException("Input or handshake frames must not enter the event buffer.");
            }
            lock (sync)
            {
                if (frame.Sequence == 0 || frame.Sequence <= lastSequence)
                {
                    throw new InvalidDataException("Event sequence must increase monotonically.");
                }
                var encoded = LocalFrameCodec.Encode(frame);
                var protectedBytes = ProtectedData.Protect(encoded, entropy, DataProtectionScope.CurrentUser);
                var stored = new byte[8 + protectedBytes.Length];
                stored[0] = (byte)'T'; stored[1] = (byte)'L'; stored[2] = (byte)'B'; stored[3] = (byte)'1';
                LocalFrameCodec.WriteUInt32(stored, 4, checked((uint)protectedBytes.Length));
                Buffer.BlockCopy(protectedBytes, 0, stored, 8, protectedBytes.Length);
                var path = Path.Combine(directory, frame.Sequence.ToString("D20") + ".chunk");
                InputDeduplicator.AtomicWrite(path, stored);
                var chunk = new FileInfo(path);
                chunks.AddLast(chunk);
                storedBytes += chunk.Length;
                lastSequence = frame.Sequence;
                Trim();
            }
        }

        private void Trim()
        {
            while (storedBytes > maximumBytes && chunks.First != null)
            {
                var file = chunks.First.Value;
                chunks.RemoveFirst();
                storedBytes -= file.Length;
                file.Delete();
            }
        }

        public void DeleteAll()
        {
            lock (sync)
            {
                while (chunks.First != null)
                {
                    var file = chunks.First.Value;
                    chunks.RemoveFirst();
                    try { file.Delete(); }
                    catch (FileNotFoundException) { }
                }
                storedBytes = 0;
            }
        }

        public IList<LocalFrame> ReadFrom(
            ulong resumeFromSequence,
            out ulong earliestRetainedSequence,
            out ulong latestSequence)
        {
            bool hasMore;
            return ReadFrom(
                resumeFromSequence,
                int.MaxValue,
                long.MaxValue,
                out earliestRetainedSequence,
                out latestSequence,
                out hasMore);
        }

        public IList<LocalFrame> ReadFrom(
            ulong resumeFromSequence,
            int maximumEvents,
            long maximumStoredBytes,
            out ulong earliestRetainedSequence,
            out ulong latestSequence,
            out bool hasMore)
        {
            if (maximumEvents < 1) throw new ArgumentOutOfRangeException("maximumEvents");
            if (maximumStoredBytes < 1) throw new ArgumentOutOfRangeException("maximumStoredBytes");
            var storedChunks = new List<byte[]>();
            long selectedStoredBytes = 0;
            hasMore = false;
            lock (sync)
            {
                latestSequence = lastSequence;
                if (chunks.First != null)
                {
                    earliestRetainedSequence = ParseChunkSequence(chunks.First.Value);
                }
                else
                {
                    earliestRetainedSequence = lastSequence == 0 || lastSequence == ulong.MaxValue
                        ? 0
                        : lastSequence + 1;
                }

                foreach (var chunk in chunks)
                {
                    var sequence = ParseChunkSequence(chunk);
                    if (sequence > resumeFromSequence)
                    {
                        if (storedChunks.Count >= maximumEvents ||
                            (storedChunks.Count > 0 &&
                             chunk.Length > maximumStoredBytes - selectedStoredBytes))
                        {
                            hasMore = true;
                            break;
                        }
                        // Read while holding the buffer lock so Trim cannot remove a
                        // chunk after the path snapshot and before decryption.
                        var stored = File.ReadAllBytes(chunk.FullName);
                        storedChunks.Add(stored);
                        selectedStoredBytes = checked(selectedStoredBytes + stored.LongLength);
                    }
                }
            }

            var frames = new List<LocalFrame>(storedChunks.Count);
            foreach (var stored in storedChunks)
            {
                try { frames.Add(DecodeStored(stored)); }
                finally { Array.Clear(stored, 0, stored.Length); }
            }
            return frames;
        }

        private LocalFrame DecodeStored(byte[] stored)
        {
            if (stored == null || stored.Length < 8 || stored[0] != 'T' || stored[1] != 'L' ||
                stored[2] != 'B' || stored[3] != '1')
            {
                throw new InvalidDataException("Invalid encrypted event chunk.");
            }
            var length = checked((int)LocalFrameCodec.ReadUInt32(stored, 4));
            if (length != stored.Length - 8) throw new InvalidDataException("Invalid encrypted event chunk length.");
            var protectedBytes = new byte[length];
            Buffer.BlockCopy(stored, 8, protectedBytes, 0, length);
            try
            {
                var encoded = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
                try { return LocalFrameCodec.Decode(encoded); }
                finally { Array.Clear(encoded, 0, encoded.Length); }
            }
            finally { Array.Clear(protectedBytes, 0, protectedBytes.Length); }
        }

        private static ulong ParseChunkSequence(FileInfo file)
        {
            ulong sequence;
            if (!ulong.TryParse(Path.GetFileNameWithoutExtension(file.Name), out sequence) || sequence == 0)
            {
                throw new InvalidDataException("Invalid encrypted event chunk name: " + file.Name);
            }
            return sequence;
        }

        public static string Inspect(string directory, Guid sessionId)
        {
            var files = new List<FileInfo>(new DirectoryInfo(directory).GetFiles("*.chunk"));
            files.Sort(delegate(FileInfo left, FileInfo right)
            {
                return string.CompareOrdinal(left.Name, right.Name);
            });
            var entropy = sessionId.ToByteArray();
            var frameCount = 0;
            var outputFrames = 0;
            long outputBytes = 0;
            var inputFrames = 0;
            var gapFrames = 0;
            var removedFrames = 0;
            ulong firstSequence = 0;
            ulong lastSequence = 0;
            using (var hash = SHA256.Create())
            using (var output = new MemoryStream())
            {
                foreach (var file in files)
                {
                    var stored = File.ReadAllBytes(file.FullName);
                    if (stored.Length < 8 || stored[0] != 'T' || stored[1] != 'L' ||
                        stored[2] != 'B' || stored[3] != '1')
                    {
                        throw new InvalidDataException("Invalid encrypted event chunk: " + file.Name);
                    }
                    var length = checked((int)LocalFrameCodec.ReadUInt32(stored, 4));
                    if (length != stored.Length - 8) throw new InvalidDataException("Invalid chunk length.");
                    var protectedBytes = new byte[length];
                    Buffer.BlockCopy(stored, 8, protectedBytes, 0, length);
                    var frame = LocalFrameCodec.Decode(
                        ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser));
                    if (frame.SessionId != sessionId) throw new InvalidDataException("Chunk session mismatch.");
                    if (frame.Type == LocalFrameType.Gap)
                    {
                        ulong gapStart;
                        ulong gapEnd;
                        LocalPayloadCodec.ParseGap(frame.Payload, out gapStart, out gapEnd);
                        if (gapEnd != frame.Sequence - 1 ||
                            (lastSequence > 0 && gapStart > lastSequence + 1))
                        {
                            throw new InvalidDataException("Persisted gap does not cover the missing frame range.");
                        }
                    }
                    if (lastSequence > 0 && frame.Sequence != lastSequence + 1)
                    {
                        if (frame.Type != LocalFrameType.Gap)
                        {
                            throw new InvalidDataException("Persisted frame sequence is not continuous.");
                        }
                    }
                    if (firstSequence == 0) firstSequence = frame.Sequence;
                    lastSequence = frame.Sequence;
                    frameCount++;
                    if (frame.Type == LocalFrameType.Output)
                    {
                        outputFrames++;
                        outputBytes += frame.Payload.Length;
                        output.Write(frame.Payload, 0, frame.Payload.Length);
                    }
                    if (frame.Type == LocalFrameType.Input) inputFrames++;
                    if (frame.Type == LocalFrameType.Gap) gapFrames++;
                    if (frame.Type == LocalFrameType.SessionRemoved)
                    {
                        LocalPayloadCodec.ParseSessionRemoved(frame.Payload);
                        removedFrames++;
                    }
                }
                var digest = hash.ComputeHash(output.ToArray());
                return "__BUFFER_RESULT__:session=" + sessionId.ToString("D") +
                    ";frames=" + frameCount +
                    ";first_seq=" + firstSequence +
                    ";last_seq=" + lastSequence +
                    ";output_frames=" + outputFrames +
                    ";output_bytes=" + outputBytes +
                    ";input_frames=" + inputFrames +
                    ";gap_frames=" + gapFrames +
                    ";removed_frames=" + removedFrames +
                    ";output_sha256=" + ToHex(digest);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            foreach (var value in bytes) builder.Append(value.ToString("x2"));
            return builder.ToString();
        }

        public void Dispose()
        {
        }
    }
}
