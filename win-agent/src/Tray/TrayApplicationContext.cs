using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using TermLink.Agent;

namespace TermLink.Tray
{
    internal sealed class TrayApplicationContext : ApplicationContext
    {
        private readonly AgentController controller;
        private readonly NotifyIcon notifyIcon;
        private readonly ToolStripMenuItem statusItem;
        private readonly ToolStripMenuItem accountItem;
        private readonly ToolStripMenuItem relayItem;
        private readonly ToolStripMenuItem accountDevicesItem;
        private readonly ToolStripMenuItem linkAccountItem;
        private readonly ToolStripMenuItem sessionsItem;
        private readonly ToolStripMenuItem stopAllItem;
        private readonly ToolStripMenuItem enableItem;
        private readonly Timer refreshTimer;
        private SessionListForm sessionList;

        public TrayApplicationContext(AgentController controller)
        {
            this.controller = controller;
            statusItem = new ToolStripMenuItem("Connecting") { Enabled = false };
            accountItem = new ToolStripMenuItem("Account: checking") { Enabled = false };
            relayItem = new ToolStripMenuItem("Relay: checking") { Enabled = false };
            accountDevicesItem = new ToolStripMenuItem("Account and devices", null, delegate { OpenAccountDevices(); });
            linkAccountItem = new ToolStripMenuItem("Link account...", null, async delegate { await LinkAccountAsync(); });
            sessionsItem = new ToolStripMenuItem("Open sessions", null, delegate { OpenSessions(); });
            stopAllItem = new ToolStripMenuItem("Stop remote access", null, delegate { StopAll(); });
            enableItem = new ToolStripMenuItem("Enable remote access", null, delegate { EnableRemoteAccess(); });
            var menu = new ContextMenuStrip();
            menu.Items.Add(new ToolStripMenuItem("TermLink Agent") { Enabled = false });
            menu.Items.Add(statusItem);
            menu.Items.Add(accountItem);
            menu.Items.Add(relayItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(accountDevicesItem);
            menu.Items.Add(linkAccountItem);
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
                var production = response.Production ?? new ProductionStatus();
                statusItem.Text = response.RemoteAccessEnabled
                    ? "Remote access on - " + count + " sessions"
                    : "Remote access off";
                accountItem.Text = "Account: " + TrayDisplay.AccountStatus(production);
                relayItem.Text = "Relay: " + TrayDisplay.RelayStatus(production.RelayState);
                accountDevicesItem.Enabled = true;
                sessionsItem.Enabled = true;
                stopAllItem.Enabled = response.RemoteAccessEnabled;
                enableItem.Enabled = !response.RemoteAccessEnabled;
                notifyIcon.Text = response.RemoteAccessEnabled
                    ? "TermLink Agent - remote access on"
                    : "TermLink Agent - remote access off";
                if (sessionList != null && !sessionList.IsDisposed) sessionList.Apply(response);
                if (accountDevices != null && !accountDevices.IsDisposed) accountDevices.Apply(response);
            }
            catch (Exception error)
            {
                statusItem.Text = "Agent unavailable";
                accountItem.Text = "Account: unavailable";
                relayItem.Text = "Relay: unavailable";
                accountDevicesItem.Enabled = false;
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

        private AccountDevicesForm accountDevices;

        private void OpenAccountDevices()
        {
            if (accountDevices == null || accountDevices.IsDisposed)
            {
                accountDevices = new AccountDevicesForm(controller);
            }
            accountDevices.Show();
            accountDevices.Activate();
            accountDevices.RefreshStatus();
        }

        private async Task LinkAccountAsync()
        {
            using (var dialog = new AccountLinkForm())
            {
                if (dialog.ShowDialog() != DialogResult.OK) return;
                linkAccountItem.Enabled = false;
                try
                {
                    await Task.Run(() => controller.LinkAccount(dialog.ServerUrl, dialog.PcName));
                    var response = controller.RestartAgent();
                    if (!response.Success) throw new InvalidOperationException(response.Code);
                    notifyIcon.ShowBalloonTip(4000, "TermLink Agent", "Account linked.", ToolTipIcon.Info);
                    RefreshState(false);
                }
                catch (Exception error)
                {
                    ShowFailure(error.Message);
                }
                finally { linkAccountItem.Enabled = true; }
            }
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
            if (accountDevices != null && !accountDevices.IsDisposed) accountDevices.Close();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                refreshTimer.Dispose();
                notifyIcon.Dispose();
                if (sessionList != null && !sessionList.IsDisposed) sessionList.Dispose();
                if (accountDevices != null && !accountDevices.IsDisposed) accountDevices.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class AccountLinkForm : Form
    {
        private readonly TextBox server;
        private readonly TextBox pcName;

        public AccountLinkForm()
        {
            Text = "Link TermLink account";
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(500, 190);

            var explanation = new Label
            {
                AutoSize = false,
                Location = new Point(16, 14),
                Size = new Size(468, 42),
                Text = "TermLink will open your system browser and wait for a secure Authorization Code + PKCE callback."
            };
            var serverLabel = new Label { AutoSize = true, Location = new Point(16, 67), Text = "Server HTTPS URL" };
            server = new TextBox
            {
                Location = new Point(150, 64),
                Size = new Size(334, 23),
                Text = "https://termlink.kget.cc:8910"
            };
            var nameLabel = new Label { AutoSize = true, Location = new Point(16, 103), Text = "PC name" };
            pcName = new TextBox
            {
                Location = new Point(150, 100),
                Size = new Size(334, 23),
                Text = Environment.MachineName
            };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(328, 145), Size = new Size(75, 28) };
            var link = new Button { Text = "Open browser", DialogResult = DialogResult.OK, Location = new Point(409, 145), Size = new Size(75, 28) };
            Controls.AddRange(new Control[] { explanation, serverLabel, server, nameLabel, pcName, cancel, link });
            AcceptButton = link;
            CancelButton = cancel;
        }

        public string ServerUrl { get { return server.Text.Trim(); } }
        public string PcName { get { return pcName.Text.Trim(); } }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (DialogResult == DialogResult.OK &&
                (!Uri.TryCreate(ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    string.IsNullOrWhiteSpace(PcName)))
            {
                MessageBox.Show("Enter a valid HTTPS server URL and PC name.", "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }
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

    internal sealed class SessionListForm : Form
    {
        private readonly AgentController controller;
        private readonly Label stateLabel;
        private readonly ListView sessions;
        private readonly Button stopButton;
        private ProductionStatus productionStatus;

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
            sessions.Columns.Add("Viewers", 70);
            sessions.Columns.Add("Controller", 220);
            sessions.Columns.Add("Lease expiry", 160);
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
            productionStatus = response.Production;
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
                    var productionSession = FindProductionSession(summary.SessionId);
                    item.SubItems.Add(productionSession == null ? "0" : productionSession.ViewerCount.ToString());
                    item.SubItems.Add(
                        productionSession == null || !productionSession.ControllerDeviceId.HasValue
                            ? "-"
                            : productionSession.ControllerDeviceId.Value.ToString("D"));
                    item.SubItems.Add(
                        productionSession == null
                            ? "-"
                            : TrayDisplay.DateTimeOrDash(productionSession.LeaseExpiresAtUtc));
                    sessions.Items.Add(item);
                }
            }
            sessions.EndUpdate();
            stopButton.Enabled = false;
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
        private readonly Button revokeButton;
        private readonly TextBox challengeCodeValue;
        private readonly TextBox challengeIdValue;
        private readonly TextBox challengeNonceValue;
        private readonly Label challengeExpiresValue;
        private readonly Button copyChallengeButton;
        private ProductionStatus productionStatus;

        public AccountDevicesForm(AgentController controller)
        {
            this.controller = controller;
            Text = "TermLink account and devices";
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(780, 520);
            Size = new Size(920, 620);

            stateLabel = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Top,
                Height = 32,
                Padding = new Padding(12, 8, 12, 4),
                Text = "Loading"
            };

            accountValue = CreateValueLabel();
            serverValue = CreateValueLabel();
            pcValue = CreateValueLabel();
            relayValue = CreateValueLabel();
            connectedValue = CreateValueLabel();
            var details = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 132,
                ColumnCount = 4,
                RowCount = 3,
                Padding = new Padding(12, 2, 12, 8)
            };
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 78));
            details.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            AddDetail(details, "Account", accountValue, 0, 0);
            AddDetail(details, "Server", serverValue, 2, 0);
            AddDetail(details, "PC", pcValue, 0, 1);
            AddDetail(details, "Relay", relayValue, 2, 1);
            details.Controls.Add(new Label { Text = "Connected", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            details.Controls.Add(connectedValue, 1, 2);
            details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            devices = new ListView
            {
                Dock = DockStyle.Fill,
                FullRowSelect = true,
                HideSelection = false,
                MultiSelect = false,
                View = View.Details
            };
            devices.Columns.Add("Name", 210);
            devices.Columns.Add("Type", 120);
            devices.Columns.Add("Trust", 100);
            devices.Columns.Add("Device ID", 290);
            devices.SelectedIndexChanged += delegate { UpdateActions(); };

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8)
            };
            refreshButton = new Button { Text = "Refresh", AutoSize = true };
            refreshButton.Click += delegate { RefreshStatus(); };
            createChallengeButton = new Button { Text = "Create pairing challenge", AutoSize = true, Enabled = false };
            createChallengeButton.Click += delegate { CreateChallenge(); };
            revokeButton = new Button { Text = "Revoke device", AutoSize = true, Enabled = false };
            revokeButton.Click += delegate { RevokeSelected(); };
            actions.Controls.Add(refreshButton);
            actions.Controls.Add(revokeButton);
            actions.Controls.Add(createChallengeButton);

            challengeCodeValue = CreateReadOnlyTextBox();
            challengeIdValue = CreateReadOnlyTextBox();
            challengeNonceValue = CreateReadOnlyTextBox();
            challengeExpiresValue = CreateValueLabel();
            copyChallengeButton = new Button { Text = "Copy code", AutoSize = true, Enabled = false };
            copyChallengeButton.Click += delegate { CopyChallengeCode(); };
            var challenge = new GroupBox
            {
                Text = "Pairing challenge",
                Dock = DockStyle.Bottom,
                Height = 150,
                Padding = new Padding(8)
            };
            var challengeGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 4
            };
            challengeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 96));
            challengeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            challengeGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            for (var row = 0; row < 4; row++)
            {
                challengeGrid.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            }
            challengeGrid.Controls.Add(new Label { Text = "Code", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
            challengeGrid.Controls.Add(challengeCodeValue, 1, 0);
            challengeGrid.Controls.Add(copyChallengeButton, 2, 0);
            challengeGrid.Controls.Add(new Label { Text = "Challenge ID", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
            challengeGrid.Controls.Add(challengeIdValue, 1, 1);
            challengeGrid.SetColumnSpan(challengeIdValue, 2);
            challengeGrid.Controls.Add(new Label { Text = "Nonce", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
            challengeGrid.Controls.Add(challengeNonceValue, 1, 2);
            challengeGrid.SetColumnSpan(challengeNonceValue, 2);
            challengeGrid.Controls.Add(new Label { Text = "Expires", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
            challengeGrid.Controls.Add(challengeExpiresValue, 1, 3);
            challengeGrid.SetColumnSpan(challengeExpiresValue, 2);
            challenge.Controls.Add(challengeGrid);

            Controls.Add(devices);
            Controls.Add(challenge);
            Controls.Add(actions);
            Controls.Add(details);
            Controls.Add(stateLabel);
            ClearChallenge();
        }

        public void RefreshStatus()
        {
            try
            {
                var response = controller.EnsureAgent();
                try
                {
                    var refreshed = controller.RefreshProductionStatus();
                    if (refreshed.Success && refreshed.Production != null) response = refreshed;
                }
                catch (Exception)
                {
                    // The status list remains useful when an optional production refresh is unavailable.
                }
                Apply(response);
            }
            catch (Exception error)
            {
                stateLabel.Text = "Agent unavailable: " + error.Message;
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
                ? "Account " + TrayDisplay.AccountStatus(status) + " | Relay " + TrayDisplay.RelayStatus(status.RelayState)
                : "Production account is not configured on this PC.";
            accountValue.Text = TrayDisplay.AccountStatus(status);
            serverValue.Text = TrayDisplay.StringOrDash(status.ServerHost);
            pcValue.Text = TrayDisplay.StringOrDash(status.PcName);
            relayValue.Text = TrayDisplay.RelayStatus(status.RelayState);
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
                    item.SubItems.Add(TrayDisplay.StringOrDash(device.TrustState));
                    item.SubItems.Add(device.DeviceId == Guid.Empty ? "-" : device.DeviceId.ToString("D"));
                    devices.Items.Add(item);
                }
            }
            devices.EndUpdate();
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
            createChallengeButton.Enabled = pendingAndroid && productionStatus != null && productionStatus.Configured && productionStatus.AccountLinked;
            revokeButton.Enabled = selected != null &&
                !IsLocalPc(selected) &&
                (string.Equals(selected.TrustState, "TRUSTED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(selected.TrustState, "PENDING", StringComparison.OrdinalIgnoreCase));
        }

        private void CreateChallenge()
        {
            var selected = SelectedDevice();
            if (selected == null) return;
            try
            {
                var response = controller.CreatePairingChallenge(selected.DeviceId);
                if (!response.Success) throw new InvalidOperationException(response.Code);
                Apply(response);
                stateLabel.Text = "Pairing challenge created for " + TrayDisplay.StringOrDash(selected.Name) + ".";
            }
            catch (Exception error)
            {
                stateLabel.Text = "Could not create pairing challenge: " + error.Message;
            }
        }

        private void RevokeSelected()
        {
            var selected = SelectedDevice();
            if (selected == null || IsLocalPc(selected)) return;
            var name = TrayDisplay.StringOrDash(selected.Name);
            if (MessageBox.Show(
                "Revoke trust for device '" + name + "'? Existing remote access from this device will stop.",
                "TermLink",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                return;
            }
            try
            {
                var response = controller.RevokeDevice(selected.DeviceId);
                if (!response.Success) throw new InvalidOperationException(response.Code);
                Apply(response);
                stateLabel.Text = "Device revoked: " + name + ".";
            }
            catch (Exception error)
            {
                stateLabel.Text = "Could not revoke device: " + error.Message;
            }
        }

        private void ApplyChallenge(PairingChallengeSummary challenge)
        {
            challengeCodeValue.Text = TrayDisplay.StringOrDash(challenge.Code);
            challengeIdValue.Text = challenge.ChallengeId == Guid.Empty ? "-" : challenge.ChallengeId.ToString("D");
            challengeNonceValue.Text = TrayDisplay.StringOrDash(challenge.ChallengeNonce);
            challengeExpiresValue.Text = TrayDisplay.DateTimeOrDash(challenge.ExpiresAtUtc);
            copyChallengeButton.Enabled = !string.IsNullOrWhiteSpace(challenge.Code);
        }

        private void ClearChallenge()
        {
            challengeCodeValue.Text = string.Empty;
            challengeIdValue.Text = string.Empty;
            challengeNonceValue.Text = string.Empty;
            challengeExpiresValue.Text = "-";
            copyChallengeButton.Enabled = false;
        }

        private void CopyChallengeCode()
        {
            if (string.IsNullOrWhiteSpace(challengeCodeValue.Text)) return;
            try
            {
                Clipboard.SetText(challengeCodeValue.Text);
                stateLabel.Text = "Pairing code copied to the clipboard.";
            }
            catch (Exception error)
            {
                stateLabel.Text = "Could not copy pairing code: " + error.Message;
            }
        }
    }
}
