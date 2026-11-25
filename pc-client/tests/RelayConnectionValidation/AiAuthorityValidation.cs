// SPDX-License-Identifier: GPL-3.0-only
using System.Collections.Concurrent;
using System.Text.Json;
using TermLink.Agent.Connection;
using TermLink.Pc.Contracts;
using TermLink.Protocol;

internal sealed record AiAuthorityRecipient(Guid Id, DeviceIdentity Identity, string Permission);

internal static class AiAuthorityValidation
{
    public static async Task RunAsync()
    {
        await AdditionAsync(initiallyEmpty: false);
        await AdditionAsync(initiallyEmpty: true);
        foreach (var change in new[] { "remove", "permission", "key", "epoch", "stale-after-fence" }) await ChangeFencesAsync(change);
        await StaleSnapshotCannotEraseAdditionAsync();
        Console.WriteLine("PASS AI authority: additive/empty startup, unchanged in-flight caller, VIEW/CONTROL denial; remove/permission/key/epoch/stale snapshots fence all work");
    }

    private static object Grants(IEnumerable<AiAuthorityRecipient> recipients, long epoch = 1) => new
    {
        key_epoch = epoch,
        items = recipients.Select(r => new { device_id = r.Id, device_type = "IOS", permission = r.Permission,
            exchange_public_key = Convert.ToBase64String(r.Identity.Exchange.PublicKey) }).ToArray(),
    };

    private static async Task<RelayConnectionService> ConnectAsync(RelayFixture server, List<AuthorityHost> hosts)
    {
        var service = await RelayConnectionService.CreateAsync(new MemorySecrets(),
            new PcAgentIdentity(Guid.NewGuid(), "authority fixture", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS validation", "0.3.16.146"), CancellationToken.None,
            authorization: new FixtureAuthorization(server), aiHostFactory: _ => { var host = new AuthorityHost(); lock (hosts) hosts.Add(host); return host; });
        try
        {
            await service.ConfigureServerAsync(server.Origin, CancellationToken.None);
            await service.AuthorizeAsync("authority fixture", CancellationToken.None);
            await service.SetRemoteAccessAsync(true, CancellationToken.None);
            await Until(() => server.AuthorityReads >= 2 && service.Snapshot.State == AgentConnectionState.Connected);
            return service;
        }
        catch { await service.DisposeAsync(); throw; }
    }

    private static async Task AdditionAsync(bool initiallyEmpty)
    {
        await using var server = new RelayFixture { EnableAi = true, ManualAiRequests = true };
        using var addedIdentity = DeviceIdentity.Generate();
        using var viewIdentity = DeviceIdentity.Generate();
        using var controlIdentity = DeviceIdentity.Generate();
        var original = server.ExistingMobile;
        var added = new AiAuthorityRecipient(Guid.NewGuid(), addedIdentity, "MANAGE");
        var view = new AiAuthorityRecipient(Guid.NewGuid(), viewIdentity, "VIEW");
        var control = new AiAuthorityRecipient(Guid.NewGuid(), controlIdentity, "CONTROL");
        AiAuthorityRecipient[] current = initiallyEmpty ? [] : [original];
        server.GrantSnapshotAsync = () => Task.FromResult(Grants(Volatile.Read(ref current)));
        var hosts = new List<AuthorityHost>();
        await using var service = await ConnectAsync(server, hosts);
        var host = hosts.Single();
        if (!initiallyEmpty)
        {
            await server.SendAiAsync(original, 101, "held");
            await Until(() => host.HeldCount == 1);
        }
        Volatile.Write(ref current, initiallyEmpty ? [added, view, control] : [original, added, view, control]);
        await server.SendAiAsync(added, 102, "catalog");
        await Until(() => server.ManualReplies.ContainsKey(102), "Newly granted mobile catalog did not reply on the original connection.");
        Require(!host.Disposed && hosts.Count == 1 && server.Connections == 1, "A pure recipient addition rebuilt the AI owner or Relay connection.");
        if (!initiallyEmpty)
        {
            Require(host.Held.All(c => c.Authorized()), "A recipient addition invalidated the existing caller's work.");
            host.Release.TrySetResult();
            await Until(() => server.ManualReplies.ContainsKey(101));
        }
        var calls = host.Calls;
        await server.SendAiAsync(view, 103, "catalog");
        await server.SendAiAsync(control, 104, "catalog");
        await Task.Delay(150);
        Require(host.Calls == calls && !server.ManualReplies.ContainsKey(103) && !server.ManualReplies.ContainsKey(104), "VIEW or CONTROL acquired AI execution.");
        Require(!host.Disposed && hosts.Count == 1 && server.Connections == 1, "Non-MANAGE addition retired the current authority.");
        var authorityReads = server.AuthorityReads;
        await Until(() => server.Pings >= 2 && server.AuthorityReads > authorityReads);
        Require(!host.Disposed && hosts.Count == 1 && server.Connections == 1, "Heartbeat rejected the accepted additive authority set.");
        await service.SetRemoteAccessAsync(false, CancellationToken.None);
    }

    private static async Task ChangeFencesAsync(string change)
    {
        await using var server = new RelayFixture { EnableAi = true, ManualAiRequests = true };
        using var replacementKey = DeviceIdentity.Generate();
        var original = server.ExistingMobile;
        var unchanged = Grants([original]);
        var fresh = change switch
        {
            "remove" => Grants([]),
            "permission" => Grants([original with { Permission = "VIEW" }]),
            "key" => Grants([original with { Identity = replacementKey }]),
            "epoch" => Grants([original], 2),
            _ => Grants([]),
        };
        var changed = 0;
        var staleReadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStaleRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleClaimed = 0;
        server.GrantSnapshotAsync = async () =>
        {
            if (change == "stale-after-fence" && Volatile.Read(ref changed) == 1 && Interlocked.CompareExchange(ref staleClaimed, 1, 0) == 0)
            {
                staleReadEntered.TrySetResult();
                await releaseStaleRead.Task;
                return unchanged;
            }
            return Volatile.Read(ref changed) == 0 ? unchanged : fresh;
        };
        var hosts = new List<AuthorityHost>();
        await using var service = await ConnectAsync(server, hosts);
        var host = hosts.Single();
        try
        {
            await server.SendAiAsync(original, 201, "held");
            await server.SendAiAsync(original, 202, "held");
            await Until(() => host.HeldCount == 2);
            Volatile.Write(ref changed, 1);
            if (change == "stale-after-fence")
            {
                await server.SendAiAsync(original, 203, "catalog");
                await staleReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            await server.SendAiAsync(original, 204, "catalog");
            await Until(() => host.Disposed);
            Require(host.Held.All(c => !c.Authorized() && c.Token.IsCancellationRequested), "A non-additive authority change left an old request authorized or uncancelled.");
            releaseStaleRead.TrySetResult();
            host.Release.TrySetResult();
            await Until(() => host.Held.All(c => c.Settled.Task.IsCompleted));
            await service.SetRemoteAccessAsync(false, CancellationToken.None);
            Require(!server.ManualReplies.Keys.Any(seq => seq is >= 201 and <= 204), "A stale or changed authority released a response.");
            Require(host.Calls == 2, "A request crossed the non-additive authority fence or stale snapshot reactivated it.");
        }
        finally { releaseStaleRead.TrySetResult(); host.Release.TrySetResult(); }
    }

    private static async Task StaleSnapshotCannotEraseAdditionAsync()
    {
        await using var server = new RelayFixture { EnableAi = true, ManualAiRequests = true };
        using var addedIdentity = DeviceIdentity.Generate();
        var original = server.ExistingMobile;
        var added = new AiAuthorityRecipient(Guid.NewGuid(), addedIdentity, "MANAGE");
        var old = Grants([original]);
        var expanded = Grants([original, added]);
        var stage = 0;
        var staleEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStale = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleClaimed = 0;
        server.GrantSnapshotAsync = async () =>
        {
            if (Volatile.Read(ref stage) == 0) return old;
            if (Volatile.Read(ref stage) == 2 && Interlocked.CompareExchange(ref staleClaimed, 1, 0) == 0)
            {
                staleEntered.TrySetResult();
                await releaseStale.Task;
                return old;
            }
            return expanded;
        };
        var hosts = new List<AuthorityHost>();
        await using var service = await ConnectAsync(server, hosts);
        var host = hosts.Single();
        try
        {
            Volatile.Write(ref stage, 1);
            await server.SendAiAsync(added, 301, "held");
            await Until(() => host.HeldCount == 1);
            Volatile.Write(ref stage, 2);
            await server.SendAiAsync(original, 302, "catalog");
            await staleEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await server.SendAiAsync(original, 303, "held");
            await Until(() => host.HeldCount == 2);
            releaseStale.TrySetResult();
            await Until(() => host.Disposed);
            Require(host.Held.All(call => !call.Authorized() && call.Token.IsCancellationRequested),
                "A stale snapshot erased an accepted recipient without fencing its in-flight work.");
            host.Release.TrySetResult();
            await service.SetRemoteAccessAsync(false, CancellationToken.None);
            Require(!server.ManualReplies.Keys.Any(seq => seq is >= 301 and <= 303) && host.Calls == 2,
                "A stale authority snapshot resurrected or released a response.");
        }
        finally { releaseStale.TrySetResult(); host.Release.TrySetResult(); }
    }

    private static async Task Until(Func<bool> condition, string message = "Authority fixture did not reach its expected state.")
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Require(condition(), message);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class AuthorityHost : IRelayAiHost
    {
        private int calls, disposed;
        public int Calls => Volatile.Read(ref calls);
        public bool Disposed => Volatile.Read(ref disposed) != 0;
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentBag<HeldCall> Held { get; } = [];
        public int HeldCount => Held.Count;
        public async Task<string> ExecuteAsync(Guid caller, string request, CancellationToken token, Func<bool> authorized)
        {
            Require(authorized(), "Fixture AI entered without authority.");
            Interlocked.Increment(ref calls);
            using var json = JsonDocument.Parse(request);
            if (json.RootElement.GetProperty("action").GetString() == "held")
            {
                var held = new HeldCall(authorized, token); Held.Add(held);
                try { await Release.Task.WaitAsync(token); }
                finally { held.Settled.TrySetResult(); }
            }
            return "{\"action\":\"catalog\",\"hostId\":\"authority-fixture\"}";
        }
        public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    }
    private sealed record HeldCall(Func<bool> Authorized, CancellationToken Token)
    {
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
