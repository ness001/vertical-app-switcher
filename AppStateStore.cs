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
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerticalAppSwitcher");
            statePath = Path.Combine(directory, "state.json");
            state = LoadState();
        }

        public AppWindowCollection Compose(AppWindowCollection activeWindows)
        {
            List<AppWindow> active = activeWindows.Detach();
            activeWindows.Dispose();

            var remaining = new List<AppWindow>(active);
            var composed = new List<AppWindow>();
            var representedIdentities = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (StoredAppRecord pin in state.PinnedApps)
            {
                bool foundOpenWindow = false;
                for (int index = 0; index < remaining.Count;)
                {
                    AppWindow window = remaining[index];
                    if (!IdentitiesEqual(window.IdentityKey, pin.IdentityKey))
                    {
                        index++;
                        continue;
                    }

                    window.IsPinned = true;
                    composed.Add(window);
                    remaining.RemoveAt(index);
                    foundOpenWindow = true;
                }

                if (!foundOpenWindow)
                {
                    composed.Add(CreateStoredWindow(pin, true, false));
                }

                representedIdentities.Add(pin.IdentityKey);
            }

            foreach (AppWindow window in remaining)
            {
                composed.Add(window);
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

                composed.Add(CreateStoredWindow(recent, false, true));
                representedIdentities.Add(recent.IdentityKey);
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
            var accepted = new List<WindowSnapshot>();
            foreach (WindowSnapshot window in closedWindows)
            {
                if (!CanRelaunch(window.ExecutablePath))
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
                SaveState();
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

        private static bool CanRelaunch(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)
                || !File.Exists(executablePath))
            {
                return false;
            }

            return !string.Equals(
                Path.GetFileName(executablePath),
                "ApplicationFrameHost.exe",
                StringComparison.OrdinalIgnoreCase);
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
        private bool initialized;
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
            IList<WindowSnapshot> snapshots =
                WindowCatalog.GetSwitchableWindowSnapshots();
            var current = new Dictionary<IntPtr, WindowSnapshot>();
            var currentByIdentity =
                new Dictionary<string, List<WindowSnapshot>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (WindowSnapshot snapshot in snapshots)
            {
                current[snapshot.TrackingKey] = snapshot;
                if (!string.IsNullOrEmpty(snapshot.IdentityKey))
                {
                    List<WindowSnapshot> matchingIdentity;
                    if (!currentByIdentity.TryGetValue(
                            snapshot.IdentityKey,
                            out matchingIdentity))
                    {
                        matchingIdentity = new List<WindowSnapshot>();
                        currentByIdentity[snapshot.IdentityKey] =
                            matchingIdentity;
                    }

                    matchingIdentity.Add(snapshot);
                }
            }

            if (initialized)
            {
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
                        && string.Equals(
                            snapshot.IdentityKey,
                            currentSnapshot.IdentityKey,
                            StringComparison.OrdinalIgnoreCase);
                    if (sameWindowIdentity)
                    {
                        continue;
                    }

                    if (!current.ContainsKey(previous.Key)
                        && snapshot.Handle != IntPtr.Zero
                        && NativeMethods.IsWindow(snapshot.Handle))
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
                        foreach (WindowSnapshot candidate in matchingIdentity)
                        {
                            if (candidate.TrackingKey != snapshot.TrackingKey)
                            {
                                replacement = candidate;
                                break;
                            }
                        }
                    }

                    if (replacement != null)
                    {
                        replacements[snapshot] = replacement;
                    }
                    else if (!string.IsNullOrEmpty(snapshot.ExecutablePath)
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
            }

            knownWindows = current;
            initialized = true;
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
