using System.Text.Json;
using System.Text.Json.Nodes;
using TermLink.Agent.Local;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;

internal static class PortableStatusValidation
{
    private static readonly string[] InvalidSets =
    [
        "null", "{}", "1", "true", "\"capability\"", "[null]", "[1]", "[true]", "[{}]", "[[]]",
        "[\"\"]", JsonSerializer.Serialize(new[] { " \t" }), "[\"nul\\u0000value\"]",
        JsonSerializer.Serialize(new[] { new string('x', PcContractLimits.MaximumIdentifierBytes + 1) }),
        JsonSerializer.Serialize(Enumerable.Range(0, PcContractLimits.MaximumCapabilities + 1).Select(index => "cap_" + index)),
    ];

    public static async Task RunAsync(byte[] key)
    {
        foreach (var json in InvalidSets)
            RequireThrows<JsonException>(() => JsonSerializer.Deserialize<IReadOnlySet<string>>(json, LocalAgentProtocol.JsonOptions),
                "An invalid capability set was accepted by the local protocol.");
        var duplicate = JsonSerializer.Deserialize<IReadOnlySet<string>>("[\"a\",\"a\",\"A\"]", LocalAgentProtocol.JsonOptions)!;
        Require(duplicate.Count == 2 && duplicate.SetEquals(["a", "A"]), "Capability set deduplication lost case-sensitive values.");
        RequireImmutable(duplicate);
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        var root = Path.Combine(Path.GetTempPath(), "tl-status-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var endpoint = Path.Combine(root, "agent.sock");
        var identity = new PcAgentIdentity(Guid.NewGuid(), "Status fixture", PcPlatform.MacOS,
            PcArchitecture.Arm64, "fixture", "0.3.16");
        var source = AgentStatusSnapshot.Disconnected(identity, "FIXTURE", "Offline test") with
            { Revision = 27, StatusKnown = true, AccountNickname = "状态测试" };
        var handler = new StatusHandler(source);
        var transport = new UnixDomainSocketTransport();
        await using var server = new LocalAgentServer(transport, endpoint, key, handler);
        using var lifetime = new CancellationTokenSource();
        var running = server.RunAsync(lifetime.Token);
        try
        {
            await using(var cold=new PortableAgentClient(new(endpoint,TimeSpan.FromSeconds(2)),new NeverReadSecrets(),transport,new NeverStartSupervisor()))
            {
                var denied=await RequireThrowsAsync<TermLink.Presentation.AgentClientOperationException>(()=>cold.DetectCapabilitiesAsync(CancellationToken.None).AsTask());
                Require(denied.Code=="CAPABILITY_AGENT_AUTH_REQUIRED","Cold detection did not require an existing authorized connection.");
            }
            await using var client = new PortableAgentClient(new(endpoint, TimeSpan.FromSeconds(2)),
                new StatusSecrets(key), transport, new StatusSupervisor());
            foreach (var expected in new string[][] { [], [PcCapabilityIds.RealtimeTerminalV1, "future_能力", "Case", "case"] })
            {
                var mutableSource = new HashSet<string>(expected, StringComparer.Ordinal);
                handler.Payload = source with { Capabilities = mutableSource };
                var observed = await client.GetStatusAsync(expected.Length > 0, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3));
                Require(observed.Capabilities.Count == expected.Length && observed.Capabilities.SetEquals(expected) &&
                    observed.Capabilities.ToHashSet(StringComparer.Ordinal).SetEquals(expected), "Status IPC lost or changed capability values.");
                Require(observed.Identity == identity && observed.Revision == source.Revision && observed.AccountNickname == source.AccountNickname && observed.StatusKnown,
                    "Status IPC changed unrelated snapshot fields.");
                RequireImmutable(observed.Capabilities);
                if (expected.Length == 0)
                    Require(observed.Capabilities.IsSubsetOf(["any"]) && !observed.Capabilities.Overlaps(["any"]), "Empty set queries behaved incorrectly.");
                else
                    Require(observed.Capabilities.IsProperSupersetOf([]) && observed.Capabilities.Overlaps(["future_能力"]) &&
                        !observed.Capabilities.Contains("CASE"), "Capability membership or set comparison lost ordinal semantics.");
                mutableSource.Add("server_only_later");
                Require(!observed.Capabilities.Contains("server_only_later"), "A returned status exposed the server's mutable set.");
                var wire = JsonSerializer.SerializeToElement(observed, LocalAgentProtocol.JsonOptions).GetProperty("capabilities");
                Require(wire.ValueKind == JsonValueKind.Array && wire.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal).SetEquals(expected),
                    "Capability serialization changed the existing string-array wire format.");
            }
            Require(handler.Refreshes.SequenceEqual([false, true]), "Status requests did not preserve force_refresh through IPC.");
            foreach (var invalid in InvalidSets)
            {
                var payload = JsonSerializer.SerializeToNode(source, LocalAgentProtocol.JsonOptions)!.AsObject();
                payload["capabilities"] = JsonNode.Parse(invalid);
                handler.Payload = JsonSerializer.SerializeToElement(payload, LocalAgentProtocol.JsonOptions);
                var error = await RequireThrowsAsync<InvalidDataException>(() => client.GetStatusAsync(false, CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
                Require(error.Message == "LOCAL_AGENT_RESPONSE_PAYLOAD_INVALID" && error.InnerException is JsonException,
                    "Invalid authenticated status capabilities did not use the protocol's payload rejection path.");
            }
            // A malformed response must not poison the next authenticated request.
            handler.Payload = source;
            Require((await client.GetStatusAsync(false, CancellationToken.None)).Capabilities.Count == 0,
                "A rejected capability payload prevented a later valid status response.");
            var report=CapabilityReportFactory.Create(source,new[]{new AiAgentCapability("codex","Codex","/test/codex","1.2.3","detected","local_cli") {Protocol="codex",ProtocolStatus="available"}},false,false);
            handler.Payload=report;
            var checkedReport=await client.DetectCapabilitiesAsync(CancellationToken.None);
            Require(checkedReport.Checks.Count==report.Checks.Count && checkedReport.Agents[0].ProtocolStatus=="available",
                "Authenticated capability detection IPC lost report fields.");
            handler.Payload=report with {Checks=report.Checks.Concat(report.Checks.Take(1)).ToArray()};
            await RequireThrowsAsync<InvalidDataException>(()=>client.DetectCapabilitiesAsync(CancellationToken.None).AsTask());
            handler.Payload=report;
            Require((await client.DetectCapabilitiesAsync(CancellationToken.None)).Checks.Count==report.Checks.Count,
                "Malformed capability report poisoned the next IPC request.");
            handler.WaitForCancellation=true;
            using(var cancelDetection=new CancellationTokenSource())
            {
                var pending=client.DetectCapabilitiesAsync(cancelDetection.Token).AsTask();
                await handler.DetectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                cancelDetection.Cancel();
                await RequireThrowsAsync<OperationCanceledException>(()=>pending);
                await handler.DetectionCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            handler.WaitForCancellation=false;
            Require((await client.DetectCapabilitiesAsync(CancellationToken.None)).Checks.Count==report.Checks.Count,
                "Cancelled detection prevented the next authenticated request.");
            Console.WriteLine("PASS actual status IPC: empty/nonempty immutable capability sets, ordinal membership, string-array compatibility and malformed payload rejection");
        }
        finally
        {
            lifetime.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            await server.DisposeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private static void RequireImmutable(IReadOnlySet<string> values)
    {
        var before = values.ToHashSet(StringComparer.Ordinal);
        Require(values is not HashSet<string>, "The status exposed a mutable HashSet through its read-only interface.");
        if (values is ICollection<string> collection)
        {
            Require(collection.IsReadOnly, "The capability collection reported that it was mutable.");
            RequireThrows<NotSupportedException>(() => collection.Add("must_not_be_added"), "Capability mutation succeeded.");
        }
        if (values is ISet<string> set)
            RequireThrows<NotSupportedException>(() => set.UnionWith(["must_not_be_added"]), "Capability set mutation succeeded.");
        Require(values.SetEquals(before), "An attempted mutation changed the capability set.");
    }

    private static void RequireThrows<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static async Task<T> RequireThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T error) { return error; }
        throw new InvalidOperationException("An invalid capability set was accepted through status IPC.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class StatusHandler(object payload) : ILocalAgentCommandHandler
    {
        public object Payload { get; set; } = payload;
        public List<bool> Refreshes { get; } = [];
        public bool WaitForCancellation {get;set;}
        public TaskCompletionSource DetectionStarted {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DetectionCancelled {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<LocalAgentCommandResult> HandleAsync(LocalAgentCommand command, JsonElement? payload, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if(command==LocalAgentCommand.DetectCapabilities)
            {
                if(WaitForCancellation)
                {
                    DetectionStarted.TrySetResult();
                    try{await Task.Delay(Timeout.InfiniteTimeSpan,token);}
                    catch(OperationCanceledException){DetectionCancelled.TrySetResult();throw;}
                }
                return LocalAgentCommandResult.Ok(Payload);
            }
            Require(command == LocalAgentCommand.GetStatus, "Status regression requested an unrelated command.");
            Refreshes.Add(payload!.Value.GetProperty("force_refresh").GetBoolean());
            return LocalAgentCommandResult.Ok(Payload);
        }
    }

    private sealed class NeverReadSecrets : ISecretStore
    {
        public bool IsAvailable=>true;
        public ValueTask<byte[]?> RetrieveAsync(string name,CancellationToken token)=>throw new InvalidOperationException("Detection read Keychain credentials.");
        public ValueTask StoreAsync(string name,ReadOnlyMemory<byte> value,CancellationToken token)=>throw new InvalidOperationException("Detection created Keychain credentials.");
        public ValueTask DeleteAsync(string name,CancellationToken token)=>throw new InvalidOperationException("Detection deleted Keychain credentials.");
    }
    private sealed class NeverStartSupervisor : IProcessSupervisor
    {
        public ValueTask EnsureAgentRunningAsync(CancellationToken token)=>throw new InvalidOperationException("Detection started Agent.");
        public ValueTask<bool> IsAgentRunningAsync(CancellationToken token)=>ValueTask.FromResult(false);
    }

    private sealed class StatusSecrets(byte[] key) : ISecretStore
    {
        public bool IsAvailable => true;
        public ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult<byte[]?>(key.ToArray()); }
        public ValueTask StoreAsync(string name, ReadOnlyMemory<byte> value, CancellationToken token) => throw new InvalidOperationException("Status IPC must not change credentials.");
        public ValueTask DeleteAsync(string name, CancellationToken token) => throw new InvalidOperationException("Status IPC must not delete credentials.");
    }

    private sealed class StatusSupervisor : IProcessSupervisor
    {
        public ValueTask EnsureAgentRunningAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public ValueTask<bool> IsAgentRunningAsync(CancellationToken token) => ValueTask.FromResult(true);
    }
}
