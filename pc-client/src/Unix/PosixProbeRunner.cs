using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace TermLink.Platform.Unix;

internal enum PosixProbeStatus
{
    Succeeded,
    Failed,
    InvalidPath,
    RuntimeMissing,
    UnsupportedShim,
    RuntimeBlocked,
    TimedOut,
    OutputLimitExceeded,
}

internal sealed record PosixProbeResult(PosixProbeStatus Status, string Output = "");

/// <summary>Runs only fixed CLI probes, with one shared stdout/stderr byte limit.</summary>
internal sealed class PosixProbeRunner(Func<string, string?> find, TimeSpan timeout, Action<ProcessStartInfo>? configureStartInfo = null)
{
    private const int MaximumOutputBytes = 64 * 1024;

    public async Task<PosixProbeResult> RunAsync(string path, IReadOnlyList<string> arguments,
        bool disableGrokUpdater, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessStartInfo start;
        try
        {
            if (!Path.IsPathFullyQualified(path) || path.IndexOf('\0') >= 0)
                return new(PosixProbeStatus.InvalidPath);
            if (!ExecutableLocator.IsExecutable(path))
                return new(File.Exists(path) ? PosixProbeStatus.RuntimeBlocked : PosixProbeStatus.InvalidPath);
            start = BuildStartInfo(path, arguments, disableGrokUpdater);
            configureStartInfo?.Invoke(start);
        }
        catch (PosixCliLaunchDescriptor.MissingRuntimeException) { return new(PosixProbeStatus.RuntimeMissing); }
        catch (PosixCliLaunchDescriptor.UnsupportedShimException) { return new(PosixProbeStatus.UnsupportedShim); }
        catch (UnauthorizedAccessException) { return new(PosixProbeStatus.RuntimeBlocked); }
        catch (Exception error) when (error is IOException or ArgumentException or NotSupportedException)
        { return new(PosixProbeStatus.Failed); }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = start };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) return new(PosixProbeStatus.Failed);
        }
        catch (Win32Exception error) when (error.NativeErrorCode is 1 or 13)
        { return new(PosixProbeStatus.RuntimeBlocked); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        { return new(PosixProbeStatus.Failed); }
        using var cancellation = deadline.Token.Register(() => Terminate(process));
        var outputBudget = new OutputBudget(deadline);
        var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, outputBudget, deadline.Token);
        var stderr = ReadBoundedAsync(process.StandardError.BaseStream, outputBudget, deadline.Token);
        var completion = Task.WhenAll(stdout, stderr, process.WaitForExitAsync(deadline.Token));
        try
        {
            process.StandardInput.Close();
            // WaitAsync also bounds a pipe kept open by a detached descendant.
            await completion.WaitAsync(deadline.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (outputBudget.Exceeded) return new(PosixProbeStatus.OutputLimitExceeded);
            if (process.ExitCode != 0) return new(PosixProbeStatus.Failed);
            var output = Encoding.UTF8.GetString(await stdout.ConfigureAwait(false)) + "\n" +
                Encoding.UTF8.GetString(await stderr.ConfigureAwait(false));
            return new(PosixProbeStatus.Succeeded, Sanitize(output));
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or InvalidOperationException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(outputBudget.Exceeded ? PosixProbeStatus.OutputLimitExceeded :
                deadline.IsCancellationRequested ? PosixProbeStatus.TimedOut : PosixProbeStatus.Failed);
        }
        finally
        {
            Terminate(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception error) when (error is TimeoutException or InvalidOperationException or Win32Exception) { }
            // Observe late reader failures after closing pipes, including cancellation.
            process.StandardOutput.Dispose();
            process.StandardError.Dispose();
            _ = completion.ContinueWith(task => _ = task.Exception,
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private ProcessStartInfo BuildStartInfo(string path, IReadOnlyList<string> arguments, bool disableGrokUpdater) =>
        PosixCliLaunchDescriptor.Resolve(path, find).CreateStartInfo(Path.GetTempPath(), arguments, disableGrokUpdater);

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, OutputBudget budget, CancellationToken token)
    {
        using var result = new MemoryStream();
        var buffer = new byte[2048];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            if (!budget.Add(count)) throw new IOException("AI_PROBE_OUTPUT_LIMIT");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    private static string Sanitize(string output)
    {
        var clean = new StringBuilder(output.Length);
        for (var i = 0; i < output.Length; i++)
        {
            var value = output[i];
            if (value == '\u001b')
            {
                if (i + 1 < output.Length && output[i + 1] == '[')
                {
                    i += 2;
                    while (i < output.Length && (output[i] < '@' || output[i] > '~')) i++;
                }
                else if (i + 1 < output.Length && output[i + 1] == ']')
                {
                    i += 2;
                    while (i < output.Length && output[i] != '\a')
                    {
                        if (output[i] == '\u001b' && i + 1 < output.Length && output[i + 1] == '\\') { i++; break; }
                        i++;
                    }
                }
                continue;
            }
            if (value == '\r') { clean.Append('\n'); continue; }
            if (value is '\n' or '\t' || !char.IsControl(value) && char.GetUnicodeCategory(value) != UnicodeCategory.Format)
                clean.Append(value);
        }
        return clean.ToString();
    }

    private static void Terminate(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    private sealed class OutputBudget(CancellationTokenSource deadline)
    {
        private int count;
        public bool Exceeded => Volatile.Read(ref count) > MaximumOutputBytes;
        public bool Add(int size)
        {
            if (Interlocked.Add(ref count, size) <= MaximumOutputBytes) return true;
            deadline.Cancel();
            return false;
        }
    }
}
