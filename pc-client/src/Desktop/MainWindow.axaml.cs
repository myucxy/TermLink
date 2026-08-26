using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace TermLink.Desktop;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Title = DesktopProductInfo.Title;
    }
}
