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
    internal sealed class AppWindow : IDisposable
    {
        private const uint ProcessTerminate = 0x0001;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ErrorInvalidParameter = 87;
        private bool processIdentityCaptured;

        public AppWindow(
            IntPtr handle,
            uint processId,
            string title,
            string processName,
            string executablePath,
            string launchTarget,
            Icon icon,
            bool minimized)
        {
            Handle = handle;
            ProcessId = processId;
            Title = title;
            ProcessName = processName;
            ExecutablePath = executablePath;
            LaunchTarget = launchTarget;
            Icon = icon;
            IsMinimized = minimized;
            PinOrder = int.MaxValue;
        }

        public DateTime? ProcessStartTimeUtc { get; private set; }
        public IntPtr Handle { get; private set; }
        public uint ProcessId { get; private set; }
        public string Title { get; private set; }
        public string ProcessName { get; private set; }
        public string ExecutablePath { get; private set; }
        public string LaunchTarget { get; private set; }
        public Icon Icon { get; private set; }
        public bool IsMinimized { get; private set; }
        public bool IsOnAnotherDesktop { get; set; }
        public bool IsPinned { get; set; }
        public int PinOrder { get; set; }
        public bool IsRecent { get; set; }
        public bool IsTray { get; set; }
        public string TrayTooltip { get; set; }
        public string AppUserModelId { get; set; }

        public bool IsOpen
        {
            get { return IsWindowOpen(Handle, ProcessId, IsTray, ProcessStartTimeUtc); }
        }

        internal void CaptureProcessIdentity()
        {
            if (processIdentityCaptured || ProcessId == 0)
            {
                return;
            }

            processIdentityCaptured = true;
            using (SafeProcessHandle process = NativeMethods.OpenProcess(
                ProcessQueryLimitedInformation,
                false,
                ProcessId))
            {
                if (!process.IsInvalid)
                {
                    ProcessStartTimeUtc = GetProcessStartTimeUtc(process);
                }
            }
        }

        private static DateTime? GetProcessStartTimeUtc(SafeProcessHandle process)
        {
            long created;
            long exited;
            long kernel;
            long user;
            return NativeMethods.GetProcessTimes(
                process,
                out created,
                out exited,
                out kernel,
                out user)
                ? DateTime.FromFileTimeUtc(created)
                : (DateTime?)null;
        }

        internal static bool IsWindowOpen(
            IntPtr handle,
            uint processId,
            bool isTray,
            DateTime? processStartTimeUtc)
        {
            if (handle != IntPtr.Zero && NativeMethods.IsWindow(handle))
            {
                uint windowProcessId;
                NativeMethods.GetWindowThreadProcessId(handle, out windowProcessId);
                return processId != 0 && windowProcessId == processId;
            }

            if (!isTray || processId == 0)
            {
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById((int)processId))
                {
                    return !process.HasExited
                        && (!processStartTimeUtc.HasValue
                            || process.StartTime.ToUniversalTime() == processStartTimeUtc.Value);
                }
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
            catch (Win32Exception)
            {
                return true;
            }
        }

        public string IdentityKey
        {
            get { return BuildIdentityKey(ExecutablePath, LaunchTarget); }
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

        public bool Activate(out string error)
        {
            error = null;

            if (Handle == IntPtr.Zero)
            {
                return LaunchExecutable(out error);
            }

            if (!NativeMethods.IsWindow(Handle))
            {
                error = "The window \"" + Title + "\" is no longer available.";
                return false;
            }

            if (NativeMethods.IsIconic(Handle))
            {
                NativeMethods.ShowWindowAsync(Handle, NativeMethods.SwRestore);
            }
            else
            {
                NativeMethods.ShowWindowAsync(Handle, NativeMethods.SwShow);
            }

            if (NativeMethods.SetForegroundWindow(Handle))
            {
                return true;
            }

            NativeMethods.BringWindowToTop(Handle);
            bool activated = NativeMethods.SetForegroundWindow(Handle)
                || NativeMethods.GetForegroundWindow() == Handle;
            if (!activated)
            {
                error =
                    "The window \"" + Title
                    + "\" could not be activated. It may have stopped responding.";
            }

            return activated;
        }

        public bool RequestClose(out string error)
        {
            error = null;
            if (!IsOpen)
            {
                error = "The selected application is not currently open.";
                return false;
            }

            if (Handle == IntPtr.Zero)
            {
                error =
                    "This notification-area app does not expose a window that "
                    + "can receive a normal close request.";
                return false;
            }

            if (!CanRequestClose(out error))
            {
                return false;
            }

            if (!NativeMethods.PostMessage(
                    Handle,
                    NativeMethods.WmClose,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                int postError = Marshal.GetLastWin32Error();
                if (!CanRequestClose(out error))
                {
                    return false;
                }

                UIntPtr ignoredResult;
                IntPtr sendResult = NativeMethods.SendMessageTimeout(
                    Handle,
                    NativeMethods.WmClose,
                    UIntPtr.Zero,
                    IntPtr.Zero,
                    NativeMethods.SmtoAbortIfHung,
                    500,
                    out ignoredResult);
                if (sendResult == IntPtr.Zero && NativeMethods.IsWindow(Handle))
                {
                    int sendError = Marshal.GetLastWin32Error();
                    int errorCode = sendError != 0 ? sendError : postError;
                    string reason = errorCode == 0
                        ? "the window rejected the request"
                        : new Win32Exception(errorCode).Message;
                    error =
                        "Windows could not send a close request to \""
                        + Title
                        + "\": "
                        + reason;
                    return false;
                }
            }

            return true;
        }

        public bool ForceStop(out string error)
        {
            error = null;
            if (ProcessId == 0 || ProcessId == (uint)Environment.ProcessId)
            {
                error = "The selected application process is unavailable.";
                return false;
            }

            if (!ProcessStartTimeUtc.HasValue)
            {
                error = "The application process could not be verified. Reopen the switcher and try again.";
                return false;
            }

            using (SafeProcessHandle process = NativeMethods.OpenProcess(
                ProcessTerminate | ProcessQueryLimitedInformation,
                false,
                ProcessId))
            {
                if (process.IsInvalid)
                {
                    int errorCode = Marshal.GetLastWin32Error();
                    if (errorCode == ErrorInvalidParameter)
                    {
                        return true;
                    }

                    error = "Could not stop \"" + Title + "\": "
                        + new Win32Exception(errorCode).Message;
                    return false;
                }

                if (!HasCurrentIdentity(process))
                {
                    error = "The selected app has changed or closed. Reopen the switcher and try again.";
                    return false;
                }

                // Validate and terminate the same native process handle.
                if (!NativeMethods.TerminateProcess(process, 1))
                {
                    error = "Could not stop \"" + Title + "\": "
                        + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }

                return true;
            }
        }

        private bool CanRequestClose(out string error)
        {
            error = null;
            using (SafeProcessHandle process = NativeMethods.OpenProcess(
                ProcessQueryLimitedInformation,
                false,
                ProcessId))
            {
                if (process.IsInvalid || !HasCurrentIdentity(process))
                {
                    error = "The selected app has changed or could not be verified. Reopen the switcher and try again.";
                    return false;
                }
            }

            return true;
        }

        private bool HasCurrentIdentity(SafeProcessHandle process)
        {
            uint windowProcessId = ProcessId;
            if (Handle != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(Handle, out windowProcessId);
            }

            return ProcessStartTimeUtc.HasValue
                && GetProcessStartTimeUtc(process) == ProcessStartTimeUtc
                && windowProcessId == ProcessId
                && (Handle != IntPtr.Zero || IsTray)
                && HasCurrentLaunchTarget();
        }

        private bool HasCurrentLaunchTarget()
        {
            if (Handle == IntPtr.Zero
                || (!string.Equals(ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(
                        Path.GetFileName(ExecutablePath),
                        "explorer.exe",
                        StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            string currentTarget = ExplorerFolderResolver.GetCurrentFolderPath(Handle);
            return !string.IsNullOrEmpty(LaunchTarget)
                && !string.IsNullOrEmpty(currentTarget)
                && string.Equals(LaunchTarget, currentTarget, StringComparison.OrdinalIgnoreCase);
        }

        public void MarkRecentlyClosed()
        {
            Handle = IntPtr.Zero;
            ProcessId = 0;
            ProcessStartTimeUtc = null;
            processIdentityCaptured = false;
            IsMinimized = false;
            IsOnAnotherDesktop = false;
            IsRecent = true;
            IsTray = false;
            TrayTooltip = null;
        }

        public void ApplySnapshot(WindowSnapshot snapshot)
        {
            Handle = snapshot.Handle;
            ProcessId = snapshot.ProcessId;
            ProcessStartTimeUtc = snapshot.ProcessStartTimeUtc;
            processIdentityCaptured = ProcessStartTimeUtc.HasValue;
            CaptureProcessIdentity();
            Title = snapshot.Title;
            ProcessName = snapshot.ProcessName;
            ExecutablePath = snapshot.ExecutablePath;
            LaunchTarget = snapshot.LaunchTarget;
            AppUserModelId = snapshot.AppUserModelId;
            IsMinimized = snapshot.IsMinimized;
            IsOnAnotherDesktop = snapshot.IsOnAnotherDesktop;
            IsRecent = false;
            IsTray = snapshot.IsTray;
            TrayTooltip = snapshot.TrayTooltip;
        }

        public static string BuildIdentityKey(
            string executablePath,
            string launchTarget)
        {
            if (string.IsNullOrEmpty(executablePath))
            {
                return null;
            }

            return executablePath
                + "\n"
                + (string.IsNullOrEmpty(launchTarget) ? string.Empty : launchTarget);
        }

        private bool LaunchExecutable(out string error)
        {
            error = null;
            if (!string.IsNullOrEmpty(AppUserModelId))
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments =
                            "shell:AppsFolder\\" + AppUserModelId,
                        UseShellExecute = true
                    });
                    return true;
                }
                catch (Win32Exception exception)
                {
                    error =
                        "Could not open \"" + Title + "\": " + exception.Message;
                    return false;
                }
                catch (InvalidOperationException exception)
                {
                    error =
                        "Could not open \"" + Title + "\": " + exception.Message;
                    return false;
                }
            }

            if (string.IsNullOrEmpty(ExecutablePath)
                || !File.Exists(ExecutablePath))
            {
                error = "The executable for \"" + Title + "\" is unavailable.";
                return false;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ExecutablePath,
                    UseShellExecute = true
                };
                if (!string.IsNullOrEmpty(LaunchTarget))
                {
                    startInfo.Arguments =
                        "\"" + LaunchTarget.Replace("\"", "\"\"") + "\"";
                }

                Process.Start(startInfo);
                return true;
            }
            catch (Win32Exception exception)
            {
                error = "Could not open \"" + Title + "\": " + exception.Message;
                return false;
            }
            catch (InvalidOperationException exception)
            {
                error = "Could not open \"" + Title + "\": " + exception.Message;
                return false;
            }
        }

        public void Dispose()
        {
            if (Icon != null)
            {
                Icon.Dispose();
                Icon = null;
            }
        }
    }
}
