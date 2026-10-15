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
            if (arguments.Length == 2 && arguments[0] == "--apply-update")
            {
                Environment.ExitCode = UpdateInstaller.Run(arguments[1]);
                return;
            }

            string updateReady = arguments.Length == 2 && arguments[0] == "--update-ready"
                && Guid.TryParseExact(arguments[1], "N", out _) ? arguments[1] : null;
            bool shutdownRequested = HasArgument(arguments, "--shutdown");
            if (updateReady == null && !shutdownRequested && UpdateInstaller.IsUpdateInProgress())
            {
                return;
            }
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

                if (shutdownRequested || (updateReady == null && UpdateInstaller.IsUpdateInProgress()))
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
                    using var context = new SwitcherApplicationContext(showImmediately);
                    EventHandler readyHandler = null;
                    if (updateReady != null)
                    {
                        readyHandler = delegate
                        {
                            Application.Idle -= readyHandler;
                            try
                            {
                                using EventWaitHandle ready = EventWaitHandle.OpenExisting(UpdateInstaller.ReadyPrefix + updateReady);
                                ready.Set();
                            }
                            catch (WaitHandleCannotBeOpenedException) { }
                        };
                        Application.Idle += readyHandler;
                    }

                    try { Application.Run(context); }
                    finally { if (readyHandler != null) Application.Idle -= readyHandler; }
                }
                catch (Exception exception)
                {
                    Environment.ExitCode = 1;
                    DiagnosticLog.Write("Fatal error: " + exception);
                    if (updateReady != null) return;
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
}
