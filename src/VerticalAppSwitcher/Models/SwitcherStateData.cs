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
}
