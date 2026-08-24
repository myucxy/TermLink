using System.Reflection;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);

var outputRoot = Path.GetFullPath(args.Length == 0 ? "tray-visual-smoke" : args[0]);
Directory.CreateDirectory(outputRoot);
var isolatedRootParent = Path.Combine(Path.GetTempPath(), "TermLink.TrayVisualSmoke");
var isolatedRoot = Path.Combine(isolatedRootParent, "run-" + Guid.NewGuid().ToString("N"));
var isolatedPreferencesPath = Path.Combine(isolatedRoot, "Tray", "ui.preferences");
var isolatedReconnectPreferencesPath = Path.Combine(
    isolatedRoot,
    "Agent",
    "connection.preferences.json");
Directory.CreateDirectory(isolatedRoot);
AppContext.SetData("TermLink.TrayPreferences.StoragePath", isolatedPreferencesPath);
AppContext.SetData(
    "TermLink.ProductionReconnectPreferences.StoragePath",
    isolatedReconnectPreferencesPath);
// The unified desktop executable keeps the TermLink.Tray namespace, but its
// assembly name is TermLink so the installed UI and background runtime ship as
// one program. Load the product assembly name instead of the pre-unification
// project name.
var assembly = Assembly.Load("TermLink");
var optionsType = assembly.GetType("TermLink.Tray.TrayOptions", true)!;
var controllerType = assembly.GetType("TermLink.Tray.AgentController", true)!;
var preferencesType = assembly.GetType("TermLink.Tray.TrayPreferences", true)!;
var languageType = assembly.GetType("TermLink.Tray.TrayLanguage", true)!;
var themeType = assembly.GetType("TermLink.Tray.TrayTheme", true)!;
var formType = assembly.GetType("TermLink.Tray.MainWindowForm", true)!;
var trayPopupType = assembly.GetType("TermLink.Tray.TrayPopupForm", true)!;
var accountLinkType = assembly.GetType("TermLink.Tray.AccountLinkForm", true)!;
var accountRegisterType = assembly.GetType("TermLink.Tray.AccountRegisterForm", true)!;
var updateResultType = assembly.GetType("TermLink.Tray.WindowsUpdateResult", true)!;
var updateAvailableType = assembly.GetType("TermLink.Tray.UpdateAvailableForm", true)!;
var parse = optionsType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)!;
var update = preferencesType.GetMethod("Update", BindingFlags.Public | BindingFlags.Static)!;
var languageProperty = preferencesType.GetProperty("Language", BindingFlags.Public | BindingFlags.Static)!;
var themeProperty = preferencesType.GetProperty("Theme", BindingFlags.Public | BindingFlags.Static)!;
var originalLanguage = languageProperty.GetValue(null)!;
var originalTheme = themeProperty.GetValue(null)!;
var preferencesPath = (string)preferencesType
    .GetField("FilePath", BindingFlags.NonPublic | BindingFlags.Static)!
    .GetValue(null)!;
if (!string.Equals(
        Path.GetFullPath(preferencesPath),
        Path.GetFullPath(isolatedPreferencesPath),
        StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        $"Tray visual smoke preferences were not isolated: {preferencesPath}");
}

try
{
    foreach (var targetDpi in new[] { 96, 120, 144, 192 })
    {
        foreach (var (language, theme) in new[]
        {
            ("English", "Light"),
            ("English", "Dark"),
            ("Chinese", "Light"),
            ("Chinese", "Dark")
        })
        {
            var dpiRoot = Path.Combine(outputRoot, $"dpi-{targetDpi}");
            Directory.CreateDirectory(dpiRoot);
            update.Invoke(null, new[] { Enum.Parse(languageType, language), Enum.Parse(themeType, theme) });
            var caseBufferRoot = Path.Combine(
                isolatedRoot,
                $"{targetDpi}-{language.ToLowerInvariant()}-{theme.ToLowerInvariant()}");
            var options = parse.Invoke(null, new object[]
            {
                new[]
                {
                    "--control-pipe", "termlink-visual-smoke-" + Guid.NewGuid().ToString("N"),
                    "--buffer-root", caseBufferRoot
                }
            })!;
            var controller = Activator.CreateInstance(controllerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { options }, null)!;
            try
            {
                // Visual smoke must never start the real embedded Agent, read
                // the user's account, or access the network. The product's
                // internal test constructor keeps all UI/layout behavior while
                // disabling only the sessions page's automatic status refresh.
                using var form = (Form)Activator.CreateInstance(
                    formType,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null,
                    new[] { controller, false },
                    null)!;
                var navigate = formType.GetMethod("Navigate", BindingFlags.Instance | BindingFlags.Public)!;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(10, 10);
                form.Show();
                Application.DoEvents();
                var initialDpi = form.DeviceDpi;
                AssertScaled(form.Width, 1040, initialDpi, "initial main window width");
                AssertScaled(form.Height, 720, initialDpi, "initial main window height");
                AssertMainWindowScale(form);
                AssertLayoutTree(form);
                if (targetDpi == 96)
                {
                    var initialRoot = Path.Combine(outputRoot, $"initial-dpi-{initialDpi}");
                    Directory.CreateDirectory(initialRoot);
                    Save(form, Path.Combine(
                        initialRoot,
                        $"{language.ToLowerInvariant()}-{theme.ToLowerInvariant()}-connection.png"));
                }

                ApplyDpi(form, targetDpi);
                AssertScaled(form.Width, 1040, targetDpi, "main window width");
                AssertScaled(form.Height, 720, targetDpi, "main window height");
                AssertMainWindowScale(form);
                AssertLayoutTree(form, targetDpi == initialDpi);

                foreach (var page in new[] { "sessions", "connection", "settings" })
                {
                    navigate.Invoke(form, new object[] { page });
                    Application.DoEvents();
                    Thread.Sleep(100);
                    Application.DoEvents();
                    ApplyDpiToDescendantForms(form, targetDpi);
                    AssertMainWindowScale(form);
                    AssertLayoutTree(form, targetDpi == initialDpi);
                    Save(form, Path.Combine(
                        dpiRoot,
                        $"{language.ToLowerInvariant()}-{theme.ToLowerInvariant()}-{page}.png"));
                }

                // Users can resize the window down to MinimumSize. Exercise
                // every page there as well, because wrapped action buttons and
                // dynamic status text otherwise regress without being visible
                // in the standard 1040x720 screenshots.
                var regularSize = form.Size;
                form.Size = form.MinimumSize;
                Application.DoEvents();
                foreach (var page in new[] { "sessions", "connection", "settings" })
                {
                    navigate.Invoke(form, new object[] { page });
                    Application.DoEvents();
                    ApplyDpiToDescendantForms(form, targetDpi);
                    AssertMainWindowScale(form);
                    AssertLayoutTree(form, targetDpi == initialDpi);
                }
                if (language == "Chinese" && theme == "Light")
                {
                    Save(form, Path.Combine(dpiRoot, $"chinese-light-settings-minimum.png"));
                }
                form.Size = regularSize;
                Application.DoEvents();

                if (targetDpi == 192 && language == "English" && theme == "Light")
                {
                    foreach (var roundTripDpi in new[] { 96, 192, 120, initialDpi })
                    {
                        ApplyDpi(form, roundTripDpi);
                        navigate.Invoke(form, new object[] { "settings" });
                        Application.DoEvents();
                        ApplyDpiToDescendantForms(form, roundTripDpi);
                        AssertScaled(form.Width, 1040, roundTripDpi, "round-trip main window width");
                        AssertScaled(form.Height, 720, roundTripDpi, "round-trip main window height");
                        AssertMainWindowScale(form);
                        AssertLayoutTree(form, roundTripDpi == initialDpi);
                    }
                }

                CaptureFixedDialog(accountLinkType, "account-link", language, theme, targetDpi, dpiRoot, 500, 300, null);
                CaptureFixedDialog(accountRegisterType, "account-register", language, theme, targetDpi, dpiRoot, 500, 370, null);
                CaptureFixedDialog(
                    trayPopupType,
                    "tray-popup",
                    language,
                    theme,
                    targetDpi,
                    dpiRoot,
                    380,
                    560,
                    new object[]
                    {
                        new Action<string>(_ => { }),
                        new Action(() => { }),
                        new Action(() => { }),
                        new Func<Task>(() => Task.CompletedTask)
                    });
                var updateResult = Activator.CreateInstance(updateResultType)!;
                SetProperty(updateResult, "VersionName", "9.8.7");
                SetProperty(updateResult, "BuildNumber", (long?)987);
                SetProperty(updateResult, "SizeBytes", (long?)(23 * 1024 * 1024));
                SetProperty(
                    updateResult,
                    "ReleaseNotes",
                    language == "Chinese"
                        ? "改进高 DPI、终端快捷键和会话输入体验。"
                        : "Improves high-DPI layout, terminal shortcuts, and session input.");
                CaptureFixedDialog(
                    updateAvailableType,
                    "update-available",
                    language,
                    theme,
                    targetDpi,
                    dpiRoot,
                    590,
                    405,
                    new[] { updateResult });
            }
            finally
            {
                (controller as IDisposable)?.Dispose();
            }
        }
    }
}
finally
{
    update.Invoke(null, new[] { originalLanguage, originalTheme });
    AppContext.SetData("TermLink.TrayPreferences.StoragePath", null);
    AppContext.SetData(
        "TermLink.ProductionReconnectPreferences.StoragePath",
        null);
    DeleteIsolatedRoot(isolatedRootParent, isolatedRoot);
}

static void CaptureFixedDialog(
    Type formType,
    string name,
    string language,
    string theme,
    int targetDpi,
    string outputRoot,
    int designClientWidth,
    int designClientHeight,
    object[] constructorArguments)
{
    using var form = (Form)(constructorArguments == null
        ? Activator.CreateInstance(formType, nonPublic: true)!
        : Activator.CreateInstance(
            formType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: constructorArguments,
            culture: null)!);
    form.StartPosition = FormStartPosition.Manual;
    form.Location = new Point(40, 40);
    form.Show();
    Application.DoEvents();
    var initialDpi = form.DeviceDpi;
    ApplyDpi(form, targetDpi);
    AssertScaled(form.ClientSize.Width, designClientWidth, targetDpi, name + " client width");
    AssertScaled(form.ClientSize.Height, designClientHeight, targetDpi, name + " client height");
    AssertLayoutTree(form, targetDpi == initialDpi);
    Save(form, Path.Combine(
        outputRoot,
        $"{language.ToLowerInvariant()}-{theme.ToLowerInvariant()}-{name}.png"));
}

static void SetProperty(object target, string name, object value)
{
    target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .SetValue(target, value);
}

static void ApplyDpi(Form form, int targetDpi)
{
    if (form.DeviceDpi == targetDpi) return;
    var scale = targetDpi / (double)form.DeviceDpi;
    var bounds = form.Bounds;
    var suggested = new NativeMethods.Rect
    {
        Left = bounds.Left,
        Top = bounds.Top,
        Right = bounds.Left + Math.Max(1, (int)Math.Round(bounds.Width * scale)),
        Bottom = bounds.Top + Math.Max(1, (int)Math.Round(bounds.Height * scale))
    };
    var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<NativeMethods.Rect>());
    try
    {
        Marshal.StructureToPtr(suggested, pointer, false);
        var packedDpi = (IntPtr)(targetDpi | (targetDpi << 16));
        NativeMethods.SendMessage(form.Handle, NativeMethods.WmDpiChanged, packedDpi, pointer);
    }
    finally
    {
        Marshal.FreeHGlobal(pointer);
    }
    Application.DoEvents();
    if (form.DeviceDpi != targetDpi)
        throw new InvalidOperationException($"WM_DPICHANGED did not apply {targetDpi} DPI; actual {form.DeviceDpi}.");
}

static void ApplyDpiToDescendantForms(Control root, int targetDpi)
{
    foreach (var child in Descendants(root).OfType<Form>())
    {
        if (child.IsHandleCreated) ApplyDpi(child, targetDpi);
    }
}

static void AssertScaled(int actual, int logical, int dpi, string description)
{
    var expected = (int)Math.Round(logical * dpi / 96d);
    var actualLogicalPixels = actual * 96d / dpi;
    // WinForms scales the client rectangle while the non-client frame is
    // recomputed by DWM, so compare the normalized logical size. The frame
    // delta grows with the target DPI even though its logical size is stable.
    if (Math.Abs(actualLogicalPixels - logical) > 24)
        throw new InvalidOperationException($"{description} was {actual}px; expected {expected}px at {dpi} DPI.");
}

static void AssertMainWindowScale(Form form)
{
    var navigationButtons = Descendants(form)
        .OfType<Button>()
        .Where(button => button.Tag is string page &&
            (page == "sessions" || page == "connection" || page == "settings"))
        .ToArray();
    if (navigationButtons.Length != 3)
        throw new InvalidOperationException($"Expected three navigation buttons, found {navigationButtons.Length}.");

    foreach (var button in navigationButtons)
    {
        AssertLogicalPixels(button.Height, 42, form.DeviceDpi, 4, $"{button.Tag} navigation height");
        AssertLogicalPixels(button.MinimumSize.Width, 110, form.DeviceDpi, 4, $"{button.Tag} navigation minimum width");
        AssertLogicalPixels(button.MinimumSize.Height, 34, form.DeviceDpi, 4, $"{button.Tag} navigation minimum height");
    }

    foreach (var fieldName in new[]
    {
        "refreshButton", "accountLoginButton", "accountRegisterButton", "accountSignOutButton"
    })
    {
        var button = form.GetType()
            .GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(form) as Button;
        if (button == null) throw new InvalidOperationException($"Main window field {fieldName} was unavailable.");
        AssertLogicalPixels(button.MinimumSize.Width, 110, form.DeviceDpi, 4, $"{fieldName} minimum width");
        AssertLogicalPixels(button.MinimumSize.Height, 34, form.DeviceDpi, 4, $"{fieldName} minimum height");
    }

    var expectedIconPixels = (int)Math.Round(32 * form.DeviceDpi / 96d);
    if (form.Icon == null || form.Icon.Width != expectedIconPixels || form.Icon.Height != expectedIconPixels)
    {
        throw new InvalidOperationException(
            $"Main window icon was {form.Icon?.Width ?? 0}x{form.Icon?.Height ?? 0}; expected {expectedIconPixels}px at {form.DeviceDpi} DPI.");
    }
}

static void AssertLogicalPixels(int actual, int logical, int dpi, int tolerance, string description)
{
    var normalized = actual * 96d / dpi;
    if (Math.Abs(normalized - logical) > tolerance)
        throw new InvalidOperationException($"{description} was {actual}px ({normalized:F1} logical); expected {logical} at {dpi} DPI.");
}

static void AssertLayoutTree(Control root, bool validateText = true)
{
    if (validateText) AssertTextFits(root);
    var visibleChildren = root.Controls.Cast<Control>()
        .Where(child => child.Visible && child.Width > 0 && child.Height > 0)
        .ToArray();

    if (root is not ScrollableControl { AutoScroll: true })
    {
        // DisplayRectangle excludes padding for several layout containers even
        // though WinForms may legally place a margin inside that padded edge.
        // ClientRectangle is the actual clipping boundary users see.
        var client = root.ClientRectangle;
        foreach (var child in visibleChildren)
        {
            if (child.Left < client.Left - 2 || child.Top < client.Top - 2 ||
                child.Right > client.Right + 2 || child.Bottom > client.Bottom + 2)
            {
                throw new InvalidOperationException(
                    $"{ControlPath(child)} is outside {ControlPath(root)} at {root.DeviceDpi} DPI: {child.Bounds} vs {client}.");
            }
        }
    }

    var leaves = visibleChildren.Where(IsLayoutLeaf).ToArray();
    for (var left = 0; left < leaves.Length; left++)
    {
        for (var right = left + 1; right < leaves.Length; right++)
        {
            var intersection = Rectangle.Intersect(leaves[left].Bounds, leaves[right].Bounds);
            if (intersection.Width > 2 && intersection.Height > 2)
            {
                throw new InvalidOperationException(
                    $"{ControlPath(leaves[left])} overlaps {ControlPath(leaves[right])} at {root.DeviceDpi} DPI: {intersection}.");
            }
        }
    }

    foreach (var child in visibleChildren) AssertLayoutTree(child, validateText);
}

static bool IsLayoutLeaf(Control control) =>
    control is Label or ButtonBase or TextBoxBase or ComboBox or ListView or PictureBox;

static void AssertTextFits(Control control)
{
    if (string.IsNullOrWhiteSpace(control.Text)) return;
    if (control is Label label && !label.AutoSize && !label.AutoEllipsis)
    {
        var availableWidth = Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal);
        var preferred = label.GetPreferredSize(new Size(availableWidth, 0));
        if (preferred.Height > label.ClientSize.Height + 4)
        {
            throw new InvalidOperationException(
                $"Text in {ControlPath(label)} needs {preferred.Height}px but has {label.ClientSize.Height}px at {label.DeviceDpi} DPI.");
        }
    }
    else if (control is Button button && !button.AutoEllipsis)
    {
        var preferred = button.GetPreferredSize(Size.Empty);
        if (preferred.Width > button.Width + 4 || preferred.Height > button.Height + 4)
        {
            throw new InvalidOperationException(
                $"Text in {ControlPath(button)} needs {preferred} but has {button.Size} at {button.DeviceDpi} DPI.");
        }
    }
}

static IEnumerable<Control> Descendants(Control root)
{
    foreach (Control child in root.Controls)
    {
        yield return child;
        foreach (var descendant in Descendants(child)) yield return descendant;
    }
}

static string ControlPath(Control control)
{
    var name = string.IsNullOrWhiteSpace(control.Name) ? control.GetType().Name : control.Name;
    var text = (control.Text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
    return string.IsNullOrEmpty(text) ? name : $"{name} '{text}'";
}

static void DeleteIsolatedRoot(string expectedParent, string root)
{
    var parentPath = Path.GetFullPath(expectedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    var rootPath = Path.GetFullPath(root);
    if (!rootPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Refusing to remove visual-smoke path outside {parentPath}: {rootPath}");
    if (Directory.Exists(rootPath)) Directory.Delete(rootPath, recursive: true);
}

static void Save(Form form, string path)
{
    using var bitmap = new Bitmap(form.Width, form.Height);
    form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
    bitmap.Save(path, ImageFormat.Png);
}

internal static class NativeMethods
{
    internal const int WmDpiChanged = 0x02E0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}
