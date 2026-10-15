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

        public void MoveToFront(AppWindow window)
        {
            int index = windows.IndexOf(window);
            if (index <= 0)
            {
                return;
            }

            windows.RemoveAt(index);
            windows.Insert(0, window);
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
}
