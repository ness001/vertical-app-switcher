using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal sealed class ClosedAppTracker : IDisposable
    {
        private const int BackgroundPollIntervalMilliseconds = 500;
        private const int VisiblePollIntervalMilliseconds = 100;
        private readonly AppStateStore stateStore;
        private readonly Timer pollTimer;
        private Dictionary<IntPtr, WindowSnapshot> knownWindows =
            new Dictionary<IntPtr, WindowSnapshot>();
        private bool disposed;

        public ClosedAppTracker(AppStateStore stateStore)
        {
            this.stateStore = stateStore;
            pollTimer = new Timer
            {
                Interval = BackgroundPollIntervalMilliseconds
            };
            pollTimer.Tick += OnPollTimerTick;
        }

        public event Action<string> Error;
        public event Action<IList<ClosedWindowUpdate>> WindowsClosed;

        public void Start()
        {
            if (disposed)
            {
                throw new ObjectDisposedException("ClosedAppTracker");
            }

            Poll();
            pollTimer.Start();
        }

        public void ObserveWindows(IEnumerable<AppWindow> windows)
        {
            foreach (AppWindow window in windows)
            {
                ObserveWindow(window);
            }
        }

        public void ObserveWindow(AppWindow window)
        {
            if (disposed
                || window == null
                || window.ProcessId == 0
                || window.ProcessId == (uint)Environment.ProcessId
                || (window.Handle == IntPtr.Zero && !window.IsTray))
            {
                return;
            }

            // Displayed rows capture process identity once, not on every catalog poll.
            window.CaptureProcessIdentity();
            WindowSnapshot snapshot = WindowSnapshot.FromWindow(window);
            knownWindows[snapshot.TrackingKey] = PreserveKnownIdentity(snapshot);
        }

        public void SetOverlayVisible(bool visible)
        {
            if (disposed)
            {
                return;
            }

            pollTimer.Interval = visible
                ? VisiblePollIntervalMilliseconds
                : BackgroundPollIntervalMilliseconds;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            pollTimer.Stop();
            pollTimer.Tick -= OnPollTimerTick;
            pollTimer.Dispose();
        }

        private void OnPollTimerTick(object sender, EventArgs eventArgs)
        {
            try
            {
                Poll();
            }
            catch (IOException exception)
            {
                pollTimer.Stop();
                RaiseError("Recently closed apps could not be saved: " + exception.Message);
            }
            catch (UnauthorizedAccessException exception)
            {
                pollTimer.Stop();
                RaiseError("Windows denied access to recently closed apps: " + exception.Message);
            }
            catch (SecurityException exception)
            {
                pollTimer.Stop();
                RaiseError("Windows blocked recently closed app storage: " + exception.Message);
            }
            catch (SerializationException exception)
            {
                pollTimer.Stop();
                RaiseError("Recently closed app data could not be serialized: " + exception.Message);
            }
        }

        private void Poll()
        {
            UpdateSnapshots(WindowCatalog.GetSwitchableWindowSnapshots());
        }

        private void UpdateSnapshots(IList<WindowSnapshot> snapshots)
        {
            var current = new Dictionary<IntPtr, WindowSnapshot>();
            var currentByIdentity =
                new Dictionary<string, List<WindowSnapshot>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (WindowSnapshot captured in snapshots)
            {
                WindowSnapshot snapshot = PreserveKnownIdentity(captured);
                current[snapshot.TrackingKey] = snapshot;
                AddIdentitySnapshot(currentByIdentity, snapshot);
            }

            foreach (KeyValuePair<IntPtr, WindowSnapshot> previous in knownWindows)
            {
                WindowSnapshot snapshot = previous.Value;
                if (current.ContainsKey(previous.Key) || !snapshot.IsOpen)
                {
                    continue;
                }

                bool hasReplacement = false;
                List<WindowSnapshot> matchingIdentity;
                if (!string.IsNullOrEmpty(snapshot.IdentityKey)
                    && currentByIdentity.TryGetValue(
                        snapshot.IdentityKey,
                        out matchingIdentity))
                {
                    foreach (WindowSnapshot candidate in matchingIdentity)
                    {
                        if (snapshot.IsTray
                            || (candidate.IsTray && candidate.ProcessId == snapshot.ProcessId))
                        {
                            hasReplacement = true;
                            break;
                        }
                    }
                }

                if (!hasReplacement)
                {
                    // Hidden or temporarily cloaked windows can still close later.
                    current[previous.Key] = snapshot;
                    AddIdentitySnapshot(currentByIdentity, snapshot);
                }
            }

            var closed = new List<WindowSnapshot>();
            var replacements =
                new Dictionary<WindowSnapshot, WindowSnapshot>();
            var recentCandidates = new List<WindowSnapshot>();
            var recentCandidateIdentities = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<IntPtr, WindowSnapshot> previous in knownWindows)
            {
                WindowSnapshot snapshot = previous.Value;
                WindowSnapshot currentSnapshot;
                bool sameWindowIdentity =
                    current.TryGetValue(previous.Key, out currentSnapshot)
                    && snapshot.ProcessId == currentSnapshot.ProcessId
                    && (!snapshot.ProcessStartTimeUtc.HasValue
                        || !currentSnapshot.ProcessStartTimeUtc.HasValue
                        || snapshot.ProcessStartTimeUtc == currentSnapshot.ProcessStartTimeUtc)
                    && string.Equals(
                        snapshot.IdentityKey,
                        currentSnapshot.IdentityKey,
                        StringComparison.OrdinalIgnoreCase);
                if (sameWindowIdentity)
                {
                    continue;
                }

                closed.Add(snapshot);

                WindowSnapshot replacement = null;
                List<WindowSnapshot> matchingIdentity;
                if (!string.IsNullOrEmpty(snapshot.IdentityKey)
                    && currentByIdentity.TryGetValue(
                        snapshot.IdentityKey,
                        out matchingIdentity))
                {
                    replacement = matchingIdentity[0];
                }

                if (replacement != null)
                {
                    replacements[snapshot] = replacement;
                }
                else if (!string.IsNullOrEmpty(snapshot.IdentityKey)
                    && recentCandidateIdentities.Add(snapshot.IdentityKey))
                {
                    recentCandidates.Add(snapshot);
                }
            }

            if (closed.Count > 0)
            {
                IList<WindowSnapshot> accepted =
                    stateStore.AddRecentlyClosed(recentCandidates);
                var acceptedSet = new HashSet<WindowSnapshot>(accepted);
                var updates = new List<ClosedWindowUpdate>(closed.Count);
                foreach (WindowSnapshot snapshot in closed)
                {
                    WindowSnapshot replacement;
                    replacements.TryGetValue(snapshot, out replacement);
                    updates.Add(new ClosedWindowUpdate(
                        snapshot,
                        replacement,
                        acceptedSet.Contains(snapshot)));
                }

                DiagnosticLog.Write(
                    "Closed app list updated: "
                    + closed.Count
                    + " closed, "
                    + accepted.Count
                    + " recent.");
                Action<IList<ClosedWindowUpdate>> handler = WindowsClosed;
                if (handler != null)
                {
                    handler(updates);
                }
            }

            knownWindows = current;
        }

        private WindowSnapshot PreserveKnownIdentity(WindowSnapshot snapshot)
        {
            WindowSnapshot previous;
            if (knownWindows.TryGetValue(snapshot.TrackingKey, out previous)
                && previous.ProcessId == snapshot.ProcessId
                && (!previous.ProcessStartTimeUtc.HasValue
                    || !snapshot.ProcessStartTimeUtc.HasValue
                    || previous.ProcessStartTimeUtc == snapshot.ProcessStartTimeUtc)
                && string.IsNullOrEmpty(snapshot.IdentityKey)
                && !string.IsNullOrEmpty(previous.IdentityKey))
            {
                return previous;
            }

            return snapshot;
        }

        private static void AddIdentitySnapshot(
            Dictionary<string, List<WindowSnapshot>> snapshotsByIdentity,
            WindowSnapshot snapshot)
        {
            if (string.IsNullOrEmpty(snapshot.IdentityKey))
            {
                return;
            }

            List<WindowSnapshot> matchingIdentity;
            if (!snapshotsByIdentity.TryGetValue(
                    snapshot.IdentityKey,
                    out matchingIdentity))
            {
                matchingIdentity = new List<WindowSnapshot>();
                snapshotsByIdentity[snapshot.IdentityKey] = matchingIdentity;
            }

            matchingIdentity.Add(snapshot);
        }

        private void RaiseError(string message)
        {
            Action<string> handler = Error;
            if (handler != null)
            {
                handler(message);
            }
        }
    }
}
