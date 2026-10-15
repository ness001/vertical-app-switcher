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
    internal static class StartupRegistration
    {
        private const string RunKeyPath =
            @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "VerticalAppSwitcher";

        public static bool TryGetEnabled(out bool enabled, out string error)
        {
            enabled = false;
            error = null;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                    {
                        return true;
                    }

                    string value = Convert.ToString(key.GetValue(ValueName));
                    enabled = string.Equals(
                        value,
                        GetStartupCommand(),
                        StringComparison.OrdinalIgnoreCase);
                    return true;
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "Windows denied access to the startup setting: " + exception.Message;
                return false;
            }
            catch (SecurityException exception)
            {
                error = "Windows blocked access to the startup setting: " + exception.Message;
                return false;
            }
            catch (IOException exception)
            {
                error = "The startup setting could not be read: " + exception.Message;
                return false;
            }
        }

        public static bool TrySetEnabled(bool enabled, out string error)
        {
            error = null;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (key == null)
                    {
                        error = "Windows could not open the current-user startup settings.";
                        return false;
                    }

                    if (enabled)
                    {
                        key.SetValue(ValueName, GetStartupCommand(), RegistryValueKind.String);
                    }
                    else
                    {
                        key.DeleteValue(ValueName, false);
                    }

                    return true;
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                error = "Windows denied access to the startup setting: " + exception.Message;
                return false;
            }
            catch (SecurityException exception)
            {
                error = "Windows blocked access to the startup setting: " + exception.Message;
                return false;
            }
            catch (IOException exception)
            {
                error = "The startup setting could not be updated: " + exception.Message;
                return false;
            }
        }

        private static string GetStartupCommand()
        {
            return "\"" + Application.ExecutablePath + "\" --background";
        }
    }
}
