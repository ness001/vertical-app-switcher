using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    [DataContract]
    internal sealed class ShortcutSettingsData
    {
        [DataMember(Name = "shortcutId")]
        public string ShortcutId { get; set; }

        [DataMember(Name = "setupCompleted")]
        public bool SetupCompleted { get; set; }
    }
}
