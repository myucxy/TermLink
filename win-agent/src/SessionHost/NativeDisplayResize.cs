using System;
using System.Buffers.Binary;
using System.Threading.Tasks;
using TermLink.LocalIpc;
using TermLink.NativeDisplay;

namespace TermLink.SessionHost;

internal sealed partial class ConPtySessionHost
{
    private readonly object nativeFitSync = new();
    private NativeFit pendingNativeFit;

    private sealed class NativeFit
    {
        public LocalResizeRequest Request;
        public COORD Before;
        public COORD Target;
        public long Deadline;
        public bool Dispatched;
        public bool PreserveDesktopFit;
        public readonly TaskCompletionSource<LocalResizeResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private (byte Status, byte[] Frame) RenderNativeDisplay(ulong revision, uint offset)
    {
        var notice = System.Threading.Interlocked.Exchange(ref protocolResizeNotice, null);
        if (notice != null) return (NativeDisplayProtocol.HostResizeNotice, NativeDisplayProtocol.Utf8.GetBytes(notice));
        lock (nativeFitSync)
        {
            if (pendingNativeFit is { Dispatched: false } fit)
            {
                fit.Dispatched = true;
                var payload = new byte[28]; fit.Request.Id.TryWriteBytes(payload);
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(16), fit.Target.X);
                BinaryPrimitives.WriteInt16LittleEndian(payload.AsSpan(18), fit.Target.Y);
                BinaryPrimitives.WriteInt64LittleEndian(payload.AsSpan(20), fit.Deadline);
                return (NativeDisplayProtocol.FitWindow, payload);
            }
        }
        return terminalAuthority.RenderDisplay(revision, offset);
    }

    private LocalResizeResult ApplyNativeRemoteResize(LocalResizeRequest request, bool preserveDesktopFit)
    {
        var fit = new NativeFit { Request = request, PreserveDesktopFit = preserveDesktopFit, Deadline = Math.Min(request.ExpiresAtMs, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3500) };
        lock (terminalWriteSync)
        {
            var error = ValidateNativeFit(request);
            if (error != LocalResizeError.OK) return new LocalResizeResult { Id = request.Id, Error = error };
            lock (resizeSync)
            {
                fit.Before = innerSize;
                if (request.Restore && (!desktopSizeBeforeFit.HasValue || !lastFittedSize.HasValue ||
                    innerSize.X != lastFittedSize.Value.X || innerSize.Y != lastFittedSize.Value.Y))
                    return new LocalResizeResult { Id = request.Id, Error = LocalResizeError.NO_DESKTOP_SIZE };
                fit.Target = request.Restore ? desktopSizeBeforeFit.Value : new COORD { X = request.Columns, Y = request.Rows };
            }
            lock (nativeFitSync) pendingNativeFit = fit;
        }
        try
        {
            var delay = Math.Max(1, fit.Deadline - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            return fit.Completion.Task.WaitAsync(TimeSpan.FromMilliseconds(delay)).GetAwaiter().GetResult();
        }
        catch (TimeoutException)
        {
            // Serialize timeout with completion, so a timed-out request cannot later resize ConPTY.
            lock (nativeFitSync)
            {
                if (fit.Completion.Task.IsCompletedSuccessfully) return fit.Completion.Task.Result;
                if (pendingNativeFit == fit) pendingNativeFit = null;
                return new LocalResizeResult { Id = request.Id, Error = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= request.ExpiresAtMs
                    ? LocalResizeError.RESIZE_EXPIRED : LocalResizeError.WINDOW_RESIZE_UNSUPPORTED };
            }
        }
        finally { lock (nativeFitSync) if (pendingNativeFit == fit) pendingNativeFit = null; }
    }

    private LocalResizeError ValidateNativeFit(LocalResizeRequest request)
    {
        if (request.LocalInputEpoch != localInputEpoch) return LocalResizeError.LOCAL_INPUT_PRIORITY;
        if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= request.ExpiresAtMs) return LocalResizeError.RESIZE_EXPIRED;
        if (nativeDisplay == null || nativeDisplay.Failure != null || outputDisconnected || disposed) return LocalResizeError.SESSION_UNAVAILABLE;
        return LocalResizeError.OK;
    }

    private bool CompleteNativeFit(byte[] payload)
    {
        NativeDisplayProtocol.RequireSize(payload, 21);
        if (payload[20] > 1) throw new System.IO.InvalidDataException("Invalid native fit result.");
        var id = new Guid(payload.AsSpan(0, 16));
        var actual = new COORD { X = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(16)), Y = BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(18)) };
        lock (terminalWriteSync)
        lock (nativeFitSync)
        {
            var fit = pendingNativeFit;
            if (fit == null || fit.Request.Id != id || !fit.Dispatched) return false;
            var error = ValidateNativeFit(fit.Request);
            if (error == LocalResizeError.OK && (payload[20] == 0 || actual.X != fit.Target.X || actual.Y != fit.Target.Y ||
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= fit.Deadline)) error = LocalResizeError.WINDOW_RESIZE_UNSUPPORTED;
            if (error == LocalResizeError.OK)
            {
                ResizeInner(actual);
                lock (resizeSync)
                {
                    if (innerSize.X != actual.X || innerSize.Y != actual.Y) error = LocalResizeError.WINDOW_RESIZE_UNSUPPORTED;
                    else if (fit.Request.Restore || !fit.PreserveDesktopFit) { desktopSizeBeforeFit = null; lastFittedSize = null; }
                    else { desktopSizeBeforeFit ??= fit.Before; lastFittedSize = actual; }
                }
            }
            fit.Completion.TrySetResult(new LocalResizeResult { Id = id, Error = error, Columns = actual.X, Rows = actual.Y });
            pendingNativeFit = null;
            return error == LocalResizeError.OK;
        }
    }

    private bool ApplyNativeLocalResize(COORD size)
    {
        lock (terminalWriteSync)
        {
            lock (nativeFitSync) if (pendingNativeFit != null) return false;
            ResizeInner(size);
            lock (resizeSync)
            {
                if (innerSize.X != size.X || innerSize.Y != size.Y) return false;
                if (lastFittedSize.HasValue && (size.X != lastFittedSize.Value.X || size.Y != lastFittedSize.Value.Y))
                { desktopSizeBeforeFit = null; lastFittedSize = null; }
            }
            return true;
        }
    }
}
