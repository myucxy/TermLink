using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace TermLink.Desktop;

/// <summary>
/// Keeps one control UI per user and carries a bounded activation signal from
/// later launches to the retained tray process. The Agent has an independent
/// lifetime and is deliberately outside this coordinator.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private const byte ActivateMessage = 1;
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private bool ownsMutex;
    private bool disposed;

    public SingleInstanceCoordinator(string instanceScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceScope);
        var identity = string.Join('|', Environment.UserDomainName, Environment.UserName, instanceScope);
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
        mutex = new Mutex(false, $"TermLink.Desktop.{suffix}");
        pipeName = $"termlink-desktop-activate-{suffix.ToLowerInvariant()}";
    }

    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ownsMutex) return true;
        try
        {
            ownsMutex = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }
        return ownsMutex;
    }

    public void StartListening(Action activationRequested)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(activationRequested);
        if (!ownsMutex) throw new InvalidOperationException("Only the primary instance can listen for activation.");
        if (listener is not null) throw new InvalidOperationException("The activation listener is already running.");
        listener = ListenAsync(activationRequested, lifetime.Token);
    }

    public async Task<bool> SignalPrimaryAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(timeoutSource.Token).ConfigureAwait(false);
            await client.WriteAsync(new[] { ActivateMessage }, timeoutSource.Token).ConfigureAwait(false);
            await client.FlushAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ListenAsync(Action activationRequested, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                var message = new byte[1];
                var read = await server.ReadAsync(message, cancellationToken).ConfigureAwait(false);
                if (read == 1 && message[0] == ActivateMessage) activationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        lifetime.Cancel();
        try
        {
            listener?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException error) when (error.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
        }
        if (ownsMutex)
        {
            mutex.ReleaseMutex();
            ownsMutex = false;
        }
        mutex.Dispose();
        lifetime.Dispose();
    }
}
