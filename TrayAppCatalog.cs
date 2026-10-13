using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace VerticalAppSwitcher
{
    internal static class TrayAppCatalog
    {
        private const string NotifyIconSettingsPath =
            @"Control Panel\NotifyIconSettings";
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private static readonly HashSet<string> ExcludedExecutables =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "explorer.exe",
                "MoNotificationUx.exe",
                "SecurityHealthSystray.exe"
            };

        public static void AppendRunningTrayApps(
            List<AppWindow> windows,
            int currentProcessId,
            bool loadIcons)
        {
            var representedExecutables =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppWindow window in windows)
            {
                if (!string.IsNullOrEmpty(window.ExecutablePath))
                {
                    representedExecutables.Add(
                        NormalizePath(window.ExecutablePath));
                }
            }

            string currentExecutablePath = null;
            using (Process currentProcess = Process.GetCurrentProcess())
            {
                currentExecutablePath = GetProcessPath(currentProcess);
            }

            try
            {
                using (RegistryKey root = Registry.CurrentUser.OpenSubKey(
                    NotifyIconSettingsPath,
                    false))
                {
                    if (root == null)
                    {
                        return;
                    }

                    var registrations = new List<TrayRegistration>();
                    foreach (string keyName in root.GetSubKeyNames())
                    {
                        using (RegistryKey key = root.OpenSubKey(keyName, false))
                        {
                            if (key == null)
                            {
                                continue;
                            }

                            string registeredPath = Convert.ToString(
                                key.GetValue("ExecutablePath"));
                            string executablePath = ResolveExecutablePath(
                                registeredPath);
                            string normalizedPath = NormalizePath(executablePath);
                            if (string.IsNullOrEmpty(normalizedPath)
                                || representedExecutables.Contains(normalizedPath)
                                || string.Equals(
                                    normalizedPath,
                                    NormalizePath(currentExecutablePath),
                                    StringComparison.OrdinalIgnoreCase)
                                || ExcludedExecutables.Contains(
                                    Path.GetFileName(executablePath)))
                            {
                                continue;
                            }

                            string tooltip = NormalizeTooltip(
                                Convert.ToString(key.GetValue("InitialTooltip")));
                            registrations.Add(new TrayRegistration(
                                executablePath,
                                normalizedPath,
                                tooltip));
                        }
                    }

                    Dictionary<string, RunningProcess> runningProcesses =
                        GetRunningProcesses(registrations);
                    foreach (TrayRegistration registration in registrations)
                    {
                        RunningProcess runningProcess;
                        if (!runningProcesses.TryGetValue(
                                registration.NormalizedPath,
                                out runningProcess)
                            || runningProcess.ProcessId == currentProcessId
                            || representedExecutables.Contains(
                                registration.NormalizedPath))
                        {
                            continue;
                        }

                            string title = GetDisplayName(
                                registration.ExecutablePath,
                                runningProcess.ProcessName,
                                registration.Tooltip);
                            Icon icon = loadIcons
                                ? WindowCatalog.GetExecutableIcon(
                                    registration.ExecutablePath)
                                : null;

                            windows.Add(new AppWindow(
                                IntPtr.Zero,
                                (uint)runningProcess.ProcessId,
                                title,
                                runningProcess.ProcessName,
                                registration.ExecutablePath,
                                null,
                                icon,
                                false)
                            {
                                IsTray = true,
                                TrayTooltip = registration.Tooltip,
                                AppUserModelId =
                                    runningProcess.AppUserModelId
                            });
                            representedExecutables.Add(
                                registration.NormalizedPath);
                    }
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                DiagnosticLog.Write(
                    "Windows denied access to tray app registrations: "
                    + exception.Message);
            }
            catch (System.Security.SecurityException exception)
            {
                DiagnosticLog.Write(
                    "Windows blocked tray app registrations: "
                    + exception.Message);
            }
        }

        private static Dictionary<string, RunningProcess> GetRunningProcesses(
            IList<TrayRegistration> registrations)
        {
            var result = new Dictionary<string, RunningProcess>(
                StringComparer.OrdinalIgnoreCase);
            var candidateNames = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            var registrationsByProcessName =
                new Dictionary<string, List<TrayRegistration>>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (TrayRegistration registration in registrations)
            {
                string processName = Path.GetFileNameWithoutExtension(
                    registration.ExecutablePath);
                candidateNames.Add(processName);
                List<TrayRegistration> matches;
                if (!registrationsByProcessName.TryGetValue(
                        processName,
                        out matches))
                {
                    matches = new List<TrayRegistration>();
                    registrationsByProcessName[processName] = matches;
                }

                matches.Add(registration);
            }

            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    if (!candidateNames.Contains(process.ProcessName))
                    {
                        continue;
                    }

                    string path = GetProcessPath(process);
                    string normalizedPath = NormalizePath(path);
                    if (string.IsNullOrEmpty(normalizedPath))
                    {
                        List<TrayRegistration> matches;
                        if (registrationsByProcessName.TryGetValue(
                                process.ProcessName,
                                out matches))
                        {
                            TrayRegistration match = FindBestRegistration(
                                matches,
                                result);
                            if (match != null)
                            {
                                result[match.NormalizedPath] =
                                    new RunningProcess(
                                        process.Id,
                                        process.ProcessName,
                                        GetApplicationUserModelId(process.Id));
                            }
                        }

                        continue;
                    }

                    if (result.ContainsKey(normalizedPath))
                    {
                        continue;
                    }

                    result[normalizedPath] = new RunningProcess(
                        process.Id,
                        process.ProcessName,
                        GetApplicationUserModelId(process.Id));
                }
            }

            return result;
        }

        private static TrayRegistration FindBestRegistration(
            IList<TrayRegistration> registrations,
            IDictionary<string, RunningProcess> runningProcesses)
        {
            foreach (TrayRegistration registration in registrations)
            {
                if (!runningProcesses.ContainsKey(registration.NormalizedPath)
                    && File.Exists(registration.ExecutablePath))
                {
                    return registration;
                }
            }

            foreach (TrayRegistration registration in registrations)
            {
                if (!runningProcesses.ContainsKey(registration.NormalizedPath))
                {
                    return registration;
                }
            }

            return null;
        }

        private static string GetProcessPath(Process process)
        {
            try
            {
                if (process.MainModule != null)
                {
                    return process.MainModule.FileName;
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (NotSupportedException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            IntPtr processHandle = OpenProcess(
                ProcessQueryLimitedInformation,
                false,
                process.Id);
            if (processHandle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                int capacity = 32768;
                var path = new StringBuilder(capacity);
                if (QueryFullProcessImageName(
                        processHandle,
                        0,
                        path,
                        ref capacity))
                {
                    return path.ToString();
                }
            }
            finally
            {
                CloseHandle(processHandle);
            }

            return null;
        }

        private static string ResolveExecutablePath(string registeredPath)
        {
            if (string.IsNullOrWhiteSpace(registeredPath))
            {
                return null;
            }

            string path = Environment.ExpandEnvironmentVariables(
                registeredPath.Trim().Trim('"'));
            if (!path.StartsWith("{", StringComparison.Ordinal))
            {
                return path;
            }

            int closingBrace = path.IndexOf('}');
            Guid knownFolderId;
            if (closingBrace < 0
                || !Guid.TryParse(
                    path.Substring(0, closingBrace + 1),
                    out knownFolderId))
            {
                return path;
            }

            IntPtr knownFolderPath;
            int result = SHGetKnownFolderPath(
                ref knownFolderId,
                0,
                IntPtr.Zero,
                out knownFolderPath);
            if (result != 0 || knownFolderPath == IntPtr.Zero)
            {
                return path;
            }

            try
            {
                return Marshal.PtrToStringUni(knownFolderPath)
                    + path.Substring(closingBrace + 1);
            }
            finally
            {
                CoTaskMemFree(knownFolderPath);
            }
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path.Trim().Trim('"'));
            }
            catch (ArgumentException)
            {
                return path.Trim().Trim('"');
            }
            catch (NotSupportedException)
            {
                return path.Trim().Trim('"');
            }
            catch (PathTooLongException)
            {
                return path.Trim().Trim('"');
            }
        }

        private static string NormalizeTooltip(string tooltip)
        {
            if (string.IsNullOrWhiteSpace(tooltip))
            {
                return null;
            }

            string normalized = tooltip.Replace("\r", string.Empty).Trim();
            int lineBreak = normalized.IndexOf('\n');
            return lineBreak >= 0
                ? normalized.Substring(0, lineBreak).Trim()
                : normalized;
        }

        private static string GetDisplayName(
            string executablePath,
            string processName,
            string tooltip)
        {
            string description = null;
            try
            {
                FileVersionInfo version =
                    FileVersionInfo.GetVersionInfo(executablePath);
                description = version.FileDescription;
                if (string.IsNullOrWhiteSpace(description))
                {
                    description = version.ProductName;
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }

            if (!string.IsNullOrWhiteSpace(tooltip)
                && (string.IsNullOrWhiteSpace(description)
                    || description.IndexOf(
                        "Tray",
                        StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return tooltip;
            }

            if (!string.IsNullOrWhiteSpace(description))
            {
                return description.Trim();
            }

            if (!string.IsNullOrWhiteSpace(tooltip))
            {
                return tooltip;
            }

            return string.IsNullOrWhiteSpace(processName)
                ? Path.GetFileNameWithoutExtension(executablePath)
                : processName;
        }

        private sealed class RunningProcess
        {
            public RunningProcess(
                int processId,
                string processName,
                string appUserModelId)
            {
                ProcessId = processId;
                ProcessName = processName;
                AppUserModelId = appUserModelId;
            }

            public int ProcessId { get; private set; }
            public string ProcessName { get; private set; }
            public string AppUserModelId { get; private set; }
        }

        private sealed class TrayRegistration
        {
            public TrayRegistration(
                string executablePath,
                string normalizedPath,
                string tooltip)
            {
                ExecutablePath = executablePath;
                NormalizedPath = normalizedPath;
                Tooltip = tooltip;
            }

            public string ExecutablePath { get; private set; }
            public string NormalizedPath { get; private set; }
            public string Tooltip { get; private set; }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            int processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool QueryFullProcessImageName(
            IntPtr process,
            uint flags,
            StringBuilder executablePath,
            ref int size);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetApplicationUserModelId(
            IntPtr process,
            ref uint applicationUserModelIdLength,
            StringBuilder applicationUserModelId);

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath(
            ref Guid folderId,
            uint flags,
            IntPtr token,
            out IntPtr path);

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr memory);

        private static string GetApplicationUserModelId(int processId)
        {
            IntPtr processHandle = OpenProcess(
                ProcessQueryLimitedInformation,
                false,
                processId);
            if (processHandle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                uint length = 0;
                int result = GetApplicationUserModelId(
                    processHandle,
                    ref length,
                    null);
                if (result != 122 || length == 0)
                {
                    return null;
                }

                var value = new StringBuilder((int)length);
                result = GetApplicationUserModelId(
                    processHandle,
                    ref length,
                    value);
                return result == 0 ? value.ToString() : null;
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }
    }
}
