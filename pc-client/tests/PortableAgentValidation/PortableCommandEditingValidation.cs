// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
using System.Text.Json;
using TermLink.Agent.Local;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;

internal static class PortableCommandEditingValidation
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "tc-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root); File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var endpoint = Path.Combine(root, "agent.sock");
        var key = RandomNumberGenerator.GetBytes(LocalAgentProtocol.AuthenticationKeyBytes);
        using var shutdown = new CancellationTokenSource();
        var handler = new Handler();
        await using var server = new LocalAgentServer(new UnixDomainSocketTransport(), endpoint, key, handler);
        var running = server.RunAsync(shutdown.Token);
        try
        {
            var end = DateTime.UtcNow.AddSeconds(3);
            while (!File.Exists(endpoint) && DateTime.UtcNow < end) await Task.Delay(5);
            var supervisor = new Supervisor();
            await using var client = new PortableAgentClient(new(endpoint, TimeSpan.FromSeconds(2)), new Secrets(key), new UnixDomainSocketTransport(), supervisor);
            var command = new TerminalCommandCapability("custom-fixture", "Fixture", "/fixture path/literal tool", ["a b", "$(literal)", ""], false, false, false);
            Require(client.SupportsTerminalCommandEditing, "Mac IPC client did not enable editing.");
            await client.SaveTerminalCommandsAsync([command], CancellationToken.None);
            Require(handler.Saved is { Count: 1 } && handler.Saved[0].Arguments.SequenceEqual(command.Arguments) &&
                !handler.Saved[0].Enabled && !handler.Saved[0].RunAsAdministrator && supervisor.Starts == 1,
                "Authenticated command save changed its ID, argv, flags or lifecycle.");
            Console.WriteLine("PASS portable Mac command IPC: signed SaveTerminalCommands request preserves argv, disabled state and no elevation, without a real Agent");
        }
        finally
        {
            shutdown.Cancel(); await server.DisposeAsync(); try { await running; } catch (OperationCanceledException) { }
            CryptographicOperations.ZeroMemory(key); Directory.Delete(root, recursive: true);
        }
    }
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private sealed class Handler : ILocalAgentCommandHandler
    {
        public IReadOnlyList<TerminalCommandCapability>? Saved { get; private set; }
        public ValueTask<LocalAgentCommandResult> HandleAsync(LocalAgentCommand command, JsonElement? payload, CancellationToken token)
        {
            if (command != LocalAgentCommand.SaveTerminalCommands) return ValueTask.FromResult(LocalAgentCommandResult.Reject("UNEXPECTED_COMMAND"));
            Require(payload is { } value && value.EnumerateObject().Count() == 1 && value.TryGetProperty("commands", out _), "Unexpected command save wire shape.");
            Saved = payload!.Value.GetProperty("commands").Deserialize<TerminalCommandCapability[]>(LocalAgentProtocol.JsonOptions);
            return ValueTask.FromResult(LocalAgentCommandResult.Ok());
        }
    }
    private sealed class Secrets(byte[] key) : ISecretStore
    {
        public bool IsAvailable => true;
        public ValueTask<byte[]?> RetrieveAsync(string name, CancellationToken token) => ValueTask.FromResult<byte[]?>(key.ToArray());
        public ValueTask StoreAsync(string name, ReadOnlyMemory<byte> data, CancellationToken token) => throw new InvalidOperationException("No real credentials.");
        public ValueTask DeleteAsync(string name, CancellationToken token) => throw new InvalidOperationException("No real credentials.");
    }
    private sealed class Supervisor : IProcessSupervisor
    {
        public int Starts { get; private set; }
        public ValueTask EnsureAgentRunningAsync(CancellationToken token) { Starts++; return ValueTask.CompletedTask; }
        public ValueTask<bool> IsAgentRunningAsync(CancellationToken token) => ValueTask.FromResult(true);
    }
}
