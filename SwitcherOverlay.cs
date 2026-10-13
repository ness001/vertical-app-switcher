using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal sealed class SwitcherOverlay : Form
    {
        private const float PanelWidth = 390f;
        private const float HeaderHeight = 78f;
        private const float ItemHeight = 68f;
        private const float FooterHeight = 68f;
        private const float EdgeMargin = 24f;
        private const int MaximumVisibleItems = 9;

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
        private int selectedIndex;
        private int firstVisibleIndex;
        private int visibleCapacity;
        private float uiScale = 1f;
        private bool switching;
        private bool showingContextMenu;
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
            Opacity = 0.98d;
            DoubleBuffered = true;

            SetStyle(
                ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint,
                true);

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
        public event Action<AppWindow> CloseRequested;
        public event Action<AppWindow> ForceStopRequested;

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
            DisposeWindows();
            windows = nextWindows;
            selectedIndex = windows.Count > 1 ? 1 : 0;
            firstVisibleIndex = 0;
            actionStatus = null;
            ignoreDeactivationUntilUtc = protectInitialVisibility
                ? DateTime.UtcNow.AddSeconds(1.5d)
                : DateTime.MinValue;
            ConfigureBounds();
            EnsureSelectionVisible();

            if (!Visible)
            {
                Show();
            }

            WindowState = FormWindowState.Normal;
            Activate();
            Focus();
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
            Focus();
        }

        public bool HandleNavigationKey(Keys keyData)
        {
            if (!Visible)
            {
                return false;
            }

            Keys keyCode = keyData & Keys.KeyCode;
            if (keyCode == Keys.Escape)
            {
                HideSwitcher();
                return true;
            }

            if (keyCode == Keys.Up)
            {
                MoveSelection(-1);
                return true;
            }

            if (keyCode == Keys.Down)
            {
                MoveSelection(1);
                return true;
            }

            if (keyCode == Keys.Tab)
            {
                MoveSelection((keyData & Keys.Shift) == Keys.Shift ? -1 : 1);
                return true;
            }

            if (keyCode == Keys.Home)
            {
                SelectIndex(0);
                return true;
            }

            if (keyCode == Keys.End && windows != null)
            {
                SelectIndex(windows.Count - 1);
                return true;
            }

            if (keyCode == Keys.Enter || keyCode == Keys.Space)
            {
                ActivateSelection();
                return true;
            }

            if (keyCode == Keys.Delete)
            {
                RequestCloseSelection();
                return true;
            }

            if (keyCode == Keys.P)
            {
                TogglePinSelection();
                return true;
            }

            return false;
        }

        public void RequestCloseSelection()
        {
            AppWindow selected = GetSelectedWindow();
            Action<AppWindow> handler = CloseRequested;
            if (selected != null && handler != null)
            {
                handler(selected);
            }
        }

        public void TogglePinSelection()
        {
            AppWindow selected = GetSelectedWindow();
            Action<AppWindow> handler = PinToggleRequested;
            if (selected != null && handler != null)
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

            foreach (AppWindow item in windows)
            {
                if (!string.IsNullOrEmpty(window.IdentityKey)
                    && string.Equals(
                        item.IdentityKey,
                        window.IdentityKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    item.IsPinned = pinned;
                }
            }

            windows.ReorderPinnedFirst();
            selectedIndex = windows.IndexOf(window);
            EnsureSelectionVisible();
            SetActionStatus(
                pinned
                    ? "Pinned at the top."
                    : "Removed from pinned apps.");
        }

        public void SetActionStatus(string message)
        {
            actionStatus = message;
            Invalidate();
        }

        public void RemoveEntry(AppWindow window)
        {
            if (windows == null || !windows.Remove(window))
            {
                return;
            }

            if (windows.Count == 0)
            {
                HideSwitcher();
                return;
            }

            selectedIndex = Math.Min(selectedIndex, windows.Count - 1);
            firstVisibleIndex = Math.Min(
                firstVisibleIndex,
                Math.Max(0, windows.Count - visibleCapacity));
            ConfigureBounds();
            EnsureSelectionVisible();
            Invalidate();
        }

        public void ApplyClosedWindows(IList<ClosedWindowUpdate> updates)
        {
            if (windows == null || updates == null)
            {
                return;
            }

            AppWindow selectedWindow = GetSelectedWindow();
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
                        closedEntry.ApplySnapshot(update.ReplacementWindow);
                    }
                    else if (!ReferenceEquals(closedEntry, replacementEntry))
                    {
                        windows.Remove(closedEntry);
                    }

                    changed = true;
                    continue;
                }

                if (update.AddToRecent)
                {
                    if (closedEntry != null)
                    {
                        closedEntry.MarkRecentlyClosed();
                        changed = true;
                        recentChanged = true;
                    }
                    else if (!ContainsIdentity(
                        update.ClosedWindow.IdentityKey))
                    {
                        int insertionIndex = FindRecentInsertionIndex();
                        windows.Insert(
                            insertionIndex,
                            CreateRecentWindow(update.ClosedWindow));
                        changed = true;
                        recentChanged = true;
                    }
                }
                else if (closedEntry != null)
                {
                    windows.Remove(closedEntry);
                    changed = true;
                }
            }

            if (!changed)
            {
                return;
            }

            if (windows.Count == 0)
            {
                HideSwitcher();
                return;
            }

            int preservedIndex = selectedWindow == null
                ? -1
                : windows.IndexOf(selectedWindow);
            selectedIndex = preservedIndex >= 0
                ? preservedIndex
                : Math.Min(previousSelectedIndex, windows.Count - 1);
            firstVisibleIndex = Math.Min(
                firstVisibleIndex,
                Math.Max(0, windows.Count - visibleCapacity));
            ConfigureBounds();
            EnsureSelectionVisible();
            actionStatus = recentChanged
                ? "Closed. Press A / Enter to reopen."
                : "Application closed.";
            Invalidate();
        }

        private AppWindow FindSnapshotEntry(WindowSnapshot snapshot)
        {
            foreach (AppWindow window in windows)
            {
                if (window.TrackingKey == snapshot.TrackingKey
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

        private int FindRecentInsertionIndex()
        {
            for (int index = 0; index < windows.Count; index++)
            {
                if (windows[index].IsRecent && !windows[index].IsPinned)
                {
                    return index;
                }
            }

            return windows.Count;
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
            DrawWindows(graphics, logicalWidth);
            DrawFooter(graphics, logicalWidth, logicalHeight);
        }

        protected override void OnMouseMove(MouseEventArgs eventArgs)
        {
            base.OnMouseMove(eventArgs);

            int index = HitTestWindow(eventArgs.Location);
            Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
            if (index >= 0 && index != selectedIndex)
            {
                SelectIndex(index);
            }
        }

        protected override void OnMouseLeave(EventArgs eventArgs)
        {
            base.OnMouseLeave(eventArgs);
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDown(MouseEventArgs eventArgs)
        {
            base.OnMouseDown(eventArgs);
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

        protected override void OnMouseWheel(MouseEventArgs eventArgs)
        {
            base.OnMouseWheel(eventArgs);
            if (eventArgs.Delta != 0)
            {
                MoveSelection(eventArgs.Delta > 0 ? -1 : 1);
            }
        }

        protected override void OnDeactivate(EventArgs eventArgs)
        {
            base.OnDeactivate(eventArgs);
            if (showingContextMenu)
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
                    if (!switching && Visible && !ContainsFocus)
                    {
                        HideSwitcher();
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
            if (disposing)
            {
                DisposeWindows();
                headerFont.Dispose();
                subtitleFont.Dispose();
                shortcutFont.Dispose();
                itemTitleFont.Dispose();
                detailFont.Dispose();
                badgeFont.Dispose();
                itemMenu.Dispose();
            }

            base.Dispose(disposing);
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
            visibleCapacity = Math.Min(visibleCapacity, windows.Count);

            float logicalHeight =
                HeaderHeight + visibleCapacity * ItemHeight + FooterHeight;
            Size = new Size(
                (int)Math.Ceiling(PanelWidth * uiScale),
                (int)Math.Ceiling(logicalHeight * uiScale));
            Location = new Point(
                workArea.Left + (int)Math.Round(EdgeMargin * uiScale),
                workArea.Top + Math.Max(0, (workArea.Height - Height) / 2));
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
            using (var chipBrush = new SolidBrush(Color.FromArgb(47, 59, 78)))
            using (var chipBorder = new Pen(Color.FromArgb(83, 103, 133), 1f))
            using (var accentBrush = new SolidBrush(Color.FromArgb(103, 213, 255)))
            using (var dividerPen = new Pen(Color.FromArgb(48, 57, 72), 1f))
            {
                graphics.DrawString(
                    "QUICK SWITCH",
                    headerFont,
                    titleBrush,
                    new PointF(20f, 15f));

                string countText;
                if (windows == null)
                {
                    countText = "No apps";
                }
                else
                {
                    int recentCount = windows.Count - windows.OpenCount;
                    countText = windows.OpenCount + " open";
                    if (recentCount > 0)
                    {
                        countText += " | " + recentCount + " saved";
                    }
                }
                graphics.DrawString(
                    countText,
                    subtitleFont,
                    subtitleBrush,
                    new PointF(21f, 43f));

                var chipBounds = new RectangleF(width - 92f, 20f, 72f, 28f);
                using (GraphicsPath chipPath = CreateRoundedRectangle(chipBounds, 8f))
                {
                    graphics.FillPath(chipBrush, chipPath);
                    graphics.DrawPath(chipBorder, chipPath);
                }

                DrawCenteredString(
                    graphics,
                    "WIN + `",
                    shortcutFont,
                    accentBrush,
                    chipBounds);
                graphics.DrawLine(dividerPen, 12f, HeaderHeight - 1f, width - 12f, HeaderHeight - 1f);
            }
        }

        private void DrawWindows(Graphics graphics, float width)
        {
            if (windows == null)
            {
                return;
            }

            int lastIndex = Math.Min(
                windows.Count,
                firstVisibleIndex + visibleCapacity);
            for (int index = firstVisibleIndex; index < lastIndex; index++)
            {
                int slot = index - firstVisibleIndex;
                float y = HeaderHeight + slot * ItemHeight;
                DrawWindow(graphics, windows[index], index == selectedIndex, y, width);
            }

            if (windows.Count > visibleCapacity)
            {
                DrawScrollIndicator(graphics, width);
            }
        }

        private void DrawWindow(
            Graphics graphics,
            AppWindow window,
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
                Math.Max(20f, width - 98f),
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

            if (badgeText != null)
            {
                var badgeBounds = new RectangleF(width - 92f, y + 22f, 68f, 22f);
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
                trackHeight * visibleCapacity / windows.Count);
            float progress = windows.Count == visibleCapacity
                ? 0f
                : (float)firstVisibleIndex / (windows.Count - visibleCapacity);
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
            float top = height - FooterHeight;
            using (var dividerPen = new Pen(Color.FromArgb(48, 57, 72), 1f))
            using (var textBrush = new SolidBrush(Color.FromArgb(135, 147, 166)))
            {
                graphics.DrawLine(dividerPen, 12f, top, width - 12f, top);
                DrawCenteredString(
                    graphics,
                    "D-PAD / UP-DOWN  Navigate   A / ENTER  Open   B / ESC  Back",
                    detailFont,
                    textBrush,
                    new RectangleF(12f, top + 3f, width - 24f, 29f));
                DrawCenteredString(
                    graphics,
                    string.IsNullOrEmpty(actionStatus)
                        ? "X / DEL  Close   Y / P  Pin   Right-click  Actions"
                        : actionStatus,
                    detailFont,
                    textBrush,
                    new RectangleF(12f, top + 31f, width - 24f, 29f));
            }
        }

        private void MoveSelection(int delta)
        {
            if (windows == null || windows.Count == 0)
            {
                return;
            }

            int nextIndex = (selectedIndex + delta) % windows.Count;
            if (nextIndex < 0)
            {
                nextIndex += windows.Count;
            }

            SelectIndex(nextIndex);
        }

        private void SelectIndex(int index)
        {
            if (windows == null || index < 0 || index >= windows.Count)
            {
                return;
            }

            selectedIndex = index;
            EnsureSelectionVisible();
            Invalidate();
        }

        private void EnsureSelectionVisible()
        {
            if (selectedIndex < firstVisibleIndex)
            {
                firstVisibleIndex = selectedIndex;
            }
            else if (selectedIndex >= firstVisibleIndex + visibleCapacity)
            {
                firstVisibleIndex = selectedIndex - visibleCapacity + 1;
            }
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
            return index < windows.Count ? index : -1;
        }

        private void ActivateSelection()
        {
            if (windows == null
                || selectedIndex < 0
                || selectedIndex >= windows.Count)
            {
                return;
            }

            AppWindow selectedWindow = windows[selectedIndex];
            switching = true;
            Hide();
            string error;
            bool activated = selectedWindow.Activate(out error);
            DisposeWindows();
            switching = false;

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

        private void RequestForceStopSelection()
        {
            AppWindow selected = GetSelectedWindow();
            Action<AppWindow> handler = ForceStopRequested;
            if (selected != null && handler != null)
            {
                handler(selected);
            }
        }

        private AppWindow GetSelectedWindow()
        {
            if (windows == null
                || selectedIndex < 0
                || selectedIndex >= windows.Count)
            {
                return null;
            }

            return windows[selectedIndex];
        }

        private void ShowItemMenu(Point location)
        {
            AppWindow selected = GetSelectedWindow();
            if (selected == null)
            {
                return;
            }

            activateMenuItem.Text = selected.IsOpen ? "Switch to app" : "Open app";
            pinMenuItem.Text = selected.IsPinned ? "Unpin from top" : "Pin to top";
            closeMenuItem.Text = selected.IsOpen ? "Close app" : "Remove from recent";
            forceStopMenuItem.Visible = selected.IsOpen;
            showingContextMenu = true;
            itemMenu.Show(this, location);
        }

        private void OnItemMenuClosed(object sender, ToolStripDropDownClosedEventArgs eventArgs)
        {
            showingContextMenu = false;
            if (Visible)
            {
                Activate();
                Focus();
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
            if (windows != null)
            {
                windows.Dispose();
                windows = null;
            }
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
