using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using TermLink.Agent;

namespace TermLink.Tray
{
    internal sealed class TerminalCommandSettingsForm : Form
    {
        private readonly List<TerminalCommandProfile> manualProfiles;
        private readonly ListView commands;
        private readonly Button editButton;
        private readonly Button removeButton;

        internal TerminalCommandSettingsForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(DpiLayout.DesignDpi, DpiLayout.DesignDpi);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = TrayText.Pick("终端命令", "Terminal commands");
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(760, 460);
            Size = new Size(900, 560);
            FormBorderStyle = FormBorderStyle.Sizable;
            ShowInTaskbar = false;
            Icon = BrandIcons.LoadApplicationIcon(DeviceDpi);
            manualProfiles = TerminalCommandProfileStore.LoadManualProfiles().ToList();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                RowCount = 3,
                ColumnCount = 1,
                Padding = new Padding(16)
            };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(820, 0),
                Margin = new Padding(0, 0, 0, 12),
                Text = TrayText.Pick(
                    "自动检测项为只读。手动项由展示名称、可执行文件和参数组成；Android 只接收名称和命令 ID。",
                    "Detected entries are read-only. Manual entries combine a display name, executable, and arguments; Android receives only the name and command ID.")
            }, 0, 0);

            commands = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                MultiSelect = false,
                HideSelection = false
            };
            commands.Columns.Add(TrayText.Pick("手机端名称", "Android name"), 190);
            commands.Columns.Add(TrayText.Pick("来源", "Source"), 90);
            commands.Columns.Add(TrayText.Pick("实际执行命令", "Command"), 500);
            commands.SelectedIndexChanged += delegate { UpdateSelection(); };
            commands.DoubleClick += delegate { EditSelected(); };
            root.Controls.Add(commands, 0, 1);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Padding = new Padding(0, 12, 0, 0)
            };
            var save = Button(TrayText.Pick("保存", "Save"));
            save.Click += SaveAndClose;
            var cancel = Button(TrayText.Pick("取消", "Cancel"));
            cancel.DialogResult = DialogResult.Cancel;
            var add = Button(TrayText.Pick("添加", "Add"));
            add.Click += delegate { AddProfile(); };
            editButton = Button(TrayText.Pick("编辑", "Edit"));
            editButton.Click += delegate { EditSelected(); };
            removeButton = Button(TrayText.Pick("删除", "Remove"));
            removeButton.Click += delegate { RemoveSelected(); };
            actions.Controls.Add(save);
            actions.Controls.Add(cancel);
            actions.Controls.Add(removeButton);
            actions.Controls.Add(editButton);
            actions.Controls.Add(add);
            root.Controls.Add(actions, 0, 2);
            AcceptButton = save;
            CancelButton = cancel;
            RefreshRows();
            AccountLinkForm.ApplyThemeToDialog(this, TrayPalette.For(TrayPreferences.Theme));
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
            ResumeLayout(true);
        }

        private static Button Button(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(88, 34),
            UseMnemonic = false,
            Margin = new Padding(4)
        };

        private void RefreshRows()
        {
            commands.BeginUpdate();
            commands.Items.Clear();
            foreach (var profile in ManagedTerminalLauncher.LoadAvailableTerminalCommandProfiles()
                .Where(profile => profile.Source == "detected"))
            {
                AddRow(profile, TrayText.Pick("自动", "Detected"));
            }
            foreach (var profile in manualProfiles)
            {
                AddRow(
                    profile,
                    profile.Enabled ? TrayText.Pick("手动", "Manual") : TrayText.Pick("手动（停用）", "Manual (disabled)"));
            }
            commands.EndUpdate();
            UpdateSelection();
        }

        private void AddRow(TerminalCommandProfile profile, string source)
        {
            var command = "\"" + profile.ExecutablePath + "\"" +
                (string.IsNullOrWhiteSpace(profile.Arguments) ? string.Empty : " " + profile.Arguments);
            var item = new ListViewItem(new[] { profile.DisplayName, source, command }) { Tag = profile };
            commands.Items.Add(item);
        }

        private TerminalCommandProfile SelectedManualProfile()
        {
            if (commands.SelectedItems.Count != 1) return null;
            var profile = commands.SelectedItems[0].Tag as TerminalCommandProfile;
            return profile != null && profile.Source == "manual" ? profile : null;
        }

        private void UpdateSelection()
        {
            var editable = SelectedManualProfile() != null;
            editButton.Enabled = editable;
            removeButton.Enabled = editable;
        }

        private void AddProfile()
        {
            if (manualProfiles.Count >= TerminalCommandProfileStore.MaximumManualProfiles)
            {
                MessageBox.Show(this, TrayText.Pick("最多可配置 20 个手动命令。", "Up to 20 manual commands may be configured."), "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            using var editor = new TerminalCommandEditorForm(null);
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            manualProfiles.Add(editor.Result);
            RefreshRows();
        }

        private void EditSelected()
        {
            var selected = SelectedManualProfile();
            if (selected == null) return;
            using var editor = new TerminalCommandEditorForm(selected);
            if (editor.ShowDialog(this) != DialogResult.OK) return;
            var index = manualProfiles.FindIndex(profile => profile.Id == selected.Id);
            if (index >= 0) manualProfiles[index] = editor.Result;
            RefreshRows();
        }

        private void RemoveSelected()
        {
            var selected = SelectedManualProfile();
            if (selected == null) return;
            if (MessageBox.Show(
                this,
                TrayText.Pick("删除命令“" + selected.DisplayName + "”？", "Remove command \"" + selected.DisplayName + "\"?"),
                "TermLink",
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning) != DialogResult.OK) return;
            manualProfiles.RemoveAll(profile => profile.Id == selected.Id);
            RefreshRows();
        }

        private void SaveAndClose(object sender, EventArgs e)
        {
            try
            {
                TerminalCommandProfileStore.SaveManualProfiles(manualProfiles);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error) when (
                error is ArgumentException || error is InvalidDataException ||
                error is IOException || error is UnauthorizedAccessException)
            {
                MessageBox.Show(this, error.Message, "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class TerminalCommandEditorForm : Form
    {
        private readonly TerminalCommandProfile original;
        private readonly TextBox displayName;
        private readonly TextBox executable;
        private readonly TextBox arguments;
        private readonly CheckBox enabled;

        internal TerminalCommandEditorForm(TerminalCommandProfile profile)
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(DpiLayout.DesignDpi, DpiLayout.DesignDpi);
            AutoScaleMode = AutoScaleMode.Dpi;
            original = profile;
            Text = profile == null
                ? TrayText.Pick("添加终端命令", "Add terminal command")
                : TrayText.Pick("编辑终端命令", "Edit terminal command");
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(680, 400);
            Size = new Size(700, 420);
            Icon = BrandIcons.LoadApplicationIcon(DeviceDpi);

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 5,
                Padding = new Padding(16)
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (var row = 0; row < 5; row++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);
            displayName = TextInput(profile?.DisplayName);
            executable = TextInput(profile?.ExecutablePath);
            arguments = TextInput(profile?.Arguments);
            enabled = new CheckBox
            {
                Text = TrayText.Pick("在 Android 文件页显示", "Show on the Android Files page"),
                Checked = profile?.Enabled ?? true,
                AutoSize = true
            };
            AddRow(root, 0, TrayText.Pick("手机端名称", "Android name"), displayName);

            var executablePanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, AutoSize = true };
            executablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            executablePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            executablePanel.Controls.Add(executable, 0, 0);
            var browse = Button(TrayText.Pick("浏览…", "Browse…"));
            browse.Click += delegate { BrowseExecutable(); };
            executablePanel.Controls.Add(browse, 1, 0);
            AddRow(root, 1, TrayText.Pick("可执行文件", "Executable"), executablePanel);
            AddRow(root, 2, TrayText.Pick("参数组合", "Arguments"), arguments);
            root.Controls.Add(enabled, 1, 3);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(0, 12, 0, 0)
            };
            var save = Button(TrayText.Pick("确定", "OK"));
            save.Click += ValidateAndClose;
            var cancel = Button(TrayText.Pick("取消", "Cancel"));
            cancel.DialogResult = DialogResult.Cancel;
            actions.Controls.Add(save);
            actions.Controls.Add(cancel);
            root.Controls.Add(actions, 0, 4);
            root.SetColumnSpan(actions, 2);
            AcceptButton = save;
            CancelButton = cancel;
            AccountLinkForm.ApplyThemeToDialog(this, TrayPalette.For(TrayPreferences.Theme));
            TrayWindowChrome.Apply(this, TrayPalette.For(TrayPreferences.Theme));
            ResumeLayout(true);
        }

        internal TerminalCommandProfile Result { get; private set; }

        private static TextBox TextInput(string value) => new TextBox
        {
            Text = value ?? string.Empty,
            Dock = DockStyle.Fill,
            Margin = new Padding(4)
        };

        private static Button Button(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            MinimumSize = new Size(88, 34),
            UseMnemonic = false,
            Margin = new Padding(4)
        };

        private static void AddRow(TableLayoutPanel root, int row, string label, Control input)
        {
            root.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 10, 4, 4) }, 0, row);
            root.Controls.Add(input, 1, row);
        }

        private void BrowseExecutable()
        {
            using var picker = new OpenFileDialog
            {
                CheckFileExists = true,
                Multiselect = false,
                Filter = TrayText.Pick(
                    "可执行命令 (*.exe;*.com;*.cmd;*.bat)|*.exe;*.com;*.cmd;*.bat|所有文件 (*.*)|*.*",
                    "Executable commands (*.exe;*.com;*.cmd;*.bat)|*.exe;*.com;*.cmd;*.bat|All files (*.*)|*.*")
            };
            if (picker.ShowDialog(this) == DialogResult.OK) executable.Text = picker.FileName;
        }

        private void ValidateAndClose(object sender, EventArgs e)
        {
            try
            {
                Result = TerminalCommandProfileStore.CreateManualProfile(
                    original?.Id,
                    displayName.Text,
                    executable.Text,
                    arguments.Text,
                    enabled.Checked);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception error) when (
                error is ArgumentException || error is InvalidDataException ||
                error is IOException || error is UnauthorizedAccessException)
            {
                MessageBox.Show(this, error.Message, "TermLink", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }
}
