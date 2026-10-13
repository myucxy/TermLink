using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TermLink.Desktop;

internal static class ClipboardPromptValidation
{
    internal static void Run()
    {
        var owner = new Window(); owner.Show(); owner.Activate(); Dispatcher.UIThread.RunJobs();
        var enabled = true; var copied = new List<string>(); var errors = new List<string>();
        using var clipboard = new NativeTerminalClipboard(owner, () => enabled, errors.Add, (_, text) => copied.Add(text));
        void Offer(string text) {
            owner.Activate(); Dispatcher.UIThread.RunJobs();
            Require(owner.IsActive, "Headless owner did not activate.");
            clipboard.Offer(text);
            var request = clipboard.NextQuery() ?? throw new Exception("No offer query.");
            Require(request[0] == 0, "Unexpected query phase."); clipboard.Receive(request, new byte[] { 2 });
            Dispatcher.UIThread.RunJobs();
        }
        Window Prompt() => owner.OwnedWindows.Single();
        void Click(string content) {
            var prompt = Prompt(); prompt.Activate(); Dispatcher.UIThread.RunJobs();
            Require(prompt.IsActive, "Headless prompt did not activate.");
            var panel = (StackPanel)prompt.Content!;
            ((StackPanel)panel.Children.Last()).Children.OfType<Button>().Single(b => (string)b.Content! == content)
                .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        void Reply(byte result, byte phase) {
            var request = clipboard.NextQuery() ?? throw new Exception("Missing response query.");
            Require(request[0] == phase, "Wrong response phase."); clipboard.Receive(request, new[] { result });
            Dispatcher.UIThread.RunJobs();
        }
        try {
            Offer("中文\nexact"); Require(copied.Count == 0, "Prompt copied without a click.");
            Click("仅复制本次"); Require(copied.Count == 0, "Click copied before reauthorization."); Reply(2, 1);
            Require(copied.SequenceEqual(new[] { "中文\nexact" }) && owner.OwnedWindows.Count == 0, "Confirmation did not copy exactly once and close.");
            Offer("cancelled"); Click("取消"); Reply(0, 2); Require(copied.Count == 1, "Cancel copied.");
            Offer("control lost"); Click("仅复制本次"); Reply(0, 1); Require(copied.Count == 1 && errors.Count == 1, "Denied reauthorization copied.");
            Offer("disabled"); enabled = false; Reply(0, 2); Require(copied.Count == 1, "Disabled prompt copied."); enabled = true;
            Offer("closed"); clipboard.Dispose(); Require(copied.Count == 1 && owner.OwnedWindows.Count == 0, "Dispose retained a prompt.");
        } finally { clipboard.Dispose(); owner.Close(); }
        Console.WriteLine("Clipboard UI: explicit click, second authorization, exact text, cancel, disable, denial and close passed (headless, OS writer substituted).");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
}
