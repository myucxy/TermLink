using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TermLink.Setup
{
    internal enum InstallCommitGateState
    {
        Cancellable = 0,
        CancellationRequested = 1,
        CommitStarted = 2
    }

    internal sealed class InstallCommitGate
    {
        private int state;

        internal InstallCommitGateState State =>
            (InstallCommitGateState)Volatile.Read(ref state);

        internal bool TryRequestCancellation() =>
            Interlocked.CompareExchange(
                ref state,
                (int)InstallCommitGateState.CancellationRequested,
                (int)InstallCommitGateState.Cancellable) ==
            (int)InstallCommitGateState.Cancellable;

        internal bool TryBeginCommit() =>
            Interlocked.CompareExchange(
                ref state,
                (int)InstallCommitGateState.CommitStarted,
                (int)InstallCommitGateState.Cancellable) ==
            (int)InstallCommitGateState.Cancellable;
    }

    internal sealed class SetupForm : Form
    {
        private static readonly Size LogicalClientSize = new Size(560, 235);
        private readonly TextBox installDirectory;
        private readonly CheckBox startup;
        private readonly Button browse;
        private readonly Button install;
        private readonly Button cancel;
        private readonly Label status;
        private readonly SetupPrerequisiteResult prerequisites;
        private CancellationTokenSource installCancellation;
        private InstallCommitGate installCommitGate;
        private bool closeWhenFinished;
        private bool committedInstallIsFinishing;
        private readonly Dictionary<Control, Rectangle> logicalBounds = new Dictionary<Control, Rectangle>();

        internal SetupForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            var previousInstallDirectory = Installer.DiscoverPreviousInstallDirectory();
            var hasPreviousInstall = Installer.IsValidInstallDirectory(previousInstallDirectory);
            prerequisites = SetupPrerequisites.EvaluateCurrent();

            Text = SetupText.Pick("TermLink 安装程序", "TermLink Setup");
            Icon = LoadIcon();
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 235);

            var title = new Label
            {
                AutoSize = true,
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(18, 18),
                Text = SetupText.Pick("安装 TermLink Windows Agent", "Install TermLink Windows Agent")
            };
            Controls.Add(title);

            var label = new Label
            {
                AutoSize = true,
                Location = new Point(18, 60),
                Text = SetupText.Pick("安装目录：", "Install folder:")
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

            browse = new Button
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Location = new Point(462, 54),
                Size = new Size(80, 27),
                Text = SetupText.Pick("浏览...", "Browse...")
            };
            browse.Click += BrowseClick;
            Controls.Add(browse);

            startup = new CheckBox
            {
                AutoSize = true,
                Location = new Point(88, 92),
                Text = SetupText.Pick("登录 Windows 时自动启动 TermLink", "Start TermLink when I sign in to Windows"),
                Checked = true
            };
            Controls.Add(startup);

            status = new Label
            {
                AutoSize = false,
                Location = new Point(18, 127),
                Size = new Size(524, 60),
                Text = !prerequisites.Supported
                    ? SetupText.Pick("无法安装：", "Cannot install: ") + string.Join(" ", prerequisites.Errors)
                    : !prerequisites.WindowsTerminalInstalled
                        ? string.Join(" ", prerequisites.Warnings)
                        : hasPreviousInstall
                            ? SetupText.Pick(
                                "检测到已有安装，将在上次目录原地升级；如需更换目录，请先卸载现有版本。",
                                "An existing installation was found and will be upgraded in place. Uninstall it first to change folders.")
                            : SetupText.Pick(
                                "检测到 Windows Terminal；安装器将配置独立的 TermLink Profile Fragment。",
                                "Windows Terminal was found. Setup will configure a separate TermLink profile fragment.")
            };
            Controls.Add(status);

            install = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.None,
                Location = new Point(382, 196),
                Size = new Size(78, 27),
                Text = SetupText.Pick("安装", "Install"),
                Enabled = prerequisites.Supported
            };
            install.Click += InstallClick;
            Controls.Add(install);

            cancel = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.None,
                Location = new Point(464, 196),
                Size = new Size(78, 27),
                Text = SetupText.Pick("取消", "Cancel")
            };
            cancel.Click += CancelClick;
            Controls.Add(cancel);
            AcceptButton = install;
            FormClosing += SetupFormClosing;
            foreach (Control child in Controls) logicalBounds[child] = child.Bounds;
            ResumeLayout(false);
            PerformLayout();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            SuspendLayout();
            ClientSize = new Size(Scale96(LogicalClientSize.Width), Scale96(LogicalClientSize.Height));
            foreach (var pair in logicalBounds)
            {
                pair.Key.Bounds = new Rectangle(
                    Scale96(pair.Value.X),
                    Scale96(pair.Value.Y),
                    Scale96(pair.Value.Width),
                    Scale96(pair.Value.Height));
            }
            ResumeLayout(false);
            PerformLayout();
        }

        private int Scale96(int logicalPixels) => (int)Math.Round(
            logicalPixels * (double)(DeviceDpi <= 0 ? 96 : DeviceDpi) / 96,
            MidpointRounding.AwayFromZero);

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                CancelClick(this, EventArgs.Empty);
                return true;
            }
            return base.ProcessCmdKey(ref message, keyData);
        }

        private void BrowseClick(object sender, EventArgs args)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = SetupText.Pick("选择 TermLink 安装目录", "Choose the TermLink install folder"),
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
            if (installCancellation != null) return;
            var target = installDirectory.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(
                    this,
                    SetupText.Pick("请输入安装目录。", "Enter an install folder."),
                    "TermLink",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            CancellationTokenSource cancellation = null;
            InstallCommitGate commitGate = null;
            var cancelled = false;
            var committed = false;
            var closeSilently = false;
            string completionMessage = null;
            try
            {
                // Fail before process discovery/termination so choosing a new
                // directory cannot disturb a working installation in the old
                // directory.
                Installer.EnsureInstallDirectoryCompatible(target);
                var isUpgrade = Installer.IsValidInstallDirectory(target);
                var runningProcesses = Installer.GetRunningOwnedProcesses(target);
                var runningServices = Installer.GetRunningOwnedServices(target);
                var terminateRunningProcesses = false;
                if (isUpgrade)
                {
                    var decision = MessageBox.Show(
                        this,
                        Installer.FormatUpgradeTerminationPrompt(runningProcesses, runningServices),
                        SetupText.Pick("TermLink 升级", "TermLink Upgrade"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (decision != DialogResult.Yes)
                    {
                        status.Text = SetupText.Pick(
                            "升级已取消；现有 TermLink 程序未被终止。",
                            "Upgrade cancelled. The existing TermLink processes were not stopped.");
                        return;
                    }
                    terminateRunningProcesses = true;
                }

                var createStartup = startup.Checked;
                closeWhenFinished = false;
                cancellation = new CancellationTokenSource();
                commitGate = new InstallCommitGate();
                installCancellation = cancellation;
                installCommitGate = commitGate;
                SetInstallInputsEnabled(false);
                status.Text = SetupText.Pick("正在安装，请稍候...", "Installing. Please wait...");
                var outcome = await Task.Run(() => Installer.Install(
                    target,
                    createStartup,
                    terminateRunningProcesses,
                    cancellation.Token,
                    commitGate.TryBeginCommit));

                // A successful return is the install transaction's commit
                // boundary. Cancellation requested after its final safe check
                // must not be reported as a rollback: the new version is already
                // authoritative at this point.
                committed = true;
                committedInstallIsFinishing = true;
                if (ReferenceEquals(installCancellation, cancellation))
                    installCancellation = null;
                cancel.Enabled = false;
                cancel.Text = SetupText.Pick("关闭", "Close");
                status.Text = SetupText.Pick(
                    "安装完成，正在启动 TermLink...",
                    "Installation complete. Starting TermLink...");
                var trayRestarted = await Task.Run(() => Installer.TryLaunchTray(target));
                committedInstallIsFinishing = false;
                cancel.Enabled = true;
                if (!trayRestarted)
                {
                    status.Text = SetupText.Pick(
                        "安装完成，但主程序启动失败。",
                        "Installation completed, but the app did not start.");
                    if (closeWhenFinished)
                    {
                        closeSilently = true;
                    }
                    else
                    {
                        MessageBox.Show(
                            this,
                            SetupText.Pick(
                                "TermLink 文件已完成安装，但主程序未通过本地控制管道健康检查。" +
                                Environment.NewLine +
                                "请从开始菜单重试；若仍失败，请查看本机 TermLink 日志。",
                                "TermLink files were installed, but the app did not pass its local control-pipe health check." +
                                Environment.NewLine +
                                "Try again from the Start menu. If it still fails, check the local TermLink logs."),
                            SetupText.Pick("TermLink 启动失败", "TermLink Startup Failed"),
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }
                }
                else if (closeWhenFinished)
                {
                    closeSilently = true;
                }
                else
                {
                    status.Text = SetupText.Pick("安装完成。", "Installation complete.");
                    completionMessage = outcome.TerminalConfigured
                        ? SetupText.Pick(
                            "TermLink 安装完成。重新打开 Windows Terminal 后即可看到 TermLink Profile。",
                            "TermLink installation is complete. Reopen Windows Terminal to see the TermLink profile.")
                        : outcome.TerminalMessage +
                            (string.IsNullOrWhiteSpace(outcome.TerminalDiagnosticLogPath)
                                ? string.Empty
                                : Environment.NewLine + Environment.NewLine +
                                    SetupText.DiagnosticRecorded());
                    completionMessage += Environment.NewLine + Environment.NewLine +
                        SetupText.Pick(
                            "TermLink 已使用新版本重新启动。",
                            "TermLink restarted with the new version.");
                }
            }
            catch (OperationCanceledException) when (
                !committed && cancellation != null && cancellation.IsCancellationRequested)
            {
                cancelled = true;
                status.Text = SetupText.Pick("安装已取消。", "Installation cancelled.");
                if (Installer.IsValidInstallDirectory(target))
                {
                    if (Installer.IsOwnedTrayRunning(target))
                    {
                        status.Text = SetupText.Pick(
                            "安装已取消，现有 TermLink 仍在运行。",
                            "Installation cancelled. The existing TermLink version is still running.");
                    }
                    else
                    {
                        status.Text = SetupText.Pick(
                            "安装已取消，正在恢复现有 TermLink...",
                            "Installation cancelled. Restoring the existing TermLink version...");
                        var restoredProcess = await Task.Run(() => Installer.TryLaunchTray(target));
                        status.Text = restoredProcess
                            ? SetupText.Pick(
                                "安装已取消，现有版本已恢复运行。",
                                "Installation cancelled. The existing version is running again.")
                            : SetupText.Pick(
                                "安装已取消，现有文件已恢复，但主程序未能自动启动。",
                                "Installation cancelled. Existing files were restored, but the app could not be started automatically.");
                    }
                }
            }
            catch (Exception error)
            {
                install.Enabled = true;
                status.Text = SetupText.Pick("安装失败。", "Installation failed.");
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
                    SetupText.PublicError(error) + Environment.NewLine + Environment.NewLine +
                        SetupText.DiagnosticRecorded(),
                    SetupText.Pick("TermLink 安装失败", "TermLink Installation Failed"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                if (ReferenceEquals(installCancellation, cancellation))
                    installCancellation = null;
                if (ReferenceEquals(installCommitGate, commitGate))
                    installCommitGate = null;
                committedInstallIsFinishing = false;
                cancellation?.Dispose();
                SetInstallInputsEnabled(true);
                cancel.Enabled = true;
            }

            if (closeSilently)
            {
                Close();
                return;
            }
            if (cancelled)
            {
                if (closeWhenFinished) Close();
                return;
            }
            if (!string.IsNullOrWhiteSpace(completionMessage))
            {
                MessageBox.Show(this, completionMessage,
                    "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
        }

        private void CancelClick(object sender, EventArgs args)
        {
            if (committedInstallIsFinishing)
            {
                closeWhenFinished = true;
                cancel.Enabled = false;
                status.Text = SetupText.Pick(
                    "安装已提交，完成启动检查后将关闭。",
                    "Installation is committed. Setup will close after the startup check.");
                return;
            }
            var cancellation = installCancellation;
            if (cancellation == null)
            {
                Close();
                return;
            }

            closeWhenFinished = true;
            RequestInstallCancellation(cancellation, installCommitGate);
        }

        private void SetupFormClosing(object sender, FormClosingEventArgs args)
        {
            if (committedInstallIsFinishing)
            {
                args.Cancel = true;
                closeWhenFinished = true;
                cancel.Enabled = false;
                status.Text = SetupText.Pick(
                    "安装已提交，完成启动检查后将关闭。",
                    "Installation is committed. Setup will close after the startup check.");
                return;
            }
            var cancellation = installCancellation;
            if (cancellation == null) return;
            args.Cancel = true;
            closeWhenFinished = true;
            RequestInstallCancellation(cancellation, installCommitGate);
        }

        private void RequestInstallCancellation(
            CancellationTokenSource cancellation,
            InstallCommitGate commitGate)
        {
            if (cancellation.IsCancellationRequested) return;
            if (commitGate != null && !commitGate.TryRequestCancellation())
            {
                if (commitGate.State == InstallCommitGateState.CommitStarted)
                {
                    cancel.Enabled = false;
                    status.Text = SetupText.Pick(
                        "安装已提交，完成启动检查后将关闭。",
                        "Installation is committed. Setup will close after the startup check.");
                }
                return;
            }
            cancel.Enabled = false;
            status.Text = SetupText.Pick(
                "正在安全取消并回滚，请稍候...",
                "Cancelling safely and rolling back. Please wait...");
            cancellation.Cancel();
        }

        private void SetInstallInputsEnabled(bool enabled)
        {
            installDirectory.Enabled = enabled;
            browse.Enabled = enabled;
            startup.Enabled = enabled;
            install.Enabled = enabled && prerequisites.Supported;
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
