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
    internal sealed class WindowSnapshot
    {
        public WindowSnapshot(
            IntPtr handle,
            uint processId,
            string title,
            string processName,
            string executablePath,
            string launchTarget,
            string appUserModelId,
            bool isMinimized,
            bool isOnAnotherDesktop,
            bool isTray,
            string trayTooltip,
            DateTime? processStartTimeUtc = null)
        {
            Handle = handle;
            ProcessId = processId;
            Title = title;
            ProcessName = processName;
            ExecutablePath = executablePath;
            LaunchTarget = launchTarget;
            AppUserModelId = appUserModelId;
            IsMinimized = isMinimized;
            IsOnAnotherDesktop = isOnAnotherDesktop;
            IsTray = isTray;
            TrayTooltip = trayTooltip;
            ProcessStartTimeUtc = processStartTimeUtc;
        }

        public DateTime? ProcessStartTimeUtc { get; private set; }
        public IntPtr Handle { get; private set; }
        public uint ProcessId { get; private set; }
        public string Title { get; private set; }
        public string ProcessName { get; private set; }
        public string ExecutablePath { get; private set; }
        public string LaunchTarget { get; private set; }
        public string AppUserModelId { get; private set; }
        public bool IsMinimized { get; private set; }
        public bool IsOnAnotherDesktop { get; private set; }
        public bool IsTray { get; private set; }
        public string TrayTooltip { get; private set; }

        public bool IsOpen
        {
            get
            {
                return AppWindow.IsWindowOpen(
                    Handle,
                    ProcessId,
                    IsTray,
                    ProcessStartTimeUtc);
            }
        }

        public static WindowSnapshot FromWindow(AppWindow window)
        {
            return new WindowSnapshot(
                window.Handle,
                window.ProcessId,
                window.Title,
                window.ProcessName,
                window.ExecutablePath,
                window.LaunchTarget,
                window.AppUserModelId,
                window.IsMinimized,
                window.IsOnAnotherDesktop,
                window.IsTray,
                window.TrayTooltip,
                window.ProcessStartTimeUtc);
        }

        public string IdentityKey
        {
            get
            {
                return AppWindow.BuildIdentityKey(
                    ExecutablePath,
                    LaunchTarget);
            }
        }

        public IntPtr TrackingKey
        {
            get
            {
                return Handle != IntPtr.Zero
                    ? Handle
                    : new IntPtr(-((long)ProcessId + 1L));
            }
        }
    }
}
