using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
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
}
