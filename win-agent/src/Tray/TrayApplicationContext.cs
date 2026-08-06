using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using TermLink.Agent;

namespace TermLink.Tray
{
    internal enum TrayLanguage
    {
        FollowSystem = 0,
        Chinese = 1,
        English = 2
    }

    internal enum TrayTheme
    {
        FollowSystem = 0,
        Light = 1,
        Dark = 2
    }

    /// <summary>
    /// Small, non-sensitive preference store for the tray shell.  It deliberately
    /// contains only presentation preferences and is kept outside Agent state.
    /// </summary>
    internal static class TrayPreferences
    {
        private static readonly object Gate = new object();
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TermLink",
            "Tray",
            "ui.preferences");
        private static TrayLanguage language;
        private static TrayTheme theme;

        public static event Action Changed;

        static TrayPreferences()
        {
            language = TrayLanguage.FollowSystem;
            theme = TrayTheme.FollowSystem;
            Load();
        }

        public static TrayLanguage Language
        {
            get { lock (Gate) return language; }
        }

        public static TrayTheme Theme
        {
            get { lock (Gate) return theme; }
        }

        public static void Update(TrayLanguage nextLanguage, TrayTheme nextTheme)
        {
            Action changed;
            lock (Gate)
            {
                language = nextLanguage;
                theme = nextTheme;
                SaveLocked();
                changed = Changed;
            }
            if (changed != null) changed();
        }

        private static void Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var parts = line.Split(new[] { '=' }, 2);
                    if (parts.Length != 2) continue;
                    if (string.Equals(parts[0], "language", StringComparison.OrdinalIgnoreCase) &&
                        Enum.TryParse(parts[1], true, out TrayLanguage parsedLanguage) &&
                        Enum.IsDefined(typeof(TrayLanguage), parsedLanguage))
                    {
                        language = parsedLanguage;
                    }
                    else if (string.Equals(parts[0], "theme", StringComparison.OrdinalIgnoreCase) &&
                        Enum.TryParse(parts[1], true, out TrayTheme parsedTheme) &&
                        Enum.IsDefined(typeof(TrayTheme), parsedTheme))
                    {
                        theme = parsedTheme;
                    }
                }
            }
            catch (IOException)
            {
                // Preferences are optional; fall back to system defaults.
            }
            catch (UnauthorizedAccessException)
            {
                // Preferences are optional; fall back to system defaults.
            }
        }

        private static void SaveLocked()
        {
            try
            {
                var directory = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.WriteAllLines(FilePath, new[]
                {
                    "language=" + language,
                    "theme=" + theme
                });
            }
            catch (IOException)
            {
                // A read-only profile must not prevent the tray from opening.
            }
            catch (UnauthorizedAccessException)
            {
                // A read-only profile must not prevent the tray from opening.
            }
        }
    }

    internal static class TrayText
    {
        public static bool IsChinese
        {
            get
            {
                if (TrayPreferences.Language == TrayLanguage.Chinese) return true;
                if (TrayPreferences.Language == TrayLanguage.English) return false;
                return string.Equals(
                    CultureInfo.CurrentUICulture.TwoLetterISOLanguageName,
                    "zh",
                    StringComparison.OrdinalIgnoreCase);
            }
        }

        public static string Pick(string chinese, string english)
        {
            return IsChinese ? chinese : english;
        }

        public static string LanguageName(TrayLanguage language)
        {
            switch (language)
            {
                case TrayLanguage.Chinese: return "简体中文";
                case TrayLanguage.English: return "English";
                default: return Pick("跟随系统", "Follow system");
            }
        }

        public static string ThemeName(TrayTheme theme)
        {
            switch (theme)
            {
                case TrayTheme.Light: return Pick("浅色", "Light");
                case TrayTheme.Dark: return Pick("深色", "Dark");
                default: return Pick("跟随系统", "Follow system");
            }
        }
    }

    internal sealed class TrayPalette
    {
        public Color Canvas { get; private set; }
        public Color Surface { get; private set; }
        public Color Sidebar { get; private set; }
        public Color Text { get; private set; }
        public Color MutedText { get; private set; }
        public Color Border { get; private set; }
        public Color Accent { get; private set; }
        public Color Danger { get; private set; }

        public static TrayPalette For(TrayTheme theme)
        {
            var dark = theme == TrayTheme.Dark ||
                (theme == TrayTheme.FollowSystem && IsSystemDark());
            return dark
                ? new TrayPalette
                {
                    Canvas = Color.FromArgb(16, 21, 29),
                    Surface = Color.FromArgb(24, 32, 42),
                    Sidebar = Color.FromArgb(20, 27, 36),
                    Text = Color.FromArgb(244, 247, 251),
                    MutedText = Color.FromArgb(172, 185, 199),
                    Border = Color.FromArgb(58, 72, 88),
                    Accent = Color.FromArgb(98, 230, 197),
                    Danger = Color.FromArgb(240, 103, 112)
                }
                : new TrayPalette
                {
                    Canvas = Color.FromArgb(247, 249, 252),
                    Surface = Color.White,
                    Sidebar = Color.FromArgb(239, 243, 247),
                    Text = Color.FromArgb(23, 32, 42),
                    MutedText = Color.FromArgb(83, 99, 116),
                    Border = Color.FromArgb(210, 219, 229),
                    Accent = Color.FromArgb(21, 142, 117),
                    Danger = Color.FromArgb(190, 45, 57)
                };
        }

        private static bool IsSystemDark()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    "Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
                {
                    var value = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return value is int intValue && intValue == 0;
                }
            }
            catch (System.Security.SecurityException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    internal static class TrayListViewStyle
    {
        public static void Configure(ListView list)
        {
            if (list == null) throw new ArgumentNullException(nameof(list));
            list.OwnerDraw = true;
            list.DrawColumnHeader += delegate(object sender, DrawListViewColumnHeaderEventArgs e)
            {
                var palette = TrayPalette.For(TrayPreferences.Theme);
                using (var background = new SolidBrush(palette.Surface))
                using (var border = new Pen(palette.Border))
                {
                    e.Graphics.FillRectangle(background, e.Bounds);
                    e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                    e.Graphics.DrawLine(border, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
                }
                var textBounds = new Rectangle(e.Bounds.X + 3, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 5), e.Bounds.Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    e.Header == null ? string.Empty : e.Header.Text,
                    list.Font,
                    textBounds,
                    palette.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            };
            list.DrawItem += delegate(object sender, DrawListViewItemEventArgs e)
            {
                e.DrawDefault = false;
            };
            list.DrawSubItem += delegate(object sender, DrawListViewSubItemEventArgs e)
            {
                var palette = TrayPalette.For(TrayPreferences.Theme);
                var selected = e.Item != null && e.Item.Selected;
                var background = selected ? palette.Accent : palette.Surface;
                var foreground = selected
                    ? (palette.Accent.GetBrightness() > 0.55f ? palette.Canvas : Color.White)
                    : palette.Text;
                using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
                var textBounds = new Rectangle(e.Bounds.X + 3, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 5), e.Bounds.Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    e.SubItem == null ? string.Empty : e.SubItem.Text,
                    list.Font,
                    textBounds,
                    foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            };
        }
    }

    internal static class TrayComboBoxStyle
    {
        public static void Configure(ComboBox combo)
        {
            if (combo == null) throw new ArgumentNullException(nameof(combo));
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                var palette = TrayPalette.For(TrayPreferences.Theme);
                var selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
                var background = selected ? palette.Accent : palette.Surface;
                var foreground = selected
                    ? (palette.Accent.GetBrightness() > 0.55f ? palette.Canvas : Color.White)
                    : palette.Text;
                using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
                var text = e.Index >= 0 && e.Index < combo.Items.Count
                    ? Convert.ToString(combo.Items[e.Index], CultureInfo.CurrentCulture)
                    : combo.Text;
                var textBounds = new Rectangle(e.Bounds.X + 5, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 8), e.Bounds.Height);
                TextRenderer.DrawText(
                    e.Graphics,
                    text ?? string.Empty,
                    combo.Font,
                    textBounds,
                    foreground,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
        }
    }

    internal sealed class TrayThemedButton : Button
    {
        public Color DisabledBackColor { get; set; } = SystemColors.Control;
        public Color DisabledForeColor { get; set; } = SystemColors.GrayText;
        public Color DisabledBorderColor { get; set; } = SystemColors.ControlDark;

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Enabled)
            {
                base.OnPaint(e);
                return;
            }

            using (var background = new SolidBrush(DisabledBackColor))
            {
                e.Graphics.FillRectangle(background, ClientRectangle);
            }
            if (FlatAppearance.BorderSize > 0 && Width > 1 && Height > 1)
            {
                using (var border = new Pen(DisabledBorderColor, FlatAppearance.BorderSize))
                {
                    var inset = Math.Max(0, FlatAppearance.BorderSize / 2);
                    e.Graphics.DrawRectangle(
                        border,
                        inset,
                        inset,
                        Math.Max(0, Width - FlatAppearance.BorderSize),
                        Math.Max(0, Height - FlatAppearance.BorderSize));
                }
            }

            var flags = TextFormatFlags.HorizontalCenter |
                TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis;
            if (!UseMnemonic) flags |= TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, DisabledForeColor, flags);
        }
    }

    internal static class TrayWindowChrome
    {
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
        private const int DwmwaUseImmersiveDarkMode = 20;

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr windowHandle,
            int attribute,
            ref int attributeValue,
            int attributeSize);

        public static void Apply(Form form, TrayPalette palette)
        {
            if (form == null || palette == null || !form.IsHandleCreated || !OperatingSystem.IsWindows()) return;
            var enabled = palette.Canvas.GetBrightness() < 0.5F ? 1 : 0;
            try
            {
                var result = DwmSetWindowAttribute(
                    form.Handle,
                    DwmwaUseImmersiveDarkMode,
                    ref enabled,
                    sizeof(int));
                if (result != 0)
                {
                    DwmSetWindowAttribute(
                        form.Handle,
                        DwmwaUseImmersiveDarkModeBefore20H1,
                        ref enabled,
                        sizeof(int));
                }
            }
            catch (DllNotFoundException)
            {
                // Older Windows editions keep their native title bar.
            }
            catch (EntryPointNotFoundException)
            {
                // Older Windows editions keep their native title bar.
            }
        }
    }

    internal static class BrandIcons
    {
        public static Icon LoadApplicationIcon()
        {
            try
            {
                using (var stream = typeof(BrandIcons).Assembly.GetManifestResourceStream("TermLink.Tray.Assets.TermLink.ico"))
                {
                    if (stream != null)
                    {
                        using (var embedded = new Icon(stream))
                        {
                            return (Icon)embedded.Clone();
                        }
                    }
                }
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }
            try
            {
                var icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (icon != null) return icon;
            }
            catch (ArgumentException)
            {
            }
            return (Icon)SystemIcons.Application.Clone();
        }
    }

    internal static class TrayInputValidation
    {
        public static bool TryNormalizeServerOrigin(string value, out string origin)
        {
            origin = null;
            if (!Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrWhiteSpace(uri.Host) ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/"))
            {
                return false;
            }
            origin = uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        public static bool TryNormalizePcName(string value, out string name)
        {
            name = (value ?? string.Empty).Trim();
            if (name.Length < 1 || name.Length > 80) return false;
            foreach (var character in name)
            {
                if (char.IsControl(character)) return false;
            }
            return true;
        }
    }

    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly AgentController controller;
        private readonly Icon applicationIcon;
        private readonly NotifyIcon notifyIcon;
        private readonly ToolStripMenuItem statusItem;
        private readonly ToolStripMenuItem accountItem;
        private readonly ToolStripMenuItem relayItem;
        private readonly ToolStripMenuItem accountDevicesItem;
        private readonly ToolStripMenuItem linkAccountItem;
        private readonly ToolStripMenuItem registerAccountItem;
        private readonly ToolStripMenuItem sessionsItem;
        private readonly ToolStripMenuItem stopAllItem;
        private readonly ToolStripMenuItem enableItem;
        private readonly ToolStripMenuItem restartItem;
        private readonly ToolStripMenuItem openItem;
        private readonly ToolStripMenuItem settingsItem;
        private readonly ToolStripMenuItem exitItem;
        private readonly System.Windows.Forms.Timer refreshTimer;
        private int refreshInFlight;
        private MainWindowForm mainWindow;
        private TrayPopupForm trayPopup;
        private RegistryResponse latestResponse;
        private Exception latestError;

        public TrayApplicationContext(AgentController controller)
        {
            this.controller = controller;
            if (SynchronizationContext.Current == null)
            {
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            }
            applicationIcon = BrandIcons.LoadApplicationIcon();
            statusItem = new ToolStripMenuItem(TrayText.Pick("正在连接", "Connecting")) { Enabled = false };
            accountItem = new ToolStripMenuItem(TrayText.Pick("账户：检查中", "Account: checking")) { Enabled = false };
            relayItem = new ToolStripMenuItem(TrayText.Pick("Relay：检查中", "Relay: checking")) { Enabled = false };
            accountDevicesItem = new ToolStripMenuItem(TrayText.Pick("账户与设备", "Account and devices"), null, delegate { OpenAccountDevices(); });
            linkAccountItem = new ToolStripMenuItem(TrayText.Pick("登录账户…", "Sign in..."), null, async delegate { await LoginAccountAsync(); });
            registerAccountItem = new ToolStripMenuItem(TrayText.Pick("创建账户…", "Create account..."), null, async delegate { await RegisterAccountAsync(); });
            sessionsItem = new ToolStripMenuItem(TrayText.Pick("打开会话", "Open sessions"), null, delegate { OpenSessions(); });
            stopAllItem = new ToolStripMenuItem(TrayText.Pick("停止远程访问", "Stop remote access"), null, delegate { StopAll(); });
            enableItem = new ToolStripMenuItem(TrayText.Pick("恢复远程访问", "Enable remote access"), null, delegate { EnableRemoteAccess(); });
            restartItem = new ToolStripMenuItem(TrayText.Pick("重启 Agent", "Restart agent"), null, delegate { RestartAgent(); });
            openItem = new ToolStripMenuItem(TrayText.Pick("打开 TermLink", "Open TermLink"), null, delegate { OpenMainWindow("sessions"); });
            settingsItem = new ToolStripMenuItem(TrayText.Pick("设置", "Settings"), null, delegate { OpenMainWindow("settings"); });
            exitItem = new ToolStripMenuItem(TrayText.Pick("退出托盘", "Exit tray"), null, delegate { ExitTray(); });
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("TermLink Agent") { Enabled = false });
            menu.Items.Add(statusItem);
            menu.Items.Add(accountItem);
            menu.Items.Add(relayItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(accountDevicesItem);
            menu.Items.Add(linkAccountItem);
            menu.Items.Add(registerAccountItem);
            menu.Items.Add(sessionsItem);
            menu.Items.Add(stopAllItem);
            menu.Items.Add(enableItem);
            menu.Items.Add(restartItem);
            menu.Items.Add(openItem);
            menu.Items.Add(settingsItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(exitItem);
            menu.Opening += delegate
            {
                if (trayPopup != null && trayPopup.Visible) trayPopup.Hide();
                _ = RefreshStateAsync(false);
            };

            notifyIcon = new NotifyIcon
            {
                Icon = applicationIcon,
                Text = "TermLink Agent",
                ContextMenuStrip = menu,
                Visible = true
            };
            notifyIcon.MouseUp += delegate(object sender, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ToggleTrayPopup();
            };
            notifyIcon.DoubleClick += delegate { OpenMainWindow("sessions"); };
            refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            refreshTimer.Tick += async delegate { await RefreshStateAsync(false); };
            refreshTimer.Start();
            TrayPreferences.Changed += PreferencesChanged;
            _ = RefreshStateAsync(true);
        }

        private async Task RefreshStateAsync(bool showFailure)
        {
            if (Interlocked.Exchange(ref refreshInFlight, 1) != 0) return;
            try
            {
                var response = await Task.Run(() => controller.EnsureAgent());
                ApplyState(response);
            }
            catch (Exception error)
            {
                ApplyUnavailable(error, showFailure);
            }
            finally
            {
                Volatile.Write(ref refreshInFlight, 0);
            }
        }

        private void PreferencesChanged()
        {
            accountDevicesItem.Text = TrayText.Pick("账户与设备", "Account and devices");
            linkAccountItem.Text = TrayText.Pick("登录账户…", "Sign in...");
            registerAccountItem.Text = TrayText.Pick("创建账户…", "Create account...");
            sessionsItem.Text = TrayText.Pick("打开会话", "Open sessions");
            stopAllItem.Text = TrayText.Pick("停止远程访问", "Stop remote access");
            enableItem.Text = TrayText.Pick("恢复远程访问", "Enable remote access");
            restartItem.Text = TrayText.Pick("重启 Agent", "Restart agent");
            openItem.Text = TrayText.Pick("打开 TermLink", "Open TermLink");
            settingsItem.Text = TrayText.Pick("设置", "Settings");
            exitItem.Text = TrayText.Pick("退出托盘", "Exit tray");
            if (trayPopup != null)
            {
                trayPopup.Close();
                trayPopup.Dispose();
                trayPopup = null;
            }
        }

        private void ApplyState(RegistryResponse response)
        {
            if (response == null) return;
            latestResponse = response;
            latestError = null;
            var count = response.Summaries == null ? 0 : response.Summaries.Count;
            var production = response.Production ?? new ProductionStatus();
            statusItem.Text = response.RemoteAccessEnabled
                ? TrayText.Pick("远程访问已开启 · " + count + " 个会话", "Remote access on · " + count + " sessions")
                : TrayText.Pick("远程访问已停止", "Remote access off");
            accountItem.Text = TrayText.Pick("账户：", "Account: ") + TrayDisplay.LocalizedAccountStatus(production);
            relayItem.Text = TrayText.Pick("Relay：", "Relay: ") + TrayDisplay.LocalizedRelayStatus(production.RelayState);
            accountDevicesItem.Enabled = true;
            sessionsItem.Enabled = true;
            stopAllItem.Enabled = response.RemoteAccessEnabled;
            enableItem.Enabled = !response.RemoteAccessEnabled;
            notifyIcon.Text = response.RemoteAccessEnabled
                ? "TermLink · " + TrayText.Pick("远程开启", "remote on")
                : "TermLink · " + TrayText.Pick("远程停止", "remote off");
            if (trayPopup != null && !trayPopup.IsDisposed) trayPopup.Apply(response);
            if (mainWindow != null && !mainWindow.IsDisposed) mainWindow.Apply(response);
        }

        private void ApplyUnavailable(Exception error, bool showFailure)
        {
            latestResponse = null;
            latestError = error;
            statusItem.Text = TrayText.Pick("Agent 不可用", "Agent unavailable");
            accountItem.Text = TrayText.Pick("账户：不可用", "Account: unavailable");
            relayItem.Text = TrayText.Pick("Relay：不可用", "Relay: unavailable");
            accountDevicesItem.Enabled = false;
            sessionsItem.Enabled = false;
            stopAllItem.Enabled = false;
            enableItem.Enabled = false;
            if (trayPopup != null && !trayPopup.IsDisposed) trayPopup.ApplyUnavailable(error);
            if (mainWindow != null && !mainWindow.IsDisposed) mainWindow.ApplyUnavailable(error);
            if (showFailure) ShowFailure(error.Message);
        }

        // Retained as a small compatibility shim for callers in older integrations.
        private void RefreshState(bool showFailure)
        {
            _ = RefreshStateAsync(showFailure);
        }

        private void OpenMainWindow(string page)
        {
            if (trayPopup != null && trayPopup.Visible) trayPopup.Hide();
            if (mainWindow == null || mainWindow.IsDisposed)
            {
                mainWindow = new MainWindowForm(controller);
                mainWindow.FormClosed += delegate { mainWindow = null; };
            }
            mainWindow.Show();
            mainWindow.Activate();
            mainWindow.Navigate(page);
            _ = mainWindow.RefreshAsync();
        }

        private void ToggleTrayPopup()
        {
            if (trayPopup != null && !trayPopup.IsDisposed && trayPopup.Visible)
            {
                trayPopup.Hide();
                return;
            }
            if (trayPopup == null || trayPopup.IsDisposed)
            {
                trayPopup = new TrayPopupForm(
                    OpenMainWindow,
                    StopAll,
                    EnableRemoteAccess,
                    LoginAccountAsync);
                if (latestResponse != null) trayPopup.Apply(latestResponse);
                else if (latestError != null) trayPopup.ApplyUnavailable(latestError);
            }
            trayPopup.ShowNear(Cursor.Position);
            _ = RefreshStateAsync(false);
        }

        private void OpenSessions()
        {
            OpenMainWindow("sessions");
        }

        private void OpenAccountDevices()
        {
            OpenMainWindow("devices");
        }

        private async Task LoginAccountAsync()
        {
            using (var dialog = new AccountLinkForm())
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                linkAccountItem.Enabled = false;
                try
                {
                    await Task.Run(() => controller.LoginAccount(dialog.Username, dialog.Password, dialog.PcName));
                    var response = await Task.Run(() => controller.RestartAgent());
                    if (!response.Success) throw new InvalidOperationException(response.Code);
                    notifyIcon.ShowBalloonTip(4000, "TermLink", TrayText.Pick("账户已登录。", "Account signed in."), ToolTipIcon.Info);
                    await RefreshStateAsync(false);
                }
                catch (Exception error)
                {
                    ShowFailure(error.Message);
                }
                finally { linkAccountItem.Enabled = true; }
            }
        }

        private async Task RegisterAccountAsync()
        {
            using (var dialog = new AccountRegisterForm())
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                registerAccountItem.Enabled = false;
                try
                {
                    await Task.Run(() => controller.RegisterAccount(dialog.Username, dialog.Email, dialog.Password, dialog.Confirm, dialog.PcName));
                    var response = await Task.Run(() => controller.RestartAgent());
                    if (!response.Success) throw new InvalidOperationException(response.Code);
                    notifyIcon.ShowBalloonTip(4000, "TermLink", TrayText.Pick("账户已创建并登录。", "Account created and signed in."), ToolTipIcon.Info);
                    await RefreshStateAsync(false);
                }
                catch (Exception error)
                {
                    ShowFailure(error.Message);
                }
                finally { registerAccountItem.Enabled = true; }
            }
        }

        private async void StopAll()
        {
            if (MessageBox.Show(
                TrayText.Pick("停止所有远程访问？本地 Shell 将继续运行。", "Stop all remote access? Local shells will keep running."),
                "TermLink",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK)
            {
                return;
            }
            try
            {
                var response = await Task.Run(() => controller.Send(RegistryCommand.StopAllRemoteAccess));
                if (!response.Success) throw new InvalidOperationException(response.Code);
                await RefreshStateAsync(false);
            }
            catch (Exception error)
            {
                ShowFailure(error.Message);
            }
        }

        private async void EnableRemoteAccess()
        {
            try
            {
                var response = await Task.Run(() => controller.Send(RegistryCommand.EnableRemoteAccess));
                if (!response.Success) throw new InvalidOperationException(response.Code);
                await RefreshStateAsync(false);
            }
            catch (Exception error)
            {
                ShowFailure(error.Message);
            }
        }

        private async void RestartAgent()
        {
            try
            {
                var response = await Task.Run(() => controller.RestartAgent());
                if (!response.Success) throw new InvalidOperationException(response.Code);
                await RefreshStateAsync(false);
            }
            catch (Exception error)
            {
                ShowFailure(error.Message);
            }
        }

        private void ShowFailure(string message)
        {
            notifyIcon.ShowBalloonTip(4000, "TermLink", message, ToolTipIcon.Error);
        }

        private void ExitTray()
        {
            refreshTimer.Stop();
            if (trayPopup != null && !trayPopup.IsDisposed) trayPopup.Close();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            if (mainWindow != null && !mainWindow.IsDisposed) mainWindow.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                TrayPreferences.Changed -= PreferencesChanged;
                refreshTimer.Dispose();
                notifyIcon.Dispose();
                if (trayPopup != null && !trayPopup.IsDisposed) trayPopup.Dispose();
                if (mainWindow != null && !mainWindow.IsDisposed) mainWindow.Dispose();
                applicationIcon.Dispose();
            }
            base.Dispose(disposing);
        }

    }

    /// <summary>
    /// Compact left-click surface for immediate status and safety actions. It is
    /// intentionally backed only by RegistryResponse data; unavailable latency,
    /// entitlement and launch capabilities are not synthesized for presentation.
    /// </summary>
    internal sealed class TrayPopupForm : Form
    {
        private readonly Action<string> navigate;
        private readonly Action stopAllRemoteAccess;
        private readonly Action enableRemoteAccess;
        private readonly Func<Task> signInAccount;
        private readonly Icon applicationIcon;
        private readonly Bitmap brandBitmap;
        private readonly TableLayoutPanel root;
        private readonly TableLayoutPanel header;
        private readonly TableLayoutPanel connectionStrip;
        private readonly TableLayoutPanel policySurface;
        private readonly FlowLayoutPanel sessionList;
        private readonly TableLayoutPanel actions;
        private readonly TableLayoutPanel footer;
        private readonly Label pcNameLabel;
        private readonly Label connectionLabel;
        private readonly Label connectionDetailLabel;
        private readonly Label policyLabel;
        private readonly Label sessionCountLabel;
        private readonly Button settingsButton;
        private readonly Button policyButton;
        private readonly Button openSessionsButton;
        private readonly Button accountButton;
        private readonly Button remoteActionButton;
        private readonly Button openMainButton;
        private RegistryResponse latestResponse;

        public TrayPopupForm(
            Action<string> navigate,
            Action stopAllRemoteAccess,
            Action enableRemoteAccess,
            Func<Task> signInAccount)
        {
            this.navigate = navigate ?? throw new ArgumentNullException(nameof(navigate));
            this.stopAllRemoteAccess = stopAllRemoteAccess ?? throw new ArgumentNullException(nameof(stopAllRemoteAccess));
            this.enableRemoteAccess = enableRemoteAccess ?? throw new ArgumentNullException(nameof(enableRemoteAccess));
            this.signInAccount = signInAccount ?? throw new ArgumentNullException(nameof(signInAccount));
            applicationIcon = BrandIcons.LoadApplicationIcon();
            brandBitmap = applicationIcon.ToBitmap();

            Text = "TermLink";
            Icon = applicationIcon;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(380, 560);
            MinimumSize = new Size(380, 560);
            MaximumSize = new Size(380, 560);
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9F, FontStyle.Regular);
            AccessibleName = TrayText.Pick("TermLink 托盘状态", "TermLink tray status");

            root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 7,
                Padding = new Padding(1),
                Margin = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 114));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

            header = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                Padding = new Padding(12, 7, 10, 5),
                Margin = new Padding(0)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 45));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            var mark = new PictureBox
            {
                Dock = DockStyle.Fill,
                Image = brandBitmap,
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = new Padding(0, 1, 9, 1),
                AccessibleName = TrayText.Pick("TermLink 图标", "TermLink icon")
            };
            var productLabel = CreateLabel("TermLink", ContentAlignment.BottomLeft, true);
            productLabel.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            pcNameLabel = CreateLabel(Environment.MachineName, ContentAlignment.TopLeft, false);
            pcNameLabel.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Regular);
            settingsButton = CreatePopupButton("⚙", TrayText.Pick("打开设置", "Open settings"));
            settingsButton.Dock = DockStyle.Fill;
            settingsButton.Margin = new Padding(0);
            settingsButton.AutoEllipsis = false;
            settingsButton.Font = new Font(Font.FontFamily, 10F, FontStyle.Regular);
            settingsButton.Click += delegate { OpenPage("settings"); };
            header.Controls.Add(mark, 0, 0);
            header.SetRowSpan(mark, 2);
            header.Controls.Add(productLabel, 1, 0);
            header.Controls.Add(pcNameLabel, 1, 1);
            header.Controls.Add(settingsButton, 2, 0);
            header.SetRowSpan(settingsButton, 2);

            connectionStrip = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12, 0, 12, 0),
                Margin = new Padding(0)
            };
            connectionStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58));
            connectionStrip.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
            connectionLabel = CreateLabel(TrayText.Pick("○ 正在连接", "○ Connecting"), ContentAlignment.MiddleLeft, true);
            connectionLabel.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold);
            connectionDetailLabel = CreateLabel(string.Empty, ContentAlignment.MiddleRight, false);
            connectionDetailLabel.Font = new Font(Font.FontFamily, 8F, FontStyle.Regular);
            connectionStrip.Controls.Add(connectionLabel, 0, 0);
            connectionStrip.Controls.Add(connectionDetailLabel, 1, 0);

            policySurface = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(10, 6, 8, 6),
                Margin = new Padding(12, 8, 12, 6)
            };
            policySurface.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            policySurface.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            policySurface.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            policySurface.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var policyEyebrow = CreateLabel(TrayText.Pick("远程策略", "REMOTE POLICY"), ContentAlignment.MiddleLeft, false);
            policyEyebrow.Font = new Font(Font.FontFamily, 7.5F, FontStyle.Regular);
            policyLabel = CreateLabel(TrayText.Pick("正在读取状态", "Reading status"), ContentAlignment.MiddleLeft, true);
            policyLabel.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Bold);
            policyButton = CreatePopupButton(TrayText.Pick("查看", "View"), TrayText.Pick("打开远程访问设置", "Open remote access settings"));
            policyButton.Dock = DockStyle.Fill;
            policyButton.Margin = new Padding(5, 2, 0, 2);
            policyButton.Click += delegate { OpenPage("remote"); };
            policySurface.Controls.Add(policyEyebrow, 0, 0);
            policySurface.Controls.Add(policyLabel, 0, 1);
            policySurface.Controls.Add(policyButton, 1, 0);
            policySurface.SetRowSpan(policyButton, 2);

            var sessionHeading = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(13, 0, 13, 0),
                Margin = new Padding(0)
            };
            sessionHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            sessionHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var sessionHeadingLabel = CreateLabel(TrayText.Pick("活动会话", "Active sessions"), ContentAlignment.MiddleLeft, true);
            sessionHeadingLabel.Font = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            sessionCountLabel = CreateLabel("0", ContentAlignment.MiddleRight, false);
            sessionCountLabel.Font = new Font(Font.FontFamily, 8F, FontStyle.Bold);
            sessionHeading.Controls.Add(sessionHeadingLabel, 0, 0);
            sessionHeading.Controls.Add(sessionCountLabel, 1, 0);

            sessionList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(8, 0, 8, 0),
                Margin = new Padding(0)
            };
            sessionList.ClientSizeChanged += delegate { FitSessionRows(); };

            actions = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(12, 3, 12, 3),
                Margin = new Padding(0)
            };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 33.34F));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            actions.RowStyles.Add(new RowStyle(SizeType.Percent, 33.33F));
            openSessionsButton = CreatePopupButton(
                TrayText.Pick("打开受管会话", "Open managed sessions"),
                TrayText.Pick("在主窗口打开受管会话", "Open managed sessions in the main window"));
            accountButton = CreatePopupButton(
                TrayText.Pick("登录账户 ›", "Sign in ›"),
                TrayText.Pick("直接登录 TermLink 账户", "Sign in to the TermLink account directly"));
            remoteActionButton = CreatePopupButton(
                TrayText.Pick("检查远程访问状态", "Check remote access status"),
                TrayText.Pick("停止或恢复远程访问", "Stop or enable remote access"));
            openSessionsButton.Click += delegate { OpenPage("sessions"); };
            accountButton.Click += async delegate { await signInAccount(); };
            remoteActionButton.Click += delegate
            {
                if (latestResponse == null)
                {
                    OpenPage("remote");
                    return;
                }
                Hide();
                if (latestResponse.RemoteAccessEnabled) stopAllRemoteAccess();
                else enableRemoteAccess();
            };
            actions.Controls.Add(openSessionsButton, 0, 0);
            actions.Controls.Add(accountButton, 0, 1);
            actions.Controls.Add(remoteActionButton, 0, 2);

            footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(12, 3, 8, 3),
                Margin = new Padding(0)
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
            var version = typeof(TrayPopupForm).Assembly.GetName().Version;
            var versionText = version == null
                ? "Agent"
                : "Agent " + version.Major + "." + version.Minor + "." + version.Build;
            var versionLabel = CreateLabel(versionText, ContentAlignment.MiddleLeft, false);
            versionLabel.Font = new Font(Font.FontFamily, 8F, FontStyle.Regular);
            openMainButton = CreatePopupButton(TrayText.Pick("打开 TermLink", "Open TermLink"), TrayText.Pick("打开 TermLink 主窗口", "Open the TermLink main window"));
            openMainButton.Margin = new Padding(0);
            openMainButton.Click += delegate { OpenPage("sessions"); };
            footer.Controls.Add(versionLabel, 0, 0);
            footer.Controls.Add(openMainButton, 1, 0);

            root.Controls.Add(header, 0, 0);
            root.Controls.Add(connectionStrip, 0, 1);
            root.Controls.Add(policySurface, 0, 2);
            root.Controls.Add(sessionHeading, 0, 3);
            root.Controls.Add(sessionList, 0, 4);
            root.Controls.Add(actions, 0, 5);
            root.Controls.Add(footer, 0, 6);
            Controls.Add(root);

            Deactivate += delegate { if (Visible) Hide(); };
            SetConnecting();
            ApplyTheme();
        }

        public void ShowNear(Point anchor)
        {
            var workArea = Screen.FromPoint(anchor).WorkingArea;
            var x = Math.Max(workArea.Left, Math.Min(anchor.X - Width + 24, workArea.Right - Width));
            var y = anchor.Y >= workArea.Top + (workArea.Height / 2)
                ? anchor.Y - Height - 8
                : anchor.Y + 8;
            y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - Height));
            Location = new Point(x, y);
            ApplyTheme();
            if (!Visible) Show();
            BringToFront();
            Activate();
        }

        public void Apply(RegistryResponse response)
        {
            if (response == null || IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<RegistryResponse>(Apply), response);
                return;
            }
            latestResponse = response;
            var production = response.Production ?? new ProductionStatus();
            pcNameLabel.Text = string.IsNullOrWhiteSpace(production.PcName) ? Environment.MachineName : production.PcName;
            connectionLabel.Text = production.RelayState == ProductionRelayState.Connected
                ? TrayText.Pick("● Relay 已连接", "● Relay connected")
                : TrayText.Pick("○ Relay ", "○ Relay ") + TrayDisplay.LocalizedRelayStatus(production.RelayState);
            connectionDetailLabel.Text = string.IsNullOrWhiteSpace(production.ServerHost)
                ? TrayText.Pick("未配置服务器", "No server")
                : production.ServerHost;
            policyLabel.Text = response.RemoteAccessEnabled
                ? TrayText.Pick("同账号设备默认可连接并操作", "Same-account devices can connect and operate by default")
                : TrayText.Pick("远程访问已停止", "Remote access is stopped");
            accountButton.Text = production.AccountLinked
                ? TrayText.Pick("账户已登录 · 重新登录 ›", "Account signed in · sign in again ›")
                : TrayText.Pick("登录账户 ›", "Sign in ›");
            remoteActionButton.Text = response.RemoteAccessEnabled
                ? TrayText.Pick("停止所有远程访问", "Stop all remote access")
                : TrayText.Pick("恢复远程访问", "Enable remote access");
            remoteActionButton.Enabled = true;
            RebuildSessions(response.Summaries, production.Sessions);
            ApplyTheme();
        }

        public void ApplyUnavailable(Exception error)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<Exception>(ApplyUnavailable), error);
                return;
            }
            latestResponse = null;
            pcNameLabel.Text = Environment.MachineName;
            connectionLabel.Text = TrayText.Pick("○ Agent 不可用", "○ Agent unavailable");
            connectionDetailLabel.Text = TrayText.Pick("需要检查", "Needs attention");
            policyLabel.Text = TrayText.Pick("无法读取远程策略", "Remote policy unavailable");
            sessionCountLabel.Text = "0";
            accountButton.Text = TrayText.Pick("登录账户 ›", "Sign in ›");
            remoteActionButton.Text = TrayText.Pick("远程访问状态不可用", "Remote access unavailable");
            remoteActionButton.Enabled = true;
            ShowEmptySessions(TrayText.Pick("无法读取 Agent 状态\r\n请打开 TermLink 查看诊断", "Agent status is unavailable\r\nOpen TermLink for diagnostics"));
            ApplyTheme();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                Hide();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var palette = TrayPalette.For(TrayPreferences.Theme);
            using (var pen = new Pen(palette.Border))
            {
                e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            brandBitmap.Dispose();
            applicationIcon.Dispose();
            base.OnFormClosed(e);
        }

        private void SetConnecting()
        {
            latestResponse = null;
            connectionLabel.Text = TrayText.Pick("○ 正在连接", "○ Connecting");
            connectionDetailLabel.Text = string.Empty;
            policyLabel.Text = TrayText.Pick("正在读取状态", "Reading status");
            accountButton.Text = TrayText.Pick("登录账户 ›", "Sign in ›");
            remoteActionButton.Text = TrayText.Pick("检查远程访问状态", "Check remote access status");
            remoteActionButton.Enabled = true;
            sessionCountLabel.Text = "0";
            ShowEmptySessions(TrayText.Pick("正在读取受管会话…", "Loading managed sessions…"));
        }

        private void RebuildSessions(IList<SessionSummary> summaries, IList<ProductionSessionSummary> productionSessions)
        {
            ClearSessionControls();
            var count = summaries == null ? 0 : summaries.Count;
            sessionCountLabel.Text = count.ToString(CultureInfo.CurrentCulture);
            if (count == 0)
            {
                ShowEmptySessions(TrayText.Pick("没有活动的受管会话\r\n请从 Windows Terminal 的 TermLink Profile 启动", "No active managed sessions\r\nStart one from the TermLink Windows Terminal profile"));
                return;
            }
            foreach (var summary in summaries)
            {
                if (summary == null) continue;
                var production = FindProductionSession(summary.SessionId, productionSessions);
                var button = CreatePopupButton(SessionText(summary, production), SessionAccessibleName(summary, production));
                button.Tag = "session";
                button.Dock = DockStyle.None;
                button.Height = 68;
                button.TextAlign = ContentAlignment.MiddleLeft;
                button.Padding = new Padding(9, 3, 7, 3);
                button.Margin = new Padding(0, 0, 0, 4);
                button.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Regular);
                button.Click += delegate { OpenPage("sessions"); };
                sessionList.Controls.Add(button);
            }
            FitSessionRows();
        }

        private void ShowEmptySessions(string text)
        {
            ClearSessionControls();
            var empty = CreateLabel(text, ContentAlignment.MiddleCenter, false);
            empty.Tag = "session-empty";
            empty.Dock = DockStyle.None;
            empty.Height = 92;
            empty.Margin = new Padding(0, 10, 0, 0);
            empty.Font = new Font(Font.FontFamily, 8.5F, FontStyle.Regular);
            sessionList.Controls.Add(empty);
            FitSessionRows();
        }

        private void ClearSessionControls()
        {
            while (sessionList.Controls.Count > 0)
            {
                var control = sessionList.Controls[0];
                sessionList.Controls.RemoveAt(0);
                control.Dispose();
            }
        }

        private void FitSessionRows()
        {
            if (sessionList.IsDisposed) return;
            var width = Math.Max(120,
                sessionList.ClientSize.Width - sessionList.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
            foreach (Control child in sessionList.Controls) child.Width = width;
        }

        private void OpenPage(string page)
        {
            Hide();
            navigate(page);
        }

        private void ApplyTheme()
        {
            if (IsDisposed) return;
            var palette = TrayPalette.For(TrayPreferences.Theme);
            BackColor = palette.Surface;
            ForeColor = palette.Text;
            root.BackColor = palette.Surface;
            header.BackColor = palette.Surface;
            footer.BackColor = palette.Surface;
            connectionStrip.BackColor = Blend(palette.Surface, palette.Accent, 14);
            connectionLabel.ForeColor = palette.Accent;
            connectionDetailLabel.ForeColor = palette.MutedText;
            policySurface.BackColor = palette.Canvas;
            sessionList.BackColor = palette.Surface;
            actions.BackColor = palette.Surface;
            ApplyThemeToLabels(this, palette);
            connectionLabel.ForeColor = palette.Accent;
            connectionDetailLabel.ForeColor = palette.MutedText;
            StyleButton(settingsButton, palette.Surface, palette.Text, palette.Surface, 0);
            StyleButton(policyButton, palette.Surface, palette.Text, palette.Border, 1);
            StyleButton(openSessionsButton, palette.Accent,
                palette.Accent.GetBrightness() > 0.55F ? palette.Canvas : Color.White,
                palette.Accent, 0);
            StyleButton(accountButton, palette.Canvas, palette.Text, palette.Border, 1);
            if (latestResponse == null)
            {
                StyleButton(remoteActionButton, palette.Canvas, palette.MutedText, palette.Border, 1);
            }
            else if (latestResponse.RemoteAccessEnabled)
            {
                StyleButton(remoteActionButton, Blend(palette.Surface, palette.Danger, 16), palette.Danger, palette.Danger, 1);
            }
            else
            {
                StyleButton(remoteActionButton, palette.Canvas, palette.Text, palette.Border, 1);
            }
            StyleButton(openMainButton, palette.Surface, palette.Accent, palette.Surface, 0);
            foreach (Control control in sessionList.Controls)
            {
                if (string.Equals(control.Tag as string, "session", StringComparison.Ordinal))
                {
                    StyleButton((Button)control, palette.Surface, palette.Text, palette.Border, 1);
                }
                else
                {
                    control.BackColor = palette.Surface;
                    control.ForeColor = palette.MutedText;
                }
            }
            Invalidate(true);
        }

        private static void ApplyThemeToLabels(Control parent, TrayPalette palette)
        {
            foreach (Control control in parent.Controls)
            {
                if (control is Label)
                {
                    control.BackColor = Color.Transparent;
                    control.ForeColor = palette.Text;
                    ((Label)control).UseMnemonic = false;
                }
                ApplyThemeToLabels(control, palette);
            }
        }

        private static void StyleButton(Button button, Color background, Color foreground, Color border, int borderSize)
        {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = background;
            button.ForeColor = foreground;
            button.FlatAppearance.BorderColor = border;
            button.FlatAppearance.BorderSize = borderSize;
            button.FlatAppearance.MouseOverBackColor = Blend(background, foreground, 8);
        }

        private static Color Blend(Color background, Color foreground, int foregroundPercent)
        {
            foregroundPercent = Math.Max(0, Math.Min(100, foregroundPercent));
            var backgroundPercent = 100 - foregroundPercent;
            return Color.FromArgb(
                (background.R * backgroundPercent + foreground.R * foregroundPercent) / 100,
                (background.G * backgroundPercent + foreground.G * foregroundPercent) / 100,
                (background.B * backgroundPercent + foreground.B * foregroundPercent) / 100);
        }

        private static Label CreateLabel(string text, ContentAlignment alignment, bool strong)
        {
            return new Label
            {
                Text = text,
                Dock = DockStyle.Fill,
                AutoSize = false,
                AutoEllipsis = true,
                TextAlign = alignment,
                UseMnemonic = false,
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9F, strong ? FontStyle.Bold : FontStyle.Regular)
            };
        }

        private static Button CreatePopupButton(string text, string accessibleName)
        {
            return new Button
            {
                Text = text,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                FlatStyle = FlatStyle.Flat,
                UseMnemonic = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Margin = new Padding(0, 2, 0, 2),
                AccessibleRole = AccessibleRole.PushButton,
                AccessibleName = accessibleName
            };
        }

        private static ProductionSessionSummary FindProductionSession(
            Guid sessionId,
            IList<ProductionSessionSummary> sessions)
        {
            if (sessions == null) return null;
            foreach (var session in sessions)
            {
                if (session != null && session.SessionId == sessionId) return session;
            }
            return null;
        }

        private static string SessionText(SessionSummary summary, ProductionSessionSummary production)
        {
            var shortId = summary.SessionId.ToString("N").Substring(0, 8);
            return LocalizedShell(summary.ShellType) + " · " + shortId + Environment.NewLine +
                LocalizedLifecycle(summary.Lifecycle) + " · " + ViewerText(production) + " · " + RelativeActivity(summary.LastActivityUtc);
        }

        private static string SessionAccessibleName(SessionSummary summary, ProductionSessionSummary production)
        {
            return TrayText.Pick("受管会话 ", "Managed session ") + summary.SessionId.ToString("D") + ", " +
                LocalizedShell(summary.ShellType) + ", " + LocalizedLifecycle(summary.Lifecycle) + ", " + ViewerText(production);
        }

        private static string LocalizedShell(ManagedShellType shell)
        {
            switch (shell)
            {
                case ManagedShellType.CommandPrompt: return "CMD";
                case ManagedShellType.WindowsPowerShell: return "Windows PowerShell";
                case ManagedShellType.PowerShell7: return "PowerShell 7";
                case ManagedShellType.Wsl: return "WSL";
                default: return TrayText.Pick("自定义 Shell", "Custom shell");
            }
        }

        private static string LocalizedLifecycle(SessionAgentLifecycle lifecycle)
        {
            switch (lifecycle)
            {
                case SessionAgentLifecycle.Starting: return TrayText.Pick("正在启动", "Starting");
                case SessionAgentLifecycle.Connected: return TrayText.Pick("本地运行中", "Running locally");
                case SessionAgentLifecycle.Disconnected: return TrayText.Pick("已断开", "Disconnected");
                case SessionAgentLifecycle.Ended: return TrayText.Pick("已退出", "Ended");
                case SessionAgentLifecycle.RemoteStopped: return TrayText.Pick("远程已停止", "Remote stopped");
                case SessionAgentLifecycle.HostLost: return TrayText.Pick("Host 已丢失", "Host lost");
                default: return TrayText.Pick("异常", "Faulted");
            }
        }

        private static string ViewerText(ProductionSessionSummary production)
        {
            if (production == null || production.ViewerCount <= 0) return TrayText.Pick("仅本地", "Local only");
            var viewers = TrayText.Pick(production.ViewerCount + " 个查看者", production.ViewerCount + (production.ViewerCount == 1 ? " viewer" : " viewers"));
            return production.ControllerDeviceId.HasValue
                ? viewers + TrayText.Pick(" · 控制中", " · controlling")
                : viewers + TrayText.Pick(" · 只读", " · view only");
        }

        private static string RelativeActivity(DateTime value)
        {
            var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            var age = DateTime.UtcNow - utc;
            if (age < TimeSpan.Zero || age < TimeSpan.FromMinutes(1)) return TrayText.Pick("刚刚", "just now");
            if (age < TimeSpan.FromHours(1))
            {
                var minutes = Math.Max(1, (int)Math.Floor(age.TotalMinutes));
                return TrayText.Pick(minutes + " 分钟前", minutes + " min ago");
            }
            if (age < TimeSpan.FromDays(1))
            {
                var hours = Math.Max(1, (int)Math.Floor(age.TotalHours));
                return TrayText.Pick(hours + " 小时前", hours + " hr ago");
            }
            return utc.ToLocalTime().ToString(TrayText.IsChinese ? "M月d日 HH:mm" : "MMM d HH:mm", CultureInfo.CurrentCulture);
        }
    }

    internal sealed class AccountLinkForm : Form
    {
        private readonly TextBox username;
        private readonly TextBox password;
        private readonly TextBox pcName;

        public AccountLinkForm()
        {
            Text = TrayText.Pick("登录 TermLink 账户", "Sign in to TermLink");
            Icon = BrandIcons.LoadApplicationIcon();
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(500, 300);
            AutoScaleMode = AutoScaleMode.Dpi;
            AccessibleName = TrayText.Pick("TermLink 账户登录", "TermLink account sign in");

            var explanation = new Label
            {
                AutoSize = false,
                Location = new Point(16, 14),
                Size = new Size(468, 52),
                Text = TrayText.Pick(
                    "直接在此窗口登录，密码只通过 HTTPS 发送到 TermLink。\r\n登录后此 PC 会自动连接到你的账号。",
                    "Sign in directly in this window. The password is sent only over HTTPS.\r\nThis PC connects to your account after sign-in."),
                UseMnemonic = false
            };
            var serverLabel = new Label { AutoSize = true, Location = new Point(16, 79), Text = TrayText.Pick("TermLink 服务", "TermLink service"), UseMnemonic = false };
            var server = new TextBox
            {
                Location = new Point(180, 75),
                Size = new Size(304, 25),
                Text = ProductionEnrollment.PublicServerUrl,
                ReadOnly = true,
                TabStop = false
            };
            var usernameLabel = new Label { AutoSize = true, Location = new Point(16, 117), Text = TrayText.Pick("用户名或邮箱", "Username or email"), UseMnemonic = false };
            username = new TextBox { Location = new Point(180, 113), Size = new Size(304, 25) };
            var passwordLabel = new Label { AutoSize = true, Location = new Point(16, 155), Text = TrayText.Pick("密码", "Password"), UseMnemonic = false };
            password = new TextBox { Location = new Point(180, 151), Size = new Size(304, 25), UseSystemPasswordChar = true };
            var nameLabel = new Label { AutoSize = true, Location = new Point(16, 193), Text = TrayText.Pick("PC 名称", "PC name"), UseMnemonic = false };
            pcName = new TextBox
            {
                Location = new Point(180, 189),
                Size = new Size(304, 25),
                Text = Environment.MachineName
            };
            var cancel = new Button { Text = TrayText.Pick("取消", "Cancel"), DialogResult = DialogResult.Cancel, Location = new Point(218, 245), Size = new Size(88, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, UseMnemonic = false, AccessibleName = TrayText.Pick("取消账户登录", "Cancel account sign in") };
            var link = new Button { Text = TrayText.Pick("登录", "Sign in"), DialogResult = DialogResult.OK, Location = new Point(314, 245), Size = new Size(170, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, UseMnemonic = false, AccessibleName = TrayText.Pick("直接登录 TermLink 账户", "Sign in to TermLink account") };
            Controls.AddRange(new Control[] { explanation, serverLabel, server, usernameLabel, username, passwordLabel, password, nameLabel, pcName, cancel, link });
            AcceptButton = link;
            CancelButton = cancel;
            ApplyThemeToDialog(this, TrayPalette.For(TrayPreferences.Theme));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
        }

        public string ServerUrl { get { return ProductionEnrollment.PublicServerUrl; } }
        public string Username { get { return username.Text.Trim(); } }
        public string Password { get { return password.Text; } }
        public string PcName { get { return pcName.Text.Trim(); } }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            string normalizedName = null;
            if (DialogResult == DialogResult.OK &&
                (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password) ||
                    !TrayInputValidation.TryNormalizePcName(PcName, out normalizedName)))
            {
                MessageBox.Show(TrayText.Pick("请输入用户名、密码和有效的 PC 名称。", "Enter a username, password, and valid PC name."), "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
            if (DialogResult == DialogResult.OK)
            {
                pcName.Text = normalizedName;
            }
            base.OnFormClosing(e);
        }

        internal static void ApplyThemeToDialog(Control control, TrayPalette palette)
        {
            control.BackColor = palette.Canvas;
            control.ForeColor = palette.Text;
            if (control is Label) control.BackColor = Color.Transparent;
            if (control is TextBox)
            {
                control.BackColor = palette.Surface;
                control.ForeColor = palette.Text;
            }
            if (control is Button)
            {
                var button = (Button)control;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = palette.Border;
                button.BackColor = palette.Surface;
                button.ForeColor = palette.Text;
            }
            foreach (Control child in control.Controls) ApplyThemeToDialog(child, palette);
        }
    }

    internal sealed class AccountRegisterForm : Form
    {
        private readonly TextBox username;
        private readonly TextBox email;
        private readonly TextBox password;
        private readonly TextBox confirm;
        private readonly TextBox pcName;

        public AccountRegisterForm()
        {
            Text = TrayText.Pick("创建 TermLink 账户", "Create TermLink account");
            Icon = BrandIcons.LoadApplicationIcon();
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(500, 370);
            AutoScaleMode = AutoScaleMode.Dpi;
            AccessibleName = TrayText.Pick("TermLink 账户注册", "TermLink account registration");
            var explanation = new Label { AutoSize = false, Location = new Point(16, 14), Size = new Size(468, 42), Text = TrayText.Pick("直接在此窗口创建账号，密码只通过 HTTPS 发送到固定服务。", "Create the account directly in this window. The password is sent only over HTTPS."), UseMnemonic = false };
            var serviceLabel = new Label { AutoSize = true, Location = new Point(16, 70), Text = TrayText.Pick("TermLink 服务", "TermLink service"), UseMnemonic = false };
            var service = new TextBox { Location = new Point(180, 66), Size = new Size(304, 25), Text = ProductionEnrollment.PublicServerUrl, ReadOnly = true, TabStop = false };
            var usernameLabel = new Label { AutoSize = true, Location = new Point(16, 108), Text = TrayText.Pick("用户名", "Username"), UseMnemonic = false };
            username = new TextBox { Location = new Point(180, 104), Size = new Size(304, 25) };
            var emailLabel = new Label { AutoSize = true, Location = new Point(16, 146), Text = TrayText.Pick("邮箱", "Email"), UseMnemonic = false };
            email = new TextBox { Location = new Point(180, 142), Size = new Size(304, 25) };
            var passwordLabel = new Label { AutoSize = true, Location = new Point(16, 184), Text = TrayText.Pick("密码", "Password"), UseMnemonic = false };
            password = new TextBox { Location = new Point(180, 180), Size = new Size(304, 25), UseSystemPasswordChar = true };
            var confirmLabel = new Label { AutoSize = true, Location = new Point(16, 222), Text = TrayText.Pick("确认密码", "Confirm password"), UseMnemonic = false };
            confirm = new TextBox { Location = new Point(180, 218), Size = new Size(304, 25), UseSystemPasswordChar = true };
            var nameLabel = new Label { AutoSize = true, Location = new Point(16, 260), Text = TrayText.Pick("PC 名称", "PC name"), UseMnemonic = false };
            pcName = new TextBox { Location = new Point(180, 256), Size = new Size(304, 25), Text = Environment.MachineName };
            var cancel = new Button { Text = TrayText.Pick("取消", "Cancel"), DialogResult = DialogResult.Cancel, Location = new Point(218, 318), Size = new Size(88, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, UseMnemonic = false };
            var submit = new Button { Text = TrayText.Pick("创建并登录", "Create and sign in"), DialogResult = DialogResult.OK, Location = new Point(314, 318), Size = new Size(170, 34), Anchor = AnchorStyles.Bottom | AnchorStyles.Right, UseMnemonic = false };
            Controls.AddRange(new Control[] { explanation, serviceLabel, service, usernameLabel, username, emailLabel, email, passwordLabel, password, confirmLabel, confirm, nameLabel, pcName, cancel, submit });
            AcceptButton = submit;
            CancelButton = cancel;
            AccountLinkForm.ApplyThemeToDialog(this, TrayPalette.For(TrayPreferences.Theme));
        }

        public string Username { get { return username.Text.Trim(); } }
        public string Email { get { return email.Text.Trim(); } }
        public string Password { get { return password.Text; } }
        public string Confirm { get { return confirm.Text; } }
        public string PcName { get { return pcName.Text.Trim(); } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK && (Username.Length < 3 || Email.Length < 3 || Password.Length < 8 || Password != Confirm || !TrayInputValidation.TryNormalizePcName(PcName, out var normalized)))
            {
                MessageBox.Show(TrayText.Pick("请填写有效的用户名、邮箱、密码和 PC 名称。", "Enter a valid username, email, password, and PC name."), "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
            if (DialogResult == DialogResult.OK && TrayInputValidation.TryNormalizePcName(PcName, out var pc)) pcName.Text = pc;
            base.OnFormClosing(e);
        }
    }

    internal static class TrayDisplay
    {
        public static string AccountStatus(ProductionStatus status)
        {
            if (status == null || !status.Configured) return "not configured";
            return status.AccountLinked ? "linked" : "not linked";
        }

        public static string RelayStatus(ProductionRelayState state)
        {
            switch (state)
            {
                case ProductionRelayState.Connecting: return "connecting";
                case ProductionRelayState.Connected: return "connected";
                case ProductionRelayState.Reconnecting: return "reconnecting";
                case ProductionRelayState.Failed: return "failed";
                case ProductionRelayState.Stopped: return "stopped";
                default: return "not configured";
            }
        }

        public static string LocalizedAccountStatus(ProductionStatus status)
        {
            if (status == null || !status.Configured) return TrayText.Pick("未配置", "not configured");
            return status.AccountLinked ? TrayText.Pick("已关联", "linked") : TrayText.Pick("未关联", "not linked");
        }

        public static string LocalizedRelayStatus(ProductionRelayState state)
        {
            switch (state)
            {
                case ProductionRelayState.Connecting: return TrayText.Pick("连接中", "connecting");
                case ProductionRelayState.Connected: return TrayText.Pick("已连接", "connected");
                case ProductionRelayState.Reconnecting: return TrayText.Pick("重新连接中", "reconnecting");
                case ProductionRelayState.Failed: return TrayText.Pick("失败", "failed");
                case ProductionRelayState.Stopped: return TrayText.Pick("已停止", "stopped");
                default: return TrayText.Pick("未配置", "not configured");
            }
        }

        public static string DateTimeOrDash(DateTime? value)
        {
            return value.HasValue
                ? value.Value.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'")
                : "-";
        }

        public static string StringOrDash(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "-" : value;
        }
    }

    /// <summary>
    /// Cohesive desktop shell for the Agent.  The tray remains the fast status
    /// surface while this window hosts navigation, diagnostics and account/device
    /// actions in one keyboard-accessible place.
    /// </summary>
    internal sealed class MainWindowForm : Form
    {
        private readonly AgentController controller;
        private readonly TableLayoutPanel root;
        private readonly Panel navigationPanel;
        private readonly Panel contentHost;
        private readonly FlowLayoutPanel navigationList;
        private readonly Dictionary<string, Button> navigationButtons = new Dictionary<string, Button>(StringComparer.OrdinalIgnoreCase);
        private readonly Label identityName;
        private readonly Label identityState;
        private readonly Label footerLabel;
        private readonly Label shellStatusLabel;
        private readonly Button refreshButton;
        private readonly Icon applicationIcon;
        private readonly Bitmap brandBitmap;
        private readonly List<string> navigationOrder = new List<string>
        {
            "sessions", "remote", "devices", "activity", "settings"
        };
        private string currentPage = "sessions";
        private Control activePage;
        private SessionListForm sessionChild;
        private AccountDevicesForm devicesChild;
        private RegistryResponse latestResponse;
        private Label remoteStatusLabel;
        private Label remoteConnectionLabel;
        private Label remoteSessionsLabel;
        private Label remotePolicyLabel;
        private Button remoteToggleButton;
        private Button remoteStopButton;
        private Label activitySummaryLabel;
        private Label diagnosticsSummaryLabel;
        private Label updateStatusLabel;
        private Button updateCheckButton;
        private Button updateOpenButton;
        private string updateDownloadUrl;
        private Label membershipAccountStatusLabel;
        private Label membershipAccountDetailLabel;
        private Label membershipDeviceQuotaLabel;
        private Label membershipSessionQuotaLabel;
        private ComboBox languageCombo;
        private ComboBox themeCombo;
        private bool applyingPreferences;

        public MainWindowForm(AgentController controller)
        {
            this.controller = controller ?? throw new ArgumentNullException(nameof(controller));
            applicationIcon = BrandIcons.LoadApplicationIcon();
            brandBitmap = applicationIcon.ToBitmap();
            Text = "TermLink";
            Icon = applicationIcon;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(900, 620);
            Size = new Size(1040, 720);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 9F, FontStyle.Regular);
            AccessibleName = "TermLink Agent control plane";

            root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 220));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            navigationPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(14, 16, 12, 10) };
            contentHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1) };
            footerLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(12, 0, 12, 0) };
            shellStatusLabel = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, Padding = new Padding(12, 0, 12, 0), AutoEllipsis = true };
            root.Controls.Add(navigationPanel, 0, 0);
            root.Controls.Add(contentHost, 1, 0);
            root.Controls.Add(footerLabel, 0, 1);
            root.Controls.Add(shellStatusLabel, 1, 1);
            Controls.Add(root);
            footerLabel.Text = "TermLink Agent";

            var brand = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0, 0, 0, 8),
                Tag = "sidebar"
            };
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 46));
            brand.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 27));
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            brand.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            var mark = new PictureBox
            {
                Dock = DockStyle.Fill,
                Image = brandBitmap,
                SizeMode = PictureBoxSizeMode.Zoom,
                Margin = new Padding(0, 1, 8, 5),
                AccessibleName = "TermLink app icon"
            };
            var product = new Label
            {
                Text = "TermLink",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font(Font.FontFamily, 14, FontStyle.Bold),
                UseMnemonic = false
            };
            identityName = new Label { Text = Environment.MachineName, Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            identityState = new Label { Text = TrayText.Pick("正在连接…", "Connecting…"), Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
            brand.Controls.Add(mark, 0, 0);
            brand.SetRowSpan(mark, 2);
            brand.Controls.Add(product, 1, 0);
            brand.Controls.Add(identityName, 1, 1);
            brand.Controls.Add(identityState, 0, 2);
            brand.SetColumnSpan(identityState, 2);

            navigationList = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = false,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0),
                Tag = "sidebar"
            };
            navigationList.ClientSizeChanged += delegate { FitNavigationButtons(); };
            BuildNavigation();

            var refreshPanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Padding = new Padding(0, 4, 0, 0),
                Tag = "sidebar"
            };
            refreshButton = CreateButton(TrayText.Pick("刷新状态", "Refresh status"));
            refreshButton.Click += async delegate { await RefreshAsync(); };
            refreshButton.AccessibleName = TrayText.Pick("刷新 Agent 状态", "Refresh Agent status");
            refreshPanel.Controls.Add(refreshButton);
            var navigationLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Margin = new Padding(0),
                Padding = new Padding(0),
                Tag = "sidebar"
            };
            navigationLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            navigationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
            navigationLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            navigationLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
            navigationLayout.Controls.Add(brand, 0, 0);
            navigationLayout.Controls.Add(navigationList, 0, 1);
            navigationLayout.Controls.Add(refreshPanel, 0, 2);
            navigationPanel.Controls.Add(navigationLayout);

            Navigate(currentPage);
            ApplyTheme();
        }

        public async Task RefreshAsync()
        {
            if (IsDisposed) return;
            SetBusy(true);
            try
            {
                var response = await Task.Run(() => controller.EnsureAgent());
                Apply(response);
            }
            catch (Exception error)
            {
                ApplyUnavailable(error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        public void Navigate(string page)
        {
            if (string.IsNullOrWhiteSpace(page)) page = "sessions";
            if (!navigationButtons.ContainsKey(page)) page = "sessions";
            currentPage = page;
            foreach (var key in navigationOrder)
            {
                if (!navigationButtons.TryGetValue(key, out var button)) continue;
                button.BackColor = key.Equals(currentPage, StringComparison.OrdinalIgnoreCase)
                    ? TrayPalette.For(TrayPreferences.Theme).Accent
                    : TrayPalette.For(TrayPreferences.Theme).Sidebar;
                button.ForeColor = key.Equals(currentPage, StringComparison.OrdinalIgnoreCase)
                    ? (TrayPalette.For(TrayPreferences.Theme).Accent.GetBrightness() > 0.55f ? TrayPalette.For(TrayPreferences.Theme).Canvas : Color.White)
                    : TrayPalette.For(TrayPreferences.Theme).Text;
            }
            DisposeActivePage();
            switch (currentPage)
            {
                case "remote": CreateRemotePage(); break;
                case "devices": CreateDevicesPage(); break;
                case "activity": CreateActivityPage(); break;
                case "settings": CreateSettingsPage(); break;
                default: CreateSessionsPage(); break;
            }
            ApplyTheme();
            if (latestResponse != null) Apply(latestResponse);
        }

        public void Apply(RegistryResponse response)
        {
            if (response == null || IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<RegistryResponse>(Apply), response);
                return;
            }
            latestResponse = response;
            var production = response.Production ?? new ProductionStatus();
            identityName.Text = string.IsNullOrWhiteSpace(production.PcName) ? Environment.MachineName : production.PcName;
            identityState.Text = !production.Configured
                ? TrayText.Pick("● 本地 Agent · 账户未配置", "● Local Agent · account not configured")
                : !production.AccountLinked
                    ? TrayText.Pick("● 本地 Agent · 等待账户关联", "● Local Agent · awaiting account link")
                    : production.RelayState == ProductionRelayState.Connected
                        ? TrayText.Pick("● Relay 已连接", "● Relay connected")
                        : TrayText.Pick("○ Relay ", "○ Relay ") + TrayDisplay.LocalizedRelayStatus(production.RelayState);
            shellStatusLabel.Text = response.Success
                ? TrayText.Pick("Agent 状态已同步", "Agent status synced")
                : TrayText.Pick("Agent 返回错误", "Agent returned an error");
            if (sessionChild != null && !sessionChild.IsDisposed) sessionChild.Apply(response);
            if (devicesChild != null && !devicesChild.IsDisposed) devicesChild.Apply(response);
            UpdateRemotePage(production);
            UpdateActivityPage(production);
        }

        public void ApplyUnavailable(Exception error)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<Exception>(ApplyUnavailable), error);
                return;
            }
            latestResponse = null;
            identityState.Text = TrayText.Pick("○ Agent 不可用", "○ Agent unavailable");
            shellStatusLabel.Text = error == null
                ? TrayText.Pick("未连接", "Not connected")
                : TrayText.Pick("Agent 不可用 · 请确认服务正在运行", "Agent unavailable · confirm the service is running");
            if (remoteStatusLabel != null) remoteStatusLabel.Text = TrayText.Pick("Agent 不可用", "Agent unavailable");
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            DisposeActivePage();
            brandBitmap.Dispose();
            applicationIcon.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
        }

        private void BuildNavigation()
        {
            navigationList.Controls.Clear();
            navigationButtons.Clear();
            foreach (var key in navigationOrder)
            {
                var button = CreateButton(NavigationText(key));
                button.Height = 42;
                button.Margin = new Padding(0, 2, 0, 2);
                button.TextAlign = ContentAlignment.MiddleLeft;
                button.Padding = new Padding(8, 0, 2, 0);
                button.Tag = key;
                button.AccessibleRole = AccessibleRole.PushButton;
                button.AccessibleName = NavigationLabel(key);
                button.Click += delegate { Navigate((string)button.Tag); };
                navigationButtons.Add(key, button);
                navigationList.Controls.Add(button);
            }
            FitNavigationButtons();
        }

        private void FitNavigationButtons()
        {
            if (navigationList == null || navigationList.IsDisposed) return;
            var width = Math.Max(120, navigationList.ClientSize.Width - navigationList.Padding.Horizontal);
            foreach (Control child in navigationList.Controls) child.Width = width;
        }

        private static string NavigationText(string key)
        {
            switch (key)
            {
                case "remote": return "↗ " + NavigationDisplayLabel(key);
                case "devices": return "◇ " + NavigationDisplayLabel(key);
                case "activity": return "⌁ " + NavigationDisplayLabel(key);
                case "settings": return "⚙ " + NavigationDisplayLabel(key);
                default: return "▣ " + NavigationDisplayLabel(key);
            }
        }

        private static string NavigationDisplayLabel(string key)
        {
            switch (key)
            {
                case "remote": return TrayText.Pick("远程访问", "Remote access");
                case "devices": return TrayText.Pick("设备", "Devices");
                case "activity": return TrayText.Pick("活动", "Activity");
                case "settings": return TrayText.Pick("设置", "Settings");
                default: return TrayText.Pick("会话", "Sessions");
            }
        }

        private static string NavigationLabel(string key)
        {
            switch (key)
            {
                case "remote": return TrayText.Pick("远程访问", "Remote access");
                case "devices": return TrayText.Pick("设备列表", "Device list");
                case "activity": return TrayText.Pick("活动与诊断", "Activity & diagnostics");
                case "settings": return TrayText.Pick("Agent 设置", "Agent settings");
                default: return TrayText.Pick("会话", "Sessions");
            }
        }

        private void DisposeActivePage()
        {
            if (activePage == null) return;
            contentHost.Controls.Remove(activePage);
            activePage.Dispose();
            activePage = null;
            sessionChild = null;
            devicesChild = null;
        }

        private Panel CreatePageShell(string eyebrow, string title, string description, out Panel body)
        {
            var shell = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 22, 28, 20), Tag = "page" };
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 104,
                ColumnCount = 2,
                RowCount = 2,
                Padding = new Padding(0, 0, 0, 16),
                Tag = "page"
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            var eyebrowLabel = new Label { Text = eyebrow, Dock = DockStyle.Fill, AutoSize = false, Font = new Font(Font.FontFamily, 9, FontStyle.Bold), TextAlign = ContentAlignment.TopLeft };
            var titleLabel = new Label { Text = title, Dock = DockStyle.Fill, AutoSize = false, Font = new Font(Font.FontFamily, 20, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft };
            var descriptionLabel = new Label
            {
                Text = description,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AutoSize = true,
                MaximumSize = new Size(320, 48),
                TextAlign = ContentAlignment.TopRight,
                UseMnemonic = false
            };
            header.Controls.Add(eyebrowLabel, 0, 0);
            header.Controls.Add(titleLabel, 0, 1);
            header.SetColumnSpan(titleLabel, 2);
            header.Controls.Add(descriptionLabel, 1, 0);
            body = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 2, 0, 0), Tag = "page" };
            shell.Controls.Add(body);
            shell.Controls.Add(header);
            return shell;
        }

        private void CreateSessionsPage()
        {
            Panel body;
            var shell = CreatePageShell(
                TrayText.Pick("本机", "This PC"),
                TrayText.Pick("受管会话", "Managed sessions"),
                TrayText.Pick("本地 Shell 始终优先，远程连接可随时停止。", "Local shells stay available; remote access can be stopped at any time."),
                out body);
            sessionChild = sessionChild == null || sessionChild.IsDisposed ? new SessionListForm(controller) : sessionChild;
            sessionChild.TopLevel = false;
            sessionChild.FormBorderStyle = FormBorderStyle.None;
            sessionChild.Dock = DockStyle.Fill;
            sessionChild.MinimumSize = new Size(0, 0);
            body.Controls.Add(sessionChild);
            sessionChild.Show();
            activePage = shell;
            contentHost.Controls.Add(shell);
            contentHost.Controls.SetChildIndex(shell, 0);
            _ = sessionChild.RefreshSessionsAsync();
        }

        private void CreateDevicesPage()
        {
            Panel body;
            var shell = CreatePageShell(
                TrayText.Pick("账户设备", "Account devices"),
                TrayText.Pick("设备", "Devices"),
                TrayText.Pick("查看当前账号下已注册的设备。", "View devices registered to the current account."),
                out body);
            devicesChild = devicesChild == null || devicesChild.IsDisposed ? new AccountDevicesForm(controller) : devicesChild;
            devicesChild.TopLevel = false;
            devicesChild.FormBorderStyle = FormBorderStyle.None;
            devicesChild.Dock = DockStyle.Fill;
            devicesChild.MinimumSize = new Size(0, 0);
            body.Controls.Add(devicesChild);
            devicesChild.Show();
            activePage = shell;
            contentHost.Controls.Add(shell);
            contentHost.Controls.SetChildIndex(shell, 0);
            _ = devicesChild.RefreshStatusAsync();
        }

        private void CreateRemotePage()
        {
            Panel body;
            var shell = CreatePageShell(
                TrayText.Pick("本机策略", "Local policy"),
                TrayText.Pick("远程访问", "Remote access"),
                TrayText.Pick("本地输入和紧急停止优先于远端控制。", "Local input and emergency stop always take priority."),
                out body);
            var stack = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Padding = new Padding(0),
                Margin = new Padding(0)
            };
            var statusCard = CreateCard(TrayText.Pick("当前状态", "Current status"), out var statusBody);
            remoteStatusLabel = CreateValueLabel(TrayText.Pick("正在读取…", "Loading…"), 18, true);
            remoteConnectionLabel = CreateValueLabel("", 10, false);
            remoteSessionsLabel = CreateValueLabel("", 10, false);
            remotePolicyLabel = CreateValueLabel(TrayText.Pick("同账号设备默认具备完整访问 · 本地输入立即收回控制", "Same-account devices have full access by default · local input immediately reclaims control"), 10, false);
            statusBody.Controls.Add(remoteStatusLabel);
            statusBody.Controls.Add(remoteConnectionLabel);
            statusBody.Controls.Add(remoteSessionsLabel);
            statusBody.Controls.Add(remotePolicyLabel);
            stack.Controls.Add(statusCard);

            var actionsCard = CreateCard(TrayText.Pick("安全操作", "Safety actions"), out var actionsBody);
            var actionDescription = CreateValueLabel(
                TrayText.Pick("停止远程访问不会结束本地 Shell；恢复操作仍可从这里发起。", "Stopping remote access does not end local shells; access can be restored here."),
                10,
                false);
            actionsBody.Controls.Add(actionDescription);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(0, 10, 0, 0) };
            remoteToggleButton = CreateButton(TrayText.Pick("恢复远程访问", "Enable remote access"));
            remoteToggleButton.AccessibleName = TrayText.Pick("恢复或停止远程访问", "Enable or stop remote access");
            remoteToggleButton.Click += async delegate { await ToggleRemoteAsync(); };
            remoteStopButton = CreateButton(TrayText.Pick("停止所有远程访问", "Stop all remote access"));
            remoteStopButton.Tag = "danger";
            remoteStopButton.AccessibleName = TrayText.Pick("停止所有远程访问", "Stop all remote access");
            remoteStopButton.Click += async delegate { await StopRemoteAsync(); };
            var openSessionsButton = CreateButton(TrayText.Pick("查看会话", "View sessions"));
            openSessionsButton.Click += delegate { Navigate("sessions"); };
            actions.Controls.Add(remoteToggleButton);
            actions.Controls.Add(remoteStopButton);
            actions.Controls.Add(openSessionsButton);
            actionsBody.Controls.Add(actions);
            stack.Controls.Add(actionsCard);
            body.Controls.Add(stack);
            FitPageStack(body, stack);
            activePage = shell;
            contentHost.Controls.Add(shell);
            contentHost.Controls.SetChildIndex(shell, 0);
        }

        private void CreateActivityPage()
        {
            Panel body;
            var shell = CreatePageShell(
                TrayText.Pick("可观测性", "Observability"),
                TrayText.Pick("活动与诊断", "Activity & diagnostics"),
                TrayText.Pick("只显示状态和元数据，不显示终端正文。", "Status and metadata only; terminal content is never shown here."),
                out body);
            var stack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var health = CreateCard(TrayText.Pick("运行健康", "Runtime health"), out var healthBody);
            activitySummaryLabel = CreateValueLabel(TrayText.Pick("等待 Agent 状态…", "Waiting for Agent status…"), 12, true);
            diagnosticsSummaryLabel = CreateValueLabel("", 10, false);
            healthBody.Controls.Add(activitySummaryLabel);
            healthBody.Controls.Add(diagnosticsSummaryLabel);
            stack.Controls.Add(health);
            var note = CreateCard(TrayText.Pick("诊断原则", "Diagnostic boundary"), out var noteBody);
            noteBody.Controls.Add(CreateValueLabel(
                TrayText.Pick("诊断包只包含版本、协议、Relay 和缓冲区摘要；不会导出命令、Token 或终端输出。", "Diagnostics contain version, protocol, Relay and buffer summaries; commands, tokens and terminal output are never exported."),
                10,
                false));
            stack.Controls.Add(note);
            body.Controls.Add(stack);
            FitPageStack(body, stack);
            activePage = shell;
            contentHost.Controls.Add(shell);
            contentHost.Controls.SetChildIndex(shell, 0);
        }

        private void CreateSettingsPage()
        {
            applyingPreferences = true;
            Panel body;
            var shell = CreatePageShell(
                "TermLink Agent",
                TrayText.Pick("设置", "Agent settings"),
                TrayText.Pick("语言和外观偏好只影响本地控制平面。", "Language and appearance preferences affect this control plane only."),
                out body);
            var stack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var appearance = CreateCard(TrayText.Pick("语言与外观", "Language & appearance"), out var appearanceBody);
            languageCombo = CreatePreferenceCombo();
            languageCombo.Items.AddRange(new object[]
            {
                TrayText.LanguageName(TrayLanguage.FollowSystem),
                TrayText.LanguageName(TrayLanguage.Chinese),
                TrayText.LanguageName(TrayLanguage.English)
            });
            languageCombo.SelectedIndex = (int)TrayPreferences.Language;
            languageCombo.SelectedIndexChanged += PreferenceChanged;
            themeCombo = CreatePreferenceCombo();
            themeCombo.Items.AddRange(new object[]
            {
                TrayText.ThemeName(TrayTheme.FollowSystem),
                TrayText.ThemeName(TrayTheme.Light),
                TrayText.ThemeName(TrayTheme.Dark)
            });
            themeCombo.SelectedIndex = (int)TrayPreferences.Theme;
            themeCombo.SelectedIndexChanged += PreferenceChanged;
            appearanceBody.Controls.Add(CreateSettingRow(TrayText.Pick("界面语言", "Interface language"), TrayText.Pick("跟随系统或固定使用一种语言。", "Follow Windows or choose a fixed language."), languageCombo));
            appearanceBody.Controls.Add(CreateSettingRow(TrayText.Pick("颜色主题", "Color theme"), TrayText.Pick("跟随系统、浅色或深色。", "Follow Windows, light or dark."), themeCombo));
            stack.Controls.Add(appearance);
            var safety = CreateCard(TrayText.Pick("本地优先", "Local-first safety"), out var safetyBody);
            safetyBody.Controls.Add(CreateValueLabel(
                TrayText.Pick("本地 Shell 不依赖 Relay。停止远程访问由本机立即执行；设备撤销需要服务器连接并同步撤销状态。", "Local shells do not depend on Relay. This PC stops remote access immediately; device revocation requires the server and synchronizes revoked state."),
                10,
                false));
            stack.Controls.Add(safety);

            var updates = CreateCard(TrayText.Pick("软件更新", "Software updates"), out var updatesBody);
            updateStatusLabel = CreateValueLabel(
                TrayText.Pick("检查公开 AppShip 更新，不会自动关闭当前窗口。", "Check the public AppShip channel without closing the current window."),
                10,
                false);
            updatesBody.Controls.Add(updateStatusLabel);
            var updateActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Padding = new Padding(0, 10, 0, 0)
            };
            updateCheckButton = CreateButton(TrayText.Pick("检查更新", "Check for updates"));
            updateCheckButton.Click += async delegate { await CheckForUpdateAsync(); };
            updateOpenButton = CreateButton(TrayText.Pick("打开下载", "Open download"));
            updateOpenButton.Enabled = false;
            updateOpenButton.Click += delegate { OpenUpdateDownload(); };
            updateActions.Controls.Add(updateCheckButton);
            updateActions.Controls.Add(updateOpenButton);
            updatesBody.Controls.Add(updateActions);
            stack.Controls.Add(updates);
            body.Controls.Add(stack);
            FitPageStack(body, stack);
            activePage = shell;
            contentHost.Controls.Add(shell);
            contentHost.Controls.SetChildIndex(shell, 0);
            applyingPreferences = false;
        }

        private void CreateMembershipPage()
        {
            Panel body;
            var shell = CreatePageShell(
                TrayText.Pick("账号权益", "Account entitlements"),
                TrayText.Pick("账户与会员", "Account & membership"),
                TrayText.Pick("购买和账号安全在系统浏览器中完成。", "Purchases and account security continue in the system browser."),
                out body);
            var stack = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            var account = CreateCard(TrayText.Pick("当前账户", "Current account"), out var accountBody);
            membershipAccountStatusLabel = CreateValueLabel(TrayText.Pick("等待状态…", "Waiting for status…"), 16, true);
            membershipAccountDetailLabel = CreateValueLabel(TrayText.Pick("账号权益由服务端签发。", "Entitlements are issued by the server."), 10, false);
            accountBody.Controls.Add(membershipAccountStatusLabel);
            accountBody.Controls.Add(membershipAccountDetailLabel);
            var accountActions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 10, 0, 0) };
            var linkButton = CreateButton(TrayText.Pick("在浏览器中关联账户", "Link account in browser"));
            linkButton.Click += async delegate { await LoginAccountFromWindowAsync(); };
            linkButton.AccessibleName = TrayText.Pick("在系统浏览器中关联 TermLink 账户", "Link TermLink account in the system browser");
            accountActions.Controls.Add(linkButton);
            accountBody.Controls.Add(accountActions);
            stack.Controls.Add(account);
            var quota = CreateCard(TrayText.Pick("连接摘要 · 非权益配额", "Connection snapshot · not entitlement quota"), out var quotaBody);
            membershipDeviceQuotaLabel = CreateValueLabel(TrayText.Pick("已知账号设备：等待状态…", "Known account devices: waiting for status…"), 11, false);
            membershipSessionQuotaLabel = CreateValueLabel(TrayText.Pick("已发布 Session 元数据：等待状态…", "Published Session metadata: waiting for status…"), 11, false);
            quotaBody.Controls.Add(membershipDeviceQuotaLabel);
            quotaBody.Controls.Add(membershipSessionQuotaLabel);
            quotaBody.Controls.Add(CreateValueLabel(TrayText.Pick("套餐、远程 PC 和实时订阅占用以网页账户中心的服务端权益数据为准。", "Plan, remote-PC, and live-subscription occupancy come from the server entitlement data in the web account center."), 10, false));
            quotaBody.Controls.Add(CreateValueLabel(TrayText.Pick("会员到期不会终止 Windows 本地 Shell。", "Membership expiry never terminates local Windows shells."), 10, false));
            stack.Controls.Add(quota);
            body.Controls.Add(stack);
            FitPageStack(body, stack);
            activePage = shell;
            contentHost.Controls.Add(shell);
            contentHost.Controls.SetChildIndex(shell, 0);
        }

        private void UpdateRemotePage(ProductionStatus production)
        {
            if (remoteStatusLabel == null || latestResponse == null) return;
            var connected = production != null && production.RelayState == ProductionRelayState.Connected;
            remoteStatusLabel.Text = !latestResponse.RemoteAccessEnabled
                ? TrayText.Pick("○ 远程访问已停止", "○ Remote access is stopped")
                : production == null || !production.Configured
                    ? TrayText.Pick("○ 策略已开启 · 账户未配置", "○ Policy enabled · account not configured")
                    : !production.AccountLinked
                        ? TrayText.Pick("○ 策略已开启 · 等待账户关联", "○ Policy enabled · awaiting account link")
                        : !connected
                            ? TrayText.Pick("○ 策略已开启 · Relay 未连接", "○ Policy enabled · Relay not connected")
                            : TrayText.Pick("● 同账号设备可查看并申请控制", "● Same-account devices can view and request control");
            remoteConnectionLabel.Text = TrayText.Pick("Relay：", "Relay: ") +
                (connected ? TrayText.Pick("已连接", "connected") : TrayDisplay.LocalizedRelayStatus(production == null ? ProductionRelayState.NotConfigured : production.RelayState));
            var viewerCount = 0;
            if (production != null && production.Sessions != null)
            {
                foreach (var session in production.Sessions)
                {
                    if (session != null) viewerCount += session.ViewerCount;
                }
            }
            remoteSessionsLabel.Text = TrayText.Pick("活动会话：", "Active sessions: ") +
                (latestResponse.Summaries == null ? 0 : latestResponse.Summaries.Count) +
                TrayText.Pick(" · 查看者 " + viewerCount, " · viewers " + viewerCount);
            remotePolicyLabel.Text = TrayText.Pick("同账号设备默认具备完整访问 · 本地输入立即抢回控制权", "Same-account devices have full access by default · local input immediately reclaims control");
            remoteToggleButton.Text = latestResponse.RemoteAccessEnabled
                ? TrayText.Pick("停止远程访问", "Stop remote access")
                : TrayText.Pick("恢复远程访问", "Enable remote access");
            remoteStopButton.Enabled = latestResponse.RemoteAccessEnabled;
        }

        private void UpdateActivityPage(ProductionStatus production)
        {
            if (activitySummaryLabel == null || latestResponse == null) return;
            var relay = production == null ? ProductionRelayState.NotConfigured : production.RelayState;
            activitySummaryLabel.Text = TrayText.Pick("Agent：正常 · Relay：", "Agent: healthy · Relay: ") +
                TrayDisplay.LocalizedRelayStatus(relay);
            diagnosticsSummaryLabel.Text = TrayText.Pick(
                "会话 " + (latestResponse.Summaries == null ? 0 : latestResponse.Summaries.Count) + " · 远程访问 " + (latestResponse.RemoteAccessEnabled ? "开启" : "停止"),
                (latestResponse.Summaries == null ? 0 : latestResponse.Summaries.Count) + " sessions · remote access " + (latestResponse.RemoteAccessEnabled ? "on" : "off"));
        }

        private void UpdateMembershipPage(ProductionStatus production)
        {
            if (production == null) return;
            if (membershipAccountStatusLabel != null)
            {
                membershipAccountStatusLabel.Text = TrayText.Pick(
                    production.Configured && production.AccountLinked ? "已关联账户" : "尚未关联账户",
                    production.Configured && production.AccountLinked ? "Account linked" : "Account not linked");
            }
            if (membershipAccountDetailLabel != null)
            {
                membershipAccountDetailLabel.Text = TrayText.Pick(
                    "服务器：" + TrayDisplay.StringOrDash(production.ServerHost) + " · PC：" + TrayDisplay.StringOrDash(production.PcName),
                    "Server: " + TrayDisplay.StringOrDash(production.ServerHost) + " · PC: " + TrayDisplay.StringOrDash(production.PcName));
            }
            if (membershipDeviceQuotaLabel != null)
            {
                membershipDeviceQuotaLabel.Text = TrayText.Pick(
                    "已知账号设备：" + (production.Devices == null ? 0 : production.Devices.Count) + "（非 PC 配额）",
                    "Known account devices: " + (production.Devices == null ? 0 : production.Devices.Count) + " (not PC quota)");
            }
            if (membershipSessionQuotaLabel != null)
            {
                membershipSessionQuotaLabel.Text = TrayText.Pick(
                    "已发布 Session 元数据：" + (production.Sessions == null ? 0 : production.Sessions.Count) + "（非订阅占用）",
                    "Published Session metadata: " + (production.Sessions == null ? 0 : production.Sessions.Count) + " (not subscription occupancy)");
            }
        }

        private async Task CheckForUpdateAsync()
        {
            if (updateCheckButton == null || updateStatusLabel == null) return;
            updateCheckButton.Enabled = false;
            updateOpenButton.Enabled = false;
            updateDownloadUrl = null;
            updateStatusLabel.Text = TrayText.Pick("正在检查更新…", "Checking for updates…");
            try
            {
                var current = typeof(Program).Assembly.GetName().Version ?? new Version(0, 0, 0, 0);
                var result = await AppShipUpdateClient.CheckAsync(AppShipUpdateClient.ConfiguredBaseUrl(), current);
                updateDownloadUrl = result.DownloadUrl;
                var version = string.IsNullOrWhiteSpace(result.VersionName) ? "-" : result.VersionName;
                var prefix = result.Decision == WindowsUpdateDecision.Optional
                    ? TrayText.Pick("有可选更新：", "Optional update: ")
                    : result.Decision == WindowsUpdateDecision.Required
                        ? TrayText.Pick("需要更新：", "Required update: ")
                        : result.Decision == WindowsUpdateDecision.NoUpdate
                            ? TrayText.Pick("当前已是最新版本", "You are up to date")
                            : result.Decision == WindowsUpdateDecision.NotConfigured
                                ? TrayText.Pick("更新服务未配置", "Update service is not configured")
                                : TrayText.Pick("检查更新失败", "Update check failed");
                updateStatusLabel.Text = prefix +
                    ((result.Decision == WindowsUpdateDecision.Optional || result.Decision == WindowsUpdateDecision.Required) ? version : string.Empty) +
                    (string.IsNullOrWhiteSpace(result.Message) ? string.Empty : " · " + result.Message);
                updateOpenButton.Enabled = !string.IsNullOrWhiteSpace(updateDownloadUrl);
            }
            catch (Exception error)
            {
                updateStatusLabel.Text = TrayText.Pick("检查更新失败 · " + error.Message, "Update check failed · " + error.Message);
            }
            finally
            {
                updateCheckButton.Enabled = true;
            }
        }

        private void OpenUpdateDownload()
        {
            if (string.IsNullOrWhiteSpace(updateDownloadUrl) ||
                !Uri.TryCreate(updateDownloadUrl, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo
            {
                FileName = uri.AbsoluteUri,
                UseShellExecute = true
            });
        }

        private async Task ToggleRemoteAsync()
        {
            if (latestResponse == null) return;
            if (latestResponse.RemoteAccessEnabled)
            {
                await StopRemoteAsync();
                return;
            }
            SetBusy(true);
            try
            {
                var response = await Task.Run(() => controller.Send(RegistryCommand.EnableRemoteAccess));
                if (!response.Success) throw new InvalidOperationException(response.Code);
                Apply(response);
            }
            catch (Exception error)
            {
                ApplyUnavailable(error);
            }
            finally { SetBusy(false); }
        }

        private async Task StopRemoteAsync()
        {
            if (latestResponse != null && !latestResponse.RemoteAccessEnabled) return;
            if (MessageBox.Show(
                TrayText.Pick("停止所有远程访问？本地 Shell 将继续运行。", "Stop all remote access? Local shells will keep running."),
                "TermLink",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.OK) return;
            SetBusy(true);
            try
            {
                var response = await Task.Run(() => controller.Send(RegistryCommand.StopAllRemoteAccess));
                if (!response.Success) throw new InvalidOperationException(response.Code);
                Apply(response);
            }
            catch (Exception error)
            {
                ApplyUnavailable(error);
            }
            finally { SetBusy(false); }
        }

        private async Task LoginAccountFromWindowAsync()
        {
            using (var dialog = new AccountLinkForm())
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true);
                try
                {
                    await Task.Run(() => controller.LoginAccount(dialog.Username, dialog.Password, dialog.PcName));
                    var response = await Task.Run(() => controller.RestartAgent());
                    if (!response.Success) throw new InvalidOperationException(response.Code);
                    Apply(response);
                }
                catch (Exception)
                {
                    MessageBox.Show(TrayText.Pick("账户登录未完成，请重试或查看诊断。", "Account sign-in did not complete. Retry or open diagnostics."), "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { SetBusy(false); }
            }
        }

        private void PreferenceChanged(object sender, EventArgs e)
        {
            if (applyingPreferences || languageCombo == null || themeCombo == null || languageCombo.SelectedIndex < 0 || themeCombo.SelectedIndex < 0) return;
            TrayPreferences.Update((TrayLanguage)languageCombo.SelectedIndex, (TrayTheme)themeCombo.SelectedIndex);
            var page = currentPage;
            BuildNavigation();
            Navigate(page);
        }

        private void SetBusy(bool busy)
        {
            if (IsDisposed) return;
            refreshButton.Enabled = !busy;
            if (remoteToggleButton != null) remoteToggleButton.Enabled = !busy;
            if (remoteStopButton != null) remoteStopButton.Enabled = !busy && latestResponse != null && latestResponse.RemoteAccessEnabled;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }

        private void ApplyTheme()
        {
            if (IsDisposed) return;
            var palette = TrayPalette.For(TrayPreferences.Theme);
            ApplyThemeTo(this, palette);
            TrayWindowChrome.Apply(this, palette);
            root.BackColor = palette.Canvas;
            navigationPanel.BackColor = palette.Sidebar;
            contentHost.BackColor = palette.Canvas;
            footerLabel.BackColor = palette.Sidebar;
            shellStatusLabel.BackColor = palette.Canvas;
            foreach (var button in navigationButtons.Values)
            {
                var active = string.Equals(button.Tag as string, currentPage, StringComparison.OrdinalIgnoreCase);
                button.BackColor = active ? palette.Accent : palette.Sidebar;
                button.ForeColor = active ? (palette.Accent.GetBrightness() > 0.55f ? palette.Canvas : Color.White) : palette.Text;
                button.FlatAppearance.BorderSize = 0;
            }
            if (remoteStopButton != null && remoteStopButton.Tag as string == "danger")
            {
                remoteStopButton.BackColor = palette.Danger;
                remoteStopButton.ForeColor = palette.Danger.GetBrightness() > 0.55f ? palette.Canvas : Color.White;
            }
        }

        private static void ApplyThemeTo(Control control, TrayPalette palette)
        {
            var style = control.Tag as string;
            control.BackColor = string.Equals(style, "card", StringComparison.Ordinal)
                ? palette.Surface
                : string.Equals(style, "sidebar", StringComparison.Ordinal)
                    ? palette.Sidebar
                    : palette.Canvas;
            control.ForeColor = palette.Text;
            if (control is Label)
            {
                ((Label)control).UseMnemonic = false;
                control.BackColor = Color.Transparent;
            }
            else if (control is GroupBox)
            {
                control.BackColor = palette.Canvas;
            }
            else if ((control is Panel || control is TableLayoutPanel || control is FlowLayoutPanel) &&
                !string.Equals(style, "card", StringComparison.Ordinal))
            {
                control.BackColor = string.Equals(style, "page", StringComparison.Ordinal)
                    ? palette.Canvas
                    : string.Equals(style, "sidebar", StringComparison.Ordinal)
                        ? palette.Sidebar
                        : palette.Surface;
            }
            if (control is TextBox || control is ComboBox || control is ListView)
            {
                control.BackColor = palette.Surface;
                control.ForeColor = palette.Text;
                control.Invalidate();
            }
            if (control is Button)
            {
                var button = (Button)control;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderColor = palette.Border;
                button.FlatAppearance.BorderSize = 1;
                if ((control.Tag as string) == "danger")
                {
                    control.BackColor = palette.Danger;
                    control.ForeColor = palette.Danger.GetBrightness() > 0.55f ? palette.Canvas : Color.White;
                }
                else
                {
                    control.BackColor = palette.Surface;
                    control.ForeColor = palette.Text;
                }
                if (button is TrayThemedButton themedButton)
                {
                    themedButton.DisabledBackColor = palette.Canvas;
                    themedButton.DisabledForeColor = palette.MutedText;
                    themedButton.DisabledBorderColor = palette.Border;
                }
            }
            foreach (Control child in control.Controls) ApplyThemeTo(child, palette);
        }

        private static Label CreateValueLabel(string text, float size, bool strong)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                MaximumSize = new Size(760, 0),
                Padding = new Padding(0, 3, 0, 3),
                Font = new Font(SystemFonts.MessageBoxFont.FontFamily, size, strong ? FontStyle.Bold : FontStyle.Regular),
                AccessibleName = text
            };
        }

        private static Button CreateButton(string text)
        {
            return new Button
            {
                Text = text,
                AutoSize = true,
                MinimumSize = new Size(110, 34),
                FlatStyle = FlatStyle.Flat,
                UseMnemonic = false,
                AccessibleRole = AccessibleRole.PushButton,
                AccessibleName = text,
                Margin = new Padding(4)
            };
        }

        private static Panel CreateCard(string title, out FlowLayoutPanel body)
        {
            var card = new Panel
            {
                AutoSize = false,
                Width = 600,
                Height = 80,
                MinimumSize = new Size(460, 0),
                Padding = new Padding(16),
                Margin = new Padding(0, 0, 0, 14),
                BorderStyle = BorderStyle.FixedSingle,
                Tag = "card"
            };
            var heading = new Label { Text = title, AutoSize = false, Dock = DockStyle.Top, Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 11, FontStyle.Bold), Padding = new Padding(0, 0, 0, 8), UseMnemonic = false };
            body = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = false, Width = 566, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0), Tag = "card" };
            card.Controls.Add(body);
            card.Controls.Add(heading);
            var capturedBody = body;
            var fitting = false;
            Action fit = delegate
            {
                if (fitting) return;
                fitting = true;
                try { FitCardContent(card, capturedBody, heading); }
                finally { fitting = false; }
            };
            card.SizeChanged += delegate { fit(); };
            body.ControlAdded += delegate { fit(); };
            body.ControlRemoved += delegate { fit(); };
            fit();
            return card;
        }

        private static void FitPageStack(Panel body, FlowLayoutPanel stack)
        {
            Action resize = delegate
            {
                if (body.IsDisposed || stack.IsDisposed) return;
                var width = Math.Max(460, body.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
                stack.Width = width;
                foreach (Control child in stack.Controls) child.Width = width;
            };
            body.ClientSizeChanged += delegate { resize(); };
            stack.ControlAdded += delegate { resize(); };
            resize();
        }

        private static void FitCardContent(Panel card, FlowLayoutPanel body, Label heading)
        {
            if (card.IsDisposed || body.IsDisposed || heading.IsDisposed) return;
            var width = Math.Max(320, card.ClientSize.Width - card.Padding.Horizontal);
            body.Width = width;
            foreach (Control child in body.Controls)
            {
                if (child is Label)
                {
                    child.MaximumSize = new Size(width, 0);
                }
                else if (child is TableLayoutPanel || child is FlowLayoutPanel || child is Panel)
                {
                    child.Width = width;
                    child.PerformLayout();
                    if (child is FlowLayoutPanel)
                    {
                        child.Height = child.GetPreferredSize(new Size(width, 0)).Height;
                    }
                }
            }
            body.PerformLayout();
            var preferredHeight = body.GetPreferredSize(new Size(width, 0)).Height;
            body.Size = new Size(width, preferredHeight);
            heading.MaximumSize = new Size(width, 0);
            heading.Height = heading.GetPreferredSize(new Size(width, 0)).Height;
            card.Height = card.Padding.Vertical + heading.Height + body.Height + 2;
            card.PerformLayout();
        }

        private static ComboBox CreatePreferenceCombo()
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Width = 190,
                Height = 30,
                AccessibleRole = AccessibleRole.ComboBox
            };
            TrayComboBoxStyle.Configure(combo);
            return combo;
        }

        private static Control CreateSettingRow(string title, string description, Control editor)
        {
            var row = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Width = 566, ColumnCount = 2, RowCount = 2, Padding = new Padding(0, 5, 0, 5), Tag = "card" };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            row.Controls.Add(new Label { Text = title, AutoSize = true, MaximumSize = new Size(350, 0), Font = new Font(SystemFonts.MessageBoxFont.FontFamily, 10, FontStyle.Bold) }, 0, 0);
            row.Controls.Add(new Label { Text = description, AutoSize = true, MaximumSize = new Size(350, 0), ForeColor = SystemColors.GrayText }, 0, 1);
            editor.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            row.Controls.Add(editor, 1, 0);
            row.SetRowSpan(editor, 2);
            return row;
        }
    }

    internal sealed class SessionListForm : Form
    {
        private readonly AgentController controller;
        private readonly Label stateLabel;
        private readonly ListView sessions;
        private ProductionStatus productionStatus;

        public SessionListForm(AgentController controller)
        {
            this.controller = controller;
            Text = TrayText.Pick("TermLink 会话", "TermLink sessions");
            Icon = BrandIcons.LoadApplicationIcon();
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 380);
            Size = new Size(900, 480);
            AutoScaleMode = AutoScaleMode.Dpi;
            AccessibleName = TrayText.Pick("TermLink 受管会话", "TermLink managed sessions");

            stateLabel = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                Padding = new Padding(12, 12, 12, 8),
                Text = TrayText.Pick("正在读取…", "Loading…")
            };
            sessions = new ListView
            {
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                View = View.Details,
                ShowItemToolTips = true,
                AccessibleName = TrayText.Pick("受管会话列表", "Managed sessions list")
            };
            TrayListViewStyle.Configure(sessions);
            sessions.Columns.Add(TrayText.Pick("会话", "Session"), 175);
            sessions.Columns.Add("Shell", 90);
            sessions.Columns.Add(TrayText.Pick("本地状态", "Local state"), 90);
            sessions.Columns.Add(TrayText.Pick("远程状态", "Remote"), 100);
            sessions.Columns.Add(TrayText.Pick("查看者", "Viewers"), 65);
            sessions.Columns.Add(TrayText.Pick("控制者", "Controller"), 110);
            sessions.Columns.Add(TrayText.Pick("最近活动", "Last activity"), 120);
            sessions.ClientSizeChanged += delegate { FitSessionColumns(); };

            var refreshButton = new Button { Text = TrayText.Pick("刷新", "Refresh"), AutoSize = true, AccessibleName = TrayText.Pick("刷新会话", "Refresh sessions") };
            refreshButton.Click += delegate { RefreshSessions(); };
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            actions.Controls.Add(refreshButton);
            Controls.Add(sessions);
            Controls.Add(actions);
            Controls.Add(stateLabel);
            FitSessionColumns();
        }

        public void RefreshSessions()
        {
            _ = RefreshSessionsAsync();
        }

        public async Task RefreshSessionsAsync()
        {
            try
            {
                Apply(await Task.Run(() => controller.EnsureAgent()));
            }
            catch (Exception)
            {
                stateLabel.Text = TrayText.Pick("Agent 不可用 · 请确认服务正在运行", "Agent unavailable · confirm the service is running");
                sessions.Items.Clear();
            }
        }

        public void Apply(RegistryResponse response)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<RegistryResponse>(Apply), response);
                return;
            }
            stateLabel.Text = response.RemoteAccessEnabled
                ? TrayText.Pick("远程访问已开启", "Remote access enabled")
                : TrayText.Pick("远程访问已停止", "Remote access disabled");
            productionStatus = response.Production;
            sessions.BeginUpdate();
            sessions.Items.Clear();
            if (response.Summaries != null)
            {
                foreach (var summary in response.Summaries)
                {
                    var sessionText = summary.SessionId.ToString("D");
                    var item = new ListViewItem(sessionText.Substring(0, 8) + "…") { Tag = summary.SessionId };
                    item.SubItems.Add(summary.ShellType.ToString());
                    item.SubItems.Add(summary.Lifecycle.ToString());
                    var productionSession = FindProductionSession(summary.SessionId);
                    item.SubItems.Add(productionSession == null
                        ? TrayText.Pick("仅本地", "Local only")
                        : productionSession.ViewerCount > 0
                            ? TrayText.Pick("实时", "Live")
                            : TrayText.Pick("可连接", "Available"));
                    item.SubItems.Add(productionSession == null ? "0" : productionSession.ViewerCount.ToString());
                    item.SubItems.Add(
                        productionSession == null || !productionSession.ControllerDeviceId.HasValue
                            ? "-"
                            : productionSession.ControllerDeviceId.Value.ToString("D").Substring(0, 8) + "…");
                    item.SubItems.Add(TrayDisplay.DateTimeOrDash(summary.LastActivityUtc));
                    item.ToolTipText = "Session " + sessionText + Environment.NewLine +
                        "Host PID " + summary.HostProcessId + " · seq " + summary.LastSequence + Environment.NewLine +
                        TrayText.Pick("租约到期 ", "Lease expiry ") +
                        (productionSession == null ? "-" : TrayDisplay.DateTimeOrDash(productionSession.LeaseExpiresAtUtc));
                    sessions.Items.Add(item);
                }
            }
            sessions.EndUpdate();
            FitSessionColumns();
        }

        private ProductionSessionSummary FindProductionSession(Guid sessionId)
        {
            if (productionStatus == null || productionStatus.Sessions == null) return null;
            foreach (var item in productionStatus.Sessions)
            {
                if (item != null && item.SessionId == sessionId) return item;
            }
            return null;
        }

        private void FitSessionColumns()
        {
            if (sessions.IsDisposed || sessions.Columns.Count == 0) return;
            var rowHeight = sessions.Items.Count == 0
                ? sessions.Font.Height + 5
                : Math.Max(1, sessions.GetItemRect(0).Height);
            var needsVerticalScroll = sessions.Items.Count * rowHeight + sessions.Font.Height + 10 > sessions.ClientSize.Height;
            var available = sessions.ClientSize.Width - (needsVerticalScroll ? SystemInformation.VerticalScrollBarWidth : 0) - 2;
            if (available <= 0) return;

            var minimumWidths = new int[sessions.Columns.Count];
            var minimumTotal = 0;
            for (var index = 0; index < sessions.Columns.Count; index++)
            {
                minimumWidths[index] = TextRenderer.MeasureText(
                    sessions.Columns[index].Text,
                    sessions.Font,
                    Size.Empty,
                    TextFormatFlags.NoPadding).Width + 16;
                minimumTotal += minimumWidths[index];
            }

            if (minimumTotal >= available)
            {
                var used = 0;
                for (var index = 0; index < sessions.Columns.Count - 1; index++)
                {
                    var width = Math.Max(36, (int)Math.Floor(minimumWidths[index] * (double)available / minimumTotal));
                    sessions.Columns[index].Width = width;
                    used += width;
                }
                sessions.Columns[sessions.Columns.Count - 1].Width = Math.Max(36, available - used);
                return;
            }

            var extra = available - minimumTotal;
            var weights = new[] { 35, 5, 10, 10, 5, 10, 25 };
            var allocated = 0;
            for (var index = 0; index < sessions.Columns.Count - 1; index++)
            {
                var width = minimumWidths[index] + (extra * weights[index] / 100);
                sessions.Columns[index].Width = width;
                allocated += width;
            }
            sessions.Columns[sessions.Columns.Count - 1].Width = Math.Max(36, available - allocated);
        }

    }

    internal sealed class AccountDevicesForm : Form
    {
        private readonly AgentController controller;
        private readonly Label stateLabel;
        private readonly Label accountValue;
        private readonly Label serverValue;
        private readonly Label pcValue;
        private readonly Label relayValue;
        private readonly Label connectedValue;
        private readonly ListView devices;
        private readonly Button refreshButton;
        private readonly Button createChallengeButton;
        private readonly Button grantButton;
        private readonly Button revokeButton;
        private readonly ComboBox pairingPermission;
        private readonly TextBox challengeCodeValue;
        private readonly Label challengePermissionValue;
        private readonly Label challengeFingerprintValue;
        private readonly Label challengeExpiresValue;
        private readonly Button copyChallengeButton;
        private string challengeId;
        private string challengeNonce;
        private string challengePermission;
        private string challengeFingerprint;
        private ProductionStatus productionStatus;

        public AccountDevicesForm(AgentController controller)
        {
            this.controller = controller;
            Text = TrayText.Pick("TermLink 账户与设备", "TermLink account and devices");
            Icon = BrandIcons.LoadApplicationIcon();
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(780, 520);
            Size = new Size(920, 620);
            AutoScaleMode = AutoScaleMode.Dpi;
            AccessibleName = TrayText.Pick("TermLink 账户设备", "TermLink account devices");

            stateLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(12, 8, 12, 4),
                Text = TrayText.Pick("正在读取…", "Loading…")
            };

            accountValue = CreateValueLabel();
            serverValue = CreateValueLabel();
            pcValue = CreateValueLabel();
            relayValue = CreateValueLabel();
            connectedValue = CreateValueLabel();
            var details = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 122,
                ColumnCount = 4,
                RowCount = 3,
                Padding = new Padding(12, 2, 12, 8)
            };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            AddDetail(details, TrayText.Pick("账户", "Account"), accountValue, 0, 0);
            AddDetail(details, TrayText.Pick("服务器", "Server"), serverValue, 2, 0);
            AddDetail(details, "PC", pcValue, 0, 1);
            AddDetail(details, "Relay", relayValue, 2, 1);
            details.Controls.Add(new Label { Text = TrayText.Pick("已连接", "Connected"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            details.Controls.Add(connectedValue, 1, 2);
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            devices = new ListView
            {
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                View = View.Details,
                ShowItemToolTips = true,
                AccessibleName = TrayText.Pick("账户设备列表", "Account devices list")
            };
            TrayListViewStyle.Configure(devices);
            devices.Columns.Add(TrayText.Pick("名称", "Name"), 300);
            devices.Columns.Add(TrayText.Pick("类型", "Type"), 150);
            devices.Columns.Add(TrayText.Pick("状态", "Status"), 150);
            devices.ClientSizeChanged += delegate { FitDeviceColumns(); };
            devices.SelectedIndexChanged += delegate { UpdateActions(); };

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            refreshButton = new Button { Text = TrayText.Pick("刷新", "Refresh"), AutoSize = true, AccessibleName = TrayText.Pick("刷新设备状态", "Refresh device status") };
            refreshButton.Click += delegate { RefreshStatus(); };
            createChallengeButton = new TrayThemedButton { Text = TrayText.Pick("创建配对请求", "Create pairing request"), AutoSize = true, Enabled = false };
            createChallengeButton.Click += delegate { CreateChallenge(); };
            grantButton = new TrayThemedButton { Text = TrayText.Pick("直接授权", "Grant on this PC"), AutoSize = true, Enabled = false };
            grantButton.Click += delegate { GrantSelected(); };
            revokeButton = new TrayThemedButton { Text = TrayText.Pick("撤销设备", "Revoke device"), AutoSize = true, Enabled = false };
            revokeButton.Tag = "danger";
            revokeButton.Click += delegate { RevokeSelected(); };
            pairingPermission = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                Width = 112,
                AccessibleName = TrayText.Pick("授予此 PC 的权限", "Permission granted on this PC")
            };
            pairingPermission.Items.AddRange(new object[] { "VIEW", "CONTROL", "MANAGE" });
            pairingPermission.SelectedIndex = 0;
            pairingPermission.SelectedIndexChanged += delegate { UpdateActions(); };
            TrayComboBoxStyle.Configure(pairingPermission);
            // Account/device authorization is automatic in the first release.
            // Keep the legacy pairing controls in code for protocol compatibility,
            // but do not expose them in the product UI.
            actions.Controls.Add(refreshButton);

            challengeCodeValue = CreateReadOnlyTextBox();
            challengePermissionValue = CreateValueLabel();
            challengeFingerprintValue = CreateValueLabel();
            challengeExpiresValue = CreateValueLabel();
            copyChallengeButton = new TrayThemedButton { Text = TrayText.Pick("复制配对信息", "Copy pairing details"), AutoSize = true, Enabled = false };
            copyChallengeButton.Click += delegate { CopyChallengeCode(); };
            var challenge = new GroupBox
            {
                Text = TrayText.Pick("短时配对请求 · 仅交给目标设备", "Short-lived pairing request · target device only"),
                Dock = DockStyle.Bottom,
                Height = 172,
                Padding = new Padding(8)
            };
            var challengeGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 4
            };
            challengeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            challengeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            challengeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (var row = 0; row < 4; row++)
            {
                challengeGrid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }
            challengeGrid.Controls.Add(new Label { Text = TrayText.Pick("代码", "Code"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            challengeGrid.Controls.Add(challengeCodeValue, 1, 0);
            challengeGrid.Controls.Add(copyChallengeButton, 2, 0);
            challengeGrid.Controls.Add(new Label { Text = TrayText.Pick("权限", "Permission"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            challengeGrid.Controls.Add(challengePermissionValue, 1, 1);
            challengeGrid.SetColumnSpan(challengePermissionValue, 2);
            challengeGrid.Controls.Add(new Label { Text = TrayText.Pick("密钥指纹", "Key fingerprint"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            challengeGrid.Controls.Add(challengeFingerprintValue, 1, 2);
            challengeGrid.SetColumnSpan(challengeFingerprintValue, 2);
            challengeGrid.Controls.Add(new Label { Text = TrayText.Pick("到期", "Expires"), AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
            challengeGrid.Controls.Add(challengeExpiresValue, 1, 3);
            challengeGrid.SetColumnSpan(challengeExpiresValue, 2);
            challenge.Controls.Add(challengeGrid);

            Controls.Add(devices);
            Controls.Add(actions);
            Controls.Add(details);
            Controls.Add(stateLabel);
            ClearChallenge();
            FitDeviceColumns();
        }

        public void RefreshStatus()
        {
            _ = RefreshStatusAsync();
        }

        public async Task RefreshStatusAsync()
        {
            try
            {
                var response = await Task.Run(() => controller.EnsureAgent());
                try
                {
                    var refreshed = await Task.Run(() => controller.RefreshProductionStatus());
                    if (refreshed.Success && refreshed.Production != null) response = refreshed;
                }
                catch (Exception)
                {
                    // The status list remains useful when an optional production refresh is unavailable.
                }
                Apply(response);
            }
            catch (Exception)
            {
                stateLabel.Text = TrayText.Pick("Agent 不可用 · 请确认服务正在运行", "Agent unavailable · confirm the service is running");
                productionStatus = null;
                devices.Items.Clear();
                ClearChallenge();
                UpdateActions();
            }
        }

        public void Apply(RegistryResponse response)
        {
            if (response == null) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action<RegistryResponse>(Apply), response);
                return;
            }
            productionStatus = response.Production ?? new ProductionStatus();
            var status = productionStatus;
            stateLabel.Text = status.Configured
                ? TrayText.Pick("账户 " + TrayDisplay.LocalizedAccountStatus(status) + " | Relay " + TrayDisplay.LocalizedRelayStatus(status.RelayState), "Account " + TrayDisplay.LocalizedAccountStatus(status) + " | Relay " + TrayDisplay.LocalizedRelayStatus(status.RelayState))
                : TrayText.Pick("此 PC 尚未配置生产账户。", "Production account is not configured on this PC.");
            accountValue.Text = TrayDisplay.LocalizedAccountStatus(status);
            serverValue.Text = TrayDisplay.StringOrDash(status.ServerHost);
            pcValue.Text = TrayDisplay.StringOrDash(status.PcName);
            relayValue.Text = TrayDisplay.LocalizedRelayStatus(status.RelayState);
            connectedValue.Text = TrayDisplay.DateTimeOrDash(status.LastConnectedAtUtc);

            devices.BeginUpdate();
            devices.Items.Clear();
            if (status.Devices != null)
            {
                foreach (var device in status.Devices)
                {
                    if (device == null) continue;
                    var item = new ListViewItem(TrayDisplay.StringOrDash(device.Name)) { Tag = device };
                    item.SubItems.Add(TrayDisplay.StringOrDash(device.Type));
                    item.SubItems.Add(DeviceAvailabilityLabel(device));
                    item.ToolTipText = device.DeviceId == Guid.Empty ? string.Empty : "Device " + device.DeviceId.ToString("D");
                    devices.Items.Add(item);
                }
            }
            devices.EndUpdate();
            FitDeviceColumns();
            if (status.PairingChallenge == null) ClearChallenge();
            else ApplyChallenge(status.PairingChallenge);
            UpdateActions();
        }

        private static Label CreateValueLabel()
        {
            return new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                AutoEllipsis = true,
                Padding = new Padding(2, 0, 8, 0)
            };
        }

        private static string DeviceAvailabilityLabel(ProductionDeviceSummary device)
        {
            if (device != null && string.Equals(device.TrustState, "REVOKED", StringComparison.OrdinalIgnoreCase))
                return TrayText.Pick("已停用", "Disabled");
            return TrayText.Pick("已登记", "Registered");
        }

        private void FitDeviceColumns()
        {
            if (devices.IsDisposed || devices.Columns.Count != 3) return;
            var rowHeight = devices.Items.Count == 0
                ? devices.Font.Height + 5
                : Math.Max(1, devices.GetItemRect(0).Height);
            var needsVerticalScroll = devices.Items.Count * rowHeight + devices.Font.Height + 10 > devices.ClientSize.Height;
            var available = devices.ClientSize.Width - (needsVerticalScroll ? SystemInformation.VerticalScrollBarWidth : 0) - 2;
            if (available <= 0) return;
            devices.Columns[0].Width = available / 2;
            devices.Columns[1].Width = available / 4;
            devices.Columns[2].Width = available - devices.Columns[0].Width - devices.Columns[1].Width;
        }

        private static TextBox CreateReadOnlyTextBox()
        {
            return new TextBox
            {
                ReadOnly = true,
                BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill,
                TabStop = false
            };
        }

        private static void AddDetail(TableLayoutPanel table, string name, Control value, int column, int row)
        {
            table.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left }, column, row);
            table.Controls.Add(value, column + 1, row);
        }

        private ProductionDeviceSummary SelectedDevice()
        {
            if (devices.SelectedItems.Count != 1) return null;
            return devices.SelectedItems[0].Tag as ProductionDeviceSummary;
        }

        private bool IsLocalPc(ProductionDeviceSummary device)
        {
            return device != null &&
                string.Equals(device.Type, "WINDOWS_PC", StringComparison.OrdinalIgnoreCase) &&
                productionStatus != null &&
                !string.IsNullOrWhiteSpace(productionStatus.PcName) &&
                string.Equals(device.Name, productionStatus.PcName, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateActions()
        {
            var selected = SelectedDevice();
            var pendingAndroid = selected != null &&
                string.Equals(selected.Type, "ANDROID", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(selected.TrustState, "PENDING", StringComparison.OrdinalIgnoreCase);
            var trustedAndroid = selected != null &&
                string.Equals(selected.Type, "ANDROID", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(selected.TrustState, "TRUSTED", StringComparison.OrdinalIgnoreCase);
            createChallengeButton.Enabled = pendingAndroid && pairingPermission.SelectedIndex >= 0 &&
                productionStatus != null && productionStatus.Configured && productionStatus.AccountLinked;
            grantButton.Enabled = trustedAndroid && pairingPermission.SelectedIndex >= 0 &&
                productionStatus != null && productionStatus.Configured && productionStatus.AccountLinked;
            revokeButton.Enabled = selected != null &&
                !IsLocalPc(selected) &&
                (string.Equals(selected.TrustState, "TRUSTED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(selected.TrustState, "PENDING", StringComparison.OrdinalIgnoreCase));
        }

        private async void CreateChallenge()
        {
            var selected = SelectedDevice();
            if (selected == null || !string.Equals(selected.Type, "ANDROID", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(selected.TrustState, "PENDING", StringComparison.OrdinalIgnoreCase)) return;
            var permission = pairingPermission.SelectedItem as string ?? "VIEW";
            var permissionDetail = permission == "MANAGE"
                ? TrayText.Pick("可查看、控制并管理此 PC 的设备授权", "view, control, and manage device access on this PC")
                : permission == "CONTROL"
                    ? TrayText.Pick("可查看并申请此 PC 的短时控制租约", "view and request short control leases on this PC")
                    : TrayText.Pick("只能查看此 PC 的终端", "view terminals on this PC only");
            var targetName = TrayDisplay.StringOrDash(selected.Name);
            if (MessageBox.Show(
                TrayText.Pick("为设备“" + targetName + "”授予 " + permission + "？\n\n" + permissionDetail + "。", "Grant " + permission + " to device '" + targetName + "'?\n\nIt may " + permissionDetail + "."),
                "TermLink",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                var response = await Task.Run(() => controller.CreatePairingChallenge(selected.DeviceId, permission));
                if (!response.Success) throw new InvalidOperationException(response.Code);
                Apply(response);
                stateLabel.Text = TrayText.Pick("已为 " + targetName + " 创建 " + permission + " 配对请求。", permission + " pairing request created for " + targetName + ".");
            }
            catch (Exception)
            {
                stateLabel.Text = TrayText.Pick("无法创建配对请求，请刷新后重试。", "Could not create the pairing request. Refresh and try again.");
            }
        }

        private async void GrantSelected()
        {
            var selected = SelectedDevice();
            if (selected == null || !string.Equals(selected.Type, "ANDROID", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(selected.TrustState, "TRUSTED", StringComparison.OrdinalIgnoreCase)) return;
            var permission = pairingPermission.SelectedItem as string ?? "VIEW";
            var permissionDetail = permission == "MANAGE"
                ? TrayText.Pick("可查看、控制并管理此 PC 的设备授权", "view, control, and manage device access on this PC")
                : permission == "CONTROL"
                    ? TrayText.Pick("可查看并申请此 PC 的短时控制租约", "view and request short control leases on this PC")
                    : TrayText.Pick("只能查看此 PC 的终端", "view terminals on this PC only");
            var targetName = TrayDisplay.StringOrDash(selected.Name);
            if (MessageBox.Show(
                TrayText.Pick("直接为已信任设备“" + targetName + "”授予 " + permission + "？\n\n" + permissionDetail + "。", "Grant " + permission + " directly to trusted device '" + targetName + "'?\n\nIt may " + permissionDetail + "."),
                "TermLink",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            try
            {
                var response = await Task.Run(() => controller.GrantDevice(selected.DeviceId, permission));
                if (!response.Success) throw new InvalidOperationException(response.Code);
                Apply(response);
                stateLabel.Text = TrayText.Pick("已为已信任设备“" + targetName + "”授予 " + permission + "。", permission + " granted directly to trusted device '" + targetName + "'.");
            }
            catch (Exception)
            {
                stateLabel.Text = TrayText.Pick("无法直接授权设备，请刷新后重试。", "Could not grant the trusted device. Refresh and try again.");
            }
        }

        private async void RevokeSelected()
        {
            var selected = SelectedDevice();
            if (selected == null || IsLocalPc(selected)) return;
            var name = TrayDisplay.StringOrDash(selected.Name);
            if (MessageBox.Show(
                TrayText.Pick("撤销设备“" + name + "”的信任？此设备的现有远程访问将停止。", "Revoke trust for device '" + name + "'? Existing remote access from this device will stop."),
                "TermLink",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            try
            {
                var response = await Task.Run(() => controller.RevokeDevice(selected.DeviceId));
                if (!response.Success) throw new InvalidOperationException(response.Code);
                Apply(response);
                stateLabel.Text = TrayText.Pick("已撤销设备：" + name + "。", "Device revoked: " + name + ".");
            }
            catch (Exception)
            {
                stateLabel.Text = TrayText.Pick("无法撤销设备，请刷新后重试。", "Could not revoke the device. Refresh and try again.");
            }
        }

        private void ApplyChallenge(PairingChallengeSummary challenge)
        {
            challengeCodeValue.Text = TrayDisplay.StringOrDash(challenge.Code);
            challengeId = challenge.ChallengeId == Guid.Empty ? string.Empty : challenge.ChallengeId.ToString("D");
            challengeNonce = challenge.ChallengeNonce ?? string.Empty;
            challengePermission = challenge.Permission ?? string.Empty;
            challengePermissionValue.Text = TrayDisplay.StringOrDash(challengePermission) +
                TrayText.Pick(" · 仅此 PC", " · this PC only");
            challengeFingerprint = challenge.TargetFingerprint ?? string.Empty;
            challengeFingerprintValue.Text = challengeFingerprint.Length > 28
                ? challengeFingerprint.Substring(0, 14) + "…" + challengeFingerprint.Substring(challengeFingerprint.Length - 12)
                : TrayDisplay.StringOrDash(challengeFingerprint);
            challengeFingerprintValue.AccessibleName = TrayText.Pick("目标设备密钥指纹 ", "Target device key fingerprint ") + challengeFingerprint;
            challengeExpiresValue.Text = TrayDisplay.DateTimeOrDash(challenge.ExpiresAtUtc);
            copyChallengeButton.Enabled = !string.IsNullOrWhiteSpace(challenge.Code) &&
                !string.IsNullOrWhiteSpace(challengeId) && !string.IsNullOrWhiteSpace(challengeNonce);
        }

        private void ClearChallenge()
        {
            challengeCodeValue.Text = string.Empty;
            challengeId = string.Empty;
            challengeNonce = string.Empty;
            challengePermission = string.Empty;
            challengeFingerprint = string.Empty;
            challengePermissionValue.Text = "-";
            challengeFingerprintValue.Text = "-";
            challengeExpiresValue.Text = "-";
            copyChallengeButton.Enabled = false;
        }

        private void CopyChallengeCode()
        {
            if (string.IsNullOrWhiteSpace(challengeCodeValue.Text) ||
                string.IsNullOrWhiteSpace(challengeId) || string.IsNullOrWhiteSpace(challengeNonce)) return;
            try
            {
                var payload = JsonSerializer.Serialize(new
                {
                    schema = "termlink.pairing.v1",
                    server_host = productionStatus == null ? string.Empty : productionStatus.ServerHost,
                    challenge_id = challengeId,
                    code = challengeCodeValue.Text,
                    challenge_nonce = challengeNonce,
                    permission = challengePermission,
                    target_fingerprint = challengeFingerprint
                });
                Clipboard.SetText(payload);
                stateLabel.Text = TrayText.Pick("配对信息已复制；仅发送给所选目标设备，五分钟后失效。", "Pairing details copied. Send them only to the selected target device; they expire in five minutes.");
            }
            catch (Exception)
            {
                stateLabel.Text = TrayText.Pick("无法复制配对信息，请重试。", "Could not copy the pairing details. Try again.");
            }
        }
    }
}
