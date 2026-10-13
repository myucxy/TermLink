using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.SessionHost;
using TermLink.TerminalCore;

internal static class BrokerCaptureTransportValidation
{
    internal static async Task Run()
    {
        var session = Guid.NewGuid();
        var token = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var pipeName = "termlink-broker-ack-" + session.ToString("N");
        var root = Path.Combine(Path.GetTempPath(), "termlink-broker-root-" + session.ToString("N"));
        var applied = new List<ulong>();
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var bridge = new AgentPipeBridge(pipeName, session, token, (_, _) => { },
            (_, _, _) => LocalRemoteInputResult.Accepted, 1024 * 1024,
            frame => { lock (applied) applied.Add(frame.Sequence); },
            semanticProfile: TerminalSemanticProfile.Graphics, requireBrokerCaptureRoot: true);
        bridge.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await pipe.WaitForConnectionAsync(deadline.Token);
        var hello = await Task.Run(() => LocalFrameCodec.Read(pipe), deadline.Token).WaitAsync(deadline.Token);
        Require(hello.Type == LocalFrameType.HostHello &&
            LocalPayloadCodec.ParseTerminalHello(hello.Payload, token, TerminalSemanticProfile.Graphics) == Environment.ProcessId,
            "Broker Host handshake failed.");
        LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.AgentHello, session, 0,
            LocalPayloadCodec.TerminalHello(Environment.ProcessId, token, TerminalSemanticProfile.Graphics)));
        Require(LocalFrameCodec.Read(pipe).Type == LocalFrameType.Capabilities, "Broker Host capabilities missing.");
        Reject(() => bridge.WaitForBrokerCaptureRoot(20), "Missing broker root was accepted.");
        LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.BrokerCaptureRoot, session, 0,
            LocalPayloadCodec.BrokerCaptureRoot(root)));
        Require(bridge.WaitForBrokerCaptureRoot(2000) == root, "Authenticated broker root was not pinned.");

        var transaction = Task.Run(() => bridge.ExecuteBrokerTransaction((_, publish) =>
            publish(LocalFrameType.Output, Encoding.ASCII.GetBytes("broker callback")), () => false));
        var output = await Task.Run(() => LocalFrameCodec.Read(pipe), deadline.Token).WaitAsync(deadline.Token);
        Require(output.Type == LocalFrameType.Output && output.Sequence == 1, "Broker Output sequence changed.");
        var localInputGate = Task.Run(() => bridge.WithPublicationGate(() => true));
        Require(!localInputGate.Wait(50), "Local input entered native authority while Broker Output held publication order.");
        var inputId = Guid.NewGuid();
        LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.RemoteInputV2, session, 1,
            LocalPayloadCodec.RemoteInput(inputId, 0, Encoding.ASCII.GetBytes("x"))));
        LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.Ack, session, 1, Array.Empty<byte>()));
        Require(await transaction.WaitAsync(deadline.Token) == 1, "Broker transaction did not complete after durable ACK.");
        Require(await localInputGate.WaitAsync(deadline.Token), "Local input gate did not resume after Broker commit.");
        lock (applied) Require(applied.Count == 0, "ACK thread applied the native broker Output twice.");
        var inputResult = await Task.Run(() => LocalFrameCodec.Read(pipe), deadline.Token).WaitAsync(deadline.Token);
        Require(inputResult.Type == LocalFrameType.RemoteInputResult && inputResult.Sequence == 2 &&
            LocalPayloadCodec.ParseRemoteInputResult(inputResult.Payload, out var result) == inputId &&
            result == LocalRemoteInputResult.Accepted, "Queued input blocked the ACK reader.");
        LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.Ack, session, 2, Array.Empty<byte>()));
        Require(bridge.WaitForAck(2, 2000), "Authority did not apply the following result.");
        lock (applied) Require(applied.SequenceEqual(new ulong[] { 2 }), "Broker authority cursor was applied twice.");
        LocalFrameCodec.Write(pipe, new LocalFrame(LocalFrameType.BrokerCaptureRoot, session, 1,
            LocalPayloadCodec.BrokerCaptureRoot(root)));
        Require(SpinWait.SpinUntil(() => bridge.HasDisconnected, 2000),
            "Nonzero broker root control sequence did not close the Agent pipe.");
        Console.WriteLine("Broker Host durable ACK and queued input: passed");
    }

    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
