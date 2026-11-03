using Avalonia.Controls;
using TermLink.Desktop;
using TermLink.Pc.Contracts;
using TermLink.Presentation;

namespace TermLink.Desktop.HeadlessTests;

internal static class CompleteExitValidation
{
    public static async Task ValidateAsync()
    {
        var client = new ShutdownClient();
        var coordinator = new CompleteExitCoordinator();
        var uiExits = 0;
        var failures = 0;
        var confirms = 0;
        var confirmed = true;
        Task<bool> Confirm() { confirms++; return Task.FromResult(confirmed); }
        var menu = DesktopTrayMenu.Create(() => { }, () => { }, () => { }, () => uiExits++,
            () => coordinator.ExitAsync(client, Confirm, () => uiExits++, _ => { failures++; return Task.CompletedTask; }));
        var full = menu.Items.OfType<NativeMenuItem>().Single(item => item.Header == PresentationText.ExitTermLinkCompletely);
        var uiOnly = menu.Items.OfType<NativeMenuItem>().Single(item => item.Header == PresentationText.ExitControlUi);
        uiOnly.Command!.Execute(null);
        Require(uiExits == 1 && client.Requests == 0, "UI-only tray exit stopped the Agent.");

        full.Command!.Execute(null);
        Require(client.Requests == 1 && uiExits == 1 && confirms == 1, "Complete tray exit skipped confirmation or closed the UI before the Agent.");
        await coordinator.ExitAsync(client, Confirm, () => uiExits++, _ => Task.CompletedTask);
        Require(client.Requests == 1 && confirms == 1, "Duplicate full-exit clicks opened multiple confirmations.");
        client.Stopped.TrySetResult();
        await WaitAsync((AsyncCommand)full.Command);
        Require(uiExits == 2 && failures == 0, "Complete exit failed to close the UI after Agent shutdown.");

        confirmed = false;
        full.Command.Execute(null);
        await WaitAsync((AsyncCommand)full.Command);
        Require(client.Requests == 1 && uiExits == 2, "Cancelled full exit changed process lifetime.");

        confirmed = true;
        client.Reject = true;
        full.Command.Execute(null);
        await WaitAsync((AsyncCommand)full.Command);
        Require(uiExits == 2 && failures == 1, "Shutdown failure closed the UI or hid the error.");
        client.Reject = false;
        full.Command.Execute(null);
        await WaitAsync((AsyncCommand)full.Command);
        Require(uiExits == 3, "Failed full exit could not be retried.");

        var noBackend = DesktopTrayMenu.Create(() => { }, () => { }, () => { }, () => { }, null);
        Require(!noBackend.Items.OfType<NativeMenuItem>().Any(item => item.Header == PresentationText.ExitTermLinkCompletely),
            "Preview without a backend advertised complete shutdown.");
        Console.WriteLine("PASS tray complete exit: Agent-before-UI ordering, confirmation/cancel, duplicate clicks, failure/retry, UI-only separation");
    }

    private static async Task WaitAsync(AsyncCommand command)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (command.IsRunning && DateTime.UtcNow < deadline) await Task.Delay(1);
        Require(!command.IsRunning, "Tray command did not finish.");
    }

    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }

    private sealed class ShutdownClient : IAgentClient
    {
        public bool SupportsCompleteShutdown => true;
        public bool Reject { get; set; }
        public int Requests { get; private set; }
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask ShutdownCompletelyAsync(CancellationToken token)
        {
            Requests++;
            if (Reject) throw new IOException("SHUTDOWN_FAILED");
            await Stopped.Task.WaitAsync(token);
        }
        public ValueTask<AgentStatusSnapshot> GetStatusAsync(bool force, CancellationToken token) => throw new NotSupportedException();
        public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken token) => throw new NotSupportedException();
        public ValueTask RetryConnectionAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask StopSessionAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask GrantDeviceAsync(Guid id, string permission, CancellationToken token) => throw new NotSupportedException();
        public ValueTask RevokeDeviceAsync(Guid id, CancellationToken token) => throw new NotSupportedException();
        public ValueTask BeginAccountAuthorizationAsync(string name, CancellationToken token) => throw new NotSupportedException();
        public ValueTask LoginAsync(string user, string password, string name, CancellationToken token) => throw new NotSupportedException();
        public ValueTask RegisterAsync(string user, string nickname, string email, string password, string confirmation, string name, CancellationToken token) => throw new NotSupportedException();
        public ValueTask SignOutAsync(CancellationToken token) => throw new NotSupportedException();
        public ValueTask SaveTerminalCommandsAsync(IReadOnlyList<TerminalCommandCapability> commands, CancellationToken token) => throw new NotSupportedException();
    }
}
