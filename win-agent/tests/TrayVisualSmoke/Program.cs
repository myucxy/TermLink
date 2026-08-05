using System.Reflection;
using System.Drawing.Imaging;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var outputRoot = Path.GetFullPath(args.Length == 0 ? "tray-visual-smoke" : args[0]);
Directory.CreateDirectory(outputRoot);
var assembly = Assembly.Load("TermLink.Tray");
var optionsType = assembly.GetType("TermLink.Tray.TrayOptions", true)!;
var controllerType = assembly.GetType("TermLink.Tray.AgentController", true)!;
var preferencesType = assembly.GetType("TermLink.Tray.TrayPreferences", true)!;
var languageType = assembly.GetType("TermLink.Tray.TrayLanguage", true)!;
var themeType = assembly.GetType("TermLink.Tray.TrayTheme", true)!;
var formType = assembly.GetType("TermLink.Tray.MainWindowForm", true)!;
var popupType = assembly.GetType("TermLink.Tray.TrayPopupForm", true)!;
var accountLinkType = assembly.GetType("TermLink.Tray.AccountLinkForm", true)!;
var parse = optionsType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
var update = preferencesType.GetMethod("Update", BindingFlags.Public | BindingFlags.Static)!;
var languageProperty = preferencesType.GetProperty("Language", BindingFlags.Public | BindingFlags.Static)!;
var themeProperty = preferencesType.GetProperty("Theme", BindingFlags.Public | BindingFlags.Static)!;
var originalLanguage = languageProperty.GetValue(null)!;
var originalTheme = themeProperty.GetValue(null)!;

try
{
    foreach (var (language, theme) in new[]
    {
        ("English", "Light"),
        ("English", "Dark"),
        ("Chinese", "Light"),
        ("Chinese", "Dark")
    })
    {
        update.Invoke(null, new[] { Enum.Parse(languageType, language), Enum.Parse(themeType, theme) });
        var missingAgent = Path.Combine(outputRoot, "missing", "TermLink.Agent.exe");
        var options = parse.Invoke(null, new object[] { new[] { "--agent-exe", missingAgent, "--control-pipe", "termlink-visual-smoke-" + Guid.NewGuid().ToString("N") } })!;
        var controller = Activator.CreateInstance(controllerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { options }, null)!;
        using var form = (Form)Activator.CreateInstance(formType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { controller }, null)!;
        var navigate = formType.GetMethod("Navigate", BindingFlags.Instance | BindingFlags.Public)!;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(10, 10);
        form.Size = new Size(1040, 720);
        form.Show();
        Application.DoEvents();

        foreach (var page in new[] { "sessions", "remote", "devices", "activity", "settings", "membership" })
        {
            navigate.Invoke(form, new object[] { page });
            Application.DoEvents();
            Thread.Sleep(350);
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
            bitmap.Save(Path.Combine(outputRoot, $"{language.ToLowerInvariant()}-{theme.ToLowerInvariant()}-{page}.png"), ImageFormat.Png);
        }

        using var accountLink = (Form)Activator.CreateInstance(accountLinkType, nonPublic: true)!;
        accountLink.StartPosition = FormStartPosition.Manual;
        accountLink.Location = new Point(40, 40);
        accountLink.Show();
        Application.DoEvents();
        using (var accountLinkBitmap = new Bitmap(accountLink.Width, accountLink.Height))
        {
            accountLink.DrawToBitmap(accountLinkBitmap, new Rectangle(Point.Empty, accountLink.Size));
            accountLinkBitmap.Save(Path.Combine(outputRoot, $"{language.ToLowerInvariant()}-{theme.ToLowerInvariant()}-account-link.png"), ImageFormat.Png);
        }
        accountLink.Hide();

        Action<string> popupNavigate = _ => { };
        Action noAction = () => { };
        using var popup = (Form)Activator.CreateInstance(
            popupType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            new object[] { popupNavigate, noAction, noAction },
            null)!;
        popup.StartPosition = FormStartPosition.Manual;
        popup.Location = new Point(20, 20);
        popup.Show();
        var applyUnavailable = popupType.GetMethod("ApplyUnavailable", BindingFlags.Instance | BindingFlags.Public)!;
        applyUnavailable.Invoke(popup, new object[] { new InvalidOperationException("visual smoke") });
        Application.DoEvents();
        using var popupBitmap = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(popupBitmap, new Rectangle(Point.Empty, popup.Size));
        popupBitmap.Save(Path.Combine(outputRoot, $"{language.ToLowerInvariant()}-{theme.ToLowerInvariant()}-tray.png"), ImageFormat.Png);
    }
}
finally
{
    update.Invoke(null, new[] { originalLanguage, originalTheme });
}
