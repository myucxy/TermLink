using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.TerminalCore;

namespace TermLink.Agent;

internal static class BrokerPersistenceRetryValidation
{
    internal static void Run(string root)
    {
        RecoverWithinRegistry(root);
        RestoreAcknowledgedTail(root);
        RecoverCallbackFailure(root, "runtime-cancelled",
            () => new OperationCanceledException("Runtime account retired during Broker commit."));
        RecoverCallbackFailure(root, "storage-access",
            () => new UnauthorizedAccessException("Broker WAL is temporarily inaccessible."));
        RetryMissingRoot(root);
        RejectIntegrityFailure(root, "invalid-data",
            () => new InvalidDataException("Broker WAL identity is corrupt."));
        RejectIntegrityFailure(root, "crypto",
            () => new CryptographicException("Broker WAL authentication failed."));
    }

    private static void RecoverWithinRegistry(string root)
    {
        var session = Guid.NewGuid();
        var token = RandomNumberGenerator.GetBytes(32);
        var pipeName = "termlink-broker-retry-" + session.ToString("N");
        var bufferRoot = Path.Combine(root, "broker-retry-registry");
        var captureRoot = Path.Combine(root, "broker-retry-captures");
        Directory.CreateDirectory(captureRoot);
        var broker = Broker(session, 1, "broker output");
        using var firstFailed = new ManualResetEventSlim();
        using var secondEntered = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        using var oldDuplicateFailed = new ManualResetEventSlim();
        var attempts = 0;
        using var registry = new SessionRegistry(bufferRoot, 10000, 1024 * 1024, 5,
            (_, frame) =>
            {
                if (frame.Type != LocalFrameType.BrokerOutput) return;
                var number = Interlocked.Increment(ref attempts);
                if (number == 1)
                {
                    firstFailed.Set();
                    throw new IOException("Injected first production WAL write failure.");
                }
                if (number == 2)
                {
                    secondEntered.Set();
                    if (!releaseSecond.Wait(10000)) throw new IOException("Test release timed out.");
                }
                if (number == 4)
                {
                    oldDuplicateFailed.Set();
                    throw new IOException("Injected old Broker duplicate WAL recheck failure.");
                }
            }, brokerCaptureRootProvider: () => captureRoot);
        Require(registry.TryRegister(new SessionRegistration(session, pipeName, token,
            Process.GetCurrentProcess().Id, ManagedShellType.Custom), out var registrationError),
            "Registry rejected the broker test session: " + registrationError);
        try
        {
            using (var firstHost = Connect(pipeName, session, token, captureRoot))
            {
                LocalFrameCodec.Write(firstHost, broker);
                Require(firstFailed.Wait(5000), "First production WAL failure was not injected.");
                Require(ReadOrClosed(firstHost) == null, "Failed production WAL received an ACK.");
            }
            Require(registry.TryReadBufferedEvents(session, 0, out var retained, out _, out var latest) &&
                latest == 1 && retained.Count == 1 && retained[0].Type == LocalFrameType.BrokerOutput &&
                retained[0].Payload.SequenceEqual(broker.Payload),
                "Failed WAL write lost or changed the Agent's retained Broker Output.");

            using var retryHost = Connect(pipeName, session, token, captureRoot);
            Require(secondEntered.Wait(5000), "The same Registry/Agent did not retry the WAL after reconnect.");
            LocalFrameCodec.Write(retryHost, broker);
            LocalFrameCodec.Write(retryHost, Output(session, 2));
            LocalFrameCodec.Write(retryHost, Output(session, 3));
            var firstAck = Task.Run(() => LocalFrameCodec.Read(retryHost));
            Require(!firstAck.Wait(250), "A Broker retry or later Output was ACKed before its WAL write.");
            Require(registry.TryReadBufferedEvents(session, 0, out retained, out _, out latest) &&
                latest == 1 && retained.Count == 1,
                "Sequence 2 or 3 crossed an unresolved Broker WAL write.");
            releaseSecond.Set();
            Require(firstAck.Wait(5000) && IsAck(firstAck.Result, 1),
                "Exact Broker retransmission was not ACKed after WAL retry.");
            Require(IsAck(ReadRequired(retryHost), 2) && IsAck(ReadRequired(retryHost), 3),
                "Later Output did not resume in sequence after Broker durability.");
            Require(Volatile.Read(ref attempts) == 3,
                "Exact retransmission did not recheck production WAL durability before ACK.");
            LocalFrameCodec.Write(retryHost, broker);
            Require(oldDuplicateFailed.Wait(5000) && ReadOrClosed(retryHost) == null,
                "Old Broker WAL recheck failure was ACKed or stopped the Agent.");
            Require(registry.TryReadBufferedEvents(session, 0, out retained, out _, out latest) &&
                latest == 3 && retained.Count == 3,
                "Old Broker retry changed the already durable Agent cursor.");
            using var resumedHost = Connect(pipeName, session, token, captureRoot);
            LocalFrameCodec.Write(resumedHost, Output(session, 4));
            Require(IsAck(ReadRequired(resumedHost), 4) && Volatile.Read(ref attempts) == 5,
                "Old Broker retry did not reconfirm exact sequence 1 before sequence 4.");
            var wrong = Broker(session, 1, "changed output");
            LocalFrameCodec.Write(resumedHost, wrong);
            Require(ReadOrClosed(resumedHost) == null,
                "Different bytes for a retained Broker Output were ACKed.");
            Require(Volatile.Read(ref attempts) == 5,
                "Different Broker bytes reached the production WAL callback.");
        }
        finally
        {
            releaseSecond.Set();
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(broker.Payload);
        }
    }

    private static void RestoreAcknowledgedTail(string root)
    {
        var session = Guid.NewGuid();
        var token = RandomNumberGenerator.GetBytes(32);
        var pipeName = "termlink-broker-tail-" + session.ToString("N");
        var buffer = Path.Combine(root, "broker-acked-tail");
        var captureRoot = Path.Combine(root, "broker-acked-captures");
        Directory.CreateDirectory(captureRoot);
        var broker = Broker(session, 1, "acknowledged broker");
        var writes = 0;
        var options = AgentOptions.CreateManaged(pipeName, session, token, buffer,
            Process.GetCurrentProcess().Id, 10000, 1024 * 1024);
        void Committed(LocalFrame frame)
        {
            if (frame.Type == LocalFrameType.BrokerOutput) Interlocked.Increment(ref writes);
        }
        using (var first = new SessionAgent(options, Committed,
            brokerCaptureRootProvider: () => captureRoot))
        {
            var worker = Task.Run((Action)first.RunManaged);
            try
            {
                using var host = Connect(pipeName, session, token, captureRoot);
                LocalFrameCodec.Write(host, broker);
                Require(IsAck(ReadRequired(host), 1), "Original Broker Output was not ACKed.");
            }
            finally
            {
                first.RequestRemoteStop(false);
                Require(worker.Wait(5000), "Original Agent did not stop cleanly.");
            }
        }
        using (var restored = new SessionAgent(options, Committed,
            brokerCaptureRootProvider: () => captureRoot))
        {
            var worker = Task.Run((Action)restored.RunManaged);
            try
            {
                using var host = Connect(pipeName, session, token, captureRoot);
                LocalFrameCodec.Write(host, Output(session, 2));
                Require(IsAck(ReadRequired(host), 2),
                    "An already ACKed Broker tail blocked sequence 2 after Agent restart.");
                Require(Volatile.Read(ref writes) == 2,
                    "Restart did not idempotently reconfirm the retained Broker tail.");
            }
            finally
            {
                restored.RequestRemoteStop(false);
                Require(worker.Wait(5000), "Restored Agent did not stop cleanly.");
            }
        }
        CryptographicOperations.ZeroMemory(token);
        CryptographicOperations.ZeroMemory(broker.Payload);
    }

    private static void RecoverCallbackFailure(string root, string name,
        Func<Exception> firstFailure)
    {
        var session = Guid.NewGuid();
        var token = RandomNumberGenerator.GetBytes(32);
        var pipeName = "termlink-broker-" + name + "-" + session.ToString("N");
        var buffer = Path.Combine(root, "broker-" + name + "-events");
        var captureRoot = Path.Combine(root, "broker-" + name + "-captures");
        Directory.CreateDirectory(captureRoot);
        var broker = Broker(session, 1, name);
        var attempts = 0;
        var options = AgentOptions.CreateManaged(pipeName, session, token, buffer,
            Process.GetCurrentProcess().Id, 10000, 1024 * 1024);
        using var agent = new SessionAgent(options, frame =>
        {
            if (frame.Type == LocalFrameType.BrokerOutput &&
                Interlocked.Increment(ref attempts) == 1) throw firstFailure();
        }, brokerCaptureRootProvider: () => captureRoot);
        var worker = Task.Run((Action)agent.RunManaged);
        try
        {
            using (var firstHost = Connect(pipeName, session, token, captureRoot))
            {
                LocalFrameCodec.Write(firstHost, broker);
                Require(ReadOrClosed(firstHost) == null && !worker.IsCompleted,
                    name + " stopped the Agent or ACKed a failed WAL write.");
            }
            using (var journal = new EncryptedEventBuffer(buffer, session, 1024 * 1024))
                Require(journal.LastSequence == 1 && journal.LatestFrameIsBrokerOutput(),
                    name + " changed the retained Broker cursor or appended synthetic output.");
            using var retryHost = Connect(pipeName, session, token, captureRoot);
            LocalFrameCodec.Write(retryHost, broker);
            Require(IsAck(ReadRequired(retryHost), 1) && Volatile.Read(ref attempts) == 3,
                name + " did not reconfirm and ACK the exact Broker retransmission.");
            LocalFrameCodec.Write(retryHost, Output(session, 2));
            Require(IsAck(ReadRequired(retryHost), 2),
                name + " did not resume the next terminal sequence.");
        }
        finally
        {
            agent.RequestRemoteStop(false);
            Require(worker.Wait(5000) && !worker.IsFaulted,
                name + " Agent did not stop cleanly after retry.");
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(broker.Payload);
        }
    }

    private static void RetryMissingRoot(string root)
    {
        var session = Guid.NewGuid();
        var token = RandomNumberGenerator.GetBytes(32);
        var pipeName = "termlink-broker-root-" + session.ToString("N");
        var buffer = Path.Combine(root, "broker-root-events");
        var captureRoot = Path.Combine(root, "broker-root-captures");
        Directory.CreateDirectory(captureRoot);
        var broker = Broker(session, 1, "root recovered");
        var rootAttempts = 0;
        var options = AgentOptions.CreateManaged(pipeName, session, token, buffer,
            Process.GetCurrentProcess().Id, 10000, 1024 * 1024);
        using var agent = new SessionAgent(options, _ => { },
            brokerCaptureRootProvider: () =>
                Interlocked.Increment(ref rootAttempts) == 1 ? null : captureRoot);
        var worker = Task.Run((Action)agent.RunManaged);
        try
        {
            using (var firstHost = new NamedPipeClientStream(".", pipeName,
                PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                firstHost.Connect(5000);
                LocalFrameCodec.Write(firstHost, new LocalFrame(LocalFrameType.HostHello,
                    session, 0, LocalPayloadCodec.TerminalHello(Process.GetCurrentProcess().Id,
                        token, TerminalSemanticProfile.Graphics)));
                Require(ReadOrClosed(firstHost) == null && !worker.IsCompleted,
                    "Missing runtime Broker root sent AgentHello or faulted the Agent.");
            }
            using (var journal = new EncryptedEventBuffer(buffer, session, 1024 * 1024))
                Require(journal.LastSequence == 0,
                    "Missing runtime Broker root appended synthetic history.");
            using var retryHost = Connect(pipeName, session, token, captureRoot);
            LocalFrameCodec.Write(retryHost, broker);
            Require(IsAck(ReadRequired(retryHost), 1),
                "Broker root recovery did not accept the original session identity.");
            LocalFrameCodec.Write(retryHost, Output(session, 2));
            Require(IsAck(ReadRequired(retryHost), 2),
                "Broker root recovery did not resume normal output.");
        }
        finally
        {
            agent.RequestRemoteStop(false);
            Require(worker.Wait(5000) && !worker.IsFaulted,
                "Broker root retry Agent did not stop cleanly.");
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(broker.Payload);
        }
    }

    private static void RejectIntegrityFailure(string root, string name,
        Func<Exception> corruption)
    {
        var session = Guid.NewGuid();
        var token = RandomNumberGenerator.GetBytes(32);
        var pipeName = "termlink-broker-integrity-" + session.ToString("N");
        var buffer = Path.Combine(root, "broker-integrity-" + name);
        var captureRoot = Path.Combine(root, "broker-integrity-" + name + "-captures");
        Directory.CreateDirectory(captureRoot);
        var broker = Broker(session, 1, name);
        var options = AgentOptions.CreateManaged(pipeName, session, token, buffer,
            Process.GetCurrentProcess().Id, 10000, 1024 * 1024);
        using var agent = new SessionAgent(options, frame =>
        {
            if (frame.Type == LocalFrameType.BrokerOutput) throw corruption();
        }, brokerCaptureRootProvider: () => captureRoot);
        var worker = Task.Run((Action)agent.RunManaged);
        try
        {
            using var host = Connect(pipeName, session, token, captureRoot);
            LocalFrameCodec.Write(host, broker);
            Require(ReadOrClosed(host) == null &&
                SpinWait.SpinUntil(() => worker.IsCompleted, 5000) && worker.IsFaulted &&
                worker.Exception?.InnerException?.GetType() == corruption().GetType(),
                name + " corruption was retried or ACKed instead of faulting.");
            using var journal = new EncryptedEventBuffer(buffer, session, 1024 * 1024);
            Require(journal.LastSequence == 1 && journal.LatestFrameIsBrokerOutput(),
                name + " corruption appended a synthetic terminal event.");
        }
        finally
        {
            agent.RequestRemoteStop(false);
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(broker.Payload);
        }
    }

    private static NamedPipeClientStream Connect(string pipeName, Guid session, byte[] token,
        string expectedCaptureRoot)
    {
        var host = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            host.Connect(5000);
            LocalFrameCodec.Write(host, new LocalFrame(LocalFrameType.HostHello, session, 0,
                LocalPayloadCodec.TerminalHello(Process.GetCurrentProcess().Id, token,
                    TerminalSemanticProfile.Graphics)));
            var hello = ReadRequired(host);
            var root = ReadRequired(host);
            Require(hello.Type == LocalFrameType.AgentHello && root.Type == LocalFrameType.BrokerCaptureRoot &&
                LocalPayloadCodec.ParseBrokerCaptureRoot(root.Payload) == expectedCaptureRoot,
                "Graphics Agent handshake did not publish the stable Broker capture root.");
            return host;
        }
        catch { host.Dispose(); throw; }
    }

    private static LocalFrame Broker(Guid session, ulong sequence, string output)
    {
        var bytes = Encoding.ASCII.GetBytes(output);
        var captures = new byte[136];
        "TLCAPV1\0"u8.CopyTo(captures);
        BinaryPrimitives.WriteUInt32LittleEndian(captures.AsSpan(8), 32);
        BinaryPrimitives.WriteUInt32LittleEndian(captures.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(captures.AsSpan(16), sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(captures.AsSpan(24), (uint)captures.Length);
        var record = captures.AsSpan(32);
        BinaryPrimitives.WriteUInt32LittleEndian(record, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], 96);
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], 'f');
        BinaryPrimitives.WriteUInt64LittleEndian(record[16..], sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(record[24..], (uint)bytes.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[96..], 4);
        using var reference = NativeTerminalBrokerOutputReferenceV1.InlineFailures(sequence,
            bytes, captures);
        CryptographicOperations.ZeroMemory(bytes);
        CryptographicOperations.ZeroMemory(captures);
        return new LocalFrame(LocalFrameType.BrokerOutput, session, sequence, reference.Encode());
    }

    private static LocalFrame Output(Guid session, ulong sequence) =>
        new(LocalFrameType.Output, session, sequence, Encoding.ASCII.GetBytes("after broker"));

    private static LocalFrame ReadRequired(NamedPipeClientStream host)
    {
        var read = Task.Run(() => LocalFrameCodec.Read(host));
        Require(read.Wait(5000) && read.Result != null, "Agent response timed out or closed.");
        return read.Result;
    }

    private static LocalFrame ReadOrClosed(NamedPipeClientStream host)
    {
        var read = Task.Run(() =>
        {
            try { return LocalFrameCodec.Read(host); }
            catch (IOException) { return null; }
        });
        Require(read.Wait(5000), "Agent did not close a rejected Broker connection.");
        return read.Result;
    }

    private static bool IsAck(LocalFrame frame, ulong sequence) =>
        frame?.Type == LocalFrameType.Ack && frame.Sequence == sequence;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
