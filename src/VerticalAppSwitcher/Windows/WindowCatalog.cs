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
    internal static class WindowCatalog
    {
        public static AppWindowCollection GetSwitchableWindows()
        {
            var windows = new List<AppWindow>();
            int currentProcessId;
            using (Process process = Process.GetCurrentProcess())
            {
                currentProcessId = process.Id;
            }

            IntPtr shellWindow = NativeMethods.GetShellWindow();
            using (VirtualDesktopWindowManager desktopManager =
                VirtualDesktopWindowManager.TryCreate())
            {
                NativeMethods.EnumWindows(
                    delegate(IntPtr handle, IntPtr parameter)
                    {
                        AppWindow window;
                        if (TryCreateWindow(
                                handle,
                                shellWindow,
                                currentProcessId,
                                desktopManager,
                                true,
                                out window))
                        {
                            windows.Add(window);
                        }

                        return true;
                    },
                    IntPtr.Zero);
            }

            TrayAppCatalog.AppendRunningTrayApps(
                windows,
                currentProcessId,
                true);
            return new AppWindowCollection(windows);
        }

        public static IList<WindowSnapshot> GetSwitchableWindowSnapshots()
        {
            var windows = new List<AppWindow>();
            var snapshots = new List<WindowSnapshot>();
            int currentProcessId;
            using (Process process = Process.GetCurrentProcess())
            {
                currentProcessId = process.Id;
            }

            IntPtr shellWindow = NativeMethods.GetShellWindow();
            using (VirtualDesktopWindowManager desktopManager =
                VirtualDesktopWindowManager.TryCreate())
            {
                NativeMethods.EnumWindows(
                    delegate(IntPtr handle, IntPtr parameter)
                    {
                        AppWindow window;
                        if (TryCreateWindow(
                                handle,
                                shellWindow,
                                currentProcessId,
                                desktopManager,
                                false,
                                out window))
                        {
                            windows.Add(window);
                        }

                        return true;
                    },
                    IntPtr.Zero);
            }

            TrayAppCatalog.AppendRunningTrayApps(
                windows,
                currentProcessId,
                false);
            foreach (AppWindow window in windows)
            {
                snapshots.Add(WindowSnapshot.FromWindow(window));
                window.Dispose();
            }

            return snapshots;
        }

        public static Icon GetExecutableIcon(string executablePath)
        {
            if (!string.IsNullOrEmpty(executablePath) && File.Exists(executablePath))
            {
                try
                {
                    Icon icon = Icon.ExtractAssociatedIcon(executablePath);
                    if (icon != null)
                    {
                        return icon;
                    }
                }
                catch (ArgumentException)
                {
                }
                catch (FileNotFoundException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return (Icon)SystemIcons.Application.Clone();
        }

        private static bool TryCreateWindow(
            IntPtr handle,
            IntPtr shellWindow,
            int currentProcessId,
            VirtualDesktopWindowManager desktopManager,
            bool loadIcon,
            out AppWindow window)
        {
            window = null;

            if (handle == IntPtr.Zero
                || handle == shellWindow
                || !NativeMethods.IsWindowVisible(handle))
            {
                return false;
            }

            bool isOnAnotherDesktop = false;
            int cloaked;
            int dwmResult = NativeMethods.DwmGetWindowAttribute(
                handle,
                NativeMethods.DwmaCloaked,
                out cloaked,
                Marshal.SizeOf(typeof(int)));
            if (dwmResult == 0 && cloaked != 0)
            {
                if (cloaked != NativeMethods.DwmCloakedShell
                    || desktopManager == null
                    || !desktopManager.IsWindowOnAnotherDesktop(handle))
                {
                    return false;
                }

                isOnAnotherDesktop = true;
            }

            int extendedStyle = NativeMethods.GetWindowLong(
                handle,
                NativeMethods.GwlExStyle);
            bool explicitAppWindow =
                (extendedStyle & NativeMethods.WsExAppWindow) != 0;

            if ((extendedStyle & NativeMethods.WsExToolWindow) != 0
                && !explicitAppWindow)
            {
                return false;
            }

            if ((extendedStyle & NativeMethods.WsExNoActivate) != 0
                && !explicitAppWindow)
            {
                return false;
            }

            IntPtr owner = NativeMethods.GetWindow(handle, NativeMethods.GwOwner);
            if (owner != IntPtr.Zero && !explicitAppWindow)
            {
                return false;
            }

            int titleLength = NativeMethods.GetWindowTextLength(handle);
            if (titleLength <= 0)
            {
                return false;
            }

            var titleBuilder = new StringBuilder(titleLength + 1);
            if (NativeMethods.GetWindowText(
                    handle,
                    titleBuilder,
                    titleBuilder.Capacity) == 0)
            {
                return false;
            }

            string title = titleBuilder.ToString().Trim();
            if (title.Length == 0)
            {
                return false;
            }

            uint processId;
            NativeMethods.GetWindowThreadProcessId(handle, out processId);
            if (processId == 0 || processId == currentProcessId)
            {
                return false;
            }

            string processName;
            string executablePath;
            Icon icon;
            GetPresentation(
                handle,
                processId,
                loadIcon,
                out processName,
                out executablePath,
                out icon);
            string launchTarget = IsExplorer(processName, executablePath)
                ? ExplorerFolderResolver.GetFolderPath(handle, title)
                : null;

            window = new AppWindow(
                handle,
                processId,
                title,
                processName,
                executablePath,
                launchTarget,
                icon,
                NativeMethods.IsIconic(handle));
            window.IsOnAnotherDesktop = isOnAnotherDesktop;
            return true;
        }

        private static void GetPresentation(
            IntPtr handle,
            uint processId,
            bool loadIcon,
            out string processName,
            out string executablePath,
            out Icon icon)
        {
            processName = "Application";
            icon = null;
            executablePath = null;

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    processName = process.ProcessName;
                    if (process.MainModule != null)
                    {
                        executablePath = process.MainModule.FileName;
                    }
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (Win32Exception)
            {
            }
            catch (NotSupportedException)
            {
            }

            if (!loadIcon)
            {
                return;
            }

            if (!string.IsNullOrEmpty(executablePath))
            {
                try
                {
                    icon = Icon.ExtractAssociatedIcon(executablePath);
                }
                catch (ArgumentException)
                {
                }
                catch (FileNotFoundException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            if (icon == null)
            {
                icon = TryGetWindowIcon(handle);
            }

            if (icon == null)
            {
                icon = (Icon)SystemIcons.Application.Clone();
            }
        }

        private static Icon TryGetWindowIcon(IntPtr handle)
        {
            UIntPtr result;
            NativeMethods.SendMessageTimeout(
                handle,
                NativeMethods.WmGetIcon,
                new UIntPtr(NativeMethods.IconSmall2),
                IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung,
                75,
                out result);

            if (result == UIntPtr.Zero)
            {
                NativeMethods.SendMessageTimeout(
                    handle,
                    NativeMethods.WmGetIcon,
                    new UIntPtr(NativeMethods.IconSmall),
                    IntPtr.Zero,
                    NativeMethods.SmtoAbortIfHung,
                    75,
                    out result);
            }

            if (result == UIntPtr.Zero)
            {
                return null;
            }

            IntPtr iconHandle = UIntPtrToIntPtr(result);
            using (Icon borrowedIcon = Icon.FromHandle(iconHandle))
            {
                return (Icon)borrowedIcon.Clone();
            }
        }

        private static IntPtr UIntPtrToIntPtr(UIntPtr value)
        {
            if (IntPtr.Size == 8)
            {
                return new IntPtr(unchecked((long)value.ToUInt64()));
            }

            return new IntPtr(unchecked((int)value.ToUInt32()));
        }

        private static bool IsExplorer(string processName, string executablePath)
        {
            return string.Equals(
                    processName,
                    "explorer",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.GetFileName(executablePath),
                    "explorer.exe",
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
