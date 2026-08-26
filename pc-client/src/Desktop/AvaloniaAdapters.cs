using System.Diagnostics;
using Avalonia.Threading;
using TermLink.Presentation;

namespace TermLink.Desktop;

public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => Dispatcher.UIThread.CheckAccess();

    public async ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (CheckAccess())
        {
            cancellationToken.ThrowIfCancellationRequested();
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Normal, cancellationToken);
    }
}

public sealed class TraceNavigationTelemetry : INavigationTelemetry
{
    public void Record(NavigationMeasurement measurement) =>
        Trace.WriteLine(
            $"TermLink navigation {measurement.FromPage}->{measurement.ToPage}: " +
            $"{measurement.Duration.TotalMilliseconds:F3} ms");
}
