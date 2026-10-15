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
        private const int CloseDKeyId = 11;
        private const int LeftKeyId = 12;
        private const int RightKeyId = 13;
        private const int ShortcutProbeKeyId = 14;
        private const int SearchKeyId = 16;
        private const int FirstNumberKeyId = 20;
        private const int FirstNumberPadKeyId = 30;
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

        public string CheckShortcut(ShortcutDefinition shortcut)
        {
            if (shortcut == null)
            {
                return "Choose a shortcut to test.";
            }

            if (currentShortcut != null
                && currentShortcut.Modifiers == shortcut.Modifiers
                && currentShortcut.VirtualKey == shortcut.VirtualKey)
            {
                return null;
            }

            if (currentShortcut != null
                && shiftedRegistered
                && currentShortcut.RegisterShiftedAlias
                && shortcut.Modifiers
                    == (currentShortcut.Modifiers | NativeMethods.ModShift)
                && shortcut.VirtualKey == currentShortcut.VirtualKey)
            {
                return null;
            }

            if (!NativeMethods.RegisterHotKey(
                    Handle,
                    ShortcutProbeKeyId,
                    shortcut.Modifiers,
                    shortcut.VirtualKey))
            {
                int nativeError =
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                return shortcut.DisplayText
                    + " is unavailable. Another application may already use it. "
                    + new Win32Exception(nativeError).Message;
            }

            if (!NativeMethods.UnregisterHotKey(Handle, ShortcutProbeKeyId))
            {
                int nativeError =
                    System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                string error = "Could not release the shortcut test: "
                    + new Win32Exception(nativeError).Message;
                DiagnosticLog.Write(error);
                return error;
            }

            return null;
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
                RegisterNavigationKey(CloseDKeyId, (uint)Keys.D, NativeMethods.ModNoRepeat);
                RegisterNavigationKey(SearchKeyId, (uint)Keys.S, NativeMethods.ModNoRepeat);
                RegisterNavigationKey(LeftKeyId, (uint)Keys.Left);
                RegisterNavigationKey(RightKeyId, (uint)Keys.Right);
                for (int index = 0; index < 9; index++)
                {
                    RegisterNavigationKey(
                        FirstNumberKeyId + index,
                        (uint)Keys.D1 + (uint)index,
                        NativeMethods.ModControl | NativeMethods.ModNoRepeat);
                    RegisterNavigationKey(
                        FirstNumberPadKeyId + index,
                        (uint)Keys.NumPad1 + (uint)index,
                        NativeMethods.ModControl | NativeMethods.ModNoRepeat);
                }

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
                if (hotKeyId >= FirstNumberKeyId && hotKeyId < FirstNumberKeyId + 9)
                {
                    navigationPressed(Keys.Control | (Keys)((int)Keys.D1 + hotKeyId - FirstNumberKeyId));
                }
                else if (hotKeyId >= FirstNumberPadKeyId && hotKeyId < FirstNumberPadKeyId + 9)
                {
                    navigationPressed(Keys.Control | (Keys)((int)Keys.NumPad1 + hotKeyId - FirstNumberPadKeyId));
                }

                switch (hotKeyId)
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
                    case CloseDKeyId:
                        navigationPressed(Keys.D);
                        break;
                    case SearchKeyId:
                        navigationPressed(Keys.S);
                        break;
                    case LeftKeyId:
                        navigationPressed(Keys.Left);
                        break;
                    case RightKeyId:
                        navigationPressed(Keys.Right);
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

        private void RegisterNavigationKey(
            int id,
            uint virtualKey,
            uint modifiers = 0)
        {
            if (!NativeMethods.RegisterHotKey(Handle, id, modifiers, virtualKey))
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
            navigationRegistered = false;
            UnregisterNavigationKey(UpKeyId);
            UnregisterNavigationKey(DownKeyId);
            UnregisterNavigationKey(EnterKeyId);
            UnregisterNavigationKey(EscapeKeyId);
            UnregisterNavigationKey(DeleteKeyId);
            UnregisterNavigationKey(PinKeyId);
            UnregisterNavigationKey(HomeKeyId);
            UnregisterNavigationKey(EndKeyId);
            UnregisterNavigationKey(CloseDKeyId);
            UnregisterNavigationKey(SearchKeyId);
            UnregisterNavigationKey(LeftKeyId);
            UnregisterNavigationKey(RightKeyId);
            for (int index = 0; index < 9; index++)
            {
                UnregisterNavigationKey(FirstNumberKeyId + index);
                UnregisterNavigationKey(FirstNumberPadKeyId + index);
            }
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
}
