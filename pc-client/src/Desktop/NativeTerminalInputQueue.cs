using TermLink.NativeDisplay;
using TermLink.TerminalCore;

namespace TermLink.Desktop;

/// <summary>FIFO with bounded memory and reserved space to end accepted pointer/focus gestures.</summary>
internal sealed class NativeTerminalInputQueue
{
    // Bound by bytes: Avalonia emits one Text request per event, so an item cap
    // can silently drop a fast command tail and its Enter key.
    private const int ByteCapacity = 8 * 1024 * 1024;
    private const int ItemOverheadBytes = 128;
    private const int MaximumMergedTextBytes = 4 * 1024;
    private static readonly System.Text.UTF8Encoding StrictUtf8 = new(false, true);
    private readonly object sync = new();
    private readonly LinkedList<(byte Kind, byte[] Payload)> pending = new();
    private int bytes, heldButtons;
    private bool focused, closed;

    internal bool TryWrite(byte kind, byte[] payload)
    {
        lock (sync)
        {
            if (closed || payload.Length > NativeDisplayProtocol.MaximumRequestBytes) return false;
            var nextButtons = heldButtons; var nextFocus = focused;
            var move = kind == NativeDisplayProtocol.Mouse && payload.Length == 20 && payload[0] == (byte)NativeTerminalMouseAction.Move;
            if (kind == NativeDisplayProtocol.Mouse && payload.Length == 20 && payload[1] is >= 1 and <= 3)
            {
                var bit = 1 << (payload[1] - 1);
                if (payload[0] == (byte)NativeTerminalMouseAction.Press) nextButtons |= bit;
                if (payload[0] == (byte)NativeTerminalMouseAction.Release)
                {
                    if ((heldButtons & bit) == 0) return true; // The matching press was never admitted.
                    nextButtons &= ~bit;
                }
            }
            if (kind == NativeDisplayProtocol.Focus && payload.Length == 1)
            {
                nextFocus = payload[0] != 0;
                if (nextFocus == focused) return true;
            }
            if (kind == NativeDisplayProtocol.Mouse && payload.Length == 20) payload[2] = (byte)nextButtons;
            var reserved = System.Numerics.BitOperations.PopCount((uint)nextButtons) + (nextFocus ? 1 : 0);
            var queuedBudget = (long)bytes + (long)pending.Count * ItemOverheadBytes;
            // Reserve both record overhead and wire bytes for release/focus-loss.
            var reservationBudget = (long)reserved * ItemOverheadBytes +
                (long)System.Numerics.BitOperations.PopCount((uint)nextButtons) * 20 +
                (nextFocus ? 1L : 0L);
            // Merge only adjacent complete UTF-8 Text events; key and paste
            // boundaries stay visible to the terminal.
            if (kind == NativeDisplayProtocol.Text &&
                pending.Last is { Value: var text } && text.Kind == NativeDisplayProtocol.Text &&
                text.Payload.Length <= MaximumMergedTextBytes && payload.Length <= MaximumMergedTextBytes &&
                text.Payload.Length + payload.Length <= MaximumMergedTextBytes &&
                IsCompleteUtf8(text.Payload) && IsCompleteUtf8(payload))
            {
                if (queuedBudget + payload.Length + reservationBudget > ByteCapacity) return false;
                var merged = new byte[text.Payload.Length + payload.Length];
                text.Payload.CopyTo(merged, 0); payload.CopyTo(merged, text.Payload.Length);
                pending.Last.Value = (kind, merged);
                bytes += payload.Length;
                return true;
            }
            // Only adjacent moves with the same button/modifier state may be collapsed.
            if (move && pending.Last is { Value: var last } && last.Kind == kind &&
                last.Payload.Length == 20 && last.Payload.AsSpan(0, 4).SequenceEqual(payload.AsSpan(0, 4)))
            {
                pending.Last.Value = (kind, payload); return true;
            }
            // Keep room for every outstanding release/focus-loss record.
            var requestBudget = (long)payload.Length + ItemOverheadBytes;
            if (queuedBudget + requestBudget + reservationBudget > ByteCapacity)
                return move; // Dropping a motion sample never drops a press, release or key.
            pending.AddLast((kind, payload)); bytes += payload.Length;
            heldButtons = nextButtons; focused = nextFocus;
            return true;
        }
    }

    internal bool TryRead(out (byte Kind, byte[] Payload) request)
    {
        lock (sync)
        {
            if (pending.First == null) { request = default; return false; }
            request = pending.First.Value; pending.RemoveFirst(); bytes -= request.Payload.Length;
            return true;
        }
    }

    internal void Complete()
    {
        lock (sync) { closed = true; pending.Clear(); bytes = 0; }
    }

    private static bool IsCompleteUtf8(byte[] payload)
    {
        try { StrictUtf8.GetCharCount(payload); return true; }
        catch (System.Text.DecoderFallbackException) { return false; }
    }
}
