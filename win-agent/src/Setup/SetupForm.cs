using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TermLink.Setup
{
    internal sealed class SetupForm : Form
    {
        private readonly TextBox installDirectory;
        private readonly CheckBox startup;
        private readonly Button install;
        private readonly Label status;

        internal SetupForm()
        {
            var previousInstallDirectory = Installer.DiscoverPreviousInstallDirectory();
            var hasPreviousInstall = Installer.IsValidInstallDirectory(previousInstallDirectory);

            Text = "TermLink 安装程序";
            Icon = LoadIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 205);

            var title = new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(18, 18),
                Text = "安装 TermLink Windows Agent"
            };
            Controls.Add(title);

            var label = new Label
            {
                AutoSize = true,
                Location = new Point(18, 60),
                Text = "安装目录："
            };
            Controls.Add(label);

            installDirectory = new TextBox
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                Location = new Point(88, 56),
                Size = new Size(365, 23),
                Text = previousInstallDirectory
            };
            Controls.Add(installDirectory);

            var browse = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(462, 54),
                Size = new Size(80, 27),
                Text = "浏览..."
            };
            browse.Click += BrowseClick;
            Controls.Add(browse);

            startup = new CheckBox
            {
                AutoSize = true,
                Location = new Point(88, 92),
                Text = "登录 Windows 时自动启动 TermLink",
                Checked = true
            };
            Controls.Add(startup);

            status = new Label
            {
                AutoSize = false,
                Location = new Point(18, 127),
                Size = new Size(524, 34),
                Text = hasPreviousInstall
                    ? "检测到已有安装，将在上次目录原地升级；也可点击“浏览...”更换目录。"
                    : "安装器会自动配置 Windows Terminal Profile。"
            };
            Controls.Add(status);

            install = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.None,
                Location = new Point(382, 166),
                Size = new Size(78, 27),
                Text = "安装"
            };
            install.Click += InstallClick;
            Controls.Add(install);

            var cancel = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.Cancel,
                Location = new Point(464, 166),
                Size = new Size(78, 27),
                Text = "取消"
            };
            Controls.Add(cancel);
            AcceptButton = install;
            CancelButton = cancel;
        }

        private void BrowseClick(object sender, EventArgs args)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "选择 TermLink 安装目录",
                SelectedPath = installDirectory.Text
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    installDirectory.Text = Path.Combine(dialog.SelectedPath, "TermLink");
                }
            }
        }

        private async void InstallClick(object sender, EventArgs args)
        {
            var target = installDirectory.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(this, "请输入安装目录。", "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            install.Enabled = false;
            status.Text = "正在安装，请稍候...";
            try
            {
                await Task.Run(() => Installer.Install(target, startup.Checked));
                status.Text = "安装完成。";
                MessageBox.Show(this, "TermLink 安装完成。重新打开 Windows Terminal 后即可看到 TermLink Profile。",
                    "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception error)
            {
                install.Enabled = true;
                status.Text = "安装失败。";
                var errorLogPath = Path.Combine(Path.GetTempPath(), "TermLink-Setup-error.log");
                try
                {
                    File.AppendAllText(
                        errorLogPath,
                        DateTime.UtcNow.ToString("O") + Environment.NewLine + error + Environment.NewLine);
                }
                catch
                {
                    // Error reporting must not mask the original installer failure.
                }
                MessageBox.Show(
                    this,
                    error.Message + Environment.NewLine + Environment.NewLine + "详细日志: " + errorLogPath,
                    "TermLink 安装失败",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        private static Icon LoadIcon()
        {
            try
            {
                var stream = typeof(SetupForm).Assembly.GetManifestResourceStream("TermLink.Setup.Assets.TermLink.ico");
                if (stream != null) return new Icon(stream);
            }
            catch
            {
                // The executable icon still comes from the project ApplicationIcon.
            }
            return SystemIcons.Application;
        }
    }
}
