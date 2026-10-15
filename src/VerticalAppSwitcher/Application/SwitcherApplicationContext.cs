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
        private ShortcutSetupForm shortcutSetupForm;
        private bool shortcutSetupVisible;
        private bool forceStopConfirmationVisible;
        private string shortcutRegistrationError;
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
            overlay.ShortcutSetupRequested += OnShortcutSetupRequested;

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
            overlay.NavigationModeChanged += OnOverlayNavigationModeChanged;

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
            ForegroundWindowOrder.Start();
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
            if (forceStopConfirmationVisible)
            {
                return;
            }

            if (shortcutSetupVisible)
            {
                if (shortcutSetupForm != null)
                {
                    shortcutSetupForm.CaptureRegisteredShortcut(hotKey.CurrentShortcut);
                }

                ShowShortcutSetup(null);
                return;
            }

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
            if (shortcutSetupVisible)
            {
                ShowShortcutSetup(null);
            }
            else
            {
                ShowSwitcher(true);
            }
        }

        private void OnExternalExitRequested()
        {
            DiagnosticLog.Write("External shutdown requested.");
            ExitThread();
        }

        private void OnNavigationKeyPressed(Keys keyData)
        {
            if (!forceStopConfirmationVisible && overlay.CanUseBrowsingHotKeys)
            {
                overlay.SetInputMode(false);
                overlay.HandleNavigationKey(keyData);
            }
        }

        private void OnControllerNavigationRequested(
            ControllerNavigationAction action)
        {
            if (forceStopConfirmationVisible)
            {
                return;
            }

            overlay.SetInputMode(true);
            DiagnosticLog.Write("Controller navigation: " + action + ".");

            switch (action)
            {
                case ControllerNavigationAction.Previous:
                    overlay.HandleNavigationKey(Keys.Up);
                    break;
                case ControllerNavigationAction.Next:
                    overlay.HandleNavigationKey(Keys.Down);
                    break;
                case ControllerNavigationAction.PreviousTab:
                    overlay.HandleNavigationKey(Keys.Left);
                    break;
                case ControllerNavigationAction.NextTab:
                    overlay.HandleNavigationKey(Keys.Right);
                    break;
                case ControllerNavigationAction.Activate:
                    overlay.HandleNavigationKey(Keys.Enter);
                    break;
                case ControllerNavigationAction.Cancel:
                    overlay.HandleNavigationKey(Keys.Escape);
                    break;
                case ControllerNavigationAction.Close:
                    overlay.RequestCloseSelection(CloseRequestSource.ControllerX);
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
            bool navigationEnabled = overlay.Visible && !forceStopConfirmationVisible;
            controllerNavigation.Enabled = navigationEnabled;
            closedAppTracker.SetOverlayVisible(overlay.Visible);
            OnOverlayNavigationModeChanged();
        }

        private void OnOverlayNavigationModeChanged()
        {
            try
            {
                hotKey.SetNavigationEnabled(
                    overlay.CanUseBrowsingHotKeys && !forceStopConfirmationVisible);
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

        private void OnShortcutSetupRequested()
        {
            if (shortcutSetupVisible)
            {
                ShowShortcutSetup(null);
                return;
            }

            if (!overlay.Visible)
            {
                return;
            }

            overlay.CloseForShortcutSetup();
            ShowShortcutSetup(null);
            if (!disposed && !overlay.Visible)
            {
                ShowSwitcher(true);
            }
        }

        private void ShowShortcutSetup(string initialStatus)
        {
            if (shortcutSetupVisible)
            {
                if (shortcutSetupForm != null && !shortcutSetupForm.ContainsFocus)
                {
                    shortcutSetupForm.Activate();
                    NativeMethods.ActivateForegroundWindow(shortcutSetupForm.Handle);
                }

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
                    ApplyShortcut,
                    hotKey.CheckShortcut))
                {
                    shortcutSetupForm = form;
                    form.Shown += delegate
                    {
                        form.Activate();
                        NativeMethods.ActivateForegroundWindow(form.Handle);
                    };
                    form.ShowDialog();
                }
            }
            finally
            {
                shortcutSetupForm = null;
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
            overlay.SetInvocationShortcut(shortcut.DisplayText);
        }

        private void ShowSwitcher()
        {
            ShowSwitcher(false);
        }

        private void ShowSwitcher(bool protectInitialVisibility)
        {
            AppWindowCollection activeWindows =
                WindowCatalog.GetSwitchableWindows();
            closedAppTracker.ObserveWindows(activeWindows);
            AppWindowCollection windows = stateStore.Compose(activeWindows);
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

        private void OnCloseRequested(
            AppWindow window,
            CloseRequestSource source)
        {
            if ((source == CloseRequestSource.KeyboardD
                    || source == CloseRequestSource.KeyboardDelete)
                && !overlay.CanUseBrowsingHotKeys)
            {
                return;
            }

            if (source == CloseRequestSource.KeyboardDelete
                && (window.IsOpen || !window.IsRecent))
            {
                return;
            }

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

            closedAppTracker.ObserveWindow(window);
            string error;
            if (!window.RequestClose(out error))
            {
                DiagnosticLog.Write(error);
                overlay.SetActionStatus("Normal window close blocked.");
                ShowNotification("Could not close app", error, ToolTipIcon.Warning);
                return;
            }

            overlay.SetActionStatus("Window close requested.");
        }

        private void OnForceStopRequested(AppWindow window)
        {
            if (!window.IsOpen)
            {
                overlay.SetActionStatus("This app is not currently running.");
                return;
            }

            DialogResult result;
            forceStopConfirmationVisible = true;
            overlay.SetConfirmationVisible(true);
            OnOverlayVisibilityChanged(overlay, EventArgs.Empty);
            try
            {
                result = MessageBox.Show(
                    overlay,
                    "Force-stop \"" + window.Title
                    + "\"?\r\n\r\nUnsaved work in this app will be lost.",
                    "Vertical App Switcher",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
            }
            finally
            {
                forceStopConfirmationVisible = false;
                overlay.SetConfirmationVisible(false);
                OnOverlayVisibilityChanged(overlay, EventArgs.Empty);
            }

            if (result == DialogResult.Yes)
            {
                ForceStop(window);
            }
        }

        private void ForceStop(AppWindow window)
        {
            closedAppTracker.ObserveWindow(window);
            string error;
            if (!window.ForceStop(out error))
            {
                ShowNotification("Could not force-stop app", error, ToolTipIcon.Warning);
                return;
            }

            overlay.SetActionStatus("Application force-stopped.");
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
            overlay.NavigationModeChanged -= OnOverlayNavigationModeChanged;
            overlay.ActivationSucceeded -= OnActivationSucceeded;
            overlay.PinToggleRequested -= OnPinToggleRequested;
            overlay.CloseRequested -= OnCloseRequested;
            overlay.ForceStopRequested -= OnForceStopRequested;
            overlay.ShortcutSetupRequested -= OnShortcutSetupRequested;
            closedAppTracker.Error -= OnClosedAppTrackerError;
            closedAppTracker.WindowsClosed -= OnWindowsClosed;
            closedAppTracker.Dispose();
            ForegroundWindowOrder.Stop();
            controllerNavigation.NavigationRequested -=
                OnControllerNavigationRequested;
            controllerNavigation.Error -= OnControllerNavigationError;
            controllerNavigation.Dispose();
            hotKey.Dispose();
            overlay.Dispose();
            DiagnosticLog.Write("Application stopped.");
        }
    }
}
