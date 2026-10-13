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

namespace VerticalAppSwitcher
{
    internal sealed class AppWindow : IDisposable
    {
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
        }

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
        public bool IsRecent { get; set; }
        public bool IsTray { get; set; }
        public string TrayTooltip { get; set; }
        public string AppUserModelId { get; set; }

        public bool IsOpen
        {
            get
            {
                if (Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle))
                {
                    return true;
                }

                if (!IsTray || ProcessId == 0)
                {
                    return false;
                }

                try
                {
                    using (Process process = Process.GetProcessById((int)ProcessId))
                    {
                        return !process.HasExited;
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

            if (!NativeMethods.PostMessage(
                    Handle,
                    NativeMethods.WmClose,
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                int postError = Marshal.GetLastWin32Error();
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
            if (ProcessId == 0)
            {
                error = "The selected application process is unavailable.";
                return false;
            }

            try
            {
                using (Process process = Process.GetProcessById((int)ProcessId))
                {
                    process.Kill();
                }

                return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            catch (InvalidOperationException exception)
            {
                error = "Could not stop \"" + Title + "\": " + exception.Message;
                return false;
            }
            catch (Win32Exception exception)
            {
                error = "Could not stop \"" + Title + "\": " + exception.Message;
                return false;
            }
        }

        public void MarkRecentlyClosed()
        {
            Handle = IntPtr.Zero;
            ProcessId = 0;
            IsMinimized = false;
            IsRecent = true;
            IsTray = false;
            TrayTooltip = null;
        }

        public void ApplySnapshot(WindowSnapshot snapshot)
        {
            Handle = snapshot.Handle;
            ProcessId = snapshot.ProcessId;
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

    internal sealed class AppWindowCollection : IReadOnlyList<AppWindow>, IDisposable
    {
        private readonly List<AppWindow> windows;

        public AppWindowCollection(List<AppWindow> windows)
        {
            this.windows = windows;
        }

        public int Count
        {
            get { return windows.Count; }
        }

        public AppWindow this[int index]
        {
            get { return windows[index]; }
        }

        public IEnumerator<AppWindow> GetEnumerator()
        {
            return windows.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public int OpenCount
        {
            get
            {
                int count = 0;
                foreach (AppWindow window in windows)
                {
                    if (window.IsOpen)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        public List<AppWindow> Detach()
        {
            var detached = new List<AppWindow>(windows);
            windows.Clear();
            return detached;
        }

        public int IndexOf(AppWindow window)
        {
            return windows.IndexOf(window);
        }

        public bool Remove(AppWindow window)
        {
            if (!windows.Remove(window))
            {
                return false;
            }

            window.Dispose();
            return true;
        }

        public void Insert(int index, AppWindow window)
        {
            windows.Insert(index, window);
        }

        public void ReorderPinnedFirst()
        {
            var reordered = new List<AppWindow>(windows.Count);
            foreach (AppWindow window in windows)
            {
                if (window.IsPinned)
                {
                    reordered.Add(window);
                }
            }

            foreach (AppWindow window in windows)
            {
                if (!window.IsPinned)
                {
                    reordered.Add(window);
                }
            }

            windows.Clear();
            windows.AddRange(reordered);
        }

        public void Dispose()
        {
            foreach (AppWindow window in windows)
            {
                window.Dispose();
            }

            windows.Clear();
        }
    }

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
                snapshots.Add(new WindowSnapshot(
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
                    window.TrayTooltip));
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
            string trayTooltip)
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
        }

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

    internal static class ExplorerFolderResolver
    {
        private static readonly Dictionary<IntPtr, CachedFolder> Cache =
            new Dictionary<IntPtr, CachedFolder>();

        public static string GetFolderPath(IntPtr handle, string title)
        {
            CachedFolder cached;
            if (Cache.TryGetValue(handle, out cached)
                && string.Equals(cached.Title, title, StringComparison.Ordinal))
            {
                return cached.Path;
            }

            string path = ResolveFolderPath(handle);
            if (!string.IsNullOrEmpty(path))
            {
                Cache[handle] = new CachedFolder(title, path);
            }

            return path;
        }

        private static string ResolveFolderPath(IntPtr handle)
        {
            Type shellType = Type.GetTypeFromProgID("Shell.Application");
            if (shellType == null)
            {
                return null;
            }

            object shell = null;
            object shellWindows = null;
            try
            {
                shell = Activator.CreateInstance(shellType);
                shellWindows = shellType.InvokeMember(
                    "Windows",
                    BindingFlags.InvokeMethod,
                    null,
                    shell,
                    null);
                int count = Convert.ToInt32(GetProperty(shellWindows, "Count"));

                for (int index = 0; index < count; index++)
                {
                    object shellWindow = null;
                    try
                    {
                        shellWindow = shellWindows.GetType().InvokeMember(
                            "Item",
                            BindingFlags.InvokeMethod | BindingFlags.GetProperty,
                            null,
                            shellWindows,
                            new object[] { index });
                        if (shellWindow == null)
                        {
                            continue;
                        }

                        long shellHandle = Convert.ToInt64(
                            GetProperty(shellWindow, "HWND"));
                        if (shellHandle != handle.ToInt64())
                        {
                            continue;
                        }

                        string path = GetDocumentFolderPath(shellWindow);
                        if (!string.IsNullOrEmpty(path))
                        {
                            return path;
                        }

                        string locationUrl = Convert.ToString(
                            GetProperty(shellWindow, "LocationURL"));
                        Uri uri;
                        if (Uri.TryCreate(
                                locationUrl,
                                UriKind.Absolute,
                                out uri)
                            && uri.IsFile)
                        {
                            return uri.LocalPath;
                        }
                    }
                    finally
                    {
                        ReleaseComObject(shellWindow);
                    }
                }
            }
            catch (COMException)
            {
                return null;
            }
            catch (TargetInvocationException)
            {
                return null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
            catch (FormatException)
            {
                return null;
            }
            finally
            {
                ReleaseComObject(shellWindows);
                ReleaseComObject(shell);
            }

            return null;
        }

        private static string GetDocumentFolderPath(object shellWindow)
        {
            object document = null;
            object folder = null;
            object self = null;
            try
            {
                document = GetProperty(shellWindow, "Document");
                folder = GetProperty(document, "Folder");
                self = GetProperty(folder, "Self");
                return Convert.ToString(GetProperty(self, "Path"));
            }
            finally
            {
                ReleaseComObject(self);
                ReleaseComObject(folder);
                ReleaseComObject(document);
            }
        }

        private static object GetProperty(object target, string name)
        {
            return target.GetType().InvokeMember(
                name,
                BindingFlags.GetProperty,
                null,
                target,
                null);
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }

        private sealed class CachedFolder
        {
            public CachedFolder(string title, string path)
            {
                Title = title;
                Path = path;
            }

            public string Title { get; private set; }
            public string Path { get; private set; }
        }
    }

    internal sealed class VirtualDesktopWindowManager : IDisposable
    {
        private static readonly object LogSync = new object();
        private static bool creationFailureLogged;
        private static bool queryFailureLogged;
        private IVirtualDesktopManager manager;

        private VirtualDesktopWindowManager(IVirtualDesktopManager manager)
        {
            this.manager = manager;
        }

        public static VirtualDesktopWindowManager TryCreate()
        {
            try
            {
                return new VirtualDesktopWindowManager(
                    (IVirtualDesktopManager)
                        new VirtualDesktopManagerComObject());
            }
            catch (COMException exception)
            {
                LogCreationFailure(exception.Message);
                return null;
            }
            catch (InvalidCastException exception)
            {
                LogCreationFailure(exception.Message);
                return null;
            }
        }

        public bool IsWindowOnAnotherDesktop(IntPtr handle)
        {
            bool onCurrentDesktop;
            int result = manager.IsWindowOnCurrentVirtualDesktop(
                handle,
                out onCurrentDesktop);
            if (result < 0)
            {
                LogQueryFailure(result);
                return false;
            }

            return !onCurrentDesktop;
        }

        public void Dispose()
        {
            if (manager == null)
            {
                return;
            }

            Marshal.FinalReleaseComObject(manager);
            manager = null;
        }

        private static void LogCreationFailure(string message)
        {
            lock (LogSync)
            {
                if (creationFailureLogged)
                {
                    return;
                }

                creationFailureLogged = true;
            }

            DiagnosticLog.Write(
                "Virtual desktop discovery is unavailable: " + message);
        }

        private static void LogQueryFailure(int result)
        {
            lock (LogSync)
            {
                if (queryFailureLogged)
                {
                    return;
                }

                queryFailureLogged = true;
            }

            DiagnosticLog.Write(
                "A virtual desktop window could not be classified (HRESULT 0x"
                + unchecked((uint)result).ToString("X8")
                + ").");
        }
    }

    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(
            IntPtr topLevelWindow,
            [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(
            IntPtr topLevelWindow,
            [In] ref Guid desktopId);
    }

    [ComImport]
    [Guid("AA509086-5CA9-4C25-8F95-589D3C07B48A")]
    internal class VirtualDesktopManagerComObject
    {
    }

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
