// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using TermLink.Agent.Terminal;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Protocol.Realtime.V1;

internal static class TerminalCleanupValidation
{
    internal static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tl-cleanup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var key = RandomNumberGenerator.GetBytes(32);
        var commands = new[] { new TerminalCommandCapability("sh", "Shell", "/bin/sh", [], true, false) };
        var pty = new FixturePty();
        Guid id = Guid.Empty, operation = Guid.NewGuid();
        byte[] state = [];
        try
        {
            await using (var host = new PortableTerminalHost(pty, commands, directory, key))
            {
                Require(host.SupportsCleanup, "Persistent host did not enable cleanup.");
                id = await host.StartAsync("sh", Path.GetTempPath(), operation, CancellationToken.None);
                var denied = await host.CleanupAsync(Request(id), () => Task.FromResult(false), CancellationToken.None);
                Require(denied.ErrorCode == "MANAGE_NOT_GRANTED" && host.Sessions.Count == 1 && pty.Stops == 0, "Denied cleanup changed a live session.");
                var active = await host.CleanupAsync(Request(id), Allowed, CancellationToken.None);
                Require(active.Results.Single().Status == "SKIPPED_ACTIVE" && pty.Stops == 0, "Cleanup terminated a running shell.");
                var invalid = Request(id); invalid.SessionIds.Add(Id(id));
                Require((await host.CleanupAsync(invalid, Allowed, CancellationToken.None)).ErrorCode == "SESSION_CLEANUP_INVALID", "Duplicate IDs accepted.");
                invalid = Request(id); invalid.RequestId = ByteString.Empty;
                Require((await host.CleanupAsync(invalid, Allowed, CancellationToken.None)).ErrorCode == "SESSION_CLEANUP_INVALID", "Empty request ID accepted.");
                await host.StopAsync(id, CancellationToken.None);
                state = File.ReadAllBytes(Path.Combine(directory, id.ToString("N") + ".state"));
                var unknown = Guid.NewGuid();
                var batch = Request(id, unknown);
                var result = await host.CleanupAsync(batch, Allowed, CancellationToken.None);
                Require(result.RequestId == batch.RequestId && result.ErrorCode.Length == 0 && result.Results.Select(x => x.Status).SequenceEqual(["DELETED", "NOT_FOUND"]), "Mixed cleanup result incorrect.");
                Require(host.Sessions.Count == 0 && host.DeletedSessions.SequenceEqual([id]) && !File.Exists(Path.Combine(directory, id.ToString("N") + ".state")), "Cleanup did not remove the record/checkpoint.");
                Require((await host.CleanupAsync(Request(id), Allowed, CancellationToken.None)).Results.Single().Status == "DELETED", "Cleanup retry was not idempotent.");
                Require(await host.StartAsync("sh", Path.GetTempPath(), operation, CancellationToken.None) == id && pty.Launches == 1, "Deleted operation receipt relaunched a shell.");
                var fresh = await host.StartAsync("sh", Path.GetTempPath(), Guid.NewGuid(), CancellationToken.None);
                Require(fresh != id && pty.Launches == 2, "Cleanup prevented a new independent session.");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { await host.CleanupAsync(Request(fresh), Allowed, cancelled.Token); throw new Exception("Cancelled cleanup continued."); }
                catch (OperationCanceledException) { }
                Require(host.Sessions.Any(x => x.Id == fresh), "Cancelled cleanup changed records.");
                await host.StopAsync(fresh, CancellationToken.None);
                var closedPtyButAlive = await host.StartAsync("sh", Path.GetTempPath(), Guid.NewGuid(), CancellationToken.None);
                pty.AliveAfterEof = true; pty.EndOutput(closedPtyButAlive);
                var end = DateTimeOffset.UtcNow.AddSeconds(2);
                while (host.Sessions.Single(x => x.Id == closedPtyButAlive).State == "RUNNING")
                { if (DateTimeOffset.UtcNow > end) throw new Exception("EOF not observed"); await Task.Delay(10); }
                await Task.Delay(10);
                var stoppedBefore = pty.Stops;
                Require((await host.CleanupAsync(Request(closedPtyButAlive), Allowed, CancellationToken.None)).Results.Single().Status == "SKIPPED_ACTIVE" && pty.Stops == stoppedBefore,
                    "Cleanup signalled a live process after PTY EOF.");
                pty.AliveAfterEof = false;
                await host.StopAsync(closedPtyButAlive, CancellationToken.None);
            }
            // Recreate the pre-delete checkpoint to model a crash after the
            // tombstone rename and before unlink. The durable fence wins.
            File.WriteAllBytes(Path.Combine(directory, id.ToString("N") + ".state"), state);
            await using (var restored = new PortableTerminalHost(pty, commands, directory, key))
            {
                Require(!restored.Sessions.Any(x => x.Id == id) && restored.DeletedSessions.Contains(id), "Deleted history reappeared after restart.");
                Require(await restored.StartAsync("sh", Path.GetTempPath(), operation, CancellationToken.None) == id && pty.Launches == 3, "Restart lost the deleted launch receipt.");
            }
            var ledger = Path.Combine(directory, "deleted-sessions-v1.bin");
            var encrypted = File.ReadAllBytes(ledger); encrypted[^1] ^= 1; File.WriteAllBytes(ledger, encrypted);
            try { await using var corrupted = new PortableTerminalHost(pty, commands, directory, key); throw new Exception("Tampered cleanup ledger accepted."); }
            catch (CryptographicException) { }
        }
        finally { CryptographicOperations.ZeroMemory(key); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS terminal cleanup: active/authority fences, invalid batches, durable tombstones, retry, restart/no resurrection, operation receipts, cancellation and tamper rejection");
    }
    private static Task<bool> Allowed() => Task.FromResult(true);
    private static ByteString Id(Guid value) => ByteString.CopyFrom(Convert.FromHexString(value.ToString("N")));
    private static SessionCleanupRequestPayload Request(params Guid[] ids)
    { var request = new SessionCleanupRequestPayload { RequestId = Id(Guid.NewGuid()) }; request.SessionIds.AddRange(ids.Select(Id)); return request; }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class FixturePty : ITerminalSessionHost
    {
        private readonly Dictionary<Guid, Channel<byte[]>> output = new();
        public int Launches, Stops;
        public bool AliveAfterEof;
        public void EndOutput(Guid id) => output[id].Writer.TryComplete();
        public ValueTask<TerminalLaunchResult> LaunchAsync(TerminalLaunchRequest request, CancellationToken token)
        { var id = Guid.NewGuid(); output[id] = Channel.CreateUnbounded<byte[]>(); Launches++; return ValueTask.FromResult(new TerminalLaunchResult(id, 1, request.CommandId)); }
        public async ValueTask<int> ReadOutputAsync(Guid id, Memory<byte> buffer, CancellationToken token)
        { try { var data = await output[id].Reader.ReadAsync(token); data.CopyTo(buffer); return data.Length; } catch (ChannelClosedException) { return 0; } }
        public ValueTask StopAsync(Guid id, CancellationToken token) { Stops++; output[id].Writer.TryComplete(); return ValueTask.CompletedTask; }
        public ValueTask ResizeAsync(Guid id, int columns, int rows, CancellationToken token) => ValueTask.CompletedTask;
        public ValueTask SendInputAsync(Guid id, ReadOnlyMemory<byte> bytes, CancellationToken token) => ValueTask.CompletedTask;
        public async ValueTask<int> WaitForExitAsync(Guid id, CancellationToken token)
        { if (AliveAfterEof) await Task.Delay(Timeout.Infinite, token); return 0; }
        public ValueTask SignalAsync(Guid id, TerminalSignal signal, CancellationToken token) => ValueTask.CompletedTask;
    }
}
