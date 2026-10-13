using TermLink.NativeDisplay;
using TermLink.Desktop;

internal static class ClipboardAuthorizationValidation
{
    internal static void Run()
    {
        long time = 10000; var calls = 0; var answer = new TaskCompletionSource<bool>();
        var gate = new NativeClipboardAuthorizationGate(() => { calls++; return answer.Task; }, () => time);
        byte[] Request(Guid id, byte phase) { var data = new byte[17]; data[0] = phase; id.TryWriteBytes(data.AsSpan(1)); return data; }
        var id = Guid.NewGuid();
        Require(gate.Query(Request(id, 1), 0, true) == 0 && calls == 0, "Confirmation skipped initial authorization.");
        Require(gate.Query(Request(id, 0), 0, false) == 0 && calls == 0, "Disconnected host authorized.");
        Require(gate.Query(Request(id, 0), 0, true) == 1 && calls == 1, "Authorization was not asynchronous.");
        Require(gate.Query(Request(Guid.NewGuid(), 0), 0, true) == 0 && calls == 1, "Parallel prompts exceeded budget.");
        answer.SetResult(true);
        Require(gate.Query(Request(id, 0), 0, true) == 2, "Initial authorization failed.");
        // A user may read the preview for more than the network deadline.
        time += 8000; answer = new TaskCompletionSource<bool>();
        Require(gate.Query(Request(id, 1), 0, true) == 1 && calls == 2, "Confirmation did not reauthorize.");
        answer.SetResult(true);
        Require(gate.Query(Request(id, 1), 0, true) == 2, "Valid one-shot confirmation denied.");
        Require(gate.Query(Request(id, 1), 0, true) == 0, "Confirmation could be replayed.");
        time += 3001; id = Guid.NewGuid(); answer = new TaskCompletionSource<bool>();
        Require(gate.Query(Request(id, 0), 2, true) == 1, "Second request failed."); answer.SetResult(true);
        Require(gate.Query(Request(id, 0), 3, true) == 0, "Local/remote input switch retained permission.");
        time += 3001; id = Guid.NewGuid(); answer = new TaskCompletionSource<bool>();
        Require(gate.Query(Request(id, 0), 3, true) == 1, "Timeout fixture failed."); time += 3001;
        answer.SetResult(true);
        Require(gate.Query(Request(id, 0), 3, true) == 0, "Late network success escaped deadline.");
        time += 3001; id = Guid.NewGuid(); answer = new TaskCompletionSource<bool>();
        gate.Query(Request(id, 0), 3, true); answer.SetResult(true); gate.Query(Request(id, 0), 3, true);
        answer = new TaskCompletionSource<bool>(); gate.Query(Request(id, 1), 3, true); answer.SetResult(false);
        Require(gate.Query(Request(id, 1), 3, true) == 0, "Control loss at confirmation escaped.");
        time += 3001; id = Guid.NewGuid(); answer = new TaskCompletionSource<bool>();
        gate.Query(Request(id, 0), 3, true); gate.Query(Request(id, 2), 3, true); answer.SetResult(true);
        Require(gate.Query(Request(id, 0), 3, true) == 0, "Cancelled request revived after network completion.");
        time += 3001; id = Guid.NewGuid(); answer = new TaskCompletionSource<bool>();
        gate.Query(Request(id, 0), 3, true); answer.SetResult(true); gate.Query(Request(id, 0), 3, true); time += 30000;
        Require(gate.Query(Request(id, 1), 3, true) == 0, "Expired prompt confirmed.");
        Require(NativeTerminalClipboard.Preview("a\u001bb\u202ec\u200e\t\n") == "a\\u001Bb\\u202Ec\\u200E\\t\n", "Clipboard preview can spoof UI text.");
        Require(NativeTerminalClipboard.Preview(new string('x', 1000)).Length == 241, "Clipboard preview is unbounded.");
        Console.WriteLine("Clipboard authorization: asynchronous two-phase checks, fencing, cancellation, expiry, denial and replay passed.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
