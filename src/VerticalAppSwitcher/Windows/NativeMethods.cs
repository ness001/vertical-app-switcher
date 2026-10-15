using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VerticalAppSwitcher
{
    internal static class NativeMethods
    {
        internal const int WmHotKey = 0x0312;
        internal const int WmClose = 0x0010;
        internal const int WmShowSwitcher = 0x8001;
        internal const int WmExitSwitcher = 0x8002;
        internal const int WmGetIcon = 0x007F;
        internal const int GwlExStyle = -20;
        internal const int GwOwner = 4;
        internal const int WsExToolWindow = 0x00000080;
        internal const int WsExAppWindow = 0x00040000;
        internal const int WsExNoActivate = 0x08000000;
        internal const int DwmaCloaked = 14;
        internal const int DwmCloakedShell = 0x00000002;
        internal const int SwShow = 5;
        internal const int SwRestore = 9;
        internal const uint ModWin = 0x0008;
        internal const uint ModAlt = 0x0001;
        internal const uint ModControl = 0x0002;
        internal const uint ModShift = 0x0004;
        internal const uint ModNoRepeat = 0x4000;
        internal const uint VirtualKeyOem3 = 0xC0;
        internal const uint VirtualKeySpace = 0x20;
        internal const uint VirtualKeyUp = 0x26;
        internal const uint VirtualKeyDown = 0x28;
        internal const uint VirtualKeyReturn = 0x0D;
        internal const uint VirtualKeyEscape = 0x1B;
        internal const uint VirtualKeyDelete = 0x2E;
        internal const uint VirtualKeyHome = 0x24;
        internal const uint VirtualKeyEnd = 0x23;
        internal const uint VirtualKeyP = 0x50;
        internal const uint IconSmall = 0;
        internal const uint IconSmall2 = 2;
        internal const uint SmtoAbortIfHung = 0x0002;
        internal const int ErrorHotKeyNotRegistered = 1419;

        internal delegate bool EnumWindowsProcedure(IntPtr handle, IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        internal static extern uint GetDpiForSystem();

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(
            IntPtr window,
            int id,
            uint modifiers,
            uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr FindWindow(
            string className,
            string windowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            IntPtr window,
            int message,
            IntPtr wordParameter,
            IntPtr longParameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumWindows(
            EnumWindowsProcedure procedure,
            IntPtr parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(
            IntPtr window,
            StringBuilder text,
            int maximumCount);

        [DllImport("user32.dll")]
        internal static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetWindow(IntPtr window, int command);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            IntPtr window,
            out uint processId);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            IntPtr window,
            int attribute,
            out int value,
            int valueSize);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SendMessageTimeout(
            IntPtr window,
            uint message,
            UIntPtr wordParameter,
            IntPtr longParameter,
            uint flags,
            uint timeout,
            out UIntPtr result);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool BringWindowToTop(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetActiveWindow(IntPtr window);

        [DllImport("user32.dll")]
        internal static extern IntPtr SetFocus(IntPtr window);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AttachThreadInput(
            uint threadId,
            uint attachToThreadId,
            [MarshalAs(UnmanagedType.Bool)] bool attach);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern SafeProcessHandle OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(
            SafeProcessHandle process,
            out long creationTime,
            out long exitTime,
            out long kernelTime,
            out long userTime);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(
            SafeProcessHandle process,
            uint exitCode);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        internal static bool ActivateForegroundWindow(IntPtr window)
        {
            IntPtr foregroundWindow = GetForegroundWindow();
            uint ignoredProcessId;
            uint foregroundThreadId = foregroundWindow == IntPtr.Zero
                ? 0
                : GetWindowThreadProcessId(foregroundWindow, out ignoredProcessId);
            uint currentThreadId = GetCurrentThreadId();
            bool attached = foregroundThreadId != 0
                && foregroundThreadId != currentThreadId
                && AttachThreadInput(currentThreadId, foregroundThreadId, true);

            try
            {
                BringWindowToTop(window);
                SetActiveWindow(window);
                SetFocus(window);
                SetForegroundWindow(window);
                return GetForegroundWindow() == window;
            }
            finally
            {
                if (attached)
                {
                    AttachThreadInput(currentThreadId, foregroundThreadId, false);
                }
            }
        }
    }
}
