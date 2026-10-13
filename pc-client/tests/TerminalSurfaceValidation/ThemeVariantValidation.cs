using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using TermLink.Desktop;

internal static class ThemeVariantValidation
{
    internal static void Run()
    {
        var first = new Window();
        var second = new Window();
        try
        {
            first.Show();
            second.Show();
            Dispatcher.UIThread.RunJobs();
            var inherited = first.ActualThemeVariant;
            Require(second.ActualThemeVariant == inherited, "Headless windows did not start with the same inherited theme.");

            first.RequestedThemeVariant = ThemeVariant.Light;
            second.RequestedThemeVariant = ThemeVariant.Dark;
            Dispatcher.UIThread.RunJobs();
            Require(first.ActualThemeVariant == ThemeVariant.Light, "First window did not resolve its local Light request.");
            Require(second.ActualThemeVariant == ThemeVariant.Dark, "Second window did not resolve its local Dark request.");

            first.RequestedThemeVariant = ThemeVariant.Default;
            Dispatcher.UIThread.RunJobs();
            Require(first.ActualThemeVariant == inherited, "Default did not restore the first window's inherited theme.");
            Require(second.ActualThemeVariant == ThemeVariant.Dark, "First window's Default changed the second window.");

            first.RequestedThemeVariant = ThemeVariant.Dark;
            second.RequestedThemeVariant = ThemeVariant.Light;
            Dispatcher.UIThread.RunJobs();
            Require(first.ActualThemeVariant == ThemeVariant.Dark && second.ActualThemeVariant == ThemeVariant.Light,
                "Local theme variants leaked across windows.");
        }
        finally
        {
            first.Close();
            second.Close();
        }

        var native = new NativeTerminalWindow("termlink-display-" + Guid.NewGuid().ToString("N"), Environment.ProcessId);
        try
        {
            native.Show();
            Dispatcher.UIThread.RunJobs();
            var inherited = native.ActualThemeVariant;
            var menu = ((DockPanel)native.Content!).Children.OfType<WrapPanel>().Single();
            var themeButton = menu.Children.OfType<Button>().Single(button => Equals(button.Content, "主题"));
            var choices = menu.Children.OfType<StackPanel>().Single(panel =>
                panel.Children.OfType<Button>().Any(button => Equals(button.Content, "跟随系统")));

            Click(themeButton);
            Require(choices.IsVisible, "Theme button did not open the local choices.");
            Click(choices.Children.OfType<Button>().Single(button => Equals(button.Content, "浅色")));
            Dispatcher.UIThread.RunJobs();
            Require(native.RequestedThemeVariant == ThemeVariant.Light && native.ActualThemeVariant == ThemeVariant.Light &&
                !choices.IsVisible, "Light menu handler did not change this window's actual theme.");

            Click(themeButton);
            Click(choices.Children.OfType<Button>().Single(button => Equals(button.Content, "深色")));
            Dispatcher.UIThread.RunJobs();
            Require(native.RequestedThemeVariant == ThemeVariant.Dark && native.ActualThemeVariant == ThemeVariant.Dark,
                "Dark menu handler did not change this window's actual theme.");

            Click(themeButton);
            Click(choices.Children.OfType<Button>().Single(button => Equals(button.Content, "跟随系统")));
            Dispatcher.UIThread.RunJobs();
            Require(native.RequestedThemeVariant == ThemeVariant.Default && native.ActualThemeVariant == inherited,
                "System menu handler did not restore this window's inherited theme.");
        }
        finally { native.Close(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
