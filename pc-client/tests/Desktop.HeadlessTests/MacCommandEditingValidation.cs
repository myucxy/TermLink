// SPDX-License-Identifier: GPL-3.0-only
using Avalonia.Controls;
using Avalonia.Threading;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Presentation;

namespace TermLink.Desktop.HeadlessTests;

internal static class MacCommandEditingValidation
{
    public static async Task RunAsync()
    {
        var fixture = Path.GetTempFileName();
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fixture, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var client = new Client();
        await using var state = new AgentStateStore(client);
        await using var model = new MainWindowViewModel(state, client, new ImmediateUiDispatcher(),
            new PlatformDescriptor(PcPlatform.MacOS, PcArchitecture.Arm64, "macOS fixture", "osx-arm64", true));
        var window = new MainWindow { DataContext = model }; window.Show();
        try
        {
            model.SelectSettingsSection("commands");
            model.OpenTerminalCommandEditorCommand.Execute(null); await Drain(model.OpenTerminalCommandEditorCommand);
            var enabled = window.FindControl<CheckBox>("NewCommandEnabledToggle");
            Require(model.UsesPosixCommandArguments && model.Settings.NewArguments == "[]" && enabled?.IsVisible == true,
                "Mac command editor did not expose literal argv and enable state.");
            model.Settings.NewCommandName = "Mac fixture"; model.Settings.NewExecutable = "/fixture path ; $(literal)/tool";
            model.Settings.NewArguments = "--login --flag";
            model.AddTerminalCommandCommand.Execute(null); await Drain(model.AddTerminalCommandCommand);
            Require(model.Settings.CommandEditorOpen && model.Settings.TerminalCommands.Count == 0, "Mac accepted an ambiguous command string as argv.");
            var expected = new[] { "value with spaces", "$(literal)", "", "Unicode😀", "one\ntwo" };
            model.Settings.NewArguments = PosixTerminalCommandRules.FormatArguments(expected);
            model.Settings.NewCommandEnabled = false;
            model.AddTerminalCommandCommand.Execute(null); await Drain(model.AddTerminalCommandCommand);
            var command = model.Settings.TerminalCommands.Single();
            Require(!model.Settings.CommandEditorOpen && command.Arguments.SequenceEqual(expected) && !command.RunAsAdministrator && !command.Enabled && !command.Detected,
                "Mac editor lost argv, enabled state, source or allowed elevation.");
            model.OpenTerminalCommandEditorCommand.Execute(command); await Drain(model.OpenTerminalCommandEditorCommand);
            Require(PosixTerminalCommandRules.ParseArguments(model.Settings.NewArguments).SequenceEqual(expected) && !model.Settings.NewCommandEnabled,
                "Editing a configured Mac command collapsed its argument array.");
            model.Settings.NewCommandEnabled = true;
            model.AddTerminalCommandCommand.Execute(null); await Drain(model.AddTerminalCommandCommand);
            model.SaveTerminalCommandsCommand.Execute(null); await Drain(model.SaveTerminalCommandsCommand);
            Require(client.Saved?.Single() is { Enabled: true, RunAsAdministrator: false } saved && saved.Arguments.SequenceEqual(expected),
                "Mac save did not send the actual edited command data.");
            model.OpenTerminalCommandEditorCommand.Execute(model.Settings.TerminalCommands.Single()); await Drain(model.OpenTerminalCommandEditorCommand);
            model.Settings.NewExecutable = "relative/tool";
            model.AddTerminalCommandCommand.Execute(null); await Drain(model.AddTerminalCommandCommand);
            Require(model.Settings.CommandEditorOpen && model.Settings.TerminalCommands.Single().Executable.StartsWith('/'), "Mac editor accepted a relative executable.");
            model.CancelTerminalCommandEditorCommand.Execute(null); await Drain(model.CancelTerminalCommandEditorCommand);
            var runnable = command with { Id = "custom-runnable", Executable = fixture, Enabled = true };
            client.SetCommands([runnable, runnable with { Id = "custom-disabled", Enabled = false },
                runnable with { Id = "custom-missing", Executable = fixture + ".absent" }]);
            await state.RefreshAsync(true, CancellationToken.None); Dispatcher.UIThread.RunJobs();
            Require(model.AvailableTerminalCommands.Count == 1 && model.AvailableTerminalCommands.Single().Id == runnable.Id && model.TerminalShell == runnable.Id,
                "Mac creation list omitted a runnable manual command or included disabled/missing entries.");
            model.RemoveTerminalCommandCommand.Execute(model.Settings.TerminalCommands.Single(c => c.Id == runnable.Id)); await Drain(model.RemoveTerminalCommandCommand);
            model.SaveTerminalCommandsCommand.Execute(null); await Drain(model.SaveTerminalCommandsCommand);
            Require(client.Saved is not null && !client.Saved.Any(c => c.Id == runnable.Id) && client.Saved.Any(c => c.Id == "custom-disabled" && !c.Enabled),
                "Deleting a manual Mac command resurrected it as a disabled record or removed an explicitly disabled command.");
            Console.WriteLine("PASS Mac command UI: JSON argv roundtrip, invalid command rejection, enable state, literal path and no elevation; Windows editor behavior stays separate");
        }
        finally { window.Close(); File.Delete(fixture); }
    }

    private static async Task Drain(AsyncCommand command)
    {
        var end = DateTime.UtcNow.AddSeconds(3);
        while (command.IsRunning && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); await Task.Delay(1); }
        Dispatcher.UIThread.RunJobs(); Require(!command.IsRunning, "Command editor did not finish.");
    }
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
    private sealed class Client : IAgentClient
    {
        public IReadOnlyList<TerminalCommandCapability>? Saved { get; private set; }
        private long revision;
        public void SetCommands(IReadOnlyList<TerminalCommandCapability> commands) => Saved = commands;
        public ValueTask<AgentStatusSnapshot> GetStatusAsync(bool refresh, CancellationToken token) => ValueTask.FromResult(new AgentStatusSnapshot(
            ++revision, DateTimeOffset.UtcNow, new PcAgentIdentity(Guid.NewGuid(), "fixture", PcPlatform.MacOS, PcArchitecture.Arm64, "macOS", "0.3.16"),
            AgentConnectionState.NotConfigured, false, "", "", "https://fixture.invalid", false, null, null, new HashSet<string>(), Saved ?? [], [], []));
        public ValueTask SaveTerminalCommandsAsync(IReadOnlyList<TerminalCommandCapability> commands, CancellationToken token) { Saved = commands.ToArray(); return ValueTask.CompletedTask; }
        public ValueTask SetRemoteAccessAsync(bool enabled, CancellationToken t) => throw new NotSupportedException();
        public ValueTask RetryConnectionAsync(CancellationToken t) => throw new NotSupportedException();
        public ValueTask StopSessionAsync(Guid id, CancellationToken t) => throw new NotSupportedException();
        public ValueTask GrantDeviceAsync(Guid id, string p, CancellationToken t) => throw new NotSupportedException();
        public ValueTask RevokeDeviceAsync(Guid id, CancellationToken t) => throw new NotSupportedException();
        public ValueTask BeginAccountAuthorizationAsync(string name, CancellationToken t) => throw new NotSupportedException();
        public ValueTask LoginAsync(string u, string p, string name, CancellationToken t) => throw new NotSupportedException();
        public ValueTask RegisterAsync(string u, string n, string e, string p, string c, string name, CancellationToken t) => throw new NotSupportedException();
        public ValueTask SignOutAsync(CancellationToken t) => throw new NotSupportedException();
    }
}
