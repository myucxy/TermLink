// SPDX-License-Identifier: GPL-3.0-only
using System.Diagnostics;
using TermLink.Platform.MacOS;

/// <summary>Only observes owned authored fixture processes; never starts an Agent.</summary>
internal static class MacProcessExitValidation
{
    public static async Task RunAsync()
    {
        if (!OperatingSystem.IsMacOS()) return;
        var root = Path.Combine(Path.GetTempPath(), "tl-mac-exit-" + Guid.NewGuid().ToString("N"));
        foreach (var alias in new[] { "/tmp", "/var", "/etc" })
            if (root.StartsWith(alias + "/", StringComparison.Ordinal)) { root = "/private" + root; break; }
        Directory.CreateDirectory(root);
        try
        {
            var source = Path.Combine(root, "exit-fixture.c");
            var executable = Path.Combine(root, "fixture child ; whitespace");
            await File.WriteAllTextAsync(source, """
                #include <stdio.h>
                #include <unistd.h>
                int main(void) {
                    puts("ready"); fflush(stdout);
                    return getchar() == EOF ? 3 : 0;
                }
                """);
            using (var compiler = Process.Start(Start("/usr/bin/cc", source, "-o", executable))!)
            {
                var output = compiler.StandardOutput.ReadToEndAsync();
                var errors = compiler.StandardError.ReadToEndAsync();
                try { await Task.WhenAll(output, errors, compiler.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(15)); }
                finally { if (!compiler.HasExited) { compiler.Kill(true); await compiler.WaitForExitAsync(); } }
                Require(compiler.ExitCode == 0, "Owned exit fixture did not compile.");
            }

            var pidPath = Path.Combine(root, "fixture.pid");
            var supervisor = new MacProcessSupervisor(executable, Path.Combine(root, "unused.sock"), pidPath);
            using (var child = await LaunchAsync(executable))
            {
                await File.WriteAllTextAsync(pidPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                try
                {
                    var foreign = new MacProcessSupervisor("/usr/bin/false", Path.Combine(root, "unused.sock"), pidPath);
                    await RejectAsync<InvalidDataException>(() => foreign.WaitForExitAsync(child.Id, TimeSpan.FromSeconds(1), CancellationToken.None).AsTask(),
                        "An active foreign executable was accepted.");
                    Require(!child.HasExited && File.Exists(pidPath), "Foreign identity rejection changed the child or PID file.");
                    await RejectAsync<TimeoutException>(() => supervisor.WaitForExitAsync(child.Id, TimeSpan.FromMilliseconds(50), CancellationToken.None).AsTask(),
                        "An active owned process was reported as exited.");
                    Require(!child.HasExited && File.Exists(pidPath), "Timeout stopped the child or removed the live PID.");
                    using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
                    await RejectAsync<OperationCanceledException>(() => supervisor.WaitForExitAsync(child.Id, TimeSpan.FromSeconds(2), cancellation.Token).AsTask(),
                        "Exit observation ignored cancellation.");
                    Require(!child.HasExited && File.Exists(pidPath), "Cancellation stopped the child or removed the live PID.");
                    await ExitAsync(child);
                    await supervisor.WaitForExitAsync(child.Id, TimeSpan.FromSeconds(1), CancellationToken.None);
                    Require(!File.Exists(pidPath), "Already-exited process left its matching PID file.");
                    await File.WriteAllTextAsync(pidPath, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    await supervisor.WaitForExitAsync(child.Id, TimeSpan.FromSeconds(1), CancellationToken.None);
                    Require(File.Exists(pidPath), "An exited process removed a different recorded PID.");
                    File.Delete(pidPath);
                }
                finally { await CleanupAsync(child); }
            }
            Console.WriteLine("PASS macOS process exit: active foreign identity, timeout, cancellation, already exited, matching PID cleanup and other PID preservation");

            // The original identity check reproducibly rejects the transient
            // null MainModule window. Race a real exit against the unchanged
            // public monitor API instead of mocking the Process implementation.
            for (var i = 0; i < 150; i++)
            {
                using var child = await LaunchAsync(executable);
                await File.WriteAllTextAsync(pidPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                try
                {
                    var observation = Task.Run(async () => await supervisor.WaitForExitAsync(child.Id, TimeSpan.FromSeconds(2), CancellationToken.None));
                    if (i % 3 == 0) await Task.Delay(1);
                    await ExitAsync(child);
                    await observation;
                    Require(child.ExitCode == 0 && !File.Exists(pidPath), "Exit race failed to observe the actual exit and clean its PID.");
                }
                finally { await CleanupAsync(child); }
            }
            Console.WriteLine("PASS macOS process exit: 150 owned native child exit races, authenticated-monitor semantics, no kill by monitor");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ProcessStartInfo Start(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
    private static async Task<Process> LaunchAsync(string executable)
    {
        var process = Process.Start(Start(executable)) ?? throw new IOException("Owned fixture did not start.");
        try { Require(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) == "ready", "Owned fixture did not become ready."); return process; }
        catch { await CleanupAsync(process); process.Dispose(); throw; }
    }
    private static async Task ExitAsync(Process process)
    {
        await process.StandardInput.WriteLineAsync("exit");
        await process.StandardInput.FlushAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task CleanupAsync(Process process)
    {
        if (process.HasExited) return;
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
    }
    private static async Task RejectAsync<T>(Func<Task> action, string detail) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException(detail);
    }
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
}
