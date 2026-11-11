using TermLink.Presentation;

namespace TermLink.Desktop;

internal sealed class CompleteExitCoordinator
{
    private int inFlight;

    public async Task ExitAsync(
        IAgentClient client, Func<Task<bool>> confirm, Action exitUi, Func<Exception, Task> reportFailure)
    {
        if (!client.SupportsCompleteShutdown || Interlocked.Exchange(ref inFlight, 1) != 0) return;
        try
        {
            if (!await confirm()) return;
            await client.ShutdownCompletelyAsync(CancellationToken.None);
            exitUi();
        }
        catch (Exception error) { await reportFailure(error); }
        finally { Volatile.Write(ref inFlight, 0); }
    }
}
