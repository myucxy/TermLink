using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System.ComponentModel;
using TermLink.Presentation;

namespace TermLink.Desktop;

public sealed partial class MainWindow : Window
{
    private MainWindowViewModel? observed;
    private IInputElement? returnFocus;
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Title = DesktopProductInfo.Title;
        DataContextChanged += (_, _) => {
            if (observed is not null) observed.PropertyChanged -= OverlayChanged;
            observed = DataContext as MainWindowViewModel;
            if (observed is not null) observed.PropertyChanged += OverlayChanged;
        };
        Closed += (_, _) => { if (observed is not null) observed.PropertyChanged -= OverlayChanged; observed = null; returnFocus = null; };
        AddHandler(KeyDownEvent, (_, e) => {
            if (e.Key != Key.Escape || observed is null) return;
            if (observed.TerminalDiagnosticVisible) { observed.CloseTerminalDiagnosticCommand.Execute(null); e.Handled = true; return; }
            if (observed.ConfirmationVisible) { observed.CancelPendingActionCommand.Execute(null); e.Handled = true; }
            else if (observed.UpdateProgressVisible) { observed.CancelUpdateDownloadCommand.Execute(null); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
    }

    private async void CopyTerminalDiagnostic(object? sender, RoutedEventArgs e)
    {
        if (observed?.TerminalDiagnosticVisible != true || Clipboard is null) return;
        try { await Clipboard.SetTextAsync(observed.TerminalDiagnosticText); }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException || error is InvalidOperationException)
        { this.FindControl<TextBox>("TerminalDiagnosticTextBox")?.SelectAll(); }
    }

    private void OverlayChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.BlockingOverlayVisible) || observed is null) return;
        if (observed.BlockingOverlayVisible)
        {
            returnFocus ??= FocusManager?.GetFocusedElement();
            Dispatcher.UIThread.Post(() => {
                if (observed?.BlockingOverlayVisible == true)
                    this.FindControl<Button>(observed.TerminalDiagnosticVisible ? "CloseTerminalDiagnosticButton" : observed.UpdateProgressVisible ? "CancelUpdateDownloadButton" : "CancelConfirmationButton")?.Focus();
            });
        }
        else Dispatcher.UIThread.Post(() => {
            if (observed?.BlockingOverlayVisible != true) { returnFocus?.Focus(); returnFocus = null; }
        });
    }
}
