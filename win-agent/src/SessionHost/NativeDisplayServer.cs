using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using TermLink.NativeDisplay;
using TermLink.TerminalCore;

namespace TermLink.SessionHost
{
    internal sealed class NativeDisplayServer : IDisposable
    {
        private readonly NamedPipeServerStream pipe;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Func<ulong, uint, (byte Status, byte[] Frame)> render;
        private readonly Func<byte, byte[], bool> input;
        private readonly Func<byte, byte[], (byte, byte[])> shell;
        private readonly Action<string> inputDiagnostic;
        private Task worker;

        public NativeDisplayServer(Func<ulong, uint, (byte, byte[])> render, Func<byte, byte[], bool> input,
            Func<byte, byte[], (byte, byte[])> shell = null, Action<string> inputDiagnostic = null)
        {
            this.render = render; this.input = input; this.shell = shell; this.inputDiagnostic = inputDiagnostic;
            PipeName = "termlink-display-" + Guid.NewGuid().ToString("N");
            pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 64 * 1024, 64 * 1024);
        }

        public string PipeName { get; }
        private Exception failure;
        public Exception Failure => Volatile.Read(ref failure);
        public void Start(int expectedClientPid) { worker = Task.Run(() => RunAsync(expectedClientPid)); }

        private async Task RunAsync(int expectedClientPid)
        {
            try
            {
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                connect.CancelAfter(TimeSpan.FromSeconds(15));
                await pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
                if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var actualPid) || actualPid != expectedClientPid)
                    throw new IOException("Native display process identity mismatch.");
                uint? previousOffset = null;
                while (!lifetime.IsCancellationRequested)
                {
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                    var request = await NativeDisplayProtocol.ReadAsync(pipe, NativeDisplayProtocol.MaximumRequestBytes, requestDeadline.Token).ConfigureAwait(false);
                    byte status; byte[] payload = Array.Empty<byte>();
                    if (request.Kind == NativeDisplayProtocol.Render)
                    {
                        NativeDisplayProtocol.RequireSize(request.Payload, 12);
                        var revision = BinaryPrimitives.ReadUInt64LittleEndian(request.Payload);
                        var offset = BinaryPrimitives.ReadUInt32LittleEndian(request.Payload.AsSpan(8));
                        if (offset > 10_000) throw new InvalidDataException("Native display history offset exceeds limit.");
                        (status, payload) = render(previousOffset == offset ? revision : 0, offset);
                        previousOffset = offset;
                    }
                    else if (shell != null && request.Kind is NativeDisplayProtocol.ShellQuery or NativeDisplayProtocol.ShellSeek or NativeDisplayProtocol.SignalsQuery or NativeDisplayProtocol.ClipboardQuery) {
                        (status, payload) = shell(request.Kind, request.Payload);
                        previousOffset = null;
                    }
                    else
                    {
                        try { status = input(request.Kind, request.Payload) ? NativeDisplayProtocol.Accepted : NativeDisplayProtocol.Rejected; }
                        catch (NativeTerminalException error) when (IsRecoverableInput(error) && TryInputDiagnostic(error, out payload))
                        {
                            try { inputDiagnostic?.Invoke(System.Text.Encoding.ASCII.GetString(payload)); } catch { }
                            status = NativeDisplayProtocol.Rejected;
                        }
                    }
                    await NativeDisplayProtocol.WriteAsync(pipe, status, payload, NativeDisplayProtocol.MaximumGraphicsFrameBytes, requestDeadline.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { Volatile.Write(ref failure, error); }
        }

        private static bool TryInputDiagnostic(NativeTerminalException error, out byte[] payload)
        {
            payload = null;
            var text = error?.Diagnostic;
            if (text == null || text.Length > 2048 || !text.StartsWith("TL-INPUT-DIAG/1\ndirection=input_encoding\n", StringComparison.Ordinal) ||
                !text.EndsWith("\n", StringComparison.Ordinal) || text.Any(c => c != '\n' && (c < 32 || c > 126))) return false;
            payload = System.Text.Encoding.ASCII.GetBytes(text);
            return true;
        }

        private static bool IsRecoverableInput(NativeTerminalException error) =>
            error.Status is NativeTerminalStatus.InvalidArgument or NativeTerminalStatus.LimitExceeded or NativeTerminalStatus.Unsupported;

        public void Dispose()
        {
            lifetime.Cancel(); pipe.Dispose();
            if (worker != null) { try { worker.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { } }
            lifetime.Dispose();
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint processId);
    }
}
