using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VerticalAppSwitcher
{
    internal static class ForegroundWindowOrder
    {
        private const uint EventSystemForeground = 0x0003;
        private const uint EventObjectCreate = 0x8000;
        private const uint EventObjectDestroy = 0x8001;
        private const uint WinEventSkipOwnProcess = 0x0002;
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint GaRoot = 2;
        private const int MaximumTrackedWindows = 256;
        private static readonly Dictionary<IntPtr, WindowUse> History =
            new Dictionary<IntPtr, WindowUse>();
        private static readonly WinEventProcedure EventProcedure = OnWinEvent;
        private static IntPtr foregroundHook;
        private static IntPtr lifetimeHook;
        private static long sequence;

        // Hooks and callers use the application's UI message thread.
        public static void Start()
        {
            if (foregroundHook != IntPtr.Zero)
            {
                return;
            }

            lifetimeHook = SetWinEventHook(
                EventObjectCreate,
                EventObjectDestroy,
                IntPtr.Zero,
                EventProcedure,
                0,
                0,
                WinEventSkipOwnProcess);
            foregroundHook = SetWinEventHook(
                EventSystemForeground,
                EventSystemForeground,
                IntPtr.Zero,
                EventProcedure,
                0,
                0,
                WinEventSkipOwnProcess);
            if (foregroundHook == IntPtr.Zero || lifetimeHook == IntPtr.Zero)
            {
                Stop();
                DiagnosticLog.Write(
                    "Foreground window history is unavailable. Windows will use discovery order.");
                return;
            }

            RecordForeground(NativeMethods.GetForegroundWindow(), (long)GetTickCount64());
        }

        public static void Stop()
        {
            if (foregroundHook != IntPtr.Zero)
            {
                UnhookWinEvent(foregroundHook);
                foregroundHook = IntPtr.Zero;
            }

            if (lifetimeHook != IntPtr.Zero)
            {
                UnhookWinEvent(lifetimeHook);
                lifetimeHook = IntPtr.Zero;
            }

            History.Clear();
            sequence = 0;
        }

        public static void Apply(List<AppWindow> windows)
        {
            if (foregroundHook == IntPtr.Zero)
            {
                return;
            }

            RecordForeground(NativeMethods.GetForegroundWindow(), (long)GetTickCount64());
            RemoveClosedWindows();
            var slots = new List<int>();
            var ordered = new List<OrderedWindow>();
            for (int index = 0; index < windows.Count; index++)
            {
                AppWindow window = windows[index];
                if (window.Handle == IntPtr.Zero
                    || window.IsRecent
                    || window.IsTray
                    || window.IsOnAnotherDesktop)
                {
                    continue;
                }

                long lastUsed = long.MinValue;
                long lastSequence = 0;
                WindowUse use;
                WindowIdentity current;
                if (History.TryGetValue(window.Handle, out use)
                    && use.Identity.ProcessId == window.ProcessId
                    && (!window.ProcessStartTimeUtc.HasValue
                        || window.ProcessStartTimeUtc.Value.ToFileTimeUtc()
                            == use.Identity.ProcessStartTime)
                    && TryGetIdentity(window.Handle, out current)
                    && use.Identity.Matches(current))
                {
                    lastUsed = use.LastUsed;
                    lastSequence = use.Sequence;
                }

                slots.Add(index);
                ordered.Add(new OrderedWindow(window, index, lastUsed, lastSequence));
            }

            // Only Open entries move. Unknown windows keep discovery order.
            ordered.Sort(delegate(OrderedWindow left, OrderedWindow right)
            {
                int result = right.LastUsed.CompareTo(left.LastUsed);
                if (result == 0)
                {
                    result = right.Sequence.CompareTo(left.Sequence);
                }

                return result != 0 ? result : left.Index.CompareTo(right.Index);
            });
            for (int index = 0; index < slots.Count; index++)
            {
                windows[slots[index]] = ordered[index].Window;
            }
        }

        private static void OnWinEvent(
            IntPtr hook,
            uint eventType,
            IntPtr handle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime)
        {
            if (handle == IntPtr.Zero || objectId != 0 || childId != 0)
            {
                return;
            }

            if ((eventType == EventObjectCreate || eventType == EventObjectDestroy)
                && hook == lifetimeHook)
            {
                // A new window in the same process must not inherit a reused HWND's rank.
                History.Remove(handle);
                return;
            }

            if (eventType == EventSystemForeground && hook == foregroundHook)
            {
                long now = (long)GetTickCount64();
                long elapsed = unchecked((uint)now - eventTime);
                RecordForeground(handle, now - elapsed);
            }
        }

        private static void RecordForeground(IntPtr handle, long timestamp)
        {
            handle = GetAppWindow(handle);
            WindowIdentity identity;
            if (handle == IntPtr.Zero || !TryGetIdentity(handle, out identity))
            {
                return;
            }

            WindowUse previous;
            if (History.TryGetValue(handle, out previous)
                && previous.Identity.Matches(identity)
                && previous.LastUsed > timestamp)
            {
                return;
            }

            History[handle] = new WindowUse(identity, timestamp, ++sequence);
            if (History.Count > MaximumTrackedWindows)
            {
                RemoveClosedWindows();
                if (History.Count > MaximumTrackedWindows)
                {
                    IntPtr oldest = IntPtr.Zero;
                    long oldestSequence = long.MaxValue;
                    foreach (KeyValuePair<IntPtr, WindowUse> entry in History)
                    {
                        if (entry.Value.Sequence < oldestSequence)
                        {
                            oldest = entry.Key;
                            oldestSequence = entry.Value.Sequence;
                        }
                    }

                    History.Remove(oldest);
                }
            }
        }

        private static IntPtr GetAppWindow(IntPtr handle)
        {
            handle = GetAncestor(handle, GaRoot);
            while (handle != IntPtr.Zero)
            {
                int style = NativeMethods.GetWindowLong(handle, NativeMethods.GwlExStyle);
                if ((style & NativeMethods.WsExAppWindow) != 0)
                {
                    break;
                }

                IntPtr owner = NativeMethods.GetWindow(handle, NativeMethods.GwOwner);
                if (owner == IntPtr.Zero)
                {
                    if ((style & (NativeMethods.WsExToolWindow | NativeMethods.WsExNoActivate)) != 0)
                    {
                        return IntPtr.Zero;
                    }

                    break;
                }

                handle = owner;
            }

            if (handle == IntPtr.Zero
                || handle == NativeMethods.GetShellWindow()
                || !NativeMethods.IsWindowVisible(handle)
                || NativeMethods.GetWindowTextLength(handle) == 0)
            {
                return IntPtr.Zero;
            }

            return handle;
        }

        private static bool TryGetIdentity(IntPtr handle, out WindowIdentity identity)
        {
            identity = null;
            uint processId;
            uint threadId = NativeMethods.GetWindowThreadProcessId(handle, out processId);
            if (threadId == 0 || processId == 0 || processId == (uint)Environment.ProcessId)
            {
                return false;
            }

            using (SafeProcessHandle process = NativeMethods.OpenProcess(
                ProcessQueryLimitedInformation,
                false,
                processId))
            {
                long created;
                long exited;
                long kernel;
                long user;
                if (process.IsInvalid
                    || !NativeMethods.GetProcessTimes(
                        process,
                        out created,
                        out exited,
                        out kernel,
                        out user))
                {
                    return false;
                }

                uint currentProcessId;
                if (NativeMethods.GetWindowThreadProcessId(handle, out currentProcessId) != threadId
                    || currentProcessId != processId)
                {
                    return false;
                }

                identity = new WindowIdentity(processId, threadId, created);
                return true;
            }
        }

        private static void RemoveClosedWindows()
        {
            var removed = new List<IntPtr>();
            foreach (KeyValuePair<IntPtr, WindowUse> entry in History)
            {
                uint processId;
                uint threadId = NativeMethods.GetWindowThreadProcessId(entry.Key, out processId);
                if (threadId != entry.Value.Identity.ThreadId
                    || processId != entry.Value.Identity.ProcessId)
                {
                    removed.Add(entry.Key);
                }
            }

            foreach (IntPtr handle in removed)
            {
                History.Remove(handle);
            }
        }

        private sealed class WindowIdentity
        {
            public WindowIdentity(uint processId, uint threadId, long processStartTime)
            {
                ProcessId = processId;
                ThreadId = threadId;
                ProcessStartTime = processStartTime;
            }

            public uint ProcessId { get; private set; }
            public uint ThreadId { get; private set; }
            public long ProcessStartTime { get; private set; }

            public bool Matches(WindowIdentity other)
            {
                return ProcessId == other.ProcessId
                    && ThreadId == other.ThreadId
                    && ProcessStartTime == other.ProcessStartTime;
            }
        }

        private sealed class WindowUse
        {
            public WindowUse(WindowIdentity identity, long lastUsed, long sequence)
            {
                Identity = identity;
                LastUsed = lastUsed;
                Sequence = sequence;
            }

            public WindowIdentity Identity { get; private set; }
            public long LastUsed { get; private set; }
            public long Sequence { get; private set; }
        }

        private sealed class OrderedWindow
        {
            public OrderedWindow(AppWindow window, int index, long lastUsed, long sequence)
            {
                Window = window;
                Index = index;
                LastUsed = lastUsed;
                Sequence = sequence;
            }

            public AppWindow Window { get; private set; }
            public int Index { get; private set; }
            public long LastUsed { get; private set; }
            public long Sequence { get; private set; }
        }

        private delegate void WinEventProcedure(
            IntPtr hook,
            uint eventType,
            IntPtr handle,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWinEventHook(
            uint eventMin,
            uint eventMax,
            IntPtr module,
            WinEventProcedure procedure,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr window, uint flags);

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();
    }
}
