using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.TerminalCore;

namespace TermLink.SessionHost;

internal sealed partial class ConPtySessionHost
{
    private readonly object protocolResizeSync = new();
    private readonly Queue<(NativeTerminalHostEffectRecordV2 Effect, bool Busy)> protocolResizes = new();
    private bool protocolResizeWorkerRunning;
    private string protocolResizeNotice;

    private void QueueProtocolResize(NativeTerminalHostEffectRecordV2 effect)
    {
        lock (protocolResizeSync)
        {
            if (protocolResizes.Count >= 128)
                throw new InvalidOperationException("Protocol resize result queue is full.");
            protocolResizes.Enqueue((effect, protocolResizes.Count >= 64));
            if (protocolResizeWorkerRunning) return;
            protocolResizeWorkerRunning = true;
            // The ACK reader cannot wait for a resize that itself needs a later ACK.
            _ = Task.Run(ProcessProtocolResizes);
        }
    }

    private void ProcessProtocolResizes()
    {
        while (true)
        {
            NativeTerminalHostEffectRecordV2 effect;
            bool busy;
            lock (protocolResizeSync)
            {
                if (protocolResizes.Count == 0)
                {
                    protocolResizeWorkerRunning = false;
                    return;
                }
                (effect, busy) = protocolResizes.Dequeue();
            }
            if (stopResize || disposed || outputDisconnected) continue;
            if (busy)
            {
                try { PublishProtocolResizeResult(effect, LocalResizeError.RESIZE_BUSY); }
                catch
                {
                    authorityFailed = true;
                    outputDisconnected = true;
                    CloseInput();
                    lock (protocolResizeSync) { protocolResizes.Clear(); protocolResizeWorkerRunning = false; }
                    return;
                }
                continue;
            }
            var payload = effect.Payload.ToArray();
            var columns = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            var rows = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4));
            LocalResizeError error;
            try
            {
                var request = new LocalResizeRequest { Id = Guid.NewGuid(), ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 4000 };
                lock (terminalWriteSync)
                {
                    request.LocalInputEpoch = localInputEpoch;
                    lock (resizeSync)
                    {
                        request.Columns = columns == 0 ? innerSize.X : checked((short)columns);
                        request.Rows = rows == 0 ? innerSize.Y : checked((short)rows);
                    }
                }
                error = request.Columns < 2 || request.Rows < 2 ? LocalResizeError.WINDOW_RESIZE_UNSUPPORTED :
                    ApplyRemoteResize(request, preserveDesktopFit: false).Error;
            }
            catch { error = LocalResizeError.SESSION_UNAVAILABLE; }
            if (stopResize || disposed || outputDisconnected) continue;
            try { PublishProtocolResizeResult(effect, error); }
            catch
            {
                // A host result must not disappear while the session keeps claiming ordered state.
                authorityFailed = true;
                outputDisconnected = true;
                CloseInput();
                lock (protocolResizeSync) { protocolResizes.Clear(); protocolResizeWorkerRunning = false; }
                return;
            }
        }
    }

    private void PublishProtocolResizeResult(NativeTerminalHostEffectRecordV2 effect, LocalResizeError error)
    {
        var payload = effect.Payload.ToArray();
        var result = new LocalHostResizeResult {
            SourceSequence = effect.SourceSequence, SourceOffsetEnd = effect.SourceOffsetEnd, Ordinal = effect.Ordinal,
            RequestedColumns = checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(payload)),
            RequestedRows = checked((ushort)BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4))), Error = error
        };
        lock (resizeSync)
        {
            result.ActualColumns = checked((ushort)innerSize.X);
            result.ActualRows = checked((ushort)innerSize.Y);
        }
        if (result.Error == LocalResizeError.OK &&
            ((result.RequestedColumns != 0 && result.RequestedColumns != result.ActualColumns) ||
             (result.RequestedRows != 0 && result.RequestedRows != result.ActualRows))) result.Error = LocalResizeError.RESIZE_BUSY;
        PublishAgentFrame(LocalFrameType.HostResizeResult, LocalPayloadCodec.HostResizeResult(result));
        if (result.Error != LocalResizeError.OK)
        {
            Console.Error.WriteLine(result.Describe());
            System.Threading.Interlocked.Exchange(ref protocolResizeNotice, result.Describe());
        }
    }
}
