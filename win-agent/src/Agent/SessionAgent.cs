using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.Common;
using TermLink.TerminalCore;

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
        private sealed class BrokerPersistenceRetryException : IOException
        {
            internal BrokerPersistenceRetryException(Exception inner)
                : base("Broker Output production is temporarily unavailable.", inner) { }
        }

        private readonly AgentOptions options;
        private readonly EncryptedEventBuffer eventBuffer;
        private readonly InputDeduplicator inputDeduplicator;
        private readonly CommandSequenceStore commandSequenceStore;
        private readonly Action<LocalFrame> committedFrame;
        private readonly Func<Guid, CancellationToken, Task<bool>> authorizeClipboard;
        private readonly Func<string> brokerCaptureRootProvider;
        private string brokerCaptureRoot;
        private int clipboardAuthorizationBusy;
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
        private volatile bool remoteResizeSupported;
        private TaskCompletionSource<LocalResizeResult> pendingResize;
        private Guid pendingResizeId;
        private bool ended;
        private bool removed;
        private bool interruptedCommitted;
        private bool brokerPersistencePending;
        private ulong pendingBrokerSequence;
        private bool brokerRootPending;
        private int exitCode;
        private bool disposed;
        private string terminalCompatibilityError = string.Empty;
        internal TerminalSemanticProfile BoundProfile => eventBuffer.BoundProfile;
        internal string TerminalCompatibilityError
        {
            get
            {
                var persisted = eventBuffer.TerminalCompatibilityError;
                return persisted.Length == 0 ? Volatile.Read(ref terminalCompatibilityError) : persisted;
            }
        }

        public SessionAgent(AgentOptions options, Action<LocalFrame> committedFrame = null,
            Func<Guid, CancellationToken, Task<bool>> authorizeClipboard = null,
            Func<string> brokerCaptureRootProvider = null)
        {
            this.options = options;
            this.committedFrame = committedFrame;
            this.authorizeClipboard = authorizeClipboard;
            this.brokerCaptureRootProvider = brokerCaptureRootProvider;
            eventBuffer = new EncryptedEventBuffer(options.BufferDirectory, options.SessionId, options.MaximumBufferBytes);
            lastHostSequence = eventBuffer.LastSequence;
            brokerPersistencePending = eventBuffer.HasBoundProfile && eventBuffer.LatestFrameIsBrokerOutput();
            if (brokerPersistencePending) pendingBrokerSequence = lastHostSequence;
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
                TryTransitionUnlessStopping(SessionAgentLifecycle.Faulted);
                try { CommitInterruptedLifecycle(); }
                catch { }
                throw;
            }
            CommitInterruptedLifecycle();
        }

        private void CommitInterruptedLifecycle()
        {
            // Never append current semantics to an unauthenticated or incompatible old journal.
            if (!eventBuffer.HasBoundProfile || brokerPersistencePending || brokerRootPending) return;
            var terminalFrames = new List<LocalFrame>(2);
            lock (statusSync)
            {
                WriteSessionAgentDiagnostic("interrupt_commit_enter session=" + options.SessionId.ToString("N") +
                    " lifecycle=" + lifecycle + " removed=" + removed + " ended=" + ended +
                    " interruptedCommitted=" + interruptedCommitted + " commitInterruptedOnStop=" + commitInterruptedOnStop);
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

        internal bool TryGetRemoteInputIdentity(
            Guid inputId,
            out Guid senderDeviceId,
            out byte[] envelopeSha256)
        {
            return inputDeduplicator.TryGetIdentity(
                inputId,
                out senderDeviceId,
                out envelopeSha256);
        }

        internal bool TryFindRemoteInputResult(
            Guid inputId,
            out LocalFrame resultFrame)
        {
            return eventBuffer.TryFindRemoteInputResult(inputId, out resultFrame);
        }

        internal void DeleteBufferedEvents()
        {
            eventBuffer.DeleteAll();
            inputDeduplicator.DeletePersistentState();
            commandSequenceStore.DeletePersistentState();
        }

        public void RequestRemoteStop()
        {
            RequestRemoteStop(false);
        }

        public void RequestRemoteStop(bool commitInterruptedLifecycle)
        {
            NamedPipeServerStream wait;
            NamedPipeServerStream connected;
            lock (commandSync)
            {
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
                if (commitInterruptedLifecycle && connected != null)
                {
                    try
                    {
                        if (!connected.IsConnected) throw new IOException("Session Host pipe is disconnected.");
                        nextCommandSequence = checked(nextCommandSequence + 1);
                        commandSequenceStore.Save(nextCommandSequence);
                        LocalFrameCodec.Write(connected, new LocalFrame(
                            LocalFrameType.RemoteAccessStopped,
                            options.SessionId,
                            nextCommandSequence,
                            LocalPayloadCodec.RemoteAccessStopped()));
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }
            }
            DisposePipe(connected);
            if (!ReferenceEquals(wait, connected)) DisposePipe(wait);
        }

        private void RunCore()
        {
            var disconnectedDeadline = DateTime.UtcNow.AddMilliseconds(options.WaitMilliseconds);
            var brokerRetryDelayMilliseconds = 100;
            while (!removed && !stopRequested)
            {
                if (actualHostProcessId > 0 && !IsProcessRunning(actualHostProcessId))
                {
                    WriteSessionAgentDiagnostic("host_lost session=" + options.SessionId.ToString("N") +
                        " host_pid=" + actualHostProcessId);
                    TryTransitionUnlessStopping(SessionAgentLifecycle.HostLost);
                    return;
                }

                using (var pipe = CreatePipe(options.PipeName))
                {
                    SetWaitingPipe(pipe);
                    if (!WaitForConnection(pipe, disconnectedDeadline))
                    {
                        SetWaitingPipe(null);
                        if (stopRequested) return;
                        TryTransitionUnlessStopping(SessionAgentLifecycle.HostLost);
                        return;
                    }
                    SetWaitingPipe(null);
                    try
                    {
                        remoteInputRejectSupported = false;
                        remoteResizeSupported = false;
                        Authenticate(pipe);
                        SetConnectedPipe(pipe);
                        if (lastHostSequence > 0) reconnects++;
                        ConfirmRetainedBrokerOutput();
                        if (!brokerPersistencePending) brokerRetryDelayMilliseconds = 100;
                        ProcessConnection(pipe);
                    }
                    catch (TerminalLocalProtocolException)
                    {
                        Volatile.Write(ref terminalCompatibilityError, "TERMINAL_LOCAL_PROTOCOL_MISMATCH");
                        WriteSessionAgentDiagnostic("terminal_protocol_mismatch session=" + options.SessionId.ToString("N"));
                        // Keep the existing host and its history intact. No
                        // durable output or input crosses an unaccepted handshake.
                        Thread.Sleep(1000);
                    }
                    catch (TerminalHistoryProfileException error)
                    {
                        Volatile.Write(ref terminalCompatibilityError, error.Code);
                        WriteSessionAgentDiagnostic("terminal_history_profile_rejected session=" + options.SessionId.ToString("N") + " code=" + error.Code);
                        Thread.Sleep(1000);
                    }
                    catch (EndOfStreamException)
                    {
                    }
                    catch (BrokerPersistenceRetryException)
                    {
                        if (!stopRequested) Thread.Sleep(brokerRetryDelayMilliseconds);
                        brokerRetryDelayMilliseconds = Math.Min(500, brokerRetryDelayMilliseconds * 2);
                    }
                    catch (InvalidDataException)
                    {
                        throw;
                    }
                    catch (IOException)
                    {
                        if (brokerPersistencePending && !stopRequested)
                        {
                            Thread.Sleep(brokerRetryDelayMilliseconds);
                            brokerRetryDelayMilliseconds = Math.Min(500, brokerRetryDelayMilliseconds * 2);
                        }
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
                        TryTransitionUnlessStopping(SessionAgentLifecycle.HostLost);
                        return;
                    }
                    TryTransitionUnlessStopping(SessionAgentLifecycle.Disconnected);
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

        // Temporary lifecycle diagnostics: bounded structural metadata only.
        private static void WriteSessionAgentDiagnostic(string message)
        {
            try
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TermLink", "logs");
                Directory.CreateDirectory(root);
                File.AppendAllText(
                    Path.Combine(root, "TermLink-background-runtime.log"),
                    DateTime.UtcNow.ToString("O") + " session-agent-diag " + message + Environment.NewLine,
                    new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never become a request-path dependency.
            }
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
                    frame.Type == LocalFrameType.RemoteInputV2 || frame.Type == LocalFrameType.RemoteInputReject ||
                    frame.Type == LocalFrameType.RemoteAccessStopped || frame.Type == LocalFrameType.ClipboardAuthorization)
                {
                    throw new InvalidDataException("Host sent an invalid frame type: " + frame.Type + ".");
                }
                if (frame.Type == LocalFrameType.Capabilities)
                {
                    if (frame.Sequence != 0) throw new InvalidDataException("Host capabilities must use sequence zero.");
                    remoteInputRejectSupported = LocalPayloadCodec.ParseCapabilities(frame.Payload);
                    remoteResizeSupported = (frame.Payload[1] & 2) != 0;
                    continue;
                }
                if (frame.Type == LocalFrameType.ClipboardAuthorize)
                {
                    if (frame.Sequence != 0 || frame.Payload.Length != 16 || new Guid(frame.Payload) == Guid.Empty)
                        throw new InvalidDataException("Invalid clipboard authorization request.");
                    if (Interlocked.CompareExchange(ref clipboardAuthorizationBusy, 1, 0) != 0) continue;
                    // Network authorization must never hold up output persistence or ACKs.
                    _ = Task.Run(async () => {
                        var allowed = false;
                        try {
                            using var deadline = new CancellationTokenSource(2500);
                            allowed = authorizeClipboard != null &&
                                await authorizeClipboard(options.SessionId, deadline.Token).ConfigureAwait(false);
                        } catch { }
                        try {
                            var response = new byte[17]; Buffer.BlockCopy(frame.Payload, 0, response, 0, 16);
                            response[16] = allowed ? (byte)1 : (byte)0;
                            lock (commandSync) {
                                lock (statusSync) if (stopRequested || !ReferenceEquals(connectedPipe, pipe)) return;
                                LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.ClipboardAuthorization, options.SessionId, 0, response));
                            }
                        } catch (IOException) { } catch (ObjectDisposedException) { }
                        finally { Volatile.Write(ref clipboardAuthorizationBusy, 0); }
                    });
                    continue;
                }
                if (frame.Type == LocalFrameType.HostResizeResult &&
                    LocalPayloadCodec.ParseHostResizeResult(frame.Payload).SourceSequence >= frame.Sequence)
                    throw new InvalidDataException("Host resize result precedes its source request.");
                if (frame.Type == LocalFrameType.ResizeResult)
                {
                    if (frame.Sequence != 0) throw new InvalidDataException("Resize results must use sequence zero.");
                    var result = LocalPayloadCodec.ParseResizeResult(frame.Payload);
                    lock (commandSync)
                        if (result.Id == pendingResizeId) pendingResize?.TrySetResult(result);
                    continue;
                }
                if (frame.Type == LocalFrameType.HostTheme)
                    TermLink.TerminalCore.NativeTerminalHostThemeV1.Decode(frame.Payload);
                if (frame.Type == LocalFrameType.HostGeometry)
                    TermLink.TerminalCore.NativeTerminalHostGeometryV1.Decode(frame.Payload);
                if (brokerPersistencePending &&
                    (frame.Type != LocalFrameType.BrokerOutput || frame.Sequence != pendingBrokerSequence))
                    throw new InvalidDataException("Pending Broker Output must be committed before another terminal frame.");
                if (frame.Sequence <= lastHostSequence)
                {
                    if (frame.Type == LocalFrameType.BrokerOutput)
                    {
                        eventBuffer.RequireMatchingRetained(frame);
                        VerifyBrokerOutput(frame);
                        // Every Broker ACK rechecks production durability. In particular,
                        // a lost ACK must not bypass a later history tombstone.
                        CommitBrokerOutput(frame);
                    }
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
                    if (frame.Type == LocalFrameType.BrokerOutput) VerifyBrokerOutput(frame);
                    eventBuffer.Append(frame);
                    persistedFrames++;
                    if (frame.Type == LocalFrameType.BrokerOutput)
                    {
                        // The Agent cursor must describe the already durable retained frame,
                        // even when its independent production WAL write needs a retry.
                        lock (statusSync)
                        {
                            lastHostSequence = frame.Sequence;
                            lastActivityUtc = DateTime.UtcNow;
                        }
                        brokerPersistencePending = true;
                        pendingBrokerSequence = frame.Sequence;
                    }
                    if (frame.Type == LocalFrameType.RemoteInputResult)
                    {
                        LocalRemoteInputResult ignoredResult;
                        var completedInputId = LocalPayloadCodec.ParseRemoteInputResult(
                            frame.Payload,
                            out ignoredResult);
                        inputDeduplicator.MarkCompleted(completedInputId);
                    }
                    if (frame.Type == LocalFrameType.BrokerOutput)
                    {
                        CommitBrokerOutput(frame);
                    }
                    else if (committedFrame != null)
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
                    case LocalFrameType.BrokerOutput:
                        outputFrames++;
                        using (var reference = NativeTerminalBrokerOutputReferenceV1.Decode(frame.Payload))
                        {
                            var brokerOutput = reference.CopyOutput();
                            try { outputBytes += brokerOutput.Length; }
                            finally { CryptographicOperations.ZeroMemory(brokerOutput); }
                        }
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

        private void CommitBrokerOutput(LocalFrame frame)
        {
            brokerPersistencePending = true;
            pendingBrokerSequence = frame.Sequence;
            if (committedFrame == null)
                throw new InvalidDataException("Broker Output has no durable production callback.");
            try { committedFrame(frame); }
            catch (InvalidDataException) { throw; }
            catch (IOException error) { throw new BrokerPersistenceRetryException(error); }
            catch (OperationCanceledException error) { throw new BrokerPersistenceRetryException(error); }
            catch (UnauthorizedAccessException error) { throw new BrokerPersistenceRetryException(error); }
            catch (InvalidOperationException error)
                when (error.Message == "BROKER_CAPTURE_RUNTIME_UNAVAILABLE")
            { throw new BrokerPersistenceRetryException(error); }
            if (!eventBuffer.IsRetained(frame.Sequence))
                throw new InvalidDataException("Broker Output was removed before its durable ACK.");
            brokerPersistencePending = false;
            pendingBrokerSequence = 0;
        }

        private void ConfirmRetainedBrokerOutput()
        {
            if (!brokerPersistencePending) return;
            var sequence = pendingBrokerSequence;
            if (sequence == 0 || sequence > lastHostSequence)
                throw new InvalidDataException("Pending Broker Output sequence is invalid.");
            var frames = eventBuffer.ReadFrom(sequence - 1, 1, long.MaxValue,
                out _, out var latestSequence, out _);
            if (latestSequence < sequence || frames.Count != 1)
                throw new InvalidDataException("Pending Broker Output is not retained at the Agent cursor.");
            var frame = frames[0];
            try
            {
                if (frame.Type != LocalFrameType.BrokerOutput || frame.Sequence != sequence)
                    throw new InvalidDataException("Pending Agent frame is not the Broker Output.");
                eventBuffer.RequireMatchingRetained(frame);
                VerifyBrokerOutput(frame);
                CommitBrokerOutput(frame);
            }
            finally { CryptographicOperations.ZeroMemory(frame.Payload); }
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

        public async Task<LocalResizeResult> RequestResizeAsync(LocalResizeRequest request)
        {
            var payload = LocalPayloadCodec.RemoteResize(request);
            TaskCompletionSource<LocalResizeResult> completion;
            lock (commandSync)
            {
                if (!remoteResizeSupported) return new LocalResizeResult { Id = request.Id, Error = LocalResizeError.RESIZE_UNSUPPORTED };
                if (pendingResize != null) return new LocalResizeResult { Id = request.Id, Error = LocalResizeError.RESIZE_BUSY };
                NamedPipeServerStream pipe;
                lock (statusSync) pipe = connectedPipe;
                if (pipe == null || !pipe.IsConnected || stopRequested)
                    return new LocalResizeResult { Id = request.Id, Error = LocalResizeError.SESSION_UNAVAILABLE };
                completion = new TaskCompletionSource<LocalResizeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                pendingResize = completion;
                pendingResizeId = request.Id;
                try { Send(pipe, LocalFrameType.ResizeRequest, payload); }
                catch (IOException) { completion.TrySetResult(new LocalResizeResult { Id = request.Id, Error = LocalResizeError.SESSION_UNAVAILABLE }); }
                catch (ObjectDisposedException) { completion.TrySetResult(new LocalResizeResult { Id = request.Id, Error = LocalResizeError.SESSION_UNAVAILABLE }); }
                catch { pendingResize = null; pendingResizeId = Guid.Empty; throw; }
            }
            try
            {
                if (await Task.WhenAny(completion.Task, Task.Delay(6000)).ConfigureAwait(false) != completion.Task)
                    return new LocalResizeResult { Id = request.Id, Error = LocalResizeError.RESIZE_TIMEOUT };
                return await completion.Task.ConfigureAwait(false);
            }
            finally { lock (commandSync) { pendingResize = null; pendingResizeId = Guid.Empty; } }
        }

        public bool TrySendRemoteInput(Guid inputId, ulong expectedLocalInputEpoch, byte[] bytes)
        {
            return DispatchRemoteInput(inputId, expectedLocalInputEpoch, bytes) ==
                RemoteInputDispatchOutcome.Enqueued;
        }

        public RemoteInputDispatchOutcome DispatchRemoteInput(
            Guid inputId,
            ulong expectedLocalInputEpoch,
            byte[] bytes)
        {
            return DispatchRemoteInput(
                inputId,
                expectedLocalInputEpoch,
                bytes,
                Guid.Empty,
                null);
        }

        internal RemoteInputDispatchOutcome DispatchRemoteInput(
            Guid inputId,
            ulong expectedLocalInputEpoch,
            byte[] bytes,
            Guid senderDeviceId,
            byte[] envelopeSha256)
        {
            if (inputId == Guid.Empty || bytes == null || bytes.Length == 0)
                return RemoteInputDispatchOutcome.Unavailable;
            lock (commandSync)
            {
                // The same authenticated input may arrive through Relay and
                // Direct during the cutover grace period. Keep the first copy
                // authoritative and let its durable result satisfy the retry.
                if (inputDeduplicator.Contains(inputId))
                    return RemoteInputDispatchOutcome.Duplicate;
                NamedPipeServerStream pipe;
                lock (statusSync) pipe = connectedPipe;
                if (pipe == null || !pipe.IsConnected)
                    return RemoteInputDispatchOutcome.Unavailable;
                if (!inputDeduplicator.TryAccept(inputId, senderDeviceId, envelopeSha256))
                {
                    return inputDeduplicator.Contains(inputId)
                        ? RemoteInputDispatchOutcome.Duplicate
                        : RemoteInputDispatchOutcome.Unavailable;
                }
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
                    return RemoteInputDispatchOutcome.Enqueued;
                }
                catch (IOException)
                {
                    // A named-pipe failure can occur after a partial write. Do
                    // not roll back the persistent at-most-once fence and risk
                    // writing the command twice on a later carrier retry.
                    return RemoteInputDispatchOutcome.Duplicate;
                }
                catch (ObjectDisposedException)
                {
                    return RemoteInputDispatchOutcome.Duplicate;
                }
            }
        }

        public bool TrySendRemoteInputRejection(Guid inputId, LocalRemoteInputResult result)
        {
            return TrySendRemoteInputRejection(inputId, result, Guid.Empty, null);
        }

        internal bool TrySendRemoteInputRejection(
            Guid inputId,
            LocalRemoteInputResult result,
            Guid senderDeviceId,
            byte[] envelopeSha256)
        {
            if (inputId == Guid.Empty || result == LocalRemoteInputResult.Accepted ||
                !Enum.IsDefined(typeof(LocalRemoteInputResult), result) || !remoteInputRejectSupported) return false;
            if (senderDeviceId == Guid.Empty && envelopeSha256 != null)
                return false;
            if (senderDeviceId != Guid.Empty &&
                (envelopeSha256 == null || envelopeSha256.Length != 32))
                return false;
            lock (commandSync)
            {
                NamedPipeServerStream pipe;
                lock (statusSync) pipe = connectedPipe;
                if (pipe == null || !pipe.IsConnected) return false;
                if (!inputDeduplicator.TryAccept(inputId, senderDeviceId, envelopeSha256))
                    return false;
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
            var claimedProcessId = LocalPayloadCodec.ParseTerminalHello(hello.Payload, options.HandshakeToken,
                out TerminalSemanticProfile profile);
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
            eventBuffer.BindAuthenticatedProfile(profile);
            string authenticatedBrokerRoot = null;
            if (profile.IsGraphics && brokerCaptureRootProvider != null)
            {
                var root = brokerCaptureRootProvider();
                if (root == null)
                {
                    brokerRootPending = true;
                    throw new BrokerPersistenceRetryException(
                        new IOException("Graphics broker capture root is temporarily unavailable."));
                }
                authenticatedBrokerRoot = LocalPayloadCodec.ParseBrokerCaptureRoot(
                    LocalPayloadCodec.BrokerCaptureRoot(root));
                if (brokerCaptureRoot != null && !string.Equals(authenticatedBrokerRoot,
                    brokerCaptureRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Graphics broker capture root changed across reconnect.");
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
                LocalPayloadCodec.TerminalHello(Process.GetCurrentProcess().Id, options.HandshakeToken, profile)));
            if (authenticatedBrokerRoot != null)
            {
                brokerCaptureRoot = authenticatedBrokerRoot;
                LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.BrokerCaptureRoot,
                    options.SessionId, 0, LocalPayloadCodec.BrokerCaptureRoot(authenticatedBrokerRoot)));
            }
            brokerRootPending = false;
            Volatile.Write(ref terminalCompatibilityError, string.Empty);
        }

        private void VerifyBrokerOutput(LocalFrame frame)
        {
            if (!eventBuffer.BoundProfile.IsGraphics || brokerCaptureRoot == null)
                throw new InvalidDataException("Broker Output has no authenticated graphics capture root.");
            using var reference = NativeTerminalBrokerOutputReferenceV1.Decode(frame.Payload);
            if (reference.SourceSequence != frame.Sequence)
                throw new InvalidDataException("Broker Output source sequence mismatch.");
            if (reference.IsInline) return;
            var hash = reference.CopySha256();
            try
            {
                using var store = new KittyCaptureStore(brokerCaptureRoot, options.SessionId);
                var canonical = store.ReadExact(options.SessionId, frame.Sequence,
                    reference.ObjectId, reference.CaptureBytes, hash);
                try
                {
                    using var group = NativeTerminalBrokerCaptureCodecV1.Decode(canonical);
                    if (group.SourceSequence != frame.Sequence)
                        throw new InvalidDataException("Broker capture source sequence mismatch.");
                }
                finally { CryptographicOperations.ZeroMemory(canonical); }
            }
            finally { CryptographicOperations.ZeroMemory(hash); }
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
            try
            {
                var asyncResult = pipe.BeginWaitForConnection(null, null);
                while (!stopRequested)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return false;
                    var slice = remaining.TotalMilliseconds > 100 ? 100 : Math.Max(1, (int)remaining.TotalMilliseconds);
                    if (asyncResult.AsyncWaitHandle.WaitOne((int)slice))
                    {
                        pipe.EndWaitForConnection(asyncResult);
                        return true;
                    }
                    if (actualHostProcessId > 0 && !IsProcessRunning(actualHostProcessId)) return false;
                }
                return false;
            }
            catch (ObjectDisposedException)
            {
                if (stopRequested) return false;
                throw;
            }
            catch (IOException)
            {
                if (stopRequested) return false;
                throw;
            }
        }

        private void SetWaitingPipe(NamedPipeServerStream pipe)
        {
            lock (statusSync) waitingPipe = pipe;
        }

        private void SetConnectedPipe(NamedPipeServerStream pipe)
        {
            lock (statusSync) connectedPipe = pipe;
            if (pipe == null)
                lock (commandSync) pendingResize?.TrySetResult(new LocalResizeResult {
                    Id = pendingResizeId, Error = LocalResizeError.SESSION_UNAVAILABLE });
        }

        private void Transition(SessionAgentLifecycle value)
        {
            lock (statusSync)
            {
                lifecycle = value;
                lastActivityUtc = DateTime.UtcNow;
            }
        }

        private bool TryTransitionUnlessStopping(SessionAgentLifecycle value)
        {
            lock (statusSync)
            {
                // RequestRemoteStop uses the same lock. Once it has selected the
                // RemoteStopped terminal state, a connection teardown racing on
                // the worker thread must not overwrite it with Disconnected,
                // HostLost, or Faulted before CommitInterruptedLifecycle runs.
                if (stopRequested) return false;
                lifecycle = value;
                lastActivityUtc = DateTime.UtcNow;
                return true;
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
        // TLD2 keeps the original input fence together with the authenticated
        // sender and exact Relay Envelope digest. The old four-byte count-only
        // format remains readable, but entries loaded from it deliberately
        // have no replay identity and therefore cannot cross an auth boundary.
        private const uint CurrentFormatMarker = 0x32444c54; // "TLD2" in LE.
        private const int EntryBytes = 16 + 16 + 32 + 1;
        private readonly object sync = new object();
        private readonly string path;
        private readonly byte[] entropy;
        private readonly List<Guid> ordered = new List<Guid>();
        private readonly HashSet<Guid> known = new HashSet<Guid>();
        private readonly Dictionary<Guid, InputReplayIdentity> identities =
            new Dictionary<Guid, InputReplayIdentity>();

        private sealed class InputReplayIdentity
        {
            public Guid SenderDeviceId;
            public byte[] EnvelopeSha256;
            public bool Completed;
        }

        public InputDeduplicator(string bufferDirectory, Guid sessionId)
        {
            Directory.CreateDirectory(bufferDirectory);
            path = Path.Combine(bufferDirectory, "input-dedupe.dat");
            entropy = sessionId.ToByteArray();
            Load();
        }

        public bool TryAccept(Guid inputId)
        {
            return TryAccept(inputId, Guid.Empty, null);
        }

        internal bool TryAccept(Guid inputId, Guid senderDeviceId, byte[] envelopeSha256)
        {
            if (inputId == Guid.Empty) throw new ArgumentException("input ID is required", "inputId");
            if (senderDeviceId == Guid.Empty && envelopeSha256 != null)
                throw new ArgumentException("sender device is required for an Envelope digest", "senderDeviceId");
            if (senderDeviceId != Guid.Empty &&
                (envelopeSha256 == null || envelopeSha256.Length != 32))
            {
                throw new ArgumentException("Envelope digest must be SHA-256", "envelopeSha256");
            }
            lock (sync)
            {
                if (known.Contains(inputId)) return false;
                if (known.Count >= MaximumEntries && !EvictCompletedLocked()) return false;
                known.Add(inputId);
                ordered.Add(inputId);
                if (senderDeviceId != Guid.Empty)
                {
                    identities[inputId] = new InputReplayIdentity
                    {
                        SenderDeviceId = senderDeviceId,
                        EnvelopeSha256 = (byte[])envelopeSha256.Clone()
                    };
                }
                while (ordered.Count > MaximumEntries)
                {
                    var evicted = ordered[0];
                    known.Remove(evicted);
                    InputReplayIdentity identity;
                    if (identities.TryGetValue(evicted, out identity))
                    {
                        if (identity.EnvelopeSha256 != null)
                            Array.Clear(identity.EnvelopeSha256, 0, identity.EnvelopeSha256.Length);
                        identities.Remove(evicted);
                    }
                    ordered.RemoveAt(0);
                }
                Save();
                return true;
            }
        }

        public bool Contains(Guid inputId)
        {
            if (inputId == Guid.Empty) throw new ArgumentException("input ID is required", "inputId");
            lock (sync) return known.Contains(inputId);
        }

        internal void MarkCompleted(Guid inputId)
        {
            if (inputId == Guid.Empty) return;
            lock (sync)
            {
                if (!known.Contains(inputId)) return;
                InputReplayIdentity identity;
                if (!identities.TryGetValue(inputId, out identity))
                {
                    identity = new InputReplayIdentity();
                    identities[inputId] = identity;
                }
                if (identity.Completed) return;
                identity.Completed = true;
                Save();
            }
        }

        private bool EvictCompletedLocked()
        {
            for (var index = 0; index < ordered.Count; index++)
            {
                var candidate = ordered[index];
                InputReplayIdentity identity;
                if (!identities.TryGetValue(candidate, out identity) || !identity.Completed)
                    continue;
                known.Remove(candidate);
                ordered.RemoveAt(index);
                identities.Remove(candidate);
                if (identity.EnvelopeSha256 != null)
                    Array.Clear(identity.EnvelopeSha256, 0, identity.EnvelopeSha256.Length);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Returns whether the input fence exists. A true result with an empty
        /// sender/digest is a legacy or local entry and must be treated as an
        /// unknown authenticated identity by the production Relay boundary.
        /// </summary>
        internal bool TryGetIdentity(
            Guid inputId,
            out Guid senderDeviceId,
            out byte[] envelopeSha256)
        {
            if (inputId == Guid.Empty) throw new ArgumentException("input ID is required", "inputId");
            senderDeviceId = Guid.Empty;
            envelopeSha256 = null;
            lock (sync)
            {
                if (!known.Contains(inputId)) return false;
                InputReplayIdentity identity;
                if (!identities.TryGetValue(inputId, out identity)) return true;
                senderDeviceId = identity.SenderDeviceId;
                envelopeSha256 = identity.EnvelopeSha256 == null
                    ? null
                    : (byte[])identity.EnvelopeSha256.Clone();
                return true;
            }
        }

        private void Load()
        {
            if (!File.Exists(path)) return;
            byte[] protectedBytes = null;
            byte[] bytes = null;
            try
            {
                protectedBytes = File.ReadAllBytes(path);
                bytes = ProtectedData.Unprotect(protectedBytes, entropy, DataProtectionScope.CurrentUser);
                if (bytes.Length < 4) throw new InvalidDataException("Invalid input dedupe file.");
                var marker = LocalFrameCodec.ReadUInt32(bytes, 0);
                var current = marker == CurrentFormatMarker;
                var countOffset = current ? 4 : 0;
                var dataOffset = current ? 8 : 4;
                if (current && bytes.Length < 8)
                    throw new InvalidDataException("Invalid input dedupe format marker.");
                var count = checked((int)LocalFrameCodec.ReadUInt32(bytes, countOffset));
                var expectedLength = current
                    ? checked(8 + count * EntryBytes)
                    : checked(4 + count * 16);
                if (count < 0 || count > MaximumEntries || bytes.Length != expectedLength)
                    throw new InvalidDataException("Invalid input dedupe entry count.");
                for (var index = 0; index < count; index++)
                {
                    var offset = current
                        ? checked(dataOffset + index * EntryBytes)
                        : checked(dataOffset + index * 16);
                    var guid = new byte[16];
                    Buffer.BlockCopy(bytes, offset, guid, 0, 16);
                    var value = new Guid(guid);
                    Array.Clear(guid, 0, guid.Length);
                    if (value == Guid.Empty || !known.Add(value))
                        throw new InvalidDataException("Invalid input dedupe entry.");
                    ordered.Add(value);
                    if (!current) continue;
                    var senderBytes = new byte[16];
                    Buffer.BlockCopy(bytes, offset + 16, senderBytes, 0, 16);
                    var sender = new Guid(senderBytes);
                    Array.Clear(senderBytes, 0, senderBytes.Length);
                    var digest = new byte[32];
                    Buffer.BlockCopy(bytes, offset + 32, digest, 0, digest.Length);
                    var completed = current && bytes[offset + 64] == 1;
                    if (current && bytes[offset + 64] > 1)
                    {
                        Array.Clear(digest, 0, digest.Length);
                        throw new InvalidDataException("Invalid input dedupe completion marker.");
                    }
                    if (sender == Guid.Empty)
                    {
                        Array.Clear(digest, 0, digest.Length);
                        continue;
                    }
                    identities[value] = new InputReplayIdentity
                    {
                        SenderDeviceId = sender,
                        EnvelopeSha256 = digest,
                        Completed = completed
                    };
                }
            }
            finally
            {
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }

        private void Save()
        {
            byte[] bytes = null;
            byte[] protectedBytes = null;
            try
            {
                bytes = new byte[8 + ordered.Count * EntryBytes];
                LocalFrameCodec.WriteUInt32(bytes, 0, CurrentFormatMarker);
                LocalFrameCodec.WriteUInt32(bytes, 4, checked((uint)ordered.Count));
                for (var index = 0; index < ordered.Count; index++)
                {
                    var offset = checked(8 + index * EntryBytes);
                    var inputBytes = ordered[index].ToByteArray();
                    Buffer.BlockCopy(inputBytes, 0, bytes, offset, 16);
                    Array.Clear(inputBytes, 0, inputBytes.Length);
                    InputReplayIdentity identity;
                    if (!identities.TryGetValue(ordered[index], out identity)) continue;
                    var senderBytes = identity.SenderDeviceId.ToByteArray();
                    Buffer.BlockCopy(senderBytes, 0, bytes, offset + 16, 16);
                    Array.Clear(senderBytes, 0, senderBytes.Length);
                    if (identity.EnvelopeSha256 != null)
                    {
                        Buffer.BlockCopy(identity.EnvelopeSha256, 0, bytes, offset + 32, 32);
                    }
                    bytes[offset + 64] = identity.Completed ? (byte)1 : (byte)0;
                }
                protectedBytes = ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser);
                AtomicWrite(path, protectedBytes);
            }
            finally
            {
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
                if (protectedBytes != null) Array.Clear(protectedBytes, 0, protectedBytes.Length);
            }
        }

        public void Dispose()
        {
            lock (sync)
            {
                foreach (var identity in identities.Values)
                {
                    if (identity.EnvelopeSha256 != null)
                        Array.Clear(identity.EnvelopeSha256, 0, identity.EnvelopeSha256.Length);
                }
                identities.Clear();
                Array.Clear(entropy, 0, entropy.Length);
            }
        }

        internal void DeletePersistentState()
        {
            lock (sync)
            {
                known.Clear();
                ordered.Clear();
                foreach (var identity in identities.Values)
                {
                    if (identity.EnvelopeSha256 != null)
                        Array.Clear(identity.EnvelopeSha256, 0, identity.EnvelopeSha256.Length);
                }
                identities.Clear();
            }
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        internal static void AtomicWrite(string path, byte[] bytes)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(
                    temporary,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    64 * 1024,
                    FileOptions.WriteThrough))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    // SessionAgent sends the Host ACK only after Append returns. Keep this
                    // explicit physical-media boundary in the shared atomic writer so event
                    // chunks, command sequence state, and input de-duplication all have the
                    // same crash-durable contract.
                    stream.Flush(true);
                }
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
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

        internal void DeletePersistentState()
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            Value = 0;
        }

        public void Dispose() { }
    }

    internal sealed class EncryptedEventBuffer : IDisposable
    {
        private readonly string directory;
        private readonly Guid sessionId;
        private readonly byte[] entropy;
        private readonly long maximumBytes;
        private readonly object sync = new object();
        private readonly LinkedList<FileInfo> chunks = new LinkedList<FileInfo>();
        private long storedBytes;
        private ulong lastSequence;
        private TerminalSemanticProfile boundProfile;

        public ulong LastSequence
        {
            get { lock (sync) return lastSequence; }
        }

        public EncryptedEventBuffer(string directory, Guid sessionId, long maximumBytes)
        {
            this.directory = directory;
            this.sessionId = sessionId;
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
            if (HasBoundProfile) Trim();
        }

        internal string TerminalCompatibilityError
        {
            get
            {
                lock (sync)
                {
                    try { ValidateProfileLocked(true); return string.Empty; }
                    catch (TerminalHistoryProfileException error) { return error.Code; }
                }
            }
        }

        internal bool HasBoundProfile
        {
            get
            {
                lock (sync)
                {
                    try { return ValidateProfileLocked(true); }
                    catch (TerminalHistoryProfileException) { return false; }
                }
            }
        }

        internal TerminalSemanticProfile BoundProfile
        {
            get { lock (sync) { ValidateProfileLocked(true); return boundProfile; } }
        }

        internal void BindAuthenticatedProfile() => BindAuthenticatedProfile(TerminalSemanticProfile.Legacy);

        internal void BindAuthenticatedProfile(TerminalSemanticProfile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            lock (sync)
            {
                if (ValidateProfileLocked(true))
                {
                    if (boundProfile != profile) throw new TerminalHistoryProfileException(TerminalSessionProfile.Mismatch);
                    return;
                }
                var bytes = TerminalSessionProfile.Encode(sessionId, profile);
                byte[] stored = null;
                try
                {
                    stored = ProtectedData.Protect(bytes, entropy, DataProtectionScope.CurrentUser);
                    InputDeduplicator.AtomicWrite(Path.Combine(directory, TerminalSessionProfile.FileName), stored);
                    boundProfile = profile;
                }
                finally
                {
                    Array.Clear(bytes, 0, bytes.Length);
                    if (stored != null) Array.Clear(stored, 0, stored.Length);
                }
            }
        }

        private bool ValidateProfileLocked(bool allowUnboundEmpty)
        {
            if (boundProfile != null) return true;
            var path = Path.Combine(directory, TerminalSessionProfile.FileName);
            if (!File.Exists(path))
            {
                if (allowUnboundEmpty && chunks.Count == 0 && lastSequence == 0 &&
                    !Directory.EnumerateFileSystemEntries(directory).Any()) return false;
                throw new TerminalHistoryProfileException(TerminalSessionProfile.Missing);
            }
            byte[] stored = null;
            byte[] bytes = null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length == 0 || stream.Length > TerminalSessionProfile.MaximumProtectedBytes)
                        throw new TerminalHistoryProfileException(TerminalSessionProfile.Invalid);
                    stored = new byte[checked((int)stream.Length)];
                    var offset = 0;
                    while (offset < stored.Length)
                    {
                        var count = stream.Read(stored, offset, stored.Length - offset);
                        if (count == 0) throw new TerminalHistoryProfileException(TerminalSessionProfile.Invalid);
                        offset += count;
                    }
                }
                bytes = ProtectedData.Unprotect(stored, entropy, DataProtectionScope.CurrentUser);
                boundProfile = TerminalSessionProfile.Read(bytes, sessionId);
                return true;
            }
            catch (CryptographicException) { throw new TerminalHistoryProfileException(TerminalSessionProfile.Invalid); }
            finally
            {
                if (stored != null) Array.Clear(stored, 0, stored.Length);
                if (bytes != null) Array.Clear(bytes, 0, bytes.Length);
            }
        }

        public void Append(LocalFrame frame)
        {
            if (frame.Type == LocalFrameType.Input || frame.Type == LocalFrameType.ResizeRequest ||
                frame.Type == LocalFrameType.RemoteInputV2 || frame.Type == LocalFrameType.RemoteInputReject ||
                frame.Type == LocalFrameType.RemoteAccessStopped ||
                frame.Type == LocalFrameType.ClipboardAuthorize || frame.Type == LocalFrameType.ClipboardAuthorization ||
                frame.Type == LocalFrameType.HostHello || frame.Type == LocalFrameType.Capabilities ||
                frame.Type == LocalFrameType.AgentHello)
            {
                throw new InvalidOperationException("Input or handshake frames must not enter the event buffer.");
            }
            lock (sync)
            {
                ValidateProfileLocked(false);
                if (frame.SessionId != sessionId) throw new InvalidDataException("Event buffer session identity mismatch.");
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
                if (frame.Type == LocalFrameType.BrokerOutput && stored.Length > maximumBytes)
                    throw new InvalidDataException("Broker Output exceeds the durable Agent buffer budget.");
                var path = Path.Combine(directory, frame.Sequence.ToString("D20") + ".chunk");
                InputDeduplicator.AtomicWrite(path, stored);
                var chunk = new FileInfo(path);
                chunks.AddLast(chunk);
                storedBytes += chunk.Length;
                lastSequence = frame.Sequence;
                Trim();
            }
        }

        internal bool IsRetained(ulong sequence)
        {
            lock (sync)
                return chunks.Any(chunk => ParseChunkSequence(chunk) == sequence && chunk.Exists);
        }

        internal void RequireMatchingRetained(LocalFrame incoming)
        {
            if (incoming == null) throw new ArgumentNullException(nameof(incoming));
            lock (sync)
            {
                ValidateProfileLocked(false);
                var chunk = chunks.FirstOrDefault(file => ParseChunkSequence(file) == incoming.Sequence);
                if (chunk == null)
                    throw new InvalidDataException("Duplicate Broker Output has no retained Agent frame.");
                byte[] stored = null;
                LocalFrame retained = null;
                try
                {
                    stored = File.ReadAllBytes(chunk.FullName);
                    retained = DecodeStored(stored);
                    if (retained.Type != LocalFrameType.BrokerOutput ||
                        incoming.Type != LocalFrameType.BrokerOutput ||
                        retained.SessionId != sessionId || incoming.SessionId != sessionId ||
                        retained.Sequence != incoming.Sequence ||
                        !retained.Payload.AsSpan().SequenceEqual(incoming.Payload))
                        throw new InvalidDataException("Duplicate Broker Output differs from the retained Agent frame.");
                }
                finally
                {
                    if (retained?.Payload != null) CryptographicOperations.ZeroMemory(retained.Payload);
                    if (stored != null) CryptographicOperations.ZeroMemory(stored);
                }
            }
        }

        internal bool LatestFrameIsBrokerOutput()
        {
            lock (sync)
            {
                ValidateProfileLocked(false);
                if (chunks.Last == null) return false;
                byte[] stored = null;
                LocalFrame retained = null;
                try
                {
                    stored = File.ReadAllBytes(chunks.Last.Value.FullName);
                    retained = DecodeStored(stored);
                    if (retained.SessionId != sessionId ||
                        retained.Sequence != ParseChunkSequence(chunks.Last.Value))
                        throw new InvalidDataException("Latest Agent frame has an invalid identity.");
                    return retained.Type == LocalFrameType.BrokerOutput;
                }
                finally
                {
                    if (retained?.Payload != null) CryptographicOperations.ZeroMemory(retained.Payload);
                    if (stored != null) CryptographicOperations.ZeroMemory(stored);
                }
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
                var profilePath = Path.Combine(directory, TerminalSessionProfile.FileName);
                if (File.Exists(profilePath)) File.Delete(profilePath);
                boundProfile = null;
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
                ValidateProfileLocked(true);
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

        internal bool TryFindRemoteInputResult(
            Guid inputId,
            out LocalFrame resultFrame)
        {
            resultFrame = null;
            if (inputId == Guid.Empty) return false;
            lock (sync)
            {
                ValidateProfileLocked(true);
                for (var chunk = chunks.Last; chunk != null; chunk = chunk.Previous)
                {
                    byte[] stored = null;
                    LocalFrame frame = null;
                    try
                    {
                        stored = File.ReadAllBytes(chunk.Value.FullName);
                        frame = DecodeStored(stored);
                        if (frame.Type != LocalFrameType.RemoteInputResult)
                            continue;
                        LocalRemoteInputResult ignored;
                        if (LocalPayloadCodec.ParseRemoteInputResult(frame.Payload, out ignored) == inputId)
                        {
                            resultFrame = frame;
                            frame = null;
                            return true;
                        }
                    }
                    catch (FileNotFoundException)
                    {
                        // A concurrent external cleanup can retire a chunk
                        // between enumeration and read. Continue the bounded
                        // reverse scan; the caller will treat a miss as pending.
                    }
                    finally
                    {
                        if (frame != null && frame.Payload != null)
                            Array.Clear(frame.Payload, 0, frame.Payload.Length);
                        if (stored != null) Array.Clear(stored, 0, stored.Length);
                    }
                }
            }
            return false;
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
            ulong firstGapSequence = 0;
            ulong firstGapStart = 0;
            ulong firstGapEnd = 0;
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
                        if (firstGapSequence == 0)
                        {
                            firstGapSequence = frame.Sequence;
                            firstGapStart = gapStart;
                            firstGapEnd = gapEnd;
                        }
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
                    ";first_gap_seq=" + firstGapSequence +
                    ";first_gap_start=" + firstGapStart +
                    ";first_gap_end=" + firstGapEnd +
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
