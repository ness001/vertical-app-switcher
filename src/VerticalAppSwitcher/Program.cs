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
            bool shutdownRequested = HasArgument(arguments, "--shutdown");
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

                if (shutdownRequested)
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
                    Application.Run(new SwitcherApplicationContext(showImmediately));
                }
                catch (Exception exception)
                {
                    DiagnosticLog.Write("Fatal error: " + exception);
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
