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

            Text = "TermLink 安装程序";
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

            browse = new Button
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
                Size = new Size(524, 60),
                Text = !prerequisites.Supported
                    ? "无法安装：" + string.Join(" ", prerequisites.Errors)
                    : !prerequisites.WindowsTerminalInstalled
                        ? string.Join(" ", prerequisites.Warnings)
                        : hasPreviousInstall
                            ? "检测到已有安装，将在上次目录原地升级；如需更换目录，请先卸载现有版本。"
                            : "检测到 Windows Terminal；安装器将配置独立的 TermLink Profile Fragment。"
            };
            Controls.Add(status);

            install = new Button
            {
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
                DialogResult = DialogResult.None,
                Location = new Point(382, 196),
                Size = new Size(78, 27),
                Text = "安装",
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
                Text = "取消"
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
            if (installCancellation != null) return;
            var target = installDirectory.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(this, "请输入安装目录。", "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                var runningProcesses = Installer.GetRunningOwnedProcesses(target);
                var runningServices = Installer.GetRunningOwnedServices(target);
                var terminateRunningProcesses = false;
                if (runningProcesses.Count > 0 || runningServices.Count > 0)
                {
                    var decision = MessageBox.Show(
                        this,
                        "检测到正在使用当前 TermLink 安装目录的程序：" + Environment.NewLine +
                        Installer.FormatRunningProcessList(runningProcesses) + Environment.NewLine + Environment.NewLine +
                        "Windows 服务：" + Environment.NewLine +
                        Installer.FormatRunningServiceList(runningServices) + Environment.NewLine + Environment.NewLine +
                        "升级需要先终止这些程序及其子进程；活动终端和未保存的工作可能丢失。" +
                        "是否终止并继续升级？" + Environment.NewLine +
                        "Active terminals and unsaved work may be lost. Stop these processes and continue?",
                        "TermLink 升级 / Upgrade",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    if (decision != DialogResult.Yes)
                    {
                        status.Text = "升级已取消；现有 TermLink 程序未被终止。";
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
                status.Text = "正在安装，请稍候...";
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
                cancel.Text = "关闭";
                status.Text = "安装完成，正在启动 TermLink...";
                var trayRestarted = await Task.Run(() => Installer.TryLaunchTray(target));
                committedInstallIsFinishing = false;
                cancel.Enabled = true;
                if (!trayRestarted)
                {
                    status.Text = "安装完成，但主程序启动失败。";
                    if (closeWhenFinished)
                    {
                        closeSilently = true;
                    }
                    else
                    {
                        MessageBox.Show(
                            this,
                            "TermLink 文件已完成安装，但主程序未通过本地控制管道健康检查。" +
                            Environment.NewLine +
                            "请从开始菜单重试；若仍失败，请查看 %LOCALAPPDATA%\\TermLink\\logs\\TermLink-startup.log。",
                            "TermLink 启动失败 / Startup failed",
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
                    status.Text = "安装完成。";
                    completionMessage = outcome.TerminalConfigured
                        ? "TermLink 安装完成。重新打开 Windows Terminal 后即可看到 TermLink Profile。"
                        : outcome.TerminalMessage +
                            (string.IsNullOrWhiteSpace(outcome.TerminalDiagnosticLogPath)
                                ? string.Empty
                                : Environment.NewLine + Environment.NewLine +
                                    "诊断日志: " + outcome.TerminalDiagnosticLogPath);
                    completionMessage += Environment.NewLine + Environment.NewLine +
                        "TermLink 已使用新版本重新启动。";
                }
            }
            catch (OperationCanceledException) when (
                !committed && cancellation != null && cancellation.IsCancellationRequested)
            {
                cancelled = true;
                status.Text = "安装已取消。";
                if (Installer.IsValidInstallDirectory(target))
                {
                    if (Installer.IsOwnedTrayRunning(target))
                    {
                        status.Text = "安装已取消，现有 TermLink 仍在运行。";
                    }
                    else
                    {
                        status.Text = "安装已取消，正在恢复现有 TermLink...";
                        var restoredProcess = await Task.Run(() => Installer.TryLaunchTray(target));
                        status.Text = restoredProcess
                            ? "安装已取消，现有版本已恢复运行。"
                            : "安装已取消，现有文件已恢复，但主程序未能自动启动。";
                    }
                }
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
                status.Text = "安装已提交，完成启动检查后将关闭。";
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
                status.Text = "安装已提交，完成启动检查后将关闭。";
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
                    status.Text = "安装已提交，完成启动检查后将关闭。";
                }
                return;
            }
            cancel.Enabled = false;
            status.Text = "正在安全取消并回滚，请稍候...";
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
