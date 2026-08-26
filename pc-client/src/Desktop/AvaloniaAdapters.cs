using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Styling;
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

public sealed class AvaloniaAppearanceController : IAppearanceController
{
    private readonly string preferencePath;

    public AvaloniaAppearanceController(string? localApplicationData = null)
    {
        var root = localApplicationData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        preferencePath = Path.Combine(root, "TermLink", "ui-theme.txt");
        Current = ReadPreference();
        ApplyThemeVariant(Current);
    }

    public AppearancePreference Current { get; private set; }

    public void Apply(AppearancePreference preference)
    {
        if (!Enum.IsDefined(preference)) throw new ArgumentOutOfRangeException(nameof(preference));
        ApplyThemeVariant(preference);
        Current = preference;
        try
        {
            var directory = Path.GetDirectoryName(preferencePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(preferencePath, preference.ToString(), new UTF8Encoding(false));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning($"TermLink could not persist the UI theme preference: {error.Message}");
        }
    }

    private AppearancePreference ReadPreference()
    {
        try
        {
            if (!File.Exists(preferencePath)) return AppearancePreference.System;
            return Enum.TryParse<AppearancePreference>(File.ReadAllText(preferencePath).Trim(), true, out var value) &&
                Enum.IsDefined(value)
                ? value
                : AppearancePreference.System;
        }
        catch (IOException)
        {
            return AppearancePreference.System;
        }
        catch (UnauthorizedAccessException)
        {
            return AppearancePreference.System;
        }
    }

    private static void ApplyThemeVariant(AppearancePreference preference)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = preference switch
        {
            AppearancePreference.Light => ThemeVariant.Light,
            AppearancePreference.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
