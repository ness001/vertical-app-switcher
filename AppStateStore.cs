using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal sealed class AppStateStore
    {
        private const int MaximumRecentApps = 12;
        private readonly string statePath;
        private readonly SwitcherStateData state;

        public AppStateStore()
            : this(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerticalAppSwitcher",
                "state.json"))
        {
        }

        internal AppStateStore(string statePath)
        {
            this.statePath = statePath;
            state = LoadState();
        }

        public AppWindowCollection Compose(AppWindowCollection activeWindows)
        {
            List<AppWindow> active = activeWindows.Detach();
            activeWindows.Dispose();

            var composed = new List<AppWindow>(active);
            var representedIdentities = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (AppWindow window in active)
            {
                window.PinOrder = FindByIdentity(state.PinnedApps, window.IdentityKey);
                window.IsPinned = window.PinOrder >= 0;
                if (!window.IsPinned)
                {
                    window.PinOrder = int.MaxValue;
                }

                if (!string.IsNullOrEmpty(window.IdentityKey))
                {
                    representedIdentities.Add(window.IdentityKey);
                }
            }

            foreach (StoredAppRecord recent in state.RecentApps)
            {
                if (string.IsNullOrEmpty(recent.IdentityKey)
                    || representedIdentities.Contains(recent.IdentityKey))
                {
                    continue;
                }

                int pinOrder = FindByIdentity(state.PinnedApps, recent.IdentityKey);
                AppWindow window = CreateStoredWindow(recent, pinOrder >= 0, true);
                window.PinOrder = pinOrder >= 0 ? pinOrder : int.MaxValue;
                composed.Add(window);
                representedIdentities.Add(recent.IdentityKey);
            }

            for (int index = 0; index < state.PinnedApps.Count; index++)
            {
                StoredAppRecord pin = state.PinnedApps[index];
                if (!representedIdentities.Contains(pin.IdentityKey))
                {
                    AppWindow window = CreateStoredWindow(pin, true, false);
                    window.PinOrder = index;
                    composed.Add(window);
                    representedIdentities.Add(pin.IdentityKey);
                }
            }

            return new AppWindowCollection(composed);
        }

        public bool TogglePin(AppWindow window)
        {
            if (string.IsNullOrEmpty(window.ExecutablePath))
            {
                throw new InvalidOperationException(
                    "This application does not expose an executable path and cannot be pinned.");
            }

            int existingIndex = FindByIdentity(
                state.PinnedApps,
                window.IdentityKey);
            bool pinned;
            if (existingIndex >= 0)
            {
                state.PinnedApps.RemoveAt(existingIndex);
                pinned = false;
            }
            else
            {
                state.PinnedApps.Insert(0, StoredAppRecord.FromWindow(window));
                pinned = true;
            }

            SaveState();
            return pinned;
        }

        public void RemoveRecent(AppWindow window)
        {
            if (string.IsNullOrEmpty(window.ExecutablePath))
            {
                return;
            }

            int index = FindByIdentity(
                state.RecentApps,
                window.IdentityKey);
            if (index < 0)
            {
                return;
            }

            state.RecentApps.RemoveAt(index);
            SaveState();
        }

        public IList<WindowSnapshot> AddRecentlyClosed(
            IList<WindowSnapshot> closedWindows)
        {
            bool changed = false;
            var previousRecents = new List<StoredAppRecord>(state.RecentApps);
            var accepted = new List<WindowSnapshot>();
            var acceptedIdentities = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            foreach (WindowSnapshot window in closedWindows)
            {
                if (!CanRelaunch(window)
                    || !acceptedIdentities.Add(window.IdentityKey))
                {
                    continue;
                }

                int existingIndex = FindByIdentity(
                    state.RecentApps,
                    window.IdentityKey);
                if (existingIndex >= 0)
                {
                    state.RecentApps.RemoveAt(existingIndex);
                }

                state.RecentApps.Insert(0, StoredAppRecord.FromSnapshot(window));
                accepted.Add(window);
                changed = true;
            }

            while (state.RecentApps.Count > MaximumRecentApps)
            {
                state.RecentApps.RemoveAt(state.RecentApps.Count - 1);
                changed = true;
            }

            if (changed)
            {
                try
                {
                    SaveState();
                }
                catch
                {
                    state.RecentApps = previousRecents;
                    throw;
                }
            }

            return accepted;
        }

        private SwitcherStateData LoadState()
        {
            if (!File.Exists(statePath))
            {
                return new SwitcherStateData();
            }

            try
            {
                using (var stream = new FileStream(
                    statePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    var serializer =
                        new DataContractJsonSerializer(typeof(SwitcherStateData));
                    var loaded = (SwitcherStateData)serializer.ReadObject(stream);
                    if (loaded == null)
                    {
                        throw new SerializationException(
                            "The switcher state file contained no data.");
                    }

                    loaded.EnsureLists();
                    return loaded;
                }
            }
            catch (SerializationException exception)
            {
                throw new InvalidDataException(
                    "The switcher state file is invalid: " + statePath,
                    exception);
            }
        }

        private void SaveState()
        {
            string directory = Path.GetDirectoryName(statePath);
            Directory.CreateDirectory(directory);
            string temporaryPath = statePath + ".tmp";

            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    var serializer =
                        new DataContractJsonSerializer(typeof(SwitcherStateData));
                    serializer.WriteObject(stream, state);
                    stream.Flush();
                }

                if (File.Exists(statePath))
                {
                    File.Replace(temporaryPath, statePath, null);
                }
                else
                {
                    File.Move(temporaryPath, statePath);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static AppWindow CreateStoredWindow(
            StoredAppRecord record,
            bool pinned,
            bool recent)
        {
            string title = string.IsNullOrEmpty(record.Title)
                ? Path.GetFileNameWithoutExtension(record.ExecutablePath)
                : record.Title;
            string processName = string.IsNullOrEmpty(record.ProcessName)
                ? Path.GetFileNameWithoutExtension(record.ExecutablePath)
                : record.ProcessName;

            return new AppWindow(
                IntPtr.Zero,
                0,
                title,
                processName,
                record.ExecutablePath,
                record.LaunchTarget,
                WindowCatalog.GetExecutableIcon(record.ExecutablePath),
                false)
            {
                IsPinned = pinned,
                IsRecent = recent,
                AppUserModelId = record.AppUserModelId
            };
        }

        private static bool CanRelaunch(WindowSnapshot window)
        {
            if (window == null
                || window.ProcessId == 0
                || window.ProcessId == (uint)Environment.ProcessId
                || string.IsNullOrEmpty(window.IdentityKey)
                || !File.Exists(window.ExecutablePath))
            {
                return false;
            }

            string executableName = Path.GetFileName(window.ExecutablePath);
            if (string.Equals(
                    executableName,
                    "explorer.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                return !string.IsNullOrEmpty(window.LaunchTarget);
            }

            switch (executableName.ToLowerInvariant())
            {
                case "applicationframehost.exe":
                case "conhost.exe":
                case "dllhost.exe":
                case "rundll32.exe":
                case "runtimebroker.exe":
                case "svchost.exe":
                case "shellexperiencehost.exe":
                case "startmenuexperiencehost.exe":
                case "searchhost.exe":
                case "searchapp.exe":
                case "textinputhost.exe":
                case "lockapp.exe":
                case "systemsettings.exe":
                    return false;
                default:
                    return true;
            }
        }

        private static int FindByIdentity(
            IList<StoredAppRecord> records,
            string identityKey)
        {
            for (int index = 0; index < records.Count; index++)
            {
                if (IdentitiesEqual(records[index].IdentityKey, identityKey))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool IdentitiesEqual(string left, string right)
        {
            return !string.IsNullOrEmpty(left)
                && !string.IsNullOrEmpty(right)
                && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    internal sealed class ClosedWindowUpdate
    {
        public ClosedWindowUpdate(
            WindowSnapshot closedWindow,
            WindowSnapshot replacementWindow,
            bool addToRecent)
        {
            ClosedWindow = closedWindow;
            ReplacementWindow = replacementWindow;
            AddToRecent = addToRecent;
        }

        public WindowSnapshot ClosedWindow { get; private set; }
        public WindowSnapshot ReplacementWindow { get; private set; }
        public bool AddToRecent { get; private set; }
    }

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

    [DataContract]
    internal sealed class SwitcherStateData
    {
        public SwitcherStateData()
        {
            PinnedApps = new List<StoredAppRecord>();
            RecentApps = new List<StoredAppRecord>();
        }

        [DataMember(Name = "pinnedApps")]
        public List<StoredAppRecord> PinnedApps { get; set; }

        [DataMember(Name = "recentApps")]
        public List<StoredAppRecord> RecentApps { get; set; }

        public void EnsureLists()
        {
            if (PinnedApps == null)
            {
                PinnedApps = new List<StoredAppRecord>();
            }

            if (RecentApps == null)
            {
                RecentApps = new List<StoredAppRecord>();
            }
        }
    }

    [DataContract]
    internal sealed class StoredAppRecord
    {
        [DataMember(Name = "executablePath")]
        public string ExecutablePath { get; set; }

        [DataMember(Name = "title")]
        public string Title { get; set; }

        [DataMember(Name = "processName")]
        public string ProcessName { get; set; }

        [DataMember(Name = "launchTarget")]
        public string LaunchTarget { get; set; }

        [DataMember(Name = "appUserModelId")]
        public string AppUserModelId { get; set; }

        [DataMember(Name = "lastClosedUtc")]
        public string LastClosedUtc { get; set; }

        public string IdentityKey
        {
            get
            {
                return AppWindow.BuildIdentityKey(
                    ExecutablePath,
                    LaunchTarget);
            }
        }

        public static StoredAppRecord FromWindow(AppWindow window)
        {
            return new StoredAppRecord
            {
                ExecutablePath = window.ExecutablePath,
                LaunchTarget = window.LaunchTarget,
                AppUserModelId = window.AppUserModelId,
                Title = window.Title,
                ProcessName = window.ProcessName
            };
        }

        public static StoredAppRecord FromSnapshot(WindowSnapshot window)
        {
            return new StoredAppRecord
            {
                ExecutablePath = window.ExecutablePath,
                LaunchTarget = window.LaunchTarget,
                AppUserModelId = window.AppUserModelId,
                Title = window.Title,
                ProcessName = window.ProcessName,
                LastClosedUtc = DateTime.UtcNow.ToString("o")
            };
        }
    }
}
