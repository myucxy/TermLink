using System;
using System.IO;
using System.Linq;
using System.Text;
using TermLink.Agent;

internal static class TerminalFailureStoreValidation
{
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "termlink-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        var store = new TerminalFailureStore(directory);
        var session = Guid.NewGuid();
        const string evidence = "TL-DIAG/2\nsequence=<ESC>[?9999h\n";
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        try
        {
            Check(store.Read(session) == null, "Absent diagnostic invented evidence.");
            Check(store.TryWrite(session, evidence), "Protected diagnostic write failed.");
            var text = store.Read(session);
            Check(text != null && text.EndsWith(evidence, StringComparison.Ordinal), "Diagnostic roundtrip lost root cause.");
            Check(store.Read(Guid.NewGuid()) == null, "Diagnostic leaked between sessions.");
            Check(store.TryWrite(session, evidence.Replace("9999", "8888")) && store.Read(session) == text,
                "A secondary error replaced the first root cause.");
            Check(!store.TryWrite(Guid.NewGuid(), "TL-DIAG/2\n\u001b[2J\n"), "Raw terminal control accepted in diagnostic.");
            Check(!store.TryWrite(Guid.NewGuid(), "TL-DIAG/2\n" + new string('x', 4096)), "Unbounded diagnostic accepted.");
            var path = Directory.GetFiles(directory, "*.dpapi").Single();
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("9999", StringComparison.Ordinal), "Evidence persisted as plaintext.");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
            Check(store.Read(session) == null, "Expired evidence still visible.");
            Check(store.TryWrite(Guid.NewGuid(), evidence) && !File.Exists(path), "Expired owned record not pruned.");
            var corrupt = Directory.GetFiles(directory, "*.dpapi").Single();
            File.WriteAllBytes(corrupt, new byte[50]);
            Check(store.Read(Guid.ParseExact(Path.GetFileNameWithoutExtension(corrupt), "N")) == null, "Corrupt protected evidence accepted.");
            var macroSession = Guid.NewGuid();
            var macroEvidence = "TL-DIAG/3\nsequence=<ESC>[5*z\nexpanded_source=mixed\n" + new string('x', 8100) + "\n";
            Check(store.TryWrite(macroSession, macroEvidence) && store.Read(macroSession)?.EndsWith(macroEvidence, StringComparison.Ordinal) == true,
                "Expanded diagnostic lost bytes in protected store.");
            Check(store.TryWrite(macroSession, evidence) && store.Read(macroSession)?.EndsWith(macroEvidence, StringComparison.Ordinal) == true,
                "Expanded root cause was replaced by a secondary failure.");
            var inputSession = Guid.NewGuid();
            const string inputEvidence = "TL-INPUT-DIAG/1\ndirection=input_encoding\nunsupported_scalar=U+1F6A7\n";
            Check(store.TryWriteInput(inputSession, inputEvidence), "Protected input diagnostic write failed.");
            Check(store.Read(inputSession)?.EndsWith(inputEvidence, StringComparison.Ordinal) == true,
                "Input diagnostic roundtrip lost the rejected character.");
            Check(store.TryWrite(inputSession, evidence) && store.Read(inputSession)?.EndsWith(evidence, StringComparison.Ordinal) == true,
                "Fatal output diagnostic did not take priority over input evidence.");
            var fatalInputSession = Guid.NewGuid();
            Check(store.TryWrite(fatalInputSession, inputEvidence) && store.Read(fatalInputSession)?.EndsWith(inputEvidence, StringComparison.Ordinal) == true,
                "Fatal input diagnostic was not retained as first failure evidence.");
            Check(!store.TryWriteInput(Guid.NewGuid(), "TL-INPUT-DIAG/1\ndirection=output\n"),
                "Output-direction input diagnostic was accepted.");
            Check(!store.TryWriteInput(Guid.NewGuid(), "TL-INPUT-DIAG/1\n" + new string('x', 2048) + "\n"),
                "Oversized input diagnostic was accepted.");
            Check(!store.TryWrite(Guid.NewGuid(), "TL-DIAG/3\n" + new string('x', 8192) + "\n"), "Oversized expanded diagnostic accepted.");
            Console.WriteLine("Protected terminal diagnostics: root retention, session isolation, expiry and malformed input passed.");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }
}
