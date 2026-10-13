using System;
using System.Drawing;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal sealed class ShortcutSetupForm : Form
    {
        private readonly ComboBox shortcutSelector;
        private readonly Label statusLabel;
        private readonly Func<ShortcutDefinition, string> applyShortcut;

        public ShortcutSetupForm(
            ShortcutDefinition selectedShortcut,
            string initialStatus,
            Icon icon,
            Func<ShortcutDefinition, string> applyShortcut)
        {
            this.applyShortcut = applyShortcut;

            Text = "Set up Vertical App Switcher";
            Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 320);
            BackColor = Color.White;

            var heading = new Label
            {
                AutoSize = false,
                Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
                Location = new Point(28, 24),
                Size = new Size(464, 36),
                Text = "Choose how to open the app switcher"
            };
            Controls.Add(heading);

            var introduction = new Label
            {
                AutoSize = false,
                Location = new Point(30, 72),
                Size = new Size(460, 52),
                Text =
                    "Pick a global shortcut that is comfortable on this PC. "
                    + "Windows reserves it while Vertical App Switcher is running."
            };
            Controls.Add(introduction);

            var shortcutLabel = new Label
            {
                AutoSize = true,
                Location = new Point(30, 134),
                Text = "Global shortcut"
            };
            Controls.Add(shortcutLabel);

            shortcutSelector = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(30, 158),
                Size = new Size(460, 28)
            };
            foreach (ShortcutDefinition shortcut in ShortcutDefinition.Available)
            {
                shortcutSelector.Items.Add(shortcut);
            }

            shortcutSelector.SelectedItem =
                selectedShortcut ?? ShortcutDefinition.Default;
            shortcutSelector.SelectedIndexChanged += delegate
            {
                statusLabel.ForeColor = Color.DimGray;
                statusLabel.Text =
                    "Select Save shortcut to check whether Windows can use it.";
            };
            Controls.Add(shortcutSelector);

            statusLabel = new Label
            {
                AutoSize = false,
                ForeColor = string.IsNullOrEmpty(initialStatus)
                    ? Color.DimGray
                    : Color.Firebrick,
                Location = new Point(30, 196),
                Size = new Size(460, 48),
                Text = string.IsNullOrEmpty(initialStatus)
                    ? "If a shortcut is occupied, choose another and try again."
                    : initialStatus
            };
            Controls.Add(statusLabel);

            var saveButton = new Button
            {
                AutoSize = true,
                Location = new Point(365, 267),
                Padding = new Padding(10, 3, 10, 3),
                Text = "Save shortcut",
                UseVisualStyleBackColor = true
            };
            saveButton.Click += OnSaveClick;
            Controls.Add(saveButton);

            var cancelButton = new Button
            {
                AutoSize = true,
                DialogResult = DialogResult.Cancel,
                Location = new Point(278, 267),
                Padding = new Padding(10, 3, 10, 3),
                Text = "Not now",
                UseVisualStyleBackColor = true
            };
            Controls.Add(cancelButton);

            AcceptButton = saveButton;
            CancelButton = cancelButton;
        }

        private void OnSaveClick(object sender, EventArgs eventArgs)
        {
            var selectedShortcut =
                shortcutSelector.SelectedItem as ShortcutDefinition;
            if (selectedShortcut == null)
            {
                statusLabel.ForeColor = Color.Firebrick;
                statusLabel.Text = "Choose a shortcut before saving.";
                return;
            }

            string error = applyShortcut(selectedShortcut);
            if (!string.IsNullOrEmpty(error))
            {
                statusLabel.ForeColor = Color.Firebrick;
                statusLabel.Text = error;
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
