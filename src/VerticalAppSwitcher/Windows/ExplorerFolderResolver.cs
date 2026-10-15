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

        public static string GetCurrentFolderPath(IntPtr handle)
        {
            return ResolveFolderPath(handle);
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
}
