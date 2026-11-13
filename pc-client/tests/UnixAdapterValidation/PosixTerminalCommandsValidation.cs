// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TermLink.Pc.Contracts;
using TermLink.Platform;
using TermLink.Platform.Unix;

namespace TermLink.Platform.Unix.Validation;

public static class PosixTerminalCommandsValidation
{
    public static async Task RunAsync(string parent)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(parent, "commands ; literal $(no expansion)");
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "fixture executable ; $(literal)");
        var source = Path.Combine(root, "argv.c");
        await File.WriteAllTextAsync(source, """
            #include <stdio.h>
            #include <unistd.h>
            int main(int argc,char **argv) {
                char cwd[4096]; if (!getcwd(cwd,sizeof(cwd))) return 71;
                printf("cwd:%s\n",cwd);
                for (int i=0;i<argc;i++) printf("argv:%d:%zu:%s\n",i,__builtin_strlen(argv[i]),argv[i]);
                return 0;
            }
            """);
        using (var compiler = Process.Start(Start("/usr/bin/cc", source, "-o", executable))!)
        {
            var output = compiler.StandardOutput.ReadToEndAsync(); var errors = compiler.StandardError.ReadToEndAsync();
            try { await Task.WhenAll(output, errors, compiler.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(15)); }
            finally { if (!compiler.HasExited) { compiler.Kill(true); await compiler.WaitForExitAsync(); } }
            Require(compiler.ExitCode == 0, "Authored command fixture failed to compile.");
        }
        var builtIn = new TerminalCommandCapability("sh", "Detected fixture", executable, Array.Empty<string>(), true, false);
        var path = Path.Combine(root, "private", "terminal-commands-v1.json");
        PosixTerminalCommandStore Store() => new(path, () => [builtIn]);
        var store = Store();
        Require((await store.DiscoverAsync(CancellationToken.None)).Single().Id == "sh" && !File.Exists(path), "Discovery created settings or erased built-ins.");
        var args = new[] { "value with spaces", "$(touch should-not-exist)", "; & | > <", "", "Unicode路径😀", "one\ntwo", "\\quoted\"" };
        var command = new TerminalCommandCapability("custom-fixture", "Fixture 终端", executable, args, false, false);
        await store.SaveAsync([builtIn with { Enabled = false }, command], CancellationToken.None);
        var discovered = await new PosixTerminalCommandDiscovery(Store()).DiscoverAsync(CancellationToken.None);
        Require(discovered.Count == 2 && !discovered.Single(c => c.Id == "sh").Enabled &&
            discovered.Single(c => c.Id == command.Id).Arguments.SequenceEqual(args) && !discovered.Single(c => c.Id == command.Id).Detected,
            "Restart/discovery lost the configuration, disabled override or literal argv.");
        Require((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) == 0 &&
            File.GetUnixFileMode(Path.GetDirectoryName(path)!) == (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute), "Command settings are not private.");
        Reject(() => store.Resolve("sh"), "Disabled built-in was runnable.");
        Reject(() => store.Resolve("unknown"), "Unknown ID fell back to a shell.");
        var resolved = Store().Resolve(command.Id);
        Require(resolved.Executable == executable && resolved.Arguments.SequenceEqual(args), "ID resolution changed argv.");
        Require(PosixTerminalCommandRules.ParseArguments(PosixTerminalCommandRules.FormatArguments(args)).SequenceEqual(args), "JSON parameter roundtrip was lossy.");
        foreach (var invalid in new[] { "-l --flag", "{}", "null", "[null]", "[true]", "[1]", "[\"\\u0000\"]", "[", "[\"\\ud800\"]" })
            Reject(() => PosixTerminalCommandRules.ParseArguments(invalid), "Invalid argument JSON accepted.");
        foreach (var invalid in new[] { command with { Executable = "relative/path" }, command with { Executable = "//ambiguous/root" },
            command with { Executable = "/tmp/../bin/sh" }, command with { Executable = "/tmp/a\0b" }, command with { DisplayName = "bad\nname" },
            command with { Id = "shell" }, command with { Id = "custom bad" }, command with { Id = "custom-UPPER" }, command with { Id = "custom-" + new string('a', 58) },
            command with { DisplayName = " leading" }, command with { DisplayName = "trailing " }, command with { DisplayName = new string('界', 27) },
            command with { RunAsAdministrator = true },
            command with { Arguments = Enumerable.Repeat("x", 33).ToArray() }, command with { Arguments = [new string('x', 4097)] } })
            await RejectAsync(() => store.SaveAsync([invalid], CancellationToken.None).AsTask(), "Invalid or privileged command persisted.");
        _ = PosixTerminalCommandRules.Validate(command with { Id = "custom-" + new string('a', 57), DisplayName = new string('界', 26) + "ab" });
        await RejectAsync(() => store.SaveAsync([command, command], CancellationToken.None).AsTask(), "Duplicate command IDs accepted.");
        await RejectAsync(() => store.SaveAsync(Enumerable.Range(0, 25).Select(i => command with { Id = "custom-" + i }).ToArray(), CancellationToken.None).AsTask(), "Command count limit ignored.");
        Require(store.Resolve(command.Id).Arguments.SequenceEqual(args), "Rejected settings replaced the previous commit.");
        Console.WriteLine("PASS POSIX commands: built-in/custom discovery, literal JSON argv, limits, no privilege, ID/disabled fences and private restart recovery");

        await using (var host = new PosixTerminalSessionHost(store.Resolve))
        {
            var launch = await host.LaunchAsync(new(command.Id, root, 80, 24, new Dictionary<string, string>()), CancellationToken.None);
            var output = new MemoryStream(); var buffer = new byte[4096];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true) { var count = await host.ReadOutputAsync(launch.SessionId, buffer, deadline.Token); if (count == 0) break; output.Write(buffer, 0, count); }
            var text = Encoding.UTF8.GetString(output.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal);
            for (var i = 0; i < args.Length; i++) Require(text.Contains("argv:" + (i + 1) + ":" + Encoding.UTF8.GetByteCount(args[i]) + ":" + args[i], StringComparison.Ordinal), "PTY split or evaluated an argv element.");
            Require(!File.Exists(Path.Combine(root, "should-not-exist")) && await host.WaitForExitAsync(launch.SessionId, deadline.Token) == 0,
                "Literal argument input changed the fixture exit behavior.");
            await host.StopAsync(launch.SessionId, CancellationToken.None);
            await RejectAsync(() => host.LaunchAsync(new("unknown", root, 80, 24, new Dictionary<string, string>()), CancellationToken.None).AsTask(), "PTY accepted an unknown configured ID.");
        }
        Console.WriteLine("PASS POSIX commands: authored native PTY fixture receives space/metacharacter/empty/Unicode/newline argv unchanged, no shell expansion");

        var missing = command with { Executable = Path.Combine(root, "not installed") };
        await store.SaveAsync([missing], CancellationToken.None);
        Require((await Store().DiscoverAsync(CancellationToken.None)).Any(c => c.Id == command.Id && c.Enabled && !c.Detected), "Missing custom entry disappeared from local editing.");
        Reject(() => Store().Resolve(command.Id), "Missing custom executable became runnable.");
        await store.SaveAsync([command], CancellationToken.None);
        Require(!(await Store().DiscoverAsync(CancellationToken.None)).Single(c => c.Id == "sh").Enabled, "Stale snapshot re-enabled a disabled automatic shell.");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel(); var before = await File.ReadAllBytesAsync(path);
            await RejectAsync(() => store.SaveAsync([command with { DisplayName = "Must not persist" }], cancel.Token).AsTask(), "Cancelled save completed.");
            Require(before.SequenceEqual(await File.ReadAllBytesAsync(path)), "Cancelled save changed committed settings.");
        }
        var writes = Enumerable.Range(0, 8).Select(i => Store().SaveAsync([command with { DisplayName = "Writer " + i }], CancellationToken.None).AsTask()).ToArray();
        await Task.WhenAll(writes);
        Require((await Store().DiscoverAsync(CancellationToken.None)).Single(c => c.Id == command.Id).DisplayName.StartsWith("Writer ", StringComparison.Ordinal) &&
            !Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.new-*").Any(), "Concurrent atomic commit left torn settings or temporary files.");
        var committed = await File.ReadAllBytesAsync(path);
        await File.WriteAllTextAsync(path, "{\"schema\":1,\"schema\":1,\"commands\":[]}");
        await RejectAsync(() => Store().DiscoverAsync(CancellationToken.None).AsTask(), "Duplicate store properties were accepted.");
        await File.WriteAllBytesAsync(path, committed);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        await RejectAsync(() => Store().DiscoverAsync(CancellationToken.None).AsTask(), "Publicly readable settings accepted.");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        var link = Path.Combine(root, "linked-store.json"); File.CreateSymbolicLink(link, path);
        await RejectAsync(() => new PosixTerminalCommandStore(link, () => []).DiscoverAsync(CancellationToken.None).AsTask(), "Linked store file followed.");
        var directoryLink = Path.Combine(root, "linked-directory"); Directory.CreateSymbolicLink(directoryLink, Path.GetDirectoryName(path)!);
        await RejectAsync(() => new PosixTerminalCommandStore(Path.Combine(directoryLink, "terminal-commands-v1.json"), () => []).DiscoverAsync(CancellationToken.None).AsTask(), "Linked settings ancestor followed.");
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Reject(() => Store().Resolve(command.Id), "Executable permission change was ignored.");
        Console.WriteLine("PASS POSIX commands: missing entries retained locally, launch revalidation, cancellation, atomic concurrent save, malformed/private/link fences");
    }

    private static ProcessStartInfo Start(string executable, params string[] args)
    { var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }; foreach (var arg in args) start.ArgumentList.Add(arg); return start; }
    private static void Reject(Action action, string detail) { try { action(); } catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException) { return; } throw new InvalidOperationException(detail); }
    private static async Task RejectAsync(Func<Task> action, string detail) { try { await action(); } catch (Exception error) when (error is InvalidDataException or IOException or ArgumentException or OperationCanceledException) { return; } throw new InvalidOperationException(detail); }
    private static void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
}
