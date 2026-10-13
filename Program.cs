using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.Serialization;
using System.Security;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace VerticalAppSwitcher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] arguments)
        {
            bool shutdownRequested = HasArgument(arguments, "--shutdown");
            bool createdNew;
            using (var mutex = new Mutex(true, @"Local\VerticalAppSwitcher", out createdNew))
            {
                if (!createdNew)
                {
                    if (shutdownRequested)
                    {
                        if (!GlobalHotKey.RequestExitExistingInstance())
                        {
                            Environment.ExitCode = 1;
                        }

                        return;
                    }

                    if (!GlobalHotKey.RequestShowExistingInstance())
                    {
                        MessageBox.Show(
                            "Vertical App Switcher is already running, but its window could not be opened.",
                            "Vertical App Switcher",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Warning);
                    }

                    return;
                }

                if (shutdownRequested)
                {
                    return;
                }

                NativeMethods.SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                try
                {
                    bool showImmediately =
                        !HasArgument(arguments, "--background");

                    DiagnosticLog.Write("Application started.");
                    Application.Run(new SwitcherApplicationContext(showImmediately));
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Write("Fatal error: " + exception);
                    MessageBox.Show(
                        exception.Message,
                        "Vertical App Switcher failed",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
            }
        }

        private static bool HasArgument(string[] arguments, string expected)
        {
            foreach (string argument in arguments)
            {
                if (string.Equals(
                    argument,
                    expected,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class SwitcherApplicationContext : ApplicationContext
    {
        private readonly SwitcherOverlay overlay;
        private readonly GlobalHotKey hotKey;
        private readonly ShortcutSettingsStore shortcutSettingsStore;
        private ShortcutSettingsData shortcutSettings;
        private readonly XInputNavigationService controllerNavigation;
        private readonly AppStateStore stateStore;
        private readonly ClosedAppTracker closedAppTracker;
        private readonly NotifyIcon trayIcon;
        private readonly Icon appIcon;
        private readonly ContextMenuStrip trayMenu;
        private readonly ToolStripMenuItem openItem;
        private readonly ToolStripMenuItem startWithWindowsItem;
        private System.Windows.Forms.Timer initialShowTimer;
        private bool shortcutSetupVisible;
        private string shortcutRegistrationError;
        private string pendingCloseKey;
        private DateTime pendingCloseUntilUtc = DateTime.MinValue;
        private bool disposed;

        public SwitcherApplicationContext(bool showImmediately)
        {
            shortcutSettingsStore = new ShortcutSettingsStore();
            shortcutSettings = shortcutSettingsStore.Load();
            stateStore = new AppStateStore();
            overlay = new SwitcherOverlay();
            overlay.ActivationFailed += OnActivationFailed;
            overlay.ActivationSucceeded += OnActivationSucceeded;
            overlay.PinToggleRequested += OnPinToggleRequested;
            overlay.CloseRequested += OnCloseRequested;
            overlay.ForceStopRequested += OnForceStopRequested;

            hotKey = new GlobalHotKey(
                OnHotKeyPressed,
                OnExternalShowRequested,
                OnExternalExitRequested,
                OnNavigationKeyPressed);
            ShortcutDefinition configuredShortcut =
                ShortcutDefinition.Find(shortcutSettings.ShortcutId);
            if (!hotKey.TrySetShortcut(
                    configuredShortcut,
                    out shortcutRegistrationError))
            {
                DiagnosticLog.Write(shortcutRegistrationError);
            }

            controllerNavigation = new XInputNavigationService();
            controllerNavigation.NavigationRequested +=
                OnControllerNavigationRequested;
            controllerNavigation.Error += OnControllerNavigationError;
            closedAppTracker = new ClosedAppTracker(stateStore);
            closedAppTracker.Error += OnClosedAppTrackerError;
            closedAppTracker.WindowsClosed += OnWindowsClosed;
            overlay.VisibleChanged += OnOverlayVisibilityChanged;

            trayMenu = new ContextMenuStrip();

            openItem = new ToolStripMenuItem();
            openItem.Click += delegate { ShowSwitcher(); };
            trayMenu.Items.Add(openItem);

            var shortcutSetupItem =
                new ToolStripMenuItem("Set up shortcut...");
            shortcutSetupItem.Click += delegate { ShowShortcutSetup(null); };
            trayMenu.Items.Add(shortcutSetupItem);

            startWithWindowsItem = new ToolStripMenuItem("Start with Windows");
            startWithWindowsItem.Click += OnStartWithWindowsClick;
            trayMenu.Items.Add(startWithWindowsItem);

            trayMenu.Items.Add(new ToolStripSeparator());

            var exitItem = new ToolStripMenuItem("Exit");
            exitItem.Click += delegate { ExitThread(); };
            trayMenu.Items.Add(exitItem);

            appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath)
                ?? (Icon)SystemIcons.Application.Clone();
            trayIcon = new NotifyIcon
            {
                Icon = appIcon,
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += delegate { ShowSwitcher(); };
            UpdateShortcutText();

            string startupError;
            bool startupEnabled;
            if (StartupRegistration.TryGetEnabled(out startupEnabled, out startupError))
            {
                startWithWindowsItem.Checked = startupEnabled;
            }
            else
            {
                startWithWindowsItem.Enabled = false;
                DiagnosticLog.Write(startupError);
                ShowNotification("Startup setting unavailable", startupError, ToolTipIcon.Warning);
            }

            if (string.IsNullOrEmpty(shortcutRegistrationError))
            {
                ShowNotification(
                    "Vertical App Switcher is ready",
                    "Press "
                        + hotKey.CurrentShortcut.DisplayText
                        + " to open the app bar.",
                    ToolTipIcon.Info);
            }
            else
            {
                ShowNotification(
                    "Shortcut setup required",
                    shortcutRegistrationError,
                    ToolTipIcon.Warning);
            }

            controllerNavigation.Start();
            closedAppTracker.Start();

            if (showImmediately
                || !shortcutSettings.SetupCompleted
                || !string.IsNullOrEmpty(shortcutRegistrationError))
            {
                initialShowTimer = new System.Windows.Forms.Timer { Interval = 150 };
                initialShowTimer.Tick += OnInitialShowTimerTick;
                initialShowTimer.Start();
            }
        }

        protected override void ExitThreadCore()
        {
            DisposeResources();
            base.ExitThreadCore();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DisposeResources();
            }

            base.Dispose(disposing);
        }

        private void OnHotKeyPressed()
        {
            DiagnosticLog.Write("Global shortcut invoked.");

            if (overlay.Visible)
            {
                overlay.HandleNavigationKey(Keys.Escape);
                return;
            }

            ShowSwitcher();
        }

        private void OnExternalShowRequested()
        {
            ShowSwitcher(true);
        }

        private void OnExternalExitRequested()
        {
            DiagnosticLog.Write("External shutdown requested.");
            ExitThread();
        }

        private void OnNavigationKeyPressed(Keys keyData)
        {
            overlay.HandleNavigationKey(keyData);
        }

        private void OnControllerNavigationRequested(
            ControllerNavigationAction action)
        {
            DiagnosticLog.Write("Controller navigation: " + action + ".");

            switch (action)
            {
                case ControllerNavigationAction.Previous:
                    overlay.HandleNavigationKey(Keys.Up);
                    break;
                case ControllerNavigationAction.Next:
                    overlay.HandleNavigationKey(Keys.Down);
                    break;
                case ControllerNavigationAction.Activate:
                    overlay.HandleNavigationKey(Keys.Enter);
                    break;
                case ControllerNavigationAction.Cancel:
                    overlay.HandleNavigationKey(Keys.Escape);
                    break;
                case ControllerNavigationAction.Close:
                    overlay.RequestCloseSelection();
                    break;
                case ControllerNavigationAction.TogglePin:
                    overlay.TogglePinSelection();
                    break;
            }
        }

        private void OnControllerNavigationError(string message)
        {
            DiagnosticLog.Write(message);
            ShowNotification(
                "Controller navigation unavailable",
                message,
                ToolTipIcon.Warning);
        }

        private void OnOverlayVisibilityChanged(object sender, EventArgs eventArgs)
        {
            controllerNavigation.Enabled = overlay.Visible;
            closedAppTracker.SetOverlayVisible(overlay.Visible);

            try
            {
                hotKey.SetNavigationEnabled(overlay.Visible);
            }
            catch (Win32Exception exception)
            {
                string message =
                    "Could not enable keyboard navigation: " + exception.Message;
                DiagnosticLog.Write(message);
                ShowNotification(
                    "Keyboard navigation unavailable",
                    message,
                    ToolTipIcon.Warning);
            }
        }

        private void OnInitialShowTimerTick(object sender, EventArgs eventArgs)
        {
            initialShowTimer.Stop();
            initialShowTimer.Dispose();
            initialShowTimer = null;
            if (!shortcutSettings.SetupCompleted
                || !string.IsNullOrEmpty(shortcutRegistrationError))
            {
                ShowShortcutSetup(shortcutRegistrationError);
            }
            else
            {
                ShowSwitcher(true);
            }
        }

        private void ShowShortcutSetup(string initialStatus)
        {
            if (shortcutSetupVisible)
            {
                return;
            }

            shortcutSetupVisible = true;
            if (overlay.Visible)
            {
                overlay.HandleNavigationKey(Keys.Escape);
            }

            try
            {
                ShortcutDefinition selectedShortcut =
                    ShortcutDefinition.Find(shortcutSettings.ShortcutId)
                    ?? ShortcutDefinition.Default;
                using (var form = new ShortcutSetupForm(
                    selectedShortcut,
                    initialStatus,
                    appIcon,
                    ApplyShortcut))
                {
                    form.ShowDialog();
                }
            }
            finally
            {
                shortcutSetupVisible = false;
            }
        }

        private string ApplyShortcut(ShortcutDefinition shortcut)
        {
            ShortcutDefinition previousShortcut = hotKey.CurrentShortcut;
            string registrationError;
            if (!hotKey.TrySetShortcut(shortcut, out registrationError))
            {
                DiagnosticLog.Write(registrationError);
                return registrationError;
            }

            var updatedSettings = new ShortcutSettingsData
            {
                ShortcutId = shortcut.Id,
                SetupCompleted = true
            };

            try
            {
                shortcutSettingsStore.Save(updatedSettings);
            }
            catch (Exception exception)
                when (exception is IOException
                    || exception is UnauthorizedAccessException
                    || exception is SecurityException
                    || exception is SerializationException
                    || exception is InvalidOperationException)
            {
                string rollbackError;
                if (previousShortcut != null
                    && !hotKey.TrySetShortcut(previousShortcut, out rollbackError))
                {
                    DiagnosticLog.Write(rollbackError);
                }

                string message =
                    "The shortcut could not be saved: " + exception.Message;
                DiagnosticLog.Write(message);
                return message;
            }

            shortcutSettings = updatedSettings;
            shortcutRegistrationError = null;
            UpdateShortcutText();
            ShowNotification(
                "Shortcut updated",
                "Press " + shortcut.DisplayText + " to open the app bar.",
                ToolTipIcon.Info);
            return null;
        }

        private void UpdateShortcutText()
        {
            ShortcutDefinition shortcut = hotKey.CurrentShortcut
                ?? ShortcutDefinition.Find(shortcutSettings.ShortcutId)
                ?? ShortcutDefinition.Default;
            openItem.Text = "Open switcher\t" + shortcut.DisplayText;
            trayIcon.Text = "Vertical App Switcher - " + shortcut.DisplayText;
        }

        private void ShowSwitcher()
        {
            ShowSwitcher(false);
        }

        private void ShowSwitcher(bool protectInitialVisibility)
        {
            AppWindowCollection windows = stateStore.Compose(
                WindowCatalog.GetSwitchableWindows());
            if (windows.Count == 0)
            {
                windows.Dispose();
                ShowNotification(
                    "No apps found",
                    "There are no switchable application windows on this desktop.",
                    ToolTipIcon.Info);
                return;
            }

            overlay.ShowWindows(windows, protectInitialVisibility);
        }

        private void OnActivationFailed(string message)
        {
            DiagnosticLog.Write(message);
            ShowNotification("Could not switch apps", message, ToolTipIcon.Warning);
        }

        private void OnActivationSucceeded(AppWindow window)
        {
            if (!window.IsRecent)
            {
                return;
            }

            try
            {
                stateStore.RemoveRecent(window);
            }
            catch (IOException exception)
            {
                ShowStateError("Recently closed app could not be updated", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ShowStateError("Recently closed app could not be updated", exception);
            }
            catch (SecurityException exception)
            {
                ShowStateError("Recently closed app could not be updated", exception);
            }
            catch (SerializationException exception)
            {
                ShowStateError("Recently closed app could not be updated", exception);
            }
        }

        private void OnPinToggleRequested(AppWindow window)
        {
            try
            {
                bool pinned = stateStore.TogglePin(window);
                overlay.ApplyPinState(window, pinned);
            }
            catch (InvalidOperationException exception)
            {
                ShowStateError("This app cannot be pinned", exception);
            }
            catch (IOException exception)
            {
                ShowStateError("Pinned apps could not be saved", exception);
            }
            catch (UnauthorizedAccessException exception)
            {
                ShowStateError("Pinned apps could not be saved", exception);
            }
            catch (SecurityException exception)
            {
                ShowStateError("Pinned apps could not be saved", exception);
            }
            catch (SerializationException exception)
            {
                ShowStateError("Pinned apps could not be saved", exception);
            }
        }

        private void OnCloseRequested(AppWindow window)
        {
            if (!window.IsOpen)
            {
                if (window.IsRecent)
                {
                    try
                    {
                        stateStore.RemoveRecent(window);
                        overlay.RemoveEntry(window);
                        overlay.SetActionStatus("Removed from recently closed.");
                    }
                    catch (IOException exception)
                    {
                        ShowStateError("Recently closed app could not be removed", exception);
                    }
                    catch (UnauthorizedAccessException exception)
                    {
                        ShowStateError("Recently closed app could not be removed", exception);
                    }
                    catch (SecurityException exception)
                    {
                        ShowStateError("Recently closed app could not be removed", exception);
                    }
                    catch (SerializationException exception)
                    {
                        ShowStateError("Recently closed app could not be removed", exception);
                    }
                }
                else
                {
                    overlay.SetActionStatus("This pinned app is not currently running.");
                }

                return;
            }

            string closeKey = GetCloseKey(window);
            if (string.Equals(
                    pendingCloseKey,
                    closeKey,
                    StringComparison.Ordinal)
                && DateTime.UtcNow <= pendingCloseUntilUtc)
            {
                ForceStop(window);
                return;
            }

            string error;
            if (!window.RequestClose(out error))
            {
                pendingCloseKey = closeKey;
                pendingCloseUntilUtc = DateTime.UtcNow.AddSeconds(15);
                DiagnosticLog.Write(error);
                overlay.SetActionStatus(
                    "Normal close blocked. Press X / Delete again to force stop.");
                ShowNotification("Could not close app", error, ToolTipIcon.Warning);
                return;
            }

            pendingCloseKey = closeKey;
            pendingCloseUntilUtc = DateTime.UtcNow.AddSeconds(15);
            overlay.SetActionStatus(
                "Close requested. Press X / Delete again to force stop.");
        }

        private void OnForceStopRequested(AppWindow window)
        {
            if (!window.IsOpen)
            {
                overlay.SetActionStatus("This app is not currently running.");
                return;
            }

            DialogResult result = MessageBox.Show(
                "Force-stop \"" + window.Title
                + "\"?\r\n\r\nUnsaved work in this app will be lost.",
                "Vertical App Switcher",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);
            if (result == DialogResult.Yes)
            {
                ForceStop(window);
            }
        }

        private void ForceStop(AppWindow window)
        {
            string error;
            if (!window.ForceStop(out error))
            {
                ShowNotification("Could not force-stop app", error, ToolTipIcon.Warning);
                return;
            }

            pendingCloseKey = null;
            pendingCloseUntilUtc = DateTime.MinValue;
            overlay.SetActionStatus("Application force-stopped.");
        }

        private static string GetCloseKey(AppWindow window)
        {
            return window.Handle != IntPtr.Zero
                ? "window:" + window.Handle.ToInt64()
                : "process:"
                    + window.ProcessId
                    + ":"
                    + window.IdentityKey;
        }

        private void OnClosedAppTrackerError(string message)
        {
            DiagnosticLog.Write(message);
            ShowNotification(
                "Recently closed apps unavailable",
                message,
                ToolTipIcon.Warning);
        }

        private void OnWindowsClosed(IList<ClosedWindowUpdate> updates)
        {
            if (overlay.Visible)
            {
                overlay.ApplyClosedWindows(updates);
            }
        }

        private void ShowStateError(string title, Exception exception)
        {
            string message = title + ": " + exception.Message;
            DiagnosticLog.Write(message);
            ShowNotification(title, exception.Message, ToolTipIcon.Warning);
        }

        private void OnStartWithWindowsClick(object sender, EventArgs eventArgs)
        {
            bool enabled = !startWithWindowsItem.Checked;
            string error;
            if (!StartupRegistration.TrySetEnabled(enabled, out error))
            {
                DiagnosticLog.Write(error);
                MessageBox.Show(
                    error,
                    "Vertical App Switcher",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            startWithWindowsItem.Checked = enabled;
            ShowNotification(
                "Startup setting updated",
                enabled
                    ? "Vertical App Switcher will start when you sign in."
                    : "Vertical App Switcher will no longer start automatically.",
                ToolTipIcon.Info);
        }

        private void ShowNotification(string title, string text, ToolTipIcon icon)
        {
            trayIcon.BalloonTipTitle = title;
            trayIcon.BalloonTipText = text;
            trayIcon.BalloonTipIcon = icon;
            trayIcon.ShowBalloonTip(2500);
        }

        private void DisposeResources()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (initialShowTimer != null)
            {
                initialShowTimer.Stop();
                initialShowTimer.Dispose();
                initialShowTimer = null;
            }

            trayIcon.Visible = false;
            trayIcon.Dispose();
            appIcon.Dispose();
            trayMenu.Dispose();
            overlay.VisibleChanged -= OnOverlayVisibilityChanged;
            overlay.ActivationSucceeded -= OnActivationSucceeded;
            overlay.PinToggleRequested -= OnPinToggleRequested;
            overlay.CloseRequested -= OnCloseRequested;
            overlay.ForceStopRequested -= OnForceStopRequested;
            closedAppTracker.Error -= OnClosedAppTrackerError;
            closedAppTracker.WindowsClosed -= OnWindowsClosed;
            closedAppTracker.Dispose();
            controllerNavigation.NavigationRequested -=
                OnControllerNavigationRequested;
            controllerNavigation.Error -= OnControllerNavigationError;
            controllerNavigation.Dispose();
            hotKey.Dispose();
            overlay.Dispose();
            DiagnosticLog.Write("Application stopped.");
        }
    }

    internal sealed class GlobalHotKey : NativeWindow, IDisposable
    {
        private const int HotKeyId = 1;
        private const int UpKeyId = 2;
        private const int DownKeyId = 3;
        private const int EnterKeyId = 4;
        private const int EscapeKeyId = 5;
        private const int DeleteKeyId = 6;
        private const int PinKeyId = 7;
        private const int HomeKeyId = 8;
        private const int EndKeyId = 9;
        private const int ShiftedHotKeyId = 10;
        private const string WindowCaption = "VerticalAppSwitcherHotKey";
        private readonly Action pressed;
        private readonly Action showRequested;
        private readonly Action exitRequested;
        private readonly Action<Keys> navigationPressed;
        private ShortcutDefinition currentShortcut;
        private bool shiftedRegistered;
        private bool navigationRegistered;

        public GlobalHotKey(
            Action pressed,
            Action showRequested,
            Action exitRequested,
            Action<Keys> navigationPressed)
        {
            this.pressed = pressed;
            this.showRequested = showRequested;
            this.exitRequested = exitRequested;
            this.navigationPressed = navigationPressed;
            CreateHandle(new CreateParams { Caption = WindowCaption });
        }

        public ShortcutDefinition CurrentShortcut
        {
            get { return currentShortcut; }
        }

        public bool TrySetShortcut(
            ShortcutDefinition shortcut,
            out string error)
        {
            if (shortcut == null)
            {
                throw new ArgumentNullException("shortcut");
            }

            error = null;
            if (currentShortcut != null
                && string.Equals(
                    currentShortcut.Id,
                    shortcut.Id,
                    StringComparison.Ordinal))
            {
                return true;
            }

            ShortcutDefinition previousShortcut = currentShortcut;
            UnregisterInvocationHotKeys();

            if (!NativeMethods.RegisterHotKey(
                    Handle,
                    HotKeyId,
                    shortcut.Modifiers,
                    shortcut.VirtualKey))
            {
                int nativeError =
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                error =
                    shortcut.DisplayText
                    + " is unavailable. Another application may already use it. "
                    + new Win32Exception(nativeError).Message;

                if (previousShortcut != null)
                {
                    string restoreError;
                    if (!RegisterShortcut(previousShortcut, out restoreError))
                    {
                        error += " The previous shortcut also could not be restored: "
                            + restoreError;
                    }
                }

                return false;
            }

            currentShortcut = shortcut;
            RegisterShiftedAliasIfNeeded(shortcut);
            DiagnosticLog.Write(
                "Global shortcut registered: "
                + shortcut.DisplayText
                + (shiftedRegistered ? " (plus Win + Shift + ` alias)" : string.Empty)
                + ".");
            return true;
        }

        public void SetNavigationEnabled(bool enabled)
        {
            if (enabled == navigationRegistered)
            {
                return;
            }

            if (!enabled)
            {
                UnregisterNavigationKeys();
                return;
            }

            try
            {
                RegisterNavigationKey(UpKeyId, NativeMethods.VirtualKeyUp);
                RegisterNavigationKey(DownKeyId, NativeMethods.VirtualKeyDown);
                RegisterNavigationKey(EnterKeyId, NativeMethods.VirtualKeyReturn);
                RegisterNavigationKey(EscapeKeyId, NativeMethods.VirtualKeyEscape);
                RegisterNavigationKey(DeleteKeyId, NativeMethods.VirtualKeyDelete);
                RegisterNavigationKey(PinKeyId, NativeMethods.VirtualKeyP);
                RegisterNavigationKey(HomeKeyId, NativeMethods.VirtualKeyHome);
                RegisterNavigationKey(EndKeyId, NativeMethods.VirtualKeyEnd);
                navigationRegistered = true;
            }
            catch (Win32Exception)
            {
                UnregisterNavigationKeys();
                throw;
            }
        }

        public static bool RequestShowExistingInstance()
        {
            return RequestExistingInstance(NativeMethods.WmShowSwitcher);
        }

        public static bool RequestExitExistingInstance()
        {
            return RequestExistingInstance(NativeMethods.WmExitSwitcher);
        }

        private static bool RequestExistingInstance(int message)
        {
            IntPtr window = NativeMethods.FindWindow(null, WindowCaption);
            return window != IntPtr.Zero
                && NativeMethods.PostMessage(
                    window,
                    message,
                    IntPtr.Zero,
                    IntPtr.Zero);
        }

        public void Dispose()
        {
            UnregisterNavigationKeys();
            UnregisterInvocationHotKeys();

            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }
        }

        protected override void WndProc(ref Message message)
        {
            int hotKeyId = message.WParam.ToInt32();
            if (message.Msg == NativeMethods.WmHotKey
                && (hotKeyId == HotKeyId
                    || hotKeyId == ShiftedHotKeyId))
            {
                pressed();
            }
            else if (message.Msg == NativeMethods.WmHotKey && navigationRegistered)
            {
                switch (message.WParam.ToInt32())
                {
                    case UpKeyId:
                        navigationPressed(Keys.Up);
                        break;
                    case DownKeyId:
                        navigationPressed(Keys.Down);
                        break;
                    case EnterKeyId:
                        navigationPressed(Keys.Enter);
                        break;
                    case EscapeKeyId:
                        navigationPressed(Keys.Escape);
                        break;
                    case DeleteKeyId:
                        navigationPressed(Keys.Delete);
                        break;
                    case PinKeyId:
                        navigationPressed(Keys.P);
                        break;
                    case HomeKeyId:
                        navigationPressed(Keys.Home);
                        break;
                    case EndKeyId:
                        navigationPressed(Keys.End);
                        break;
                }
            }
            else if (message.Msg == NativeMethods.WmShowSwitcher)
            {
                showRequested();
            }
            else if (message.Msg == NativeMethods.WmExitSwitcher)
            {
                exitRequested();
                return;
            }

            base.WndProc(ref message);
        }

        private void RegisterNavigationKey(int id, uint virtualKey)
        {
            if (!NativeMethods.RegisterHotKey(Handle, id, 0, virtualKey))
            {
                throw new Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error(),
                    "Windows could not reserve a navigation key while the app bar is open.");
            }
        }

        private bool RegisterShortcut(
            ShortcutDefinition shortcut,
            out string error)
        {
            if (!NativeMethods.RegisterHotKey(
                    Handle,
                    HotKeyId,
                    shortcut.Modifiers,
                    shortcut.VirtualKey))
            {
                error = new Win32Exception(
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message;
                return false;
            }

            currentShortcut = shortcut;
            RegisterShiftedAliasIfNeeded(shortcut);
            error = null;
            return true;
        }

        private void RegisterShiftedAliasIfNeeded(ShortcutDefinition shortcut)
        {
            if (!shortcut.RegisterShiftedAlias)
            {
                return;
            }

            shiftedRegistered = NativeMethods.RegisterHotKey(
                Handle,
                ShiftedHotKeyId,
                shortcut.Modifiers | NativeMethods.ModShift,
                shortcut.VirtualKey);
            if (!shiftedRegistered)
            {
                DiagnosticLog.Write(
                    "Optional shortcut Win + Shift + ` is unavailable: "
                    + new Win32Exception(
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message);
            }
        }

        private void UnregisterInvocationHotKeys()
        {
            if (currentShortcut != null)
            {
                UnregisterInvocationHotKey(
                    HotKeyId,
                    currentShortcut.DisplayText);
                currentShortcut = null;
            }

            if (shiftedRegistered)
            {
                UnregisterInvocationHotKey(
                    ShiftedHotKeyId,
                    "Win + Shift + `");
                shiftedRegistered = false;
            }
        }

        private void UnregisterInvocationHotKey(int id, string displayName)
        {
            if (!NativeMethods.UnregisterHotKey(Handle, id))
            {
                DiagnosticLog.Write(
                    "Could not unregister "
                    + displayName
                    + ": "
                    + new Win32Exception(
                        System.Runtime.InteropServices.Marshal.GetLastWin32Error()).Message);
            }
        }

        private void UnregisterNavigationKeys()
        {
            UnregisterNavigationKey(UpKeyId);
            UnregisterNavigationKey(DownKeyId);
            UnregisterNavigationKey(EnterKeyId);
            UnregisterNavigationKey(EscapeKeyId);
            UnregisterNavigationKey(DeleteKeyId);
            UnregisterNavigationKey(PinKeyId);
            UnregisterNavigationKey(HomeKeyId);
            UnregisterNavigationKey(EndKeyId);
            navigationRegistered = false;
        }

        private void UnregisterNavigationKey(int id)
        {
            if (!NativeMethods.UnregisterHotKey(Handle, id))
            {
                int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                if (error != 0 && error != NativeMethods.ErrorHotKeyNotRegistered)
                {
                    DiagnosticLog.Write(
                        "Could not unregister a navigation key: "
                        + new Win32Exception(error).Message);
                }
            }
        }
    }

    internal static class StartupRegistration
    {
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "VerticalAppSwitcher";

        public static bool TryGetEnabled(out bool enabled, out string error)
        {
            enabled = false;
            error = null;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                    {
                        return true;
                    }

                    string value = Convert.ToString(key.GetValue(ValueName));
                    enabled = string.Equals(
                        value,
                        GetStartupCommand(),
                        StringComparison.OrdinalIgnoreCase);
                    return true;
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "Windows denied access to the startup setting: " + exception.Message;
                return false;
            }
            catch (SecurityException exception)
            {
                error = "Windows blocked access to the startup setting: " + exception.Message;
                return false;
            }
            catch (IOException exception)
            {
                error = "The startup setting could not be read: " + exception.Message;
                return false;
            }
        }

        public static bool TrySetEnabled(bool enabled, out string error)
        {
            error = null;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        error = "Windows could not open the current-user startup settings.";
                        return false;
                    }

                    if (enabled)
                    {
                        key.SetValue(ValueName, GetStartupCommand(), RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }

                    return true;
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "Windows denied access to the startup setting: " + exception.Message;
                return false;
            }
            catch (SecurityException exception)
            {
                error = "Windows blocked access to the startup setting: " + exception.Message;
                return false;
            }
            catch (IOException exception)
            {
                error = "The startup setting could not be updated: " + exception.Message;
                return false;
            }
        }

        private static string GetStartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\" --background";
        }
    }

    internal static class DiagnosticLog
    {
        private static readonly object SyncRoot = new object();

        public static void Write(string message)
        {
            string line = DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine;

            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VerticalAppSwitcher");
                Directory.CreateDirectory(directory);

                lock (SyncRoot)
                {
                    File.AppendAllText(Path.Combine(directory, "switcher.log"), line);
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                Debug.WriteLine("Could not write switcher log: " + exception.Message);
            }
            catch (IOException exception)
            {
                Debug.WriteLine("Could not write switcher log: " + exception.Message);
            }
        }
    }
}
