using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal enum CloseRequestSource
    {
        Standard,
        KeyboardD,
        KeyboardDelete,
        ControllerX
    }

    internal sealed class SwitcherOverlay : Form
    {
        private const float PanelWidth = 390f;
        private const float CollapsedHeaderHeight = 124f;
        private const float SearchHeaderHeight = 184f;
        private const float ItemHeight = 68f;
        private const float FooterHeight = 88f;
        private const float EdgeMargin = 24f;
        private const float TabTop = 72f;
        private const float TabHeight = 40f;
        private const float TabGap = 4f;
        private const int MaximumVisibleItems = 9;
        private const int OpenTab = 0;
        private const int PinnedTab = 1;
        private const int RecentTab = 2;
        private const int TrayTab = 3;
        private const int DesktopsTab = 4;

        private readonly TabView[] tabs =
        {
            new TabView("Open", "Windows on this desktop", "No open windows"),
            new TabView("Pinned", "Saved apps, open or closed", "No pinned apps"),
            new TabView("Recent", "Closed apps saved to open again", "No recently closed apps"),
            new TabView("Tray", "Apps in the Windows notification area", "No tray apps found"),
            new TabView("Desktops", "Windows on other virtual desktops", "No windows on other desktops")
        };

        private readonly Font headerFont =
            new Font("Segoe UI", 15f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font subtitleFont =
            new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font shortcutFont =
            new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font itemTitleFont =
            new Font("Segoe UI", 13f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly Font detailFont =
            new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        private readonly Font badgeFont =
            new Font("Segoe UI", 9f, FontStyle.Bold, GraphicsUnit.Pixel);
        private readonly TextBox searchBox = new TextBox();
        private readonly ContextMenuStrip itemMenu = new ContextMenuStrip();
        private readonly ToolStripMenuItem activateMenuItem =
            new ToolStripMenuItem();
        private readonly ToolStripMenuItem pinMenuItem =
            new ToolStripMenuItem();
        private readonly ToolStripMenuItem closeMenuItem =
            new ToolStripMenuItem();
        private readonly ToolStripMenuItem forceStopMenuItem =
            new ToolStripMenuItem();

        private AppWindowCollection windows;
        private Font searchFont;
        private int activeTab;
        private int selectedIndex = -1;
        private int firstVisibleIndex;
        private int visibleCapacity;
        private float uiScale = 1f;
        private bool switching;
        private bool showingContextMenu;
        private bool confirmationVisible;
        private bool searchFocused;
        private bool searchExpanded;
        private bool overlayActive;
        private bool layoutUpdatePending;
        private bool shortcutChipHovered;
        private bool gamepadInputMode;
        private Point gamepadMouseOrigin;
        private RectangleF shortcutChipBounds;
        private string invocationShortcut = "WIN + `";
        private string actionStatus;
        private DateTime ignoreDeactivationUntilUtc = DateTime.MinValue;

        public SwitcherOverlay()
        {
            Text = "Quick Switch";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(15, 18, 25);
            Opacity = 1d;
            DoubleBuffered = true;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);

            searchBox.BorderStyle = BorderStyle.None;
            searchBox.BackColor = Color.FromArgb(32, 39, 52);
            searchBox.ForeColor = Color.FromArgb(240, 249, 255);
            searchBox.Font = itemTitleFont;
            searchBox.PlaceholderText = "Title or app name";
            searchBox.AccessibleName = "Search current tab";
            searchBox.AccessibleDescription =
                "Filter by title or app name. Up or Down moves focus to the results. Enter opens the current match. Tab returns to the list.";
            searchBox.TabStop = false;
            searchBox.Visible = false;
            searchBox.Enter += OnSearchEntered;
            searchBox.TextChanged += OnSearchTextChanged;
            searchBox.KeyDown += OnSearchKeyDown;
            Controls.Add(searchBox);

            activateMenuItem.Click += delegate { ActivateSelection(); };
            pinMenuItem.Click += delegate { TogglePinSelection(); };
            closeMenuItem.Click += delegate { RequestCloseSelection(); };
            forceStopMenuItem.Click += delegate { RequestForceStopSelection(); };
            itemMenu.Items.AddRange(new ToolStripItem[]
            {
                activateMenuItem,
                pinMenuItem,
                new ToolStripSeparator(),
                closeMenuItem,
                forceStopMenuItem
            });
            itemMenu.Closed += OnItemMenuClosed;
        }

        public event Action<string> ActivationFailed;
        public event Action<AppWindow> ActivationSucceeded;
        public event Action<AppWindow> PinToggleRequested;
        public event Action<AppWindow, CloseRequestSource> CloseRequested;
        public event Action<AppWindow> ForceStopRequested;
        public event Action ShortcutSetupRequested;
        public event Action NavigationModeChanged;

        public bool CanUseBrowsingHotKeys
        {
            get
            {
                return Visible && overlayActive && !searchFocused
                    && !confirmationVisible && !showingContextMenu;
            }
        }

        private float HeaderHeight
        {
            get { return searchExpanded ? SearchHeaderHeight : CollapsedHeaderHeight; }
        }

        private List<AppWindow> VisibleWindows
        {
            get { return tabs[activeTab].FilteredWindows; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                const int dropShadow = 0x00020000;
                CreateParams parameters = base.CreateParams;
                parameters.ClassStyle |= dropShadow;
                return parameters;
            }
        }

        public void ShowWindows(
            AppWindowCollection nextWindows,
            bool protectInitialVisibility)
        {
            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            DisposeWindows();
            FocusBrowsingList();
            searchExpanded = false;
            searchBox.Visible = false;
            searchBox.Clear();
            windows = nextWindows;
            gamepadInputMode = false;
            activeTab = OpenTab;
            RebuildTabs();
            if (VisibleWindows.Count == 0)
            {
                for (int index = 1; index < tabs.Length; index++)
                {
                    if (tabs[index].Windows.Count > 0)
                    {
                        activeTab = index;
                        break;
                    }
                }
            }

            selectedIndex = VisibleWindows.Count > 0 ? 0 : -1;
            if (activeTab == OpenTab && VisibleWindows.Count > 1)
            {
                int foregroundIndex = VisibleWindows.FindIndex(
                    item => item.Handle == foregroundWindow);
                if (foregroundIndex >= 0)
                {
                    selectedIndex = (foregroundIndex + 1) % VisibleWindows.Count;
                }
            }

            firstVisibleIndex = 0;
            SaveTabSelection();
            actionStatus = null;
            ignoreDeactivationUntilUtc = protectInitialVisibility
                ? DateTime.UtcNow.AddSeconds(1.5d)
                : DateTime.MinValue;
            ConfigureBounds();
            EnsureSelectionVisible();
            UpdateAccessibleDescription();

            if (!Visible)
            {
                Show();
            }

            WindowState = FormWindowState.Normal;
            Activate();
            FocusBrowsingList();
            NativeMethods.ActivateForegroundWindow(Handle);
            Invalidate();
        }

        public void AdvanceSelection()
        {
            if (windows == null || windows.Count == 0)
            {
                return;
            }

            MoveSelection(1);
            Activate();
            FocusBrowsingList();
        }

        public bool HandleNavigationKey(Keys keyData)
        {
            if (!Visible)
            {
                return false;
            }

            if (confirmationVisible)
            {
                return true;
            }

            Keys keyCode = keyData & Keys.KeyCode;
            Keys modifiers = keyData & Keys.Modifiers;
            if (modifiers == Keys.Control)
            {
                int index = keyCode >= Keys.D1 && keyCode <= Keys.D9
                    ? (int)keyCode - (int)Keys.D1
                    : keyCode >= Keys.NumPad1 && keyCode <= Keys.NumPad9
                        ? (int)keyCode - (int)Keys.NumPad1
                        : -1;
                if (index >= 0)
                {
                    if (index < VisibleWindows.Count)
                    {
                        SelectIndex(index);
                        ActivateSelection();
                    }

                    return true;
                }
            }

            if (keyCode == Keys.Tab
                && (modifiers == Keys.None || modifiers == Keys.Shift))
            {
                if (searchFocused)
                {
                    FocusBrowsingList();
                }
                else
                {
                    MoveSelection(modifiers == Keys.Shift ? -1 : 1);
                }

                return true;
            }

            if (modifiers != Keys.None)
            {
                return false;
            }

            if (keyCode == Keys.Escape)
            {
                HideSwitcher();
                return true;
            }

            if (keyCode == Keys.Up || keyCode == Keys.Down)
            {
                if (searchFocused)
                {
                    if (VisibleWindows.Count > 0)
                    {
                        FocusBrowsingList();
                        SelectIndex(selectedIndex >= 0 ? selectedIndex : 0);
                    }
                }
                else
                {
                    MoveSelection(keyCode == Keys.Up ? -1 : 1);
                }

                return true;
            }

            if (keyCode == Keys.Enter)
            {
                ActivateSelection();
                return true;
            }

            if (searchFocused)
            {
                return false;
            }

            if (keyCode == Keys.S)
            {
                ShowSearch();
                return true;
            }

            if (keyCode == Keys.Left || keyCode == Keys.Right)
            {
                SwitchTab(keyCode == Keys.Left ? -1 : 1);
                return true;
            }

            if (keyCode == Keys.Home)
            {
                SelectIndex(0);
                return true;
            }

            if (keyCode == Keys.End && windows != null)
            {
                SelectIndex(VisibleWindows.Count - 1);
                return true;
            }

            if (keyCode == Keys.Space)
            {
                ActivateSelection();
                return true;
            }

            if (keyCode == Keys.Delete)
            {
                RequestCloseSelection(CloseRequestSource.KeyboardDelete);
                return true;
            }

            if (keyCode == Keys.D)
            {
                RequestCloseSelection(CloseRequestSource.KeyboardD);
                return true;
            }

            if (keyCode == Keys.P)
            {
                TogglePinSelection();
                return true;
            }

            return false;
        }

        private void ShowSearch()
        {
            if (!searchExpanded)
            {
                searchExpanded = true;
                ConfigureBounds();
                EnsureSelectionVisible();
                searchBox.Visible = true;
            }

            searchBox.Focus();
            Invalidate();
        }

        private void OnSearchEntered(object sender, EventArgs eventArgs)
        {
            searchFocused = true;
            SetInputMode(false);
            NotifyNavigationModeChanged();
        }

        private void OnSearchKeyDown(object sender, KeyEventArgs eventArgs)
        {
            SetInputMode(false);
            if (HandleNavigationKey(eventArgs.KeyData))
            {
                eventArgs.Handled = true;
                eventArgs.SuppressKeyPress = true;
            }
        }

        private void OnSearchTextChanged(object sender, EventArgs eventArgs)
        {
            SaveTabSelection();
            ApplySearchFilter();
            actionStatus = null;
            UpdateAccessibleDescription();
            Invalidate();
        }

        private void FocusBrowsingList()
        {
            searchFocused = false;
            ActiveControl = null;
            Focus();
            NotifyNavigationModeChanged();
        }

        private void RestoreInputFocus()
        {
            if (searchFocused)
            {
                searchBox.Focus();
            }
            else
            {
                FocusBrowsingList();
            }
        }

        private void NotifyNavigationModeChanged()
        {
            NavigationModeChanged?.Invoke();
            UpdateAccessibleDescription();
            Invalidate();
        }

        public void RequestCloseSelection(
            CloseRequestSource source = CloseRequestSource.Standard)
        {
            if (searchFocused
                && (source == CloseRequestSource.KeyboardD
                    || source == CloseRequestSource.KeyboardDelete))
            {
                return;
            }

            AppWindow selected = GetSelectedWindow();
            Action<AppWindow, CloseRequestSource> handler = CloseRequested;
            if (source == CloseRequestSource.KeyboardDelete
                && (selected == null || selected.IsOpen || !selected.IsRecent))
            {
                return;
            }

            if (Visible && !confirmationVisible && selected != null && handler != null)
            {
                handler(selected, source);
            }
        }

        public void TogglePinSelection()
        {
            AppWindow selected = GetSelectedWindow();
            Action<AppWindow> handler = PinToggleRequested;
            if (!confirmationVisible && selected != null && handler != null)
            {
                handler(selected);
            }
        }

        public void ApplyPinState(AppWindow window, bool pinned)
        {
            if (windows == null)
            {
                return;
            }

            int newestPinOrder = -1;
            if (pinned)
            {
                foreach (AppWindow item in windows)
                {
                    if (item.IsPinned)
                    {
                        newestPinOrder = Math.Min(newestPinOrder, item.PinOrder - 1);
                    }
                }
            }

            foreach (AppWindow item in windows)
            {
                if (!string.IsNullOrEmpty(window.IdentityKey)
                    && string.Equals(
                        item.IdentityKey,
                        window.IdentityKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    item.IsPinned = pinned;
                    item.PinOrder = pinned ? newestPinOrder : int.MaxValue;
                }
            }

            if (!pinned && !window.IsOpen && !window.IsRecent)
            {
                windows.Remove(window);
            }

            RebuildTabs();
            SetActionStatus(
                pinned
                    ? "Saved in Pinned."
                    : "Removed from Pinned.");
        }

        public void SetInvocationShortcut(string displayText)
        {
            invocationShortcut = string.IsNullOrWhiteSpace(displayText)
                ? "Not set"
                : displayText;
            Invalidate();
        }

        public void SetInputMode(bool gamepad)
        {
            if (gamepad && searchFocused)
            {
                FocusBrowsingList();
            }

            if (gamepadInputMode == gamepad)
            {
                return;
            }

            gamepadInputMode = gamepad;
            if (gamepad)
            {
                gamepadMouseOrigin = Cursor.Position;
                shortcutChipHovered = false;
                Cursor = Cursors.Default;
            }

            UpdateAccessibleDescription();
            Invalidate();
        }

        public void CloseForShortcutSetup()
        {
            if (Visible)
            {
                HideSwitcher();
            }
        }

        public void SetConfirmationVisible(bool visible)
        {
            confirmationVisible = visible;
            NotifyNavigationModeChanged();
            if (!visible && Visible)
            {
                Activate();
                RestoreInputFocus();
            }
        }

        public void SetActionStatus(string message)
        {
            actionStatus = message;
            Invalidate();
        }

        public void RemoveEntry(AppWindow window)
        {
            if (windows == null || windows.IndexOf(window) < 0)
            {
                return;
            }

            ClearSelectionForWindow(window);
            if (window.IsPinned && window.IsRecent && !window.IsOpen)
            {
                window.IsRecent = false;
            }
            else
            {
                windows.Remove(window);
            }

            RebuildTabs();
            Invalidate();
        }

        public void ApplyClosedWindows(IList<ClosedWindowUpdate> updates)
        {
            if (windows == null || updates == null)
            {
                return;
            }

            AppWindow selectedBeforeUpdate = GetSelectedWindow();
            int previousSelectedIndex = selectedIndex;
            bool changed = false;
            bool recentChanged = false;
            foreach (ClosedWindowUpdate update in updates)
            {
                AppWindow closedEntry = FindSnapshotEntry(
                    update.ClosedWindow);
                if (update.ReplacementWindow != null)
                {
                    if (closedEntry == null)
                    {
                        continue;
                    }

                    AppWindow replacementEntry = FindSnapshotEntry(
                        update.ReplacementWindow);
                    if (replacementEntry == null)
                    {
                        ClearSelectionForWindow(closedEntry);
                        closedEntry.ApplySnapshot(update.ReplacementWindow);
                    }
                    else if (!ReferenceEquals(closedEntry, replacementEntry))
                    {
                        ClearSelectionForWindow(closedEntry);
                        windows.Remove(closedEntry);
                    }

                    changed = true;
                    continue;
                }

                if (update.AddToRecent)
                {
                    if (closedEntry == null)
                    {
                        closedEntry = FindSavedEntry(update.ClosedWindow.IdentityKey);
                    }

                    if (closedEntry != null)
                    {
                        ClearSelectionForWindow(closedEntry);
                        closedEntry.MarkRecentlyClosed();
                        windows.MoveToFront(closedEntry);
                        changed = true;
                        recentChanged = true;
                    }
                    else if (!ContainsIdentity(
                        update.ClosedWindow.IdentityKey))
                    {
                        windows.Insert(
                            0,
                            CreateRecentWindow(update.ClosedWindow));
                        changed = true;
                        recentChanged = true;
                    }
                }
                else if (closedEntry != null)
                {
                    ClearSelectionForWindow(closedEntry);
                    if (closedEntry.IsPinned)
                    {
                        closedEntry.MarkRecentlyClosed();
                        closedEntry.IsRecent = false;
                    }
                    else
                    {
                        windows.Remove(closedEntry);
                    }

                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            RebuildTabs();
            int remainingSelectedIndex = VisibleWindows.IndexOf(selectedBeforeUpdate);
            if (remainingSelectedIndex >= 0)
            {
                SelectIndex(remainingSelectedIndex);
            }
            else if (VisibleWindows.Count > 0)
            {
                SelectIndex(Math.Max(0, Math.Min(previousSelectedIndex, VisibleWindows.Count - 1)));
            }

            actionStatus = recentChanged
                ? "Closed. Open the Recent tab to reopen."
                : "Application closed.";
            Invalidate();
        }

        private void RebuildTabs()
        {
            SaveTabSelection();
            foreach (TabView tab in tabs)
            {
                tab.Windows.Clear();
            }

            if (windows != null)
            {
                foreach (AppWindow window in windows)
                {
                    if (window.IsPinned)
                    {
                        tabs[PinnedTab].Windows.Add(window);
                    }

                    if (window.IsRecent)
                    {
                        tabs[RecentTab].Windows.Add(window);
                    }
                    else if (window.IsTray)
                    {
                        tabs[TrayTab].Windows.Add(window);
                    }
                    else if (window.Handle != IntPtr.Zero)
                    {
                        tabs[window.IsOnAnotherDesktop ? DesktopsTab : OpenTab]
                            .Windows.Add(window);
                    }
                }
            }

            ForegroundWindowOrder.Apply(tabs[OpenTab].Windows);
            tabs[PinnedTab].Windows.Sort(
                (left, right) => left.PinOrder.CompareTo(right.PinOrder));
            ApplySearchFilter();
            UpdateAccessibleDescription();
        }

        private void ApplySearchFilter()
        {
            string query = searchBox.Text;
            foreach (TabView tab in tabs)
            {
                tab.FilteredWindows.Clear();
                foreach (AppWindow window in tab.Windows)
                {
                    if (string.IsNullOrEmpty(query)
                        || ContainsSearchText(window.Title, query)
                        || ContainsSearchText(window.ProcessName, query))
                    {
                        tab.FilteredWindows.Add(window);
                    }
                }

                if (tab.SelectedWindow != null
                    && !tab.FilteredWindows.Contains(tab.SelectedWindow))
                {
                    tab.SelectedWindow = null;
                }
            }

            RestoreTabSelection();
        }

        private static bool ContainsSearchText(string text, string query)
        {
            return !string.IsNullOrEmpty(text)
                && text.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void ClearSelectionForWindow(AppWindow window)
        {
            if (ReferenceEquals(GetSelectedWindow(), window))
            {
                selectedIndex = -1;
                if (itemMenu.Visible)
                {
                    itemMenu.Close();
                }
            }

            foreach (TabView tab in tabs)
            {
                if (ReferenceEquals(tab.SelectedWindow, window))
                {
                    tab.SelectedWindow = null;
                }
            }
        }

        private void SaveTabSelection()
        {
            TabView tab = tabs[activeTab];
            tab.SelectedWindow = GetSelectedWindow();
            tab.FirstVisibleIndex = firstVisibleIndex;
        }

        private void RestoreTabSelection()
        {
            TabView tab = tabs[activeTab];
            selectedIndex = tab.SelectedWindow == null
                ? -1
                : VisibleWindows.IndexOf(tab.SelectedWindow);
            if (selectedIndex < 0 && VisibleWindows.Count > 0)
            {
                selectedIndex = 0;
            }

            firstVisibleIndex = tab.FirstVisibleIndex;
            EnsureSelectionVisible();
            SaveTabSelection();
        }

        private void SwitchTab(int delta)
        {
            SelectTab((activeTab + delta + tabs.Length) % tabs.Length);
        }

        private void SelectTab(int index)
        {
            if (index < 0 || index >= tabs.Length)
            {
                return;
            }

            if (index == activeTab)
            {
                if (selectedIndex < 0 && VisibleWindows.Count > 0)
                {
                    SelectIndex(0);
                }

                return;
            }

            if (itemMenu.Visible)
            {
                itemMenu.Close();
            }

            SaveTabSelection();
            activeTab = index;
            RestoreTabSelection();
            if (selectedIndex < 0 && VisibleWindows.Count > 0)
            {
                selectedIndex = 0;
                EnsureSelectionVisible();
            }

            actionStatus = null;
            SaveTabSelection();
            UpdateAccessibleDescription();
            Invalidate();
        }

        private void UpdateAccessibleDescription()
        {
            AppWindow selected = GetSelectedWindow();
            AccessibleName = "Quick Switch - " + tabs[activeTab].Title;
            AccessibleDescription = tabs[activeTab].Description
                + ". " + VisibleWindows.Count + " apps. "
                + (selected == null ? "No app selected." : searchFocused
                    ? "Current match: " + selected.Title + ". App list does not have focus."
                    : selected.Title + ".")
                + (string.IsNullOrEmpty(searchBox.Text)
                    ? string.Empty
                    : " Search: " + searchBox.Text + ".")
                + (gamepadInputMode
                    ? " Gamepad hints. D-pad or left stick: Left and Right change tabs;"
                        + " Up and Down select an app. A opens, B goes back, X closes"
                        + " the window or removes a closed Recent entry, and Y changes the pin state."
                    : searchFocused
                        ? " Search has focus. Type to filter. Up or Down moves focus to the results."
                            + " Enter opens. Control plus 1 through 9 opens a numbered result."
                            + " Tab returns to the list. Escape dismisses."
                        : " Keyboard hints. Left and Right change tabs. Up and Down select"
                            + " an app. Enter opens, Escape dismisses, S searches, P changes"
                            + " the pin state, and D closes the window or removes a closed Recent entry."
                            + " Delete only removes closed Recent entries."
                            + " Control plus 1 through 9 opens a numbered result.")
                + " Click the top-right shortcut to change it.";
        }

        private AppWindow FindSnapshotEntry(WindowSnapshot snapshot)
        {
            foreach (AppWindow window in windows)
            {
                if (window.TrackingKey == snapshot.TrackingKey
                    && window.ProcessId == snapshot.ProcessId
                    && (!window.ProcessStartTimeUtc.HasValue
                        || !snapshot.ProcessStartTimeUtc.HasValue
                        || window.ProcessStartTimeUtc.Value == snapshot.ProcessStartTimeUtc.Value)
                    && string.Equals(
                        window.IdentityKey,
                        snapshot.IdentityKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return window;
                }
            }

            return null;
        }

        private AppWindow FindSavedEntry(string identityKey)
        {
            if (string.IsNullOrEmpty(identityKey))
            {
                return null;
            }

            foreach (AppWindow window in windows)
            {
                if (window.Handle == IntPtr.Zero && !window.IsTray
                    && string.Equals(
                        window.IdentityKey,
                        identityKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return window;
                }
            }

            return null;
        }

        private bool ContainsIdentity(string identityKey)
        {
            if (string.IsNullOrEmpty(identityKey))
            {
                return false;
            }

            foreach (AppWindow window in windows)
            {
                if (string.Equals(
                    window.IdentityKey,
                    identityKey,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static AppWindow CreateRecentWindow(WindowSnapshot snapshot)
        {
            return new AppWindow(
                IntPtr.Zero,
                0,
                snapshot.Title,
                snapshot.ProcessName,
                snapshot.ExecutablePath,
                snapshot.LaunchTarget,
                WindowCatalog.GetExecutableIcon(snapshot.ExecutablePath),
                false)
            {
                AppUserModelId = snapshot.AppUserModelId,
                IsRecent = true
            };
        }

        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            SetInputMode(false);
            Keys keyCode = keyData & Keys.KeyCode;
            if (!searchFocused && keyCode == Keys.D
                && (message.Msg == 0x0100 || message.Msg == 0x0104)
                && (message.LParam.ToInt64() & (1L << 30)) != 0)
            {
                return true;
            }

            return HandleNavigationKey(keyData)
                || base.ProcessCmdKey(ref message, keyData);
        }

        protected override void OnPaint(PaintEventArgs eventArgs)
        {
            base.OnPaint(eventArgs);

            Graphics graphics = eventArgs.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.ScaleTransform(uiScale, uiScale);

            float logicalWidth = ClientSize.Width / uiScale;
            float logicalHeight = ClientSize.Height / uiScale;
            var panelBounds = new RectangleF(0f, 0f, logicalWidth, logicalHeight);

            using (GraphicsPath panelPath = CreateRoundedRectangle(panelBounds, 16f))
            using (var panelBrush = new LinearGradientBrush(
                panelBounds,
                Color.FromArgb(28, 33, 44),
                Color.FromArgb(13, 16, 23),
                LinearGradientMode.Vertical))
            using (var borderPen = new Pen(Color.FromArgb(78, 91, 112), 1f))
            {
                graphics.FillPath(panelBrush, panelPath);
                graphics.DrawPath(borderPen, panelPath);
            }

            DrawHeader(graphics, logicalWidth);
            DrawTabs(graphics, logicalWidth);
            DrawSearch(graphics, logicalWidth);
            DrawWindows(graphics, logicalWidth);
            DrawFooter(graphics, logicalWidth, logicalHeight);
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);
            if (gamepadInputMode)
            {
                Point location = PointToScreen(eventArgs.Location);
                int threshold = Math.Max(1, (int)Math.Ceiling(4f * uiScale));
                if (Math.Abs(location.X - gamepadMouseOrigin.X) < threshold
                    && Math.Abs(location.Y - gamepadMouseOrigin.Y) < threshold)
                {
                    return;
                }

                SetInputMode(false);
            }

            int index = HitTestWindow(eventArgs.Location);
            bool overShortcut = HitTestShortcutChip(eventArgs.Location);
            if (shortcutChipHovered != overShortcut)
            {
                shortcutChipHovered = overShortcut;
                Invalidate();
            }

            Cursor = index >= 0 || HitTestTab(eventArgs.Location) >= 0
                || overShortcut
                ? Cursors.Hand
                : Cursors.Default;
            if (index >= 0 && index != selectedIndex)
            {
                SelectIndex(index);
            }
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            base.OnMouseLeave(eventArgs);
            shortcutChipHovered = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
            SetInputMode(false);
            if (confirmationVisible)
            {
                return;
            }

            if (eventArgs.Button == MouseButtons.Left
                && HitTestShortcutChip(eventArgs.Location))
            {
                return;
            }

            var logicalPoint = new PointF(
                eventArgs.X / uiScale,
                eventArgs.Y / uiScale);
            if (eventArgs.Button == MouseButtons.Left
                && searchExpanded
                && GetSearchBounds(ClientSize.Width / uiScale).Contains(logicalPoint))
            {
                searchBox.Focus();
                return;
            }

            FocusBrowsingList();
            int tabIndex = HitTestTab(eventArgs.Location);
            if (tabIndex >= 0)
            {
                if (eventArgs.Button == MouseButtons.Left)
                {
                    SelectTab(tabIndex);
                }

                return;
            }

            int index = HitTestWindow(eventArgs.Location);
            if (index < 0)
            {
                return;
            }

            SelectIndex(index);
            if (eventArgs.Button == MouseButtons.Left)
            {
                ActivateSelection();
            }
            else if (eventArgs.Button == MouseButtons.Right)
            {
                ShowItemMenu(eventArgs.Location);
            }
        }

        protected override void OnMouseClick(MouseEventArgs eventArgs)
        {
            base.OnMouseClick(eventArgs);
            if (eventArgs.Button == MouseButtons.Left
                && !confirmationVisible
                && HitTestShortcutChip(eventArgs.Location))
            {
                ShortcutSetupRequested?.Invoke();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs eventArgs)
        {
            base.OnMouseWheel(eventArgs);
            if (eventArgs.Delta != 0)
            {
                SetInputMode(false);
                MoveSelection(eventArgs.Delta > 0 ? -1 : 1);
            }
        }

        protected override void OnActivated(EventArgs eventArgs)
        {
            overlayActive = true;
            base.OnActivated(eventArgs);
            NotifyNavigationModeChanged();
        }

        protected override void OnVisibleChanged(EventArgs eventArgs)
        {
            if (!Visible)
            {
                overlayActive = false;
            }

            base.OnVisibleChanged(eventArgs);
            NotifyNavigationModeChanged();
        }

        protected override void OnDeactivate(EventArgs eventArgs)
        {
            overlayActive = false;
            NotifyNavigationModeChanged();
            base.OnDeactivate(eventArgs);
            if (showingContextMenu || confirmationVisible)
            {
                return;
            }

            if (DateTime.UtcNow < ignoreDeactivationUntilUtc)
            {
                return;
            }

            if (!switching && Visible)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (!switching && !confirmationVisible
                        && !showingContextMenu && Visible && !ContainsFocus)
                    {
                        HideSwitcher();
                    }
                });
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int displayChange = 0x007e;
            const int settingChange = 0x001a;
            const int dpiChange = 0x02e0;
            base.WndProc(ref message);
            if ((message.Msg == displayChange || message.Msg == settingChange
                    || message.Msg == dpiChange)
                && Visible && !layoutUpdatePending && !IsDisposed && !Disposing)
            {
                layoutUpdatePending = true;
                BeginInvoke((MethodInvoker)delegate
                {
                    layoutUpdatePending = false;
                    if (Visible && !IsDisposed && !Disposing)
                    {
                        ConfigureBounds();
                        EnsureSelectionVisible();
                        Invalidate();
                    }
                });
            }
        }

        protected override void OnResize(EventArgs eventArgs)
        {
            base.OnResize(eventArgs);
            if (Width <= 0 || Height <= 0)
            {
                return;
            }

            using (GraphicsPath path = CreateRoundedRectangle(
                new RectangleF(0f, 0f, Width, Height),
                16f * uiScale))
            {
                Region oldRegion = Region;
                Region = new Region(path);
                if (oldRegion != null)
                {
                    oldRegion.Dispose();
                }
            }
        }

        protected override void Dispose(bool disposing)
        {
            // Destroy child controls before releasing fonts they can still use while closing.
            base.Dispose(disposing);
            if (disposing)
            {
                DisposeWindows();
                headerFont.Dispose();
                subtitleFont.Dispose();
                shortcutFont.Dispose();
                itemTitleFont.Dispose();
                detailFont.Dispose();
                badgeFont.Dispose();
                searchFont?.Dispose();
                itemMenu.Dispose();
            }
        }

        private void ConfigureBounds()
        {
            uiScale = GetUiScale();

            IntPtr foregroundWindow = NativeMethods.GetForegroundWindow();
            Screen screen = foregroundWindow == IntPtr.Zero
                ? Screen.FromPoint(Cursor.Position)
                : Screen.FromHandle(foregroundWindow);
            Rectangle workArea = screen.WorkingArea;

            int maximumLogicalHeight = Math.Max(
                (int)(HeaderHeight + ItemHeight + FooterHeight),
                (int)(workArea.Height / uiScale - EdgeMargin * 2f));
            visibleCapacity = Math.Max(
                1,
                Math.Min(
                    MaximumVisibleItems,
                    (int)((maximumLogicalHeight - HeaderHeight - FooterHeight)
                        / ItemHeight)));
            int largestTab = 1;
            foreach (TabView tab in tabs)
            {
                largestTab = Math.Max(largestTab, tab.Windows.Count);
            }

            visibleCapacity = Math.Min(visibleCapacity, largestTab);

            float logicalHeight =
                HeaderHeight + visibleCapacity * ItemHeight + FooterHeight;
            Size = new Size(
                (int)Math.Ceiling(PanelWidth * uiScale),
                (int)Math.Ceiling(logicalHeight * uiScale));
            Location = new Point(
                workArea.Left + (int)Math.Round(EdgeMargin * uiScale),
                workArea.Top + Math.Max(0, (workArea.Height - Height) / 2));

            float searchFontSize = 13f * uiScale;
            if (searchFont == null || searchFont.Size != searchFontSize)
            {
                Font previousSearchFont = searchFont;
                searchFont = new Font("Segoe UI", searchFontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                searchBox.Font = searchFont;
                // WinForms keeps an equal font instance, so only replace it when the size changes.
                previousSearchFont?.Dispose();
            }
            RectangleF searchBounds = GetSearchBounds(PanelWidth);
            searchBox.SetBounds(
                (int)Math.Round((searchBounds.X + 10f) * uiScale),
                (int)Math.Round((searchBounds.Y + 8f) * uiScale),
                (int)Math.Round((searchBounds.Width - 20f) * uiScale),
                searchBox.PreferredHeight);
        }

        private static float GetUiScale()
        {
            try
            {
                uint dpi = NativeMethods.GetDpiForSystem();
                return dpi == 0 ? 1f : dpi / 96f;
            }
            catch (EntryPointNotFoundException)
            {
                using (Graphics graphics = Graphics.FromHwnd(IntPtr.Zero))
                {
                    return graphics.DpiX / 96f;
                }
            }
        }

        private void DrawHeader(Graphics graphics, float width)
        {
            using (var titleBrush = new SolidBrush(Color.FromArgb(244, 247, 252)))
            using (var subtitleBrush = new SolidBrush(Color.FromArgb(151, 162, 181)))
            using (var chipBrush = new SolidBrush(shortcutChipHovered
                ? Color.FromArgb(57, 83, 105)
                : Color.FromArgb(47, 59, 78)))
            using (var chipBorder = new Pen(shortcutChipHovered
                ? Color.FromArgb(103, 213, 255)
                : Color.FromArgb(83, 103, 133), 1f))
            using (var accentBrush = new SolidBrush(Color.FromArgb(103, 213, 255)))
            using (var dividerPen = new Pen(Color.FromArgb(48, 57, 72), 1f))
            {
                graphics.DrawString(
                    "QUICK SWITCH",
                    headerFont,
                    titleBrush,
                    new PointF(20f, 15f));

                graphics.DrawString(
                    tabs[activeTab].Description,
                    subtitleFont,
                    subtitleBrush,
                    new PointF(21f, 48f));

                string chipText = invocationShortcut;
                float maximumChipWidth = width - 174f;
                float shortcutWidth = graphics.MeasureString(
                    chipText,
                    shortcutFont).Width;
                float chipWidth = Math.Min(
                    maximumChipWidth,
                    Math.Max(72f, shortcutWidth + 18f));
                shortcutChipBounds = new RectangleF(
                    width - chipWidth - 16f,
                    13f,
                    chipWidth,
                    28f);
                RectangleF chipBounds = shortcutChipBounds;
                using (GraphicsPath chipPath = CreateRoundedRectangle(chipBounds, 8f))
                {
                    graphics.FillPath(chipBrush, chipPath);
                    graphics.DrawPath(chipBorder, chipPath);
                }

                if (shortcutWidth <= chipWidth - 12f)
                {
                    DrawCenteredString(
                        graphics,
                        chipText,
                        shortcutFont,
                        accentBrush,
                        chipBounds);
                }
                else
                {
                    using (var fittedFont = new Font(
                        shortcutFont.FontFamily,
                        shortcutFont.Size * (chipWidth - 12f) / shortcutWidth,
                        shortcutFont.Style,
                        GraphicsUnit.Pixel))
                    {
                        DrawCenteredString(
                            graphics,
                            chipText,
                            fittedFont,
                            accentBrush,
                            chipBounds);
                    }
                }
                graphics.DrawLine(dividerPen, 12f, HeaderHeight - 1f, width - 12f, HeaderHeight - 1f);
            }
        }

        private void DrawTabs(Graphics graphics, float width)
        {
            for (int index = 0; index < tabs.Length; index++)
            {
                bool selected = index == activeTab;
                RectangleF bounds = GetTabBounds(index, width);
                using (GraphicsPath path = CreateRoundedRectangle(bounds, 7f))
                using (var background = new SolidBrush(selected
                    ? Color.FromArgb(42, 68, 89)
                    : Color.FromArgb(32, 39, 52)))
                using (var foreground = new SolidBrush(selected
                    ? Color.FromArgb(240, 249, 255)
                    : Color.FromArgb(180, 191, 210)))
                using (var accent = new SolidBrush(Color.FromArgb(103, 213, 255)))
                {
                    graphics.FillPath(background, path);
                    DrawCenteredString(
                        graphics,
                        tabs[index].Title,
                        shortcutFont,
                        foreground,
                        new RectangleF(bounds.X, bounds.Y + 3f, bounds.Width, 17f));
                    DrawCenteredString(
                        graphics,
                        tabs[index].FilteredWindows.Count.ToString(),
                        detailFont,
                        foreground,
                        new RectangleF(bounds.X, bounds.Y + 19f, bounds.Width, 16f));
                    if (selected)
                    {
                        graphics.FillRectangle(
                            accent,
                            bounds.X + 12f,
                            bounds.Bottom - 2f,
                            bounds.Width - 24f,
                            2f);
                    }
                }
            }
        }

        private RectangleF GetTabBounds(int index, float width)
        {
            float tabWidth = (width - 24f - (tabs.Length - 1) * TabGap)
                / tabs.Length;
            return new RectangleF(
                12f + index * (tabWidth + TabGap),
                TabTop,
                tabWidth,
                TabHeight);
        }

        private static RectangleF GetSearchBounds(float width)
        {
            return new RectangleF(16f, 138f, width - 32f, 34f);
        }

        private void DrawSearch(Graphics graphics, float width)
        {
            if (!searchExpanded)
            {
                return;
            }

            using (var labelBrush = new SolidBrush(Color.FromArgb(180, 191, 210)))
            using (var background = new SolidBrush(searchBox.BackColor))
            using (var border = new Pen(searchFocused
                ? Color.FromArgb(103, 213, 255)
                : Color.FromArgb(83, 103, 133), searchFocused ? 2f : 1f))
            using (GraphicsPath path = CreateRoundedRectangle(GetSearchBounds(width), 7f))
            {
                graphics.DrawString("Search", subtitleFont, labelBrush, new PointF(18f, 120f));
                graphics.FillPath(background, path);
                graphics.DrawPath(border, path);
            }
        }

        private void DrawWindows(Graphics graphics, float width)
        {
            if (windows == null)
            {
                return;
            }

            if (VisibleWindows.Count == 0)
            {
                using (var titleBrush = new SolidBrush(Color.FromArgb(218, 224, 234)))
                using (var detailBrush = new SolidBrush(Color.FromArgb(151, 162, 181)))
                {
                    DrawCenteredString(
                        graphics,
                        string.IsNullOrEmpty(searchBox.Text)
                            ? tabs[activeTab].EmptyText
                            : "No matching apps",
                        itemTitleFont,
                        titleBrush,
                        new RectangleF(16f, HeaderHeight + 10f, width - 32f, 24f));
                    DrawCenteredString(
                        graphics,
                        !string.IsNullOrEmpty(searchBox.Text)
                            ? "Change or clear the search to see more apps."
                            : activeTab == PinnedTab
                                ? (gamepadInputMode
                                    ? "Select an app in another tab, then press Y."
                                    : "Select an app in another tab, then press P.")
                                : (gamepadInputMode
                                    ? "Move the D-pad or left stick sideways to change tabs."
                                    : "Use Left/Right to change tabs."),
                        detailFont,
                        detailBrush,
                        new RectangleF(16f, HeaderHeight + 36f, width - 32f, 20f));
                }

                return;
            }

            int lastIndex = Math.Min(
                VisibleWindows.Count,
                firstVisibleIndex + visibleCapacity);
            for (int index = firstVisibleIndex; index < lastIndex; index++)
            {
                int slot = index - firstVisibleIndex;
                float y = HeaderHeight + slot * ItemHeight;
                DrawWindow(graphics, VisibleWindows[index], index,
                    index == selectedIndex && !searchFocused, y, width);
            }

            if (VisibleWindows.Count > visibleCapacity)
            {
                DrawScrollIndicator(graphics, width);
            }
        }

        private void DrawWindow(
            Graphics graphics,
            AppWindow window,
            int index,
            bool selected,
            float y,
            float width)
        {
            var itemBounds = new RectangleF(10f, y + 5f, width - 20f, ItemHeight - 10f);

            if (selected)
            {
                using (GraphicsPath itemPath = CreateRoundedRectangle(itemBounds, 11f))
                using (var selectedBrush = new LinearGradientBrush(
                    itemBounds,
                    Color.FromArgb(60, 78, 103),
                    Color.FromArgb(39, 50, 69),
                    LinearGradientMode.Horizontal))
                using (var selectedBorder = new Pen(Color.FromArgb(92, 127, 166), 1f))
                using (var accentBrush = new SolidBrush(Color.FromArgb(89, 211, 255)))
                {
                    graphics.FillPath(selectedBrush, itemPath);
                    graphics.DrawPath(selectedBorder, itemPath);
                    graphics.FillRectangle(
                        accentBrush,
                        itemBounds.X,
                        itemBounds.Y + 11f,
                        4f,
                        itemBounds.Height - 22f);
                }
            }

            var iconBounds = new RectangleF(25f, y + 16f, 36f, 36f);
            using (Bitmap iconBitmap = window.Icon.ToBitmap())
            {
                graphics.DrawImage(iconBitmap, iconBounds);
            }

            string badgeText = null;
            if (window.IsPinned)
            {
                badgeText = "PINNED";
            }
            else if (window.IsRecent)
            {
                badgeText = "RECENT";
            }
            else if (window.IsTray)
            {
                badgeText = "TRAY";
            }
            else if (window.IsOnAnotherDesktop)
            {
                badgeText = "DESKTOP";
            }
            else if (window.IsMinimized)
            {
                badgeText = "MINIMIZED";
            }

            float textRight = badgeText == null ? width - 22f : width - 100f;
            var titleBounds = new RectangleF(
                76f,
                y + 11f,
                Math.Max(20f, textRight - 76f),
                23f);
            var detailBounds = new RectangleF(
                76f,
                y + 36f,
                Math.Max(20f, width - (index < 9 ? 130f : 98f)),
                18f);

            using (var titleBrush = new SolidBrush(
                selected
                    ? Color.FromArgb(250, 252, 255)
                    : Color.FromArgb(218, 224, 234)))
            using (var detailBrush = new SolidBrush(Color.FromArgb(142, 153, 172)))
            using (StringFormat titleFormat = CreateEllipsisFormat())
            using (StringFormat detailFormat = CreateEllipsisFormat())
            {
                graphics.DrawString(
                    window.Title,
                    itemTitleFont,
                    titleBrush,
                    titleBounds,
                    titleFormat);
                graphics.DrawString(
                    GetDetailText(window),
                    detailFont,
                    detailBrush,
                    detailBounds,
                    detailFormat);
            }

            if (index < 9)
            {
                var numberBounds = new RectangleF(width - 46f, y + 35f, 22f, 20f);
                using (GraphicsPath numberPath = CreateRoundedRectangle(numberBounds, 4f))
                using (var numberBackground = new SolidBrush(Color.FromArgb(47, 59, 78)))
                using (var numberBrush = new SolidBrush(Color.FromArgb(184, 231, 255)))
                {
                    graphics.FillPath(numberBackground, numberPath);
                    DrawCenteredString(
                        graphics, (index + 1).ToString(), shortcutFont, numberBrush, numberBounds);
                }
            }

            if (badgeText != null)
            {
                var badgeBounds = new RectangleF(width - 92f, y + 11f, 68f, 20f);
                using (GraphicsPath badgePath = CreateRoundedRectangle(badgeBounds, 7f))
                using (var badgeBrush = new SolidBrush(
                    window.IsPinned
                        ? Color.FromArgb(39, 83, 99)
                        : window.IsRecent
                            ? Color.FromArgb(78, 65, 39)
                            : window.IsTray
                                ? Color.FromArgb(58, 55, 93)
                            : Color.FromArgb(45, 52, 67)))
                using (var badgeTextBrush = new SolidBrush(
                    window.IsPinned
                        ? Color.FromArgb(104, 220, 255)
                        : window.IsRecent
                            ? Color.FromArgb(241, 195, 99)
                            : window.IsTray
                                ? Color.FromArgb(186, 169, 255)
                            : Color.FromArgb(160, 171, 190)))
                {
                    graphics.FillPath(badgeBrush, badgePath);
                    DrawCenteredString(
                        graphics,
                        badgeText,
                        badgeFont,
                        badgeTextBrush,
                        badgeBounds);
                }
            }
        }

        private void DrawScrollIndicator(Graphics graphics, float width)
        {
            float trackTop = HeaderHeight + 10f;
            float trackHeight = visibleCapacity * ItemHeight - 20f;
            float thumbHeight = Math.Max(
                24f,
                trackHeight * visibleCapacity / VisibleWindows.Count);
            float progress = VisibleWindows.Count == visibleCapacity
                ? 0f
                : (float)firstVisibleIndex / (VisibleWindows.Count - visibleCapacity);
            float thumbTop = trackTop + (trackHeight - thumbHeight) * progress;

            using (var trackBrush = new SolidBrush(Color.FromArgb(38, 45, 57)))
            using (var thumbBrush = new SolidBrush(Color.FromArgb(88, 108, 136)))
            {
                graphics.FillRectangle(trackBrush, width - 6f, trackTop, 2f, trackHeight);
                graphics.FillRectangle(thumbBrush, width - 7f, thumbTop, 4f, thumbHeight);
            }
        }

        private void DrawFooter(Graphics graphics, float width, float height)
        {
            AppWindow selected = GetSelectedWindow();
            bool removeRecent = selected != null && selected.IsRecent && !selected.IsOpen;
            float top = height - FooterHeight;
            using (var dividerPen = new Pen(Color.FromArgb(48, 57, 72), 1f))
            using (var textBrush = new SolidBrush(Color.FromArgb(163, 175, 194)))
            {
                graphics.DrawLine(dividerPen, 12f, top, width - 12f, top);
                if (gamepadInputMode)
                {
                    DrawGamepadDirections(
                        graphics,
                        textBrush,
                        new RectangleF(12f, top + 6f, width - 24f, 22f));
                    float hintWidth = (width - 24f) / 4f;
                    DrawGamepadButtonHint(
                        graphics, "A", "Open", Color.FromArgb(100, 206, 126), textBrush,
                        new RectangleF(12f, top + 32f, hintWidth, 22f));
                    DrawGamepadButtonHint(
                        graphics, "B", "Back", Color.FromArgb(246, 117, 121), textBrush,
                        new RectangleF(12f + hintWidth, top + 32f, hintWidth, 22f));
                    DrawGamepadButtonHint(
                        graphics, "X", removeRecent ? "Remove" : "Close", Color.FromArgb(99, 175, 249), textBrush,
                        new RectangleF(12f + hintWidth * 2f, top + 32f, hintWidth, 22f));
                    DrawGamepadButtonHint(
                        graphics, "Y", "Pin", Color.FromArgb(246, 210, 98), textBrush,
                        new RectangleF(12f + hintWidth * 3f, top + 32f, hintWidth, 22f));
                }
                else
                {
                    DrawCenteredString(
                        graphics,
                        searchFocused
                            ? "UP/DOWN  Focus list   ENTER  Open   TAB  List"
                            : "LEFT/RIGHT  Tabs   UP/DOWN  Apps   S  Search",
                        detailFont,
                        textBrush,
                        new RectangleF(12f, top + 4f, width - 24f, 22f));
                    DrawCenteredString(
                        graphics,
                        searchFocused
                            ? "CTRL+1..9  Open numbered app   ESC  Back"
                            : "ENTER  Open   CTRL+1..9  Open   ESC  Back",
                        detailFont,
                        textBrush,
                        new RectangleF(12f, top + 26f, width - 24f, 22f));
                }

                string footerText = string.IsNullOrEmpty(actionStatus)
                    ? (gamepadInputMode ? null : searchFocused
                        ? "Type to filter this tab"
                        : removeRecent ? "P  Pin   D  Remove recent" : "P  Pin   D  Close window")
                    : actionStatus;
                if (!string.IsNullOrEmpty(footerText))
                {
                    using (var format = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter
                    })
                    {
                        graphics.DrawString(
                            footerText,
                            detailFont,
                            textBrush,
                            new RectangleF(12f, top + 56f, width - 24f, 26f),
                            format);
                    }
                }
            }
        }

        private void DrawGamepadDirections(
            Graphics graphics,
            Brush textBrush,
            RectangleF bounds)
        {
            const string label = "D-pad / left stick   ↔ Tabs   ↕ Apps";
            float labelWidth = graphics.MeasureString(label, detailFont).Width;
            float left = bounds.X + (bounds.Width - 54f - labelWidth) / 2f;
            float top = bounds.Y + (bounds.Height - 20f) / 2f;
            using (var glyphPen = new Pen(Color.FromArgb(163, 175, 194), 1.5f))
            using (var glyphBrush = new SolidBrush(Color.FromArgb(45, 52, 67)))
            using (var path = new GraphicsPath())
            {
                path.AddPolygon(new[]
                {
                    new PointF(left + 7f, top),
                    new PointF(left + 13f, top),
                    new PointF(left + 13f, top + 7f),
                    new PointF(left + 20f, top + 7f),
                    new PointF(left + 20f, top + 13f),
                    new PointF(left + 13f, top + 13f),
                    new PointF(left + 13f, top + 20f),
                    new PointF(left + 7f, top + 20f),
                    new PointF(left + 7f, top + 13f),
                    new PointF(left, top + 13f),
                    new PointF(left, top + 7f),
                    new PointF(left + 7f, top + 7f)
                });
                graphics.FillPath(glyphBrush, path);
                graphics.DrawPath(glyphPen, path);
                graphics.FillEllipse(glyphBrush, left + 28f, top + 2f, 18f, 18f);
                graphics.DrawEllipse(glyphPen, left + 28f, top + 2f, 18f, 18f);
                graphics.DrawEllipse(glyphPen, left + 32f, top + 3f, 10f, 10f);
            }

            DrawCenteredString(
                graphics,
                label,
                detailFont,
                textBrush,
                new RectangleF(left + 54f, bounds.Y, labelWidth, bounds.Height));
        }

        private void DrawGamepadButtonHint(
            Graphics graphics,
            string button,
            string label,
            Color color,
            Brush textBrush,
            RectangleF bounds)
        {
            const float buttonSize = 20f;
            float labelWidth = graphics.MeasureString(label, detailFont).Width;
            float left = bounds.X
                + (bounds.Width - buttonSize - 6f - labelWidth) / 2f;
            var buttonBounds = new RectangleF(
                left,
                bounds.Y + (bounds.Height - buttonSize) / 2f,
                buttonSize,
                buttonSize);
            using (var buttonBrush = new SolidBrush(color))
            using (var letterBrush = new SolidBrush(Color.FromArgb(13, 16, 23)))
            {
                graphics.FillEllipse(buttonBrush, buttonBounds);
                DrawCenteredString(
                    graphics,
                    button,
                    shortcutFont,
                    letterBrush,
                    buttonBounds);
            }

            DrawCenteredString(
                graphics,
                label,
                detailFont,
                textBrush,
                new RectangleF(buttonBounds.Right + 6f, bounds.Y, labelWidth, bounds.Height));
        }

        private void MoveSelection(int delta)
        {
            if (windows == null || VisibleWindows.Count == 0)
            {
                return;
            }

            if (selectedIndex < 0)
            {
                SelectIndex(delta > 0 ? 0 : VisibleWindows.Count - 1);
                return;
            }

            int nextIndex = (selectedIndex + delta) % VisibleWindows.Count;
            if (nextIndex < 0)
            {
                nextIndex += VisibleWindows.Count;
            }

            SelectIndex(nextIndex);
        }

        private void SelectIndex(int index)
        {
            if (windows == null || index < 0 || index >= VisibleWindows.Count)
            {
                return;
            }

            if (selectedIndex != index)
            {
                actionStatus = null;
            }

            selectedIndex = index;
            EnsureSelectionVisible();
            SaveTabSelection();
            UpdateAccessibleDescription();
            Invalidate();
        }

        private void EnsureSelectionVisible()
        {
            int capacity = Math.Max(1, visibleCapacity);
            firstVisibleIndex = Math.Max(
                0,
                Math.Min(firstVisibleIndex, VisibleWindows.Count - capacity));
            if (selectedIndex < 0)
            {
                return;
            }

            if (selectedIndex < firstVisibleIndex)
            {
                firstVisibleIndex = selectedIndex;
            }
            else if (selectedIndex >= firstVisibleIndex + capacity)
            {
                firstVisibleIndex = selectedIndex - capacity + 1;
            }
        }

        private bool HitTestShortcutChip(Point location)
        {
            var logicalPoint = new PointF(location.X / uiScale, location.Y / uiScale);
            return shortcutChipBounds.Contains(logicalPoint);
        }

        private int HitTestTab(Point location)
        {
            var logicalPoint = new PointF(location.X / uiScale, location.Y / uiScale);
            for (int index = 0; index < tabs.Length; index++)
            {
                if (GetTabBounds(index, ClientSize.Width / uiScale)
                    .Contains(logicalPoint))
                {
                    return index;
                }
            }

            return -1;
        }

        private int HitTestWindow(Point location)
        {
            if (windows == null)
            {
                return -1;
            }

            float logicalY = location.Y / uiScale;
            if (logicalY < HeaderHeight
                || logicalY >= HeaderHeight + visibleCapacity * ItemHeight)
            {
                return -1;
            }

            int slot = (int)((logicalY - HeaderHeight) / ItemHeight);
            int index = firstVisibleIndex + slot;
            return index < VisibleWindows.Count ? index : -1;
        }

        private void ActivateSelection()
        {
            AppWindow selectedWindow = GetSelectedWindow();
            if (selectedWindow == null)
            {
                return;
            }

            switching = true;
            Hide();
            try
            {
                string error;
                bool activated = selectedWindow.Activate(out error);
                if (activated)
                {
                    Action<AppWindow> succeededHandler = ActivationSucceeded;
                    if (succeededHandler != null)
                    {
                        succeededHandler(selectedWindow);
                    }
                }
                else
                {
                    Action<string> handler = ActivationFailed;
                    if (handler != null)
                    {
                        handler(error);
                    }
                }
            }
            finally
            {
                DisposeWindows();
                switching = false;
            }
        }

        private void RequestForceStopSelection()
        {
            AppWindow selected = GetSelectedWindow();
            Action<AppWindow> handler = ForceStopRequested;
            if (!confirmationVisible && selected != null && handler != null)
            {
                handler(selected);
            }
        }

        private AppWindow GetSelectedWindow()
        {
            if (windows == null
                || selectedIndex < 0
                || selectedIndex >= VisibleWindows.Count)
            {
                return null;
            }

            return VisibleWindows[selectedIndex];
        }

        private void ShowItemMenu(Point location)
        {
            AppWindow selected = GetSelectedWindow();
            if (selected == null)
            {
                return;
            }

            bool open = selected.IsOpen;
            activateMenuItem.Text = open ? "Switch to app" : "Open app";
            pinMenuItem.Text = selected.IsPinned ? "Unpin app" : "Pin app";
            closeMenuItem.Text = open ? "Close app" : "Remove from recent";
            closeMenuItem.Enabled = open || selected.IsRecent;
            forceStopMenuItem.Visible = open;
            showingContextMenu = true;
            NotifyNavigationModeChanged();
            itemMenu.Show(this, location);
        }

        private void OnItemMenuClosed(object sender, ToolStripDropDownClosedEventArgs eventArgs)
        {
            showingContextMenu = false;
            NotifyNavigationModeChanged();
            if (Visible)
            {
                Activate();
                RestoreInputFocus();
            }
        }

        private static string GetDetailText(AppWindow window)
        {
            if (!window.IsOpen && window.IsPinned)
            {
                return "Pinned - not running";
            }

            if (window.IsRecent)
            {
                return "Recently closed - "
                    + (string.IsNullOrEmpty(window.LaunchTarget)
                        ? window.ProcessName
                        : window.LaunchTarget);
            }

            if (window.IsTray)
            {
                return string.IsNullOrEmpty(window.TrayTooltip)
                    ? "Windows notification area"
                    : "Notification area - " + window.TrayTooltip;
            }

            if (window.IsOnAnotherDesktop)
            {
                return "Other desktop - " + window.ProcessName;
            }

            return window.ProcessName;
        }

        private void HideSwitcher()
        {
            Hide();
            DisposeWindows();
        }

        private void DisposeWindows()
        {
            foreach (TabView tab in tabs)
            {
                tab.Windows.Clear();
                tab.FilteredWindows.Clear();
                tab.SelectedWindow = null;
                tab.FirstVisibleIndex = 0;
            }

            selectedIndex = -1;
            firstVisibleIndex = 0;
            if (windows != null)
            {
                windows.Dispose();
                windows = null;
            }
        }

        private sealed class TabView
        {
            public TabView(string title, string description, string emptyText)
            {
                Title = title;
                Description = description;
                EmptyText = emptyText;
            }

            public readonly string Title;
            public readonly string Description;
            public readonly string EmptyText;
            public readonly List<AppWindow> Windows = new List<AppWindow>();
            public readonly List<AppWindow> FilteredWindows = new List<AppWindow>();
            public AppWindow SelectedWindow;
            public int FirstVisibleIndex;
        }

        private static StringFormat CreateEllipsisFormat()
        {
            return new StringFormat
            {
                Alignment = StringAlignment.Near,
                LineAlignment = StringAlignment.Center,
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };
        }

        private static void DrawCenteredString(
            Graphics graphics,
            string text,
            Font font,
            Brush brush,
            RectangleF bounds)
        {
            using (var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
                FormatFlags = StringFormatFlags.NoWrap
            })
            {
                graphics.DrawString(text, font, brush, bounds, format);
            }
        }

        private static GraphicsPath CreateRoundedRectangle(
            RectangleF bounds,
            float radius)
        {
            float diameter = radius * 2f;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180f, 90f);
            path.AddArc(
                bounds.Right - diameter,
                bounds.Top,
                diameter,
                diameter,
                270f,
                90f);
            path.AddArc(
                bounds.Right - diameter,
                bounds.Bottom - diameter,
                diameter,
                diameter,
                0f,
                90f);
            path.AddArc(
                bounds.Left,
                bounds.Bottom - diameter,
                diameter,
                diameter,
                90f,
                90f);
            path.CloseFigure();
            return path;
        }
    }
}
