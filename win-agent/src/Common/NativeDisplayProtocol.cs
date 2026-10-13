#nullable enable
using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TermLink.NativeDisplay
{
    // One foreground prompt, two fresh authorizations, and a terminal-input fence. Never persisted.
    internal sealed class NativeClipboardAuthorizationGate
    {
        private readonly Func<Task<bool>> authorize;
        private readonly Func<long> now;
        private Guid id;
        private ulong epoch;
        private long began, checkBegan;
        private byte phase;
        private bool finished, initialAllowed;
        private Task<bool> check = Task.FromResult(false);

        internal NativeClipboardAuthorizationGate(Func<Task<bool>> authorize, Func<long>? now = null)
        { this.authorize = authorize; this.now = now ?? (() => Environment.TickCount64); }

        // payload: phase (0 offer, 1 confirm, 2 cancel), random request UUID; reply: denied/pending/allowed.
        internal byte Query(byte[] payload, ulong currentEpoch, bool connected)
        {
            NativeDisplayProtocol.RequireSize(payload, 17);
            var requestedPhase = payload[0]; var requestedId = new Guid(payload.AsSpan(1));
            if (requestedPhase > 2 || requestedId == Guid.Empty) throw new InvalidDataException("Invalid clipboard query.");
            var time = now();
            if (requestedPhase == 2) { if (requestedId == id) finished = true; return 0; }
            if (requestedId != id) {
                if (requestedPhase != 0 || !connected || id != Guid.Empty && !finished && time - began < 30000) return 0;
                // A terminal cannot cause a network authorization flood.
                if (id != Guid.Empty && time - checkBegan < 3000) return 0;
                id = requestedId; epoch = currentEpoch; began = time; phase = 0; finished = false; initialAllowed = false;
                StartCheck(time);
            }
            if (finished || !connected || epoch != currentEpoch || time - began >= 30000) { finished = true; return 0; }
            if (requestedPhase != phase) {
                if (requestedPhase != 1 || phase != 0 || !initialAllowed) { finished = true; return 0; }
                phase = 1; StartCheck(time);
            }
            if (!check.IsCompleted && time - checkBegan < 3000) return 1;
            if (!check.IsCompletedSuccessfully || !check.Result || time - checkBegan >= 3000) { finished = true; return 0; }
            if (phase == 0) initialAllowed = true;
            else finished = true; // a confirmation is consumable exactly once
            return 2;
        }

        private void StartCheck(long time)
        {
            checkBegan = time;
            try { check = authorize(); } catch { check = Task.FromResult(false); }
        }
    }

    // Separate authenticated current-user pipe. Display commands never enter the durable journal;
    // selected-host state enters only through validated, versioned journal events.
    internal static class NativeDisplayProtocol
    {
        internal const int MaximumFrameBytes = 32 * 1024 * 1024;
        internal const int MaximumGraphicsFrameBytes = 64 * 1024 * 1024;
        internal const int MaximumRequestBytes = 1024 * 1024 + 32;
        internal const byte Render = 1, ConsoleKey = 2, Text = 3, Paste = 4, Resize = 5, Focus = 6, Mouse = 7, FitResult = 8, HostGeometry = 9, HostTheme = 10;
        internal const byte NotReady = 0, Unchanged = 1, Frame = 2, Accepted = 3, Rejected = 4, FitWindow = 5;
        internal const byte HistoryExpired = 6, HistoryLimit = 7, HostResizeNotice = 8;
        internal const byte ShellQuery = 11, ShellSeek = 12;
        internal const byte ShellState = 9, ShellAnchor = 10;
        internal const byte SignalsQuery = 13, SignalsState = 11;
        internal const byte ClipboardQuery = 14, ClipboardState = 12;
        internal const byte FrameV5 = 13;
        internal static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static async Task WriteAsync(Stream stream, byte kind, byte[] payload, int maximum, CancellationToken token)
        {
            if (payload == null || payload.Length > PayloadLimit(kind, maximum)) throw new InvalidDataException("Native display message exceeds its budget.");
            var header = new byte[6]; header[0] = 1; header[1] = kind;
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), payload.Length);
            await stream.WriteAsync(header, token).ConfigureAwait(false);
            await stream.WriteAsync(payload, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        internal static async Task<(byte Kind, byte[] Payload)> ReadAsync(Stream stream, int maximum, CancellationToken token)
        {
            var header = new byte[6];
            await stream.ReadExactlyAsync(header, token).ConfigureAwait(false);
            if (header[0] != 1) throw new InvalidDataException("Unsupported native display protocol.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(2));
            if (length < 0 || length > PayloadLimit(header[1], maximum)) throw new InvalidDataException("Native display message exceeds its budget.");
            var payload = new byte[length];
            await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
            return (header[1], payload);
        }

        private static int PayloadLimit(byte kind, int maximum) =>
            Math.Min(maximum, kind == FrameV5 ? MaximumGraphicsFrameBytes : MaximumFrameBytes);

        internal static void RequireSize(byte[] payload, int length)
        {
            if (payload.Length != length) throw new InvalidDataException("Invalid native display request length.");
        }
    }
}
