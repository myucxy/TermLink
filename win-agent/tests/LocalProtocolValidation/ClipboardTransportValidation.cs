using System;
using System.Collections.Concurrent;
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
    internal static class ClipboardTransportValidation
    {
        internal static void Run(string root)
        {
            var id = Guid.NewGuid(); var name = "termlink-clipboard-probe-" + id.ToString("N");
            var token = RandomNumberGenerator.GetBytes(32);
            var directory = Path.Combine(root, "clipboard-transport-" + id.ToString("N"));
            var committed = new ConcurrentQueue<LocalFrame>();
            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var entered = new ManualResetEventSlim();
            using var agent = new SessionAgent(AgentOptions.CreateManaged(name, id, token, directory,
                Environment.ProcessId, 10000, 1024 * 1024), committed.Enqueue, (session, cancellation) => {
                    Require(session == id, "Clipboard query crossed sessions."); entered.Set(); return pending.Task.WaitAsync(cancellation);
                });
            var worker = Task.Run((Action)agent.RunManaged);
            try {
                using var host = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                host.Connect(5000);
                void Send(LocalFrameType type, ulong sequence, byte[] bytes) => LocalFrameCodec.Write(host, new LocalFrame(type, id, sequence, bytes));
                LocalFrame Read() {
                    var task = Task.Run(() => LocalFrameCodec.Read(host));
                    Require(task.Wait(5000) && task.Result != null, "IPC response timed out."); return task.Result;
                }
                Send(LocalFrameType.HostHello, 0, LocalPayloadCodec.TerminalHello(Environment.ProcessId, token));
                Require(Read().Type == LocalFrameType.AgentHello, "Authenticated clipboard fixture did not connect.");
                Send(LocalFrameType.Capabilities, 0, LocalPayloadCodec.Capabilities(true, false));
                Send(LocalFrameType.Output, 1, Encoding.UTF8.GetBytes("before")); Require(Read().Sequence == 1, "Initial output was not ACKed.");
                var request = Guid.NewGuid(); Send(LocalFrameType.ClipboardAuthorize, 0, request.ToByteArray());
                Require(entered.Wait(5000), "Authorization delegate did not run.");
                Send(LocalFrameType.Output, 2, Encoding.UTF8.GetBytes("during authorization"));
                var ack = Read(); Require(ack.Type == LocalFrameType.Ack && ack.Sequence == 2 && !pending.Task.IsCompleted,
                    "Clipboard network wait blocked durable output ACK.");
                pending.SetResult(true);
                var response = Read();
                Require(response.Type == LocalFrameType.ClipboardAuthorization && response.Sequence == 0 &&
                    response.Payload.Length == 17 && new Guid(response.Payload.AsSpan(0,16)) == request && response.Payload[16] == 1,
                    "Authorization lost one-shot request identity.");
                Require(committed.Count == 2 && committed.All(f => f.Type == LocalFrameType.Output), "Clipboard authorization entered the durable journal.");
                pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); entered.Reset();
                request = Guid.NewGuid(); Send(LocalFrameType.ClipboardAuthorize, 0, request.ToByteArray());
                Require(entered.Wait(5000), "Second authorization did not run."); pending.SetResult(false);
                response = Read(); Require(response.Type == LocalFrameType.ClipboardAuthorization && response.Payload[16] == 0,
                    "Authorization denial was lost.");
                Send(LocalFrameType.Lifecycle, 3, LocalPayloadCodec.Lifecycle(LocalLifecycleState.Ended, 0));
                Require(Read().Sequence == 3, "Ended event was not ACKed.");
            } finally {
                pending.TrySetResult(false); agent.RequestRemoteStop(true); Require(worker.Wait(5000), "Clipboard test agent did not stop.");
            }
            using var journal = new EncryptedEventBuffer(directory, id, 1024 * 1024);
            Require(journal.ReadFrom(0, out _, out _).All(f => f.Type != LocalFrameType.ClipboardAuthorize && f.Type != LocalFrameType.ClipboardAuthorization),
                "Authorization leaked into persisted replay.");
            Console.WriteLine("Clipboard IPC: authenticated session, pending network does not block ACK, allow/deny and no durable replay passed.");
        }
        private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
