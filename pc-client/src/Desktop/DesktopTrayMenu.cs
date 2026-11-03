using Avalonia.Controls;
using TermLink.Presentation;

namespace TermLink.Desktop;

internal static class DesktopTrayMenu
{
    public static NativeMenu Create(Action open, Action update, Action diagnostics, Action exitUi, Func<Task>? exitCompletely,
        bool supportsUpdates = true)
    {
        var menu = new NativeMenu();
        void Add(string title, Action action) => menu.Items.Add(new NativeMenuItem
        {
            Header = title,
            Command = new AsyncCommand((_, _) => { action(); return Task.CompletedTask; }),
        });
        Add(PresentationText.OpenTermLink, open);
        if (supportsUpdates) Add(PresentationText.CheckUpdates, update);
        Add(PresentationText.Diagnostics, diagnostics);
        menu.Items.Add(new NativeMenuItemSeparator());
        Add(PresentationText.ExitControlUi, exitUi);
        if (exitCompletely is not null) menu.Items.Add(new NativeMenuItem
        {
            Header = PresentationText.ExitTermLinkCompletely,
            Command = new AsyncCommand((_, _) => exitCompletely()),
        });
        return menu;
    }
}
