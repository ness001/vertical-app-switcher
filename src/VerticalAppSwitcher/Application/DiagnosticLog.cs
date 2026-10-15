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
    internal static class DiagnosticLog
    {
        private static readonly object SyncRoot = new object();

        public static void Write(string message)
        {
            string line = DateTimeOffset.Now.ToString("o") + " " + message + Environment.NewLine;

            try
            {
                string directory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "VerticalAppSwitcher");
                Directory.CreateDirectory(directory);

                lock (SyncRoot)
                {
                    File.AppendAllText(Path.Combine(directory, "switcher.log"), line);
                }
            }
            catch (UnauthorizedAccessException exception)
            {
                Debug.WriteLine("Could not write switcher log: " + exception.Message);
            }
            catch (IOException exception)
            {
                Debug.WriteLine("Could not write switcher log: " + exception.Message);
            }
        }
    }
}
