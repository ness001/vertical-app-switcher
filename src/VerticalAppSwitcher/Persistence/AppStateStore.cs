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
}
