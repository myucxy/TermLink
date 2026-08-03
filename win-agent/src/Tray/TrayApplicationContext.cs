using System;
using System.Drawing;
using System.Windows.Forms;
using TermLink.Agent;

namespace TermLink.Tray
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly AgentController controller;
        private readonly NotifyIcon notifyIcon;
        private readonly ToolStripMenuItem statusItem;
        private readonly ToolStripMenuItem sessionsItem;
        private readonly ToolStripMenuItem stopAllItem;
        private readonly ToolStripMenuItem enableItem;
        private readonly Timer refreshTimer;
        private SessionListForm sessionList;

        public TrayApplicationContext(AgentController controller)
        {
            this.controller = controller;
            statusItem = new ToolStripMenuItem("Connecting") { Enabled = false };
            sessionsItem = new ToolStripMenuItem("Open sessions", null, delegate { OpenSessions(); });
            stopAllItem = new ToolStripMenuItem("Stop remote access", null, delegate { StopAll(); });
            enableItem = new ToolStripMenuItem("Enable remote access", null, delegate { EnableRemoteAccess(); });
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("TermLink Agent") { Enabled = false });
            menu.Items.Add(statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(sessionsItem);
            menu.Items.Add(stopAllItem);
            menu.Items.Add(enableItem);
            menu.Items.Add(new ToolStripMenuItem("Restart agent", null, delegate { RestartAgent(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("Exit tray", null, delegate { ExitTray(); }));
            menu.Opening += delegate { RefreshState(false); };

            notifyIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                Text = "TermLink Agent",
                ContextMenuStrip = menu,
                Visible = true
            };
            notifyIcon.DoubleClick += delegate { OpenSessions(); };
            refreshTimer = new Timer { Interval = 3000 };
            refreshTimer.Tick += delegate { RefreshState(false); };
            refreshTimer.Start();
            RefreshState(true);
        }

        private void RefreshState(bool showFailure)
        {
            try
            {
                var response = controller.EnsureAgent();
                var count = response.Summaries == null ? 0 : response.Summaries.Count;
                statusItem.Text = response.RemoteAccessEnabled
                    ? "Remote access on - " + count + " sessions"
                    : "Remote access off";
                sessionsItem.Enabled = true;
                stopAllItem.Enabled = response.RemoteAccessEnabled;
                enableItem.Enabled = !response.RemoteAccessEnabled;
                notifyIcon.Text = response.RemoteAccessEnabled
                    ? "TermLink Agent - remote access on"
                    : "TermLink Agent - remote access off";
                if (sessionList != null && !sessionList.IsDisposed) sessionList.Apply(response);
            }
            catch (Exception error)
            {
                statusItem.Text = "Agent unavailable";
                sessionsItem.Enabled = false;
                stopAllItem.Enabled = false;
                enableItem.Enabled = false;
                if (showFailure) ShowFailure(error.Message);
            }
        }

        private void OpenSessions()
        {
            if (sessionList == null || sessionList.IsDisposed)
            {
                sessionList = new SessionListForm(controller);
            }
            sessionList.Show();
            sessionList.Activate();
            sessionList.RefreshSessions();
        }

        private void StopAll()
        {
            if (MessageBox.Show(
                "Stop all remote access? Local shells will keep running.",
                "TermLink",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
            {
                return;
            }
            try
            {
                var response = controller.Send(RegistryCommand.StopAllRemoteAccess);
                if (!response.Success) throw new InvalidOperationException(response.Code);
                RefreshState(false);
            }
            catch (Exception error)
            {
                ShowFailure(error.Message);
            }
        }

        private void EnableRemoteAccess()
        {
            try
            {
                var response = controller.Send(RegistryCommand.EnableRemoteAccess);
                if (!response.Success) throw new InvalidOperationException(response.Code);
                RefreshState(false);
            }
            catch (Exception error)
            {
                ShowFailure(error.Message);
            }
        }

        private void RestartAgent()
        {
            try
            {
                var response = controller.RestartAgent();
                if (!response.Success) throw new InvalidOperationException(response.Code);
                RefreshState(false);
            }
            catch (Exception error)
            {
                ShowFailure(error.Message);
            }
        }

        private void ShowFailure(string message)
        {
            notifyIcon.ShowBalloonTip(4000, "TermLink Agent", message, ToolTipIcon.Error);
        }

        private void ExitTray()
        {
            refreshTimer.Stop();
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            if (sessionList != null && !sessionList.IsDisposed) sessionList.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Dispose();
                notifyIcon.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class SessionListForm : Form
    {
        private readonly AgentController controller;
        private readonly Label stateLabel;
        private readonly ListView sessions;
        private readonly Button stopButton;

        public SessionListForm(AgentController controller)
        {
            this.controller = controller;
            Text = "TermLink sessions";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(760, 380);
            Size = new Size(900, 480);

            stateLabel = new Label
            {
                AutoSize = true,
                Dock = DockStyle.Top,
                Padding = new Padding(12, 12, 12, 8),
                Text = "Loading"
            };
            sessions = new ListView
            {
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                View = View.Details
            };
            sessions.Columns.Add("Session", 245);
            sessions.Columns.Add("Shell", 140);
            sessions.Columns.Add("State", 120);
            sessions.Columns.Add("Host PID", 90);
            sessions.Columns.Add("Last seq", 90);
            sessions.SelectedIndexChanged += delegate { stopButton.Enabled = sessions.SelectedItems.Count == 1; };

            var refreshButton = new Button { Text = "Refresh", AutoSize = true };
            refreshButton.Click += delegate { RefreshSessions(); };
            stopButton = new Button { Text = "Stop selected remote access", AutoSize = true, Enabled = false };
            stopButton.Click += delegate { StopSelected(); };
            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            actions.Controls.Add(stopButton);
            actions.Controls.Add(refreshButton);
            Controls.Add(sessions);
            Controls.Add(actions);
            Controls.Add(stateLabel);
        }

        public void RefreshSessions()
        {
            try
            {
                Apply(controller.EnsureAgent());
            }
            catch (Exception error)
            {
                stateLabel.Text = "Agent unavailable: " + error.Message;
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
            stateLabel.Text = response.RemoteAccessEnabled ? "Remote access enabled" : "Remote access disabled";
            sessions.BeginUpdate();
            sessions.Items.Clear();
            if (response.Summaries != null)
            {
                foreach (var summary in response.Summaries)
                {
                    var item = new ListViewItem(summary.SessionId.ToString("D")) { Tag = summary.SessionId };
                    item.SubItems.Add(summary.ShellType.ToString());
                    item.SubItems.Add(summary.Lifecycle.ToString());
                    item.SubItems.Add(summary.HostProcessId.ToString());
                    item.SubItems.Add(summary.LastSequence.ToString());
                    sessions.Items.Add(item);
                }
            }
            sessions.EndUpdate();
            stopButton.Enabled = false;
        }

        private void StopSelected()
        {
            if (sessions.SelectedItems.Count != 1) return;
            var sessionId = (Guid)sessions.SelectedItems[0].Tag;
            if (MessageBox.Show(
                "Stop remote access for this session? The local shell will keep running.",
                "TermLink",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK)
            {
                return;
            }
            try
            {
                var response = controller.StopSession(sessionId);
                if (!response.Success) throw new InvalidOperationException(response.Code);
                RefreshSessions();
            }
            catch (Exception error)
            {
                MessageBox.Show(error.Message, "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
