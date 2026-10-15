using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
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
