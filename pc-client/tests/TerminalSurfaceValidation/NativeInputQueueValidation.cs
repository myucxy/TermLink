using System.Text;
using TermLink.Desktop;
using TermLink.NativeDisplay;

internal static class NativeInputQueueValidation
{
    internal static void Run()
    {
        static byte[] Mouse(byte action, byte button, byte held = 0, byte x = 0) {
            var bytes = new byte[20]; bytes[0] = action; bytes[1] = button; bytes[2] = held; bytes[4] = x; return bytes;
        }
        static byte[] Key() => new byte[26];
        static byte[] Text(string value) => NativeDisplayProtocol.Utf8.GetBytes(value);
        static List<(byte Kind, byte[] Payload)> Drain(NativeTerminalInputQueue queue)
        {
            var result = new List<(byte Kind, byte[] Payload)>();
            while (queue.TryRead(out var value)) result.Add(value);
            return result;
        }

        // Text events are individually queued by Avalonia. A command longer
        // than the old 64-item limit, including Chinese text and Enter, must
        // remain one ordered input stream.
        var queue = new NativeTerminalInputQueue();
        const string generator = "1..5200|%{\"TLTF_release_r205_shell_{0:D4}\"-f $_};\"TLTF_LIVE_98e88eb48eda27ee\"";
        var command = generator + " 中文";
        foreach (var rune in command.EnumerateRunes())
            Require(queue.TryWrite(NativeDisplayProtocol.Text, Text(rune.ToString())), "long text event was rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.ConsoleKey, Key()), "Enter was rejected after long text");
        var drained = Drain(queue);
        Require(drained.Count == 2 && drained[0].Kind == NativeDisplayProtocol.Text &&
            NativeDisplayProtocol.Utf8.GetString(drained[0].Payload) == command &&
            drained[1].Kind == NativeDisplayProtocol.ConsoleKey,
            "long text or Enter was dropped/reordered");

        // Text must not cross a key or paste boundary, and a single large
        // text request is retained as-is rather than split.
        queue = new NativeTerminalInputQueue();
        Require(queue.TryWrite(NativeDisplayProtocol.Text, Text("abc")), "text prefix rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.ConsoleKey, Key()), "key boundary rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.Text, Text("def")), "text middle rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.Paste, Text("[paste]")), "paste boundary rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.Text, Text("ghi")), "text suffix rejected");
        var separated = Drain(queue);
        Require(separated.Count == 5 && separated[0].Kind == NativeDisplayProtocol.Text &&
            NativeDisplayProtocol.Utf8.GetString(separated[0].Payload) == "abc" &&
            separated[1].Kind == NativeDisplayProtocol.ConsoleKey &&
            separated[2].Kind == NativeDisplayProtocol.Text &&
            NativeDisplayProtocol.Utf8.GetString(separated[2].Payload) == "def" &&
            separated[3].Kind == NativeDisplayProtocol.Paste &&
            separated[4].Kind == NativeDisplayProtocol.Text &&
            NativeDisplayProtocol.Utf8.GetString(separated[4].Payload) == "ghi",
            "text crossed a key or paste boundary");
        var largeText = new string('x', 4097);
        Require(queue.TryWrite(NativeDisplayProtocol.Text, Text(largeText)), "large text rejected");
        separated = Drain(queue);
        Require(separated.Count == 1 && separated[0].Kind == NativeDisplayProtocol.Text &&
            separated[0].Payload.Length == Encoding.UTF8.GetByteCount(largeText),
            "large text request was split or changed");

        // A held mouse gesture continues to coalesce moves without crossing a
        // reliable input barrier, and its release remains FIFO ordered.
        queue = new NativeTerminalInputQueue();
        Require(queue.TryWrite(NativeDisplayProtocol.Mouse, Mouse(1, 1)), "new press rejected");
        for (var i = 0; i < 10_000; i++)
            Require(queue.TryWrite(NativeDisplayProtocol.Mouse, Mouse(3, 0, 1, (byte)i)), "move rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.Text, Text("barrier")), "barrier rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.Mouse, Mouse(3, 0, 1, 99)), "move after barrier rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.Mouse, Mouse(2, 1)), "release after drag rejected");
        drained = Drain(queue);
        Require(drained.Count == 5 && drained[1].Payload[4] == unchecked((byte)9999) &&
            drained[2].Kind == NativeDisplayProtocol.Text && drained[3].Payload[4] == 99 &&
            drained[4].Kind == NativeDisplayProtocol.Mouse && drained[4].Payload[0] == 2,
            "drag coalescing crossed a reliable input boundary");

        // The byte budget includes a conservative item overhead. Seven maximum
        // paste requests fit; the eighth is rejected without dropping earlier
        // input. Gesture release/focus reservations remain available at the
        // same boundary.
        var paste = new byte[NativeDisplayProtocol.MaximumRequestBytes];
        queue = new NativeTerminalInputQueue();
        for (var i = 0; i < 7; i++) Require(queue.TryWrite(NativeDisplayProtocol.Paste, paste), "paste below byte budget rejected");
        Require(!queue.TryWrite(NativeDisplayProtocol.Paste, paste), "byte budget was exceeded");
        queue = new NativeTerminalInputQueue();
        for (var i = 0; i < 7; i++) Require(queue.TryWrite(NativeDisplayProtocol.Paste, paste), "near-limit paste rejected");
        const int itemOverhead = 128;
        const int nearLimitText = 4090;
        var fillerLength = 8 * 1024 * 1024 - 7 * (NativeDisplayProtocol.MaximumRequestBytes + itemOverhead) -
            itemOverhead - (nearLimitText + itemOverhead) - 3;
        Require(fillerLength > 0 && fillerLength <= NativeDisplayProtocol.MaximumRequestBytes, "near-limit fixture is out of bounds");
        Require(queue.TryWrite(NativeDisplayProtocol.Paste, new byte[fillerLength]), "near-limit filler rejected");
        Require(queue.TryWrite(NativeDisplayProtocol.Text, Text(new string('x', nearLimitText))), "near-limit text rejected");
        Require(!queue.TryWrite(NativeDisplayProtocol.Text, Text("123456")), "text merge crossed the byte budget");
        var nearLimit = Drain(queue);
        Require(nearLimit.Count == 9 && nearLimit[^1].Kind == NativeDisplayProtocol.Text &&
            nearLimit[^1].Payload.Length == nearLimitText, "rejected text changed the near-limit queue");
        queue = new NativeTerminalInputQueue();
        Require(queue.TryWrite(NativeDisplayProtocol.Focus, [1]), "focus rejected");
        for (byte button = 1; button <= 3; button++) Require(queue.TryWrite(NativeDisplayProtocol.Mouse, Mouse(1, button)), "press rejected");
        for (var i = 0; i < 7; i++) Require(queue.TryWrite(NativeDisplayProtocol.Paste, paste), "reserved paste rejected");
        Require(!queue.TryWrite(NativeDisplayProtocol.Paste, paste), "reserved byte budget was exceeded");
        for (byte button = 1; button <= 3; button++) Require(queue.TryWrite(NativeDisplayProtocol.Mouse, Mouse(2, button)), "reserved release lost to backpressure");
        Require(queue.TryWrite(NativeDisplayProtocol.Focus, [0]), "reserved focus loss lost to backpressure");

        queue.Complete();
        Require(!queue.TryRead(out _) && !queue.TryWrite(NativeDisplayProtocol.Text, [1]), "closed queue retained input");
        Console.WriteLine("Native input queue: reliable long text/Enter, UTF-8 and event boundaries, mouse coalescing, byte budget reservations and close behavior passed.");
    }
    private static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
}
