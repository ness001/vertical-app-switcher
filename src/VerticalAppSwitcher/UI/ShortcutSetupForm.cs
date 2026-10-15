using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal sealed class ShortcutSetupForm : Form, IMessageFilter
    {
        private static readonly Color Ink = Color.FromArgb(26, 34, 53);
        private static readonly Color Muted = Color.FromArgb(78, 91, 113);
        private static readonly Color Accent = Color.FromArgb(67, 56, 202);
        private readonly Func<ShortcutDefinition, string> applyShortcut;
        private readonly Func<ShortcutDefinition, string> checkShortcut;
        private readonly List<Font> ownedFonts = new List<Font>();
        private readonly ComboBox shortcutSelector;
        private readonly Label chordLabel;
        private readonly StatusLabel statusLabel;
        private readonly Label statusDetail;
        private readonly Panel statusPanel;
        private readonly Button recordButton;
        private readonly Button checkButton;
        private readonly Button saveButton;
        private readonly ShortcutKeyboard keyboard;
        private ShortcutDefinition candidate;
        private uint draftModifiers;
        private uint draftKey;
        private bool recording;
        private bool updatingSelector;

        public ShortcutSetupForm(
            ShortcutDefinition selectedShortcut,
            string initialStatus,
            Icon icon,
            Func<ShortcutDefinition, string> applyShortcut,
            Func<ShortcutDefinition, string> checkShortcut = null)
        {
            this.applyShortcut = applyShortcut;
            this.checkShortcut = checkShortcut;
            selectedShortcut = selectedShortcut ?? ShortcutDefinition.Default;

            Text = "Shortcut settings - Vertical App Switcher";
            Icon = icon;
            StartPosition = FormStartPosition.CenterScreen;
            MinimumSize = new Size(740, 580);
            MinimizeBox = false;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96F, 96F);
            Font = CreateFont(10F, FontStyle.Regular);
            ClientSize = new Size(960, 772);
            BackColor = Color.FromArgb(247, 249, 252);
            ForeColor = Ink;

            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 74,
                BackColor = Color.White,
                Padding = new Padding(28, 14, 28, 14),
                ColumnCount = 3,
                RowCount = 1
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var footerNote = MakeLabel("Changes take effect only after you save.", 9F, FontStyle.Regular, Muted);
            footerNote.Dock = DockStyle.Fill;
            footerNote.TextAlign = ContentAlignment.MiddleLeft;
            footer.Controls.Add(footerNote, 0, 0);

            var cancelButton = MakeButton("Cancel", false);
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Margin = new Padding(8, 0, 8, 0);
            footer.Controls.Add(cancelButton, 1, 0);
            saveButton = MakeButton("Save shortcut", true);
            saveButton.Margin = Padding.Empty;
            saveButton.Click += OnSaveClick;
            footer.Controls.Add(saveButton, 2, 0);

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var content = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top,
                Padding = new Padding(28, 22, 28, 16),
                ColumnCount = 1,
                RowCount = 9
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < content.RowCount; row++)
            {
                content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            }

            var brand = MakeLabel("VERTICAL APP SWITCHER", 9F, FontStyle.Bold, Accent);
            brand.Margin = new Padding(0, 0, 0, 6);
            content.Controls.Add(brand, 0, 0);
            var heading = MakeLabel("You are setting your shortcut", 23F, FontStyle.Bold, Ink);
            heading.Margin = new Padding(0, 0, 0, 6);
            content.Controls.Add(heading, 0, 1);
            var introduction = MakeLabel(
                "Choose a shortcut such as Ctrl + K. Click a selected key again to clear it, or clear all keys to start over.",
                10F, FontStyle.Regular, Muted);
            introduction.Margin = new Padding(0, 0, 0, 18);
            content.Controls.Add(introduction, 0, 2);

            var composer = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 2,
                BackColor = Color.White,
                Padding = new Padding(18, 12, 18, 12),
                Margin = new Padding(0, 0, 0, 12)
            };
            composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            composer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            composer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            composer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            composer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var chordTitle = MakeLabel("SHORTCUT TO USE", 9F, FontStyle.Bold, Muted);
            chordTitle.Margin = new Padding(0, 0, 0, 6);
            composer.Controls.Add(chordTitle, 0, 0);
            chordLabel = MakeLabel(selectedShortcut.DisplayText, 24F, FontStyle.Bold, Ink);
            chordLabel.AccessibleName = "Selected shortcut: " + selectedShortcut.DisplayText;
            chordLabel.Margin = Padding.Empty;
            composer.Controls.Add(chordLabel, 0, 1);
            var clearButton = MakeButton("Clear selection", false);
            clearButton.Anchor = AnchorStyles.Right;
            clearButton.Margin = new Padding(16, 0, 0, 0);
            clearButton.Click += delegate
            {
                StopRecording();
                draftModifiers = 0;
                draftKey = 0;
                RefreshCandidate();
            };
            composer.Controls.Add(clearButton, 1, 0);
            composer.SetRowSpan(clearButton, 2);
            recordButton = MakeButton("Record keys", false);
            recordButton.AccessibleDescription = "Record keys in this window only. Escape or Tab stops recording.";
            recordButton.Anchor = AnchorStyles.Right;
            recordButton.Margin = new Padding(8, 0, 0, 0);
            recordButton.Click += delegate
            {
                if (recording)
                {
                    StopRecording();
                }
                else
                {
                    StartRecording();
                }
            };
            composer.Controls.Add(recordButton, 2, 0);
            composer.SetRowSpan(recordButton, 2);
            content.Controls.Add(composer, 0, 3);

            var presets = new TableLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                Margin = new Padding(0, 0, 0, 12)
            };
            presets.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            presets.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var presetsLabel = MakeLabel("Or choose a &preset", 10F, FontStyle.Regular, Ink);
            presetsLabel.Anchor = AnchorStyles.Left;
            presetsLabel.Margin = new Padding(0, 0, 14, 0);
            presets.Controls.Add(presetsLabel, 0, 0);
            shortcutSelector = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                AccessibleName = "Shortcut preset",
                Margin = Padding.Empty,
                IntegralHeight = true
            };
            foreach (ShortcutDefinition shortcut in ShortcutDefinition.Available)
            {
                shortcutSelector.Items.Add(shortcut);
            }

            shortcutSelector.SelectedIndexChanged += OnPresetChanged;
            presets.Controls.Add(shortcutSelector, 1, 0);
            content.Controls.Add(presets, 0, 4);

            statusPanel = new Panel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 16),
                Padding = new Padding(14, 10, 14, 10)
            };
            var statusLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 2
            };
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            statusLabel = new StatusLabel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = CreateFont(10F, FontStyle.Bold),
                AccessibleRole = AccessibleRole.StatusBar,
                Margin = new Padding(0, 0, 0, 3)
            };
            statusDetail = MakeLabel(string.Empty, 9F, FontStyle.Regular, Muted);
            statusDetail.Margin = Padding.Empty;
            statusLayout.Controls.Add(statusLabel, 0, 0);
            statusLayout.Controls.Add(statusDetail, 0, 1);
            checkButton = MakeButton("Check again", false);
            checkButton.Anchor = AnchorStyles.Right;
            checkButton.Margin = new Padding(12, 0, 0, 0);
            checkButton.Click += delegate { RefreshCandidate(); };
            statusLayout.Controls.Add(checkButton, 1, 0);
            statusLayout.SetRowSpan(checkButton, 2);
            statusPanel.Controls.Add(statusLayout);
            content.Controls.Add(statusPanel, 0, 5);

            keyboard = new ShortcutKeyboard
            {
                Dock = DockStyle.Fill,
                Height = 282,
                Margin = new Padding(0, 0, 0, 10),
                Font = CreateFont(9F, FontStyle.Regular),
                AccessibleName = "Shortcut keyboard",
                AccessibleDescription = "Choose modifiers and one key. Arrow keys move. Space or Enter toggles a key. Tab leaves the keyboard."
            };
            keyboard.KeyChosen += OnKeyboardKeyChosen;
            content.Controls.Add(keyboard, 0, 6);
            var instructions = MakeLabel(
                "Click Ctrl, Alt, or Win, then a key (for example, Ctrl + K). Click a selected key again to clear it.\r\n"
                + "Use Clear selection to start over. With keyboard focus, use arrows and Space. Key positions shown are US layout.",
                9F, FontStyle.Regular, Muted);
            instructions.Margin = new Padding(0, 0, 0, 8);
            content.Controls.Add(instructions, 0, 7);
            var limits = MakeLabel(
                "The check finds registered shortcuts only. Windows or another app may still act on some combinations.",
                9F, FontStyle.Regular, Muted);
            limits.Margin = Padding.Empty;
            content.Controls.Add(limits, 0, 8);

            scroll.Controls.Add(content);
            Controls.Add(scroll);
            Controls.Add(footer);
            AcceptButton = saveButton;
            CancelButton = cancelButton;
            SetDraft(selectedShortcut);
            if (checkShortcut == null && !string.IsNullOrEmpty(initialStatus))
            {
                SetStatus("Check this shortcut", initialStatus + " Save to check again.", true, false);
            }
        }

        public bool PreFilterMessage(ref Message message)
        {
            if (!recording || !ContainsFocus || Form.ActiveForm != this)
            {
                return false;
            }

            if (message.Msg == 0x0100 || message.Msg == 0x0104)
            {
                Keys key = (Keys)(message.WParam.ToInt32() & (int)Keys.KeyCode);
                uint modifiers = ReadModifiers();
                if (key == Keys.Escape && modifiers == 0)
                {
                    StopRecording();
                    return true;
                }

                if (key == Keys.Tab && (modifiers & ~NativeMethods.ModShift) == 0)
                {
                    StopRecording();
                    SelectNextControl(ActiveControl, modifiers == 0, true, true, true);
                    return true;
                }

                if (GetKeyState((int)Keys.RMenu) < 0
                    && (modifiers & NativeMethods.ModControl) != 0)
                {
                    draftModifiers = 0;
                    draftKey = 0;
                    RefreshCandidate();
                    SetStatus("AltGr is used for typing",
                        "Use left Alt, or choose Ctrl and Alt on the on-screen keyboard.", true, false);
                    return true;
                }

                if ((message.LParam.ToInt64() & (1L << 30)) != 0)
                {
                    return true;
                }

                uint modifier = GetModifier(key);
                if (modifier != 0)
                {
                    draftModifiers = modifiers | modifier;
                    draftKey = 0;
                }
                else
                {
                    draftModifiers = modifiers;
                    draftKey = (uint)key;
                }

                RefreshCandidate();
                return true;
            }

            return message.Msg == 0x0101 || message.Msg == 0x0105
                || message.Msg == 0x0102 || message.Msg == 0x0106
                || message.Msg == 0x0103 || message.Msg == 0x0107
                || message.Msg == 0x0109 || message.Msg == 0x0286;
        }

        public void CaptureRegisteredShortcut(ShortcutDefinition shortcut)
        {
            if (recording && ContainsFocus && Form.ActiveForm == this && shortcut != null)
            {
                SetDraft(shortcut);
            }
        }

        protected override void OnShown(EventArgs eventArgs)
        {
            base.OnShown(eventArgs);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(Width, Math.Max(320, area.Width - 32));
            int height = Math.Min(Height, Math.Max(320, area.Height - 32));
            MinimumSize = new Size(Math.Min(MinimumSize.Width, width), Math.Min(MinimumSize.Height, height));
            Bounds = new Rectangle(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2, width, height);
            recordButton.Focus();
        }

        protected override void OnDeactivate(EventArgs eventArgs)
        {
            StopRecording();
            base.OnDeactivate(eventArgs);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopRecording();
            }

            base.Dispose(disposing);
            if (disposing)
            {
                foreach (Font font in ownedFonts)
                {
                    font.Dispose();
                }

                ownedFonts.Clear();
            }
        }

        private void StartRecording()
        {
            recording = true;
            recordButton.Text = "Stop recording";
            draftModifiers = 0;
            draftKey = 0;
            Application.AddMessageFilter(this);
            RefreshCandidate();
        }

        private void StopRecording()
        {
            if (!recording)
            {
                return;
            }

            recording = false;
            Application.RemoveMessageFilter(this);
            recordButton.Text = "Record keys";
            RefreshCandidate();
        }

        private void OnPresetChanged(object sender, EventArgs eventArgs)
        {
            if (updatingSelector)
            {
                return;
            }

            var shortcut = shortcutSelector.SelectedItem as ShortcutDefinition;
            if (shortcut != null)
            {
                StopRecording();
                SetDraft(shortcut);
            }
        }

        private void OnKeyboardKeyChosen(uint virtualKey, uint modifier)
        {
            StopRecording();
            if (modifier != 0)
            {
                draftModifiers ^= modifier;
            }
            else
            {
                draftKey = draftKey == virtualKey ? 0 : virtualKey;
            }

            RefreshCandidate();
        }

        private void SetDraft(ShortcutDefinition shortcut)
        {
            draftModifiers = shortcut.Modifiers & ~NativeMethods.ModNoRepeat;
            draftKey = shortcut.VirtualKey;
            RefreshCandidate();
        }

        private void RefreshCandidate()
        {
            keyboard.SetChord(draftModifiers, draftKey);
            string chord = ShortcutDefinition.GetChordDisplayText(draftModifiers, draftKey);
            chordLabel.Text = string.IsNullOrEmpty(chord)
                ? (recording ? "Press your keys" : "Choose your keys")
                : chord + (draftKey == 0 ? " + ..." : string.Empty);
            chordLabel.AccessibleName = "Selected shortcut: " + chordLabel.Text;

            string error;
            bool valid = ShortcutDefinition.TryCreate(draftModifiers, draftKey, out candidate, out error);
            updatingSelector = true;
            shortcutSelector.SelectedItem = candidate != null && ShortcutDefinition.Available.Contains(candidate)
                ? candidate : null;
            updatingSelector = false;
            checkButton.Enabled = valid && checkShortcut != null;
            saveButton.Enabled = valid;
            if (draftKey == 0)
            {
                saveButton.Enabled = false;
                SetStatus(recording ? "Listening in this window" : "Choose a combination",
                    recording
                        ? "Hold Ctrl, Alt, or Win and press one key. Escape or Tab stops recording."
                        : "Choose Ctrl, Alt, or Win and one key, such as Ctrl + K. Click a selected key to clear it, or use Clear selection to start over.",
                    false, false);
                return;
            }

            if (!valid)
            {
                SetStatus("Choose another combination", error, true, false);
                return;
            }

            if (checkShortcut == null)
            {
                SetStatus("Ready to check", "Save shortcut to check whether Windows can use it.", false, false);
                return;
            }

            error = checkShortcut(candidate);
            if (!string.IsNullOrEmpty(error))
            {
                saveButton.Enabled = false;
                SetStatus("Shortcut is not available", error + " Choose another combination, or check again.", true, false);
                return;
            }

            SetStatus("Available on this PC", "Windows can register this shortcut now. Save to use it.", false, true);
        }

        private void SetStatus(string title, string detail, bool isError, bool isAvailable)
        {
            Color color = isError ? Color.FromArgb(153, 36, 49)
                : isAvailable ? Color.FromArgb(20, 93, 66) : Color.FromArgb(47, 67, 121);
            statusPanel.BackColor = isError ? Color.FromArgb(255, 240, 242)
                : isAvailable ? Color.FromArgb(233, 247, 239) : Color.FromArgb(235, 240, 252);
            statusLabel.ForeColor = color;
            statusDetail.ForeColor = color;
            statusDetail.Text = detail;
            statusLabel.SetStatus(title, detail);
        }

        private void OnSaveClick(object sender, EventArgs eventArgs)
        {
            StopRecording();
            RefreshCandidate();
            if (candidate == null || !saveButton.Enabled)
            {
                return;
            }

            string error = applyShortcut(candidate);
            if (!string.IsNullOrEmpty(error))
            {
                SetStatus("Shortcut was not saved", error, true, false);
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        }

        private Font CreateFont(float size, FontStyle style)
        {
            var font = new Font("Segoe UI", size, style);
            ownedFonts.Add(font);
            return font;
        }

        private Label MakeLabel(string text, float size, FontStyle style, Color color)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Dock = DockStyle.Fill,
                Font = CreateFont(size, style),
                ForeColor = color,
                Margin = Padding.Empty
            };
        }

        private static Button MakeButton(string text, bool primary)
        {
            var button = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(104, 42),
                Padding = new Padding(12, 4, 12, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = primary ? Accent : Color.White,
                ForeColor = primary ? Color.White : Ink,
                UseVisualStyleBackColor = false,
                Cursor = Cursors.Hand
            };
            button.FlatAppearance.BorderColor = primary ? Accent : Color.FromArgb(172, 183, 202);
            button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(54, 44, 173) : Color.FromArgb(238, 242, 251);
            button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(45, 36, 145) : Color.FromArgb(224, 230, 247);
            return button;
        }

        private static uint GetModifier(Keys key)
        {
            switch (key)
            {
                case Keys.ControlKey:
                case Keys.LControlKey:
                case Keys.RControlKey: return NativeMethods.ModControl;
                case Keys.Menu:
                case Keys.LMenu:
                case Keys.RMenu: return NativeMethods.ModAlt;
                case Keys.ShiftKey:
                case Keys.LShiftKey:
                case Keys.RShiftKey: return NativeMethods.ModShift;
                case Keys.LWin:
                case Keys.RWin: return NativeMethods.ModWin;
                default: return 0;
            }
        }

        private static uint ReadModifiers()
        {
            uint modifiers = 0;
            if (GetKeyState((int)Keys.ControlKey) < 0) modifiers |= NativeMethods.ModControl;
            if (GetKeyState((int)Keys.Menu) < 0) modifiers |= NativeMethods.ModAlt;
            if (GetKeyState((int)Keys.ShiftKey) < 0) modifiers |= NativeMethods.ModShift;
            if (GetKeyState((int)Keys.LWin) < 0 || GetKeyState((int)Keys.RWin) < 0) modifiers |= NativeMethods.ModWin;
            return modifiers;
        }

        [DllImport("user32.dll")]
        private static extern short GetKeyState(int virtualKey);

        private sealed class StatusLabel : Label
        {
            public void SetStatus(string title, string detail)
            {
                string description = title + ". " + detail;
                if (AccessibleName == description)
                {
                    return;
                }

                Text = title;
                AccessibleName = description;
                AccessibilityNotifyClients(AccessibleEvents.NameChange, -1);
            }
        }

        private sealed class ShortcutKeyboard : Control
        {
            private readonly List<KeyCap> keys = new List<KeyCap>();
            private readonly List<List<KeyCap>> rows = new List<List<KeyCap>>();
            private uint modifiers;
            private uint virtualKey;
            private int focusedIndex;
            private int hoveredIndex = -1;

            public ShortcutKeyboard()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                    | ControlStyles.Selectable, true);
                TabStop = true;
                BackColor = Color.FromArgb(234, 239, 247);
                Cursor = Cursors.Hand;
                AddRow(
                    Key("Esc", Keys.Escape), Key("F1", Keys.F1), Key("F2", Keys.F2),
                    Key("F3", Keys.F3), Key("F4", Keys.F4), Key("F5", Keys.F5),
                    Key("F6", Keys.F6), Key("F7", Keys.F7), Key("F8", Keys.F8),
                    Key("F9", Keys.F9), Key("F10", Keys.F10), Key("F11", Keys.F11),
                    Key("F12", Keys.F12), Key("Delete", Keys.Delete, 2));
                AddRow(
                    Key("`", Keys.Oem3), Key("1", Keys.D1), Key("2", Keys.D2),
                    Key("3", Keys.D3), Key("4", Keys.D4), Key("5", Keys.D5),
                    Key("6", Keys.D6), Key("7", Keys.D7), Key("8", Keys.D8),
                    Key("9", Keys.D9), Key("0", Keys.D0), Key("-", Keys.OemMinus),
                    Key("=", Keys.Oemplus), Key("Backspace", Keys.Back, 2));
                AddRow(
                    Key("Tab", Keys.Tab, 1.5F), Key("Q", Keys.Q), Key("W", Keys.W),
                    Key("E", Keys.E), Key("R", Keys.R), Key("T", Keys.T),
                    Key("Y", Keys.Y), Key("U", Keys.U), Key("I", Keys.I),
                    Key("O", Keys.O), Key("P", Keys.P), Key("[", Keys.OemOpenBrackets),
                    Key("]", Keys.OemCloseBrackets), Key("\\", Keys.Oem5, 1.5F));
                AddRow(
                    Key("Caps", Keys.None, 1.75F), Key("A", Keys.A), Key("S", Keys.S),
                    Key("D", Keys.D), Key("F", Keys.F), Key("G", Keys.G),
                    Key("H", Keys.H), Key("J", Keys.J), Key("K", Keys.K),
                    Key("L", Keys.L), Key(";", Keys.Oem1), Key("'", Keys.Oem7),
                    Key("Enter", Keys.Enter, 2.25F));
                AddRow(
                    Modifier("Shift", NativeMethods.ModShift, 2.25F),
                    Key("Z", Keys.Z), Key("X", Keys.X), Key("C", Keys.C),
                    Key("V", Keys.V), Key("B", Keys.B), Key("N", Keys.N),
                    Key("M", Keys.M), Key(",", Keys.Oemcomma), Key(".", Keys.OemPeriod),
                    Key("/", Keys.OemQuestion), Modifier("Shift", NativeMethods.ModShift, 2.75F));
                AddRow(
                    Modifier("Ctrl", NativeMethods.ModControl, 1.25F),
                    Modifier("Win", NativeMethods.ModWin, 1.25F),
                    Modifier("Alt", NativeMethods.ModAlt, 1.25F),
                    Key("Space", Keys.Space, 6),
                    Modifier("Alt", NativeMethods.ModAlt, 1.25F),
                    Key("Left", Keys.Left), Key("Up", Keys.Up),
                    Key("Down", Keys.Down), Key("Right", Keys.Right));
            }

            public event Action<uint, uint> KeyChosen;

            public void SetChord(uint selectedModifiers, uint selectedKey)
            {
                if (modifiers == selectedModifiers && virtualKey == selectedKey)
                {
                    return;
                }

                modifiers = selectedModifiers;
                virtualKey = selectedKey;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Selection, -1);
            }

            protected override AccessibleObject CreateAccessibilityInstance()
            {
                return new KeyboardAccessibleObject(this);
            }

            protected override void OnResize(EventArgs eventArgs)
            {
                base.OnResize(eventArgs);
                LayoutKeys();
            }

            protected override void OnPaint(PaintEventArgs eventArgs)
            {
                base.OnPaint(eventArgs);
                Graphics graphics = eventArgs.Graphics;
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (var normalBrush = new SolidBrush(Color.White))
                using (var selectedBrush = new SolidBrush(Accent))
                using (var hoverBrush = new SolidBrush(Color.FromArgb(223, 229, 253)))
                using (var disabledBrush = new SolidBrush(Color.FromArgb(224, 231, 241)))
                using (var border = new Pen(Color.FromArgb(172, 184, 204)))
                using (var selectedBorder = new Pen(Accent))
                {
                    for (int index = 0; index < keys.Count; index++)
                    {
                        KeyCap key = keys[index];
                        bool selected = IsSelected(key);
                        Brush fill = !key.Enabled ? disabledBrush : selected ? selectedBrush
                            : index == hoveredIndex ? hoverBrush : normalBrush;
                        using (GraphicsPath path = RoundedRectangle(key.Bounds, Math.Max(3, DeviceDpi / 24)))
                        {
                            graphics.FillPath(fill, path);
                            graphics.DrawPath(selected ? selectedBorder : border, path);
                        }

                        TextRenderer.DrawText(graphics, key.Text, Font, key.Bounds,
                            selected ? Color.White : key.Enabled ? Ink : Muted,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                        if (Focused && index == focusedIndex)
                        {
                            Rectangle focus = key.Bounds;
                            focus.Inflate(-4, -4);
                            ControlPaint.DrawFocusRectangle(graphics, focus, selected ? Color.White : Accent,
                                selected ? Accent : Color.White);
                        }
                    }
                }
            }

            protected override bool IsInputKey(Keys keyData)
            {
                Keys key = keyData & Keys.KeyCode;
                return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down
                    || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
            }

            protected override bool ProcessDialogKey(Keys keyData)
            {
                if (keyData == Keys.Enter)
                {
                    ChooseKey(focusedIndex);
                    return true;
                }

                return base.ProcessDialogKey(keyData);
            }

            protected override void OnKeyDown(KeyEventArgs eventArgs)
            {
                int next = focusedIndex;
                Keys key = eventArgs.KeyCode;
                if (key == Keys.Space || key == Keys.Enter)
                {
                    ChooseKey(focusedIndex);
                }
                else if (key == Keys.Home)
                {
                    next = 0;
                }
                else if (key == Keys.End)
                {
                    next = keys.Count - 1;
                }
                else if (key == Keys.Left || key == Keys.Right)
                {
                    int step = key == Keys.Left ? -1 : 1;
                    do
                    {
                        next = (next + step + keys.Count) % keys.Count;
                    }
                    while (!keys[next].Enabled);
                }
                else if (key == Keys.Up || key == Keys.Down)
                {
                    KeyCap current = keys[focusedIndex];
                    int row = Math.Max(0, Math.Min(rows.Count - 1, current.Row + (key == Keys.Up ? -1 : 1)));
                    int nearest = int.MaxValue;
                    foreach (KeyCap other in rows[row])
                    {
                        int distance = Math.Abs(other.Bounds.Left + other.Bounds.Width / 2
                            - current.Bounds.Left - current.Bounds.Width / 2);
                        if (other.Enabled && distance < nearest)
                        {
                            nearest = distance;
                            next = keys.IndexOf(other);
                        }
                    }
                }
                else
                {
                    base.OnKeyDown(eventArgs);
                    return;
                }

                focusedIndex = next;
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Focus, focusedIndex);
            }

            protected override void OnMouseDown(MouseEventArgs eventArgs)
            {
                base.OnMouseDown(eventArgs);
                if (eventArgs.Button != MouseButtons.Left)
                {
                    return;
                }

                int index = HitTestKey(eventArgs.Location);
                if (index >= 0 && keys[index].Enabled)
                {
                    Focus();
                    focusedIndex = index;
                    ChooseKey(index);
                }
            }

            protected override void OnMouseMove(MouseEventArgs eventArgs)
            {
                base.OnMouseMove(eventArgs);
                int index = HitTestKey(eventArgs.Location);
                if (index != hoveredIndex)
                {
                    hoveredIndex = index;
                    Invalidate();
                }
            }

            protected override void OnMouseLeave(EventArgs eventArgs)
            {
                hoveredIndex = -1;
                Invalidate();
                base.OnMouseLeave(eventArgs);
            }

            protected override void OnGotFocus(EventArgs eventArgs)
            {
                base.OnGotFocus(eventArgs);
                Invalidate();
                AccessibilityNotifyClients(AccessibleEvents.Focus, focusedIndex);
            }

            protected override void OnLostFocus(EventArgs eventArgs)
            {
                base.OnLostFocus(eventArgs);
                Invalidate();
            }

            private void ChooseKey(int index)
            {
                KeyCap key = keys[index];
                if (key.Enabled)
                {
                    focusedIndex = index;
                    KeyChosen?.Invoke(key.VirtualKey, key.Modifier);
                    Invalidate();
                    AccessibilityNotifyClients(AccessibleEvents.StateChange, index);
                }
            }

            private bool IsSelected(KeyCap key)
            {
                return key.Modifier != 0 ? (modifiers & key.Modifier) != 0
                    : key.VirtualKey != 0 && key.VirtualKey == virtualKey;
            }

            private int HitTestKey(Point point)
            {
                for (int index = 0; index < keys.Count; index++)
                {
                    if (keys[index].Bounds.Contains(point))
                    {
                        return index;
                    }
                }

                return -1;
            }

            private void AddRow(params KeyCap[] rowKeys)
            {
                var row = new List<KeyCap>(rowKeys);
                foreach (KeyCap key in row)
                {
                    key.Row = rows.Count;
                    keys.Add(key);
                }

                rows.Add(row);
            }

            private void LayoutKeys()
            {
                if (rows.Count == 0 || Width < 1 || Height < 1)
                {
                    return;
                }

                float inset = 10F * DeviceDpi / 96F;
                float gap = 5F * DeviceDpi / 96F;
                float height = (Height - inset * 2 - gap * (rows.Count - 1)) / rows.Count;
                for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    List<KeyCap> row = rows[rowIndex];
                    float totalUnits = 0;
                    foreach (KeyCap key in row) totalUnits += key.Units;
                    float unit = (Width - inset * 2 - gap * (row.Count - 1)) / totalUnits;
                    float left = inset;
                    foreach (KeyCap key in row)
                    {
                        float width = unit * key.Units;
                        key.Bounds = new Rectangle((int)left, (int)(inset + rowIndex * (height + gap)),
                            Math.Max(1, (int)width), Math.Max(1, (int)height));
                        left += width + gap;
                    }
                }
            }

            private static KeyCap Key(string text, Keys key, float units = 1F)
            {
                return new KeyCap { Text = text, VirtualKey = (uint)key, Units = units };
            }

            private static KeyCap Modifier(string text, uint modifier, float units)
            {
                return new KeyCap { Text = text, Modifier = modifier, Units = units };
            }

            private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
            {
                int diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), radius * 2);
                var path = new GraphicsPath();
                path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
                path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
                path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
                path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
                path.CloseFigure();
                return path;
            }

            private sealed class KeyCap
            {
                public string Text;
                public uint VirtualKey;
                public uint Modifier;
                public float Units;
                public int Row;
                public Rectangle Bounds;
                public bool Enabled { get { return VirtualKey != 0 || Modifier != 0; } }
            }

            private sealed class KeyboardAccessibleObject : ControlAccessibleObject
            {
                private readonly ShortcutKeyboard keyboard;

                public KeyboardAccessibleObject(ShortcutKeyboard keyboard) : base(keyboard)
                {
                    this.keyboard = keyboard;
                }

                public override AccessibleRole Role { get { return AccessibleRole.Grouping; } }
                public override int GetChildCount() { return keyboard.keys.Count; }
                public override AccessibleObject GetChild(int index)
                {
                    return index < 0 || index >= keyboard.keys.Count ? null : new KeyAccessibleObject(keyboard, index);
                }

                public override AccessibleObject GetFocused()
                {
                    return keyboard.Focused ? GetChild(keyboard.focusedIndex) : null;
                }

                public override AccessibleObject HitTest(int x, int y)
                {
                    int index = keyboard.HitTestKey(keyboard.PointToClient(new Point(x, y)));
                    return index >= 0 ? GetChild(index) : base.HitTest(x, y);
                }
            }

            private sealed class KeyAccessibleObject : AccessibleObject
            {
                private readonly ShortcutKeyboard keyboard;
                private readonly int index;
                private KeyCap Key { get { return keyboard.keys[index]; } }

                public KeyAccessibleObject(ShortcutKeyboard keyboard, int index)
                {
                    this.keyboard = keyboard;
                    this.index = index;
                }

                public override string Name { get { return Key.Text; } }
                public override string Description
                {
                    get { return Key.Modifier != 0 ? "Toggle modifier" : "Toggle this key for the shortcut"; }
                }
                public override string DefaultAction { get { return "Toggle"; } }
                public override AccessibleObject Parent { get { return keyboard.AccessibilityObject; } }
                public override Rectangle Bounds { get { return keyboard.RectangleToScreen(Key.Bounds); } }
                public override AccessibleRole Role
                {
                    get { return Key.Modifier != 0 ? AccessibleRole.CheckButton : AccessibleRole.PushButton; }
                }
                public override AccessibleStates State
                {
                    get
                    {
                        if (!Key.Enabled) return AccessibleStates.Unavailable;
                        AccessibleStates state = AccessibleStates.Focusable | AccessibleStates.Selectable;
                        if (keyboard.Focused && keyboard.focusedIndex == index) state |= AccessibleStates.Focused;
                        if (keyboard.IsSelected(Key)) state |= Key.Modifier != 0 ? AccessibleStates.Checked : AccessibleStates.Selected;
                        return state;
                    }
                }

                public override void DoDefaultAction() { keyboard.ChooseKey(index); }
                public override void Select(AccessibleSelection flags)
                {
                    if (Key.Enabled && (flags & AccessibleSelection.TakeFocus) != 0)
                    {
                        keyboard.Focus();
                        keyboard.focusedIndex = index;
                        keyboard.Invalidate();
                    }
                }
            }
        }
    }
}
