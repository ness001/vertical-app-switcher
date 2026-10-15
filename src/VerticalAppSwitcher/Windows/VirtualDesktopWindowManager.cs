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
}
