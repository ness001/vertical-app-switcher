using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal sealed class ShortcutDefinition
    {
        private static readonly IList<ShortcutDefinition> available =
            new List<ShortcutDefinition>
            {
                new ShortcutDefinition(
                    "win-backtick",
                    "Win + ` (recommended)",
                    "Win + `",
                    NativeMethods.ModWin,
                    NativeMethods.VirtualKeyOem3,
                    true),
                new ShortcutDefinition(
                    "ctrl-shift-space",
                    "Ctrl + Shift + Space",
                    "Ctrl + Shift + Space",
                    NativeMethods.ModControl | NativeMethods.ModShift,
                    NativeMethods.VirtualKeySpace,
                    false),
                new ShortcutDefinition(
                    "ctrl-alt-space",
                    "Ctrl + Alt + Space",
                    "Ctrl + Alt + Space",
                    NativeMethods.ModControl | NativeMethods.ModAlt,
                    NativeMethods.VirtualKeySpace,
                    false),
                new ShortcutDefinition(
                    "ctrl-shift-backtick",
                    "Ctrl + Shift + `",
                    "Ctrl + Shift + `",
                    NativeMethods.ModControl | NativeMethods.ModShift,
                    NativeMethods.VirtualKeyOem3,
                    false),
                new ShortcutDefinition(
                    "alt-shift-space",
                    "Alt + Shift + Space",
                    "Alt + Shift + Space",
                    NativeMethods.ModAlt | NativeMethods.ModShift,
                    NativeMethods.VirtualKeySpace,
                    false)
            }.AsReadOnly();

        private ShortcutDefinition(
            string id,
            string selectionText,
            string displayText,
            uint modifiers,
            uint virtualKey,
            bool registerShiftedAlias)
        {
            Id = id;
            SelectionText = selectionText;
            DisplayText = displayText;
            Modifiers = modifiers | NativeMethods.ModNoRepeat;
            VirtualKey = virtualKey;
            RegisterShiftedAlias = registerShiftedAlias;
        }

        public string Id { get; private set; }

        public string SelectionText { get; private set; }

        public string DisplayText { get; private set; }

        public uint Modifiers { get; private set; }

        public uint VirtualKey { get; private set; }

        public bool RegisterShiftedAlias { get; private set; }

        public static IList<ShortcutDefinition> Available
        {
            get { return available; }
        }

        public static ShortcutDefinition Default
        {
            get { return available[0]; }
        }

        public static ShortcutDefinition Find(string id)
        {
            foreach (ShortcutDefinition shortcut in available)
            {
                if (string.Equals(shortcut.Id, id, StringComparison.Ordinal))
                {
                    return shortcut;
                }
            }

            if (string.IsNullOrEmpty(id) || !id.StartsWith("custom-", StringComparison.Ordinal))
            {
                return null;
            }

            string[] parts = id.Split('-');
            uint modifiers;
            uint virtualKey;
            ShortcutDefinition customShortcut;
            string error;
            if (parts.Length == 3
                && uint.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out modifiers)
                && uint.TryParse(parts[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out virtualKey)
                && TryCreate(modifiers, virtualKey, out customShortcut, out error))
            {
                return customShortcut;
            }

            return null;
        }

        public static bool TryCreate(
            uint modifiers,
            uint virtualKey,
            out ShortcutDefinition shortcut,
            out string error)
        {
            shortcut = null;
            error = null;
            modifiers &= ~NativeMethods.ModNoRepeat;
            uint supportedModifiers = NativeMethods.ModControl
                | NativeMethods.ModAlt | NativeMethods.ModShift | NativeMethods.ModWin;
            if ((modifiers & ~supportedModifiers) != 0)
            {
                error = "This modifier is not supported. Choose Ctrl, Alt, Shift, or Win.";
                return false;
            }

            if ((modifiers & (NativeMethods.ModControl | NativeMethods.ModAlt | NativeMethods.ModWin)) == 0)
            {
                error = "Add Ctrl, Alt, or Win. Shift alone would change normal typing.";
                return false;
            }

            string keyText = GetKeyDisplayText(virtualKey);
            if (string.IsNullOrEmpty(keyText))
            {
                error = "Choose a letter, number, function key, or another key on the keyboard below.";
                return false;
            }

            Keys key = (Keys)virtualKey;
            bool alt = (modifiers & NativeMethods.ModAlt) != 0;
            bool control = (modifiers & NativeMethods.ModControl) != 0;
            bool windows = (modifiers & NativeMethods.ModWin) != 0;
            if (key == Keys.F12
                || (alt && key == Keys.Tab)
                || (modifiers == NativeMethods.ModAlt && (key == Keys.Escape || key == Keys.F4 || key == Keys.Space))
                || (control && key == Keys.Escape)
                || (control && alt && key == Keys.Delete)
                || (windows && (key == Keys.L || key == Keys.Tab || key == Keys.Space)))
            {
                error = "Windows uses this combination for a system action. Choose another shortcut.";
                return false;
            }

            foreach (ShortcutDefinition preset in available)
            {
                if ((preset.Modifiers & ~NativeMethods.ModNoRepeat) == modifiers
                    && preset.VirtualKey == virtualKey)
                {
                    shortcut = preset;
                    return true;
                }
            }

            string displayText = GetChordDisplayText(modifiers, virtualKey);
            shortcut = new ShortcutDefinition(
                "custom-" + modifiers.ToString("X", CultureInfo.InvariantCulture)
                    + "-" + virtualKey.ToString("X2", CultureInfo.InvariantCulture),
                displayText,
                displayText,
                modifiers,
                virtualKey,
                false);
            return true;
        }

        public static string GetChordDisplayText(uint modifiers, uint virtualKey)
        {
            var parts = new List<string>(5);
            if ((modifiers & NativeMethods.ModControl) != 0)
            {
                parts.Add("Ctrl");
            }

            if ((modifiers & NativeMethods.ModAlt) != 0)
            {
                parts.Add("Alt");
            }

            if ((modifiers & NativeMethods.ModShift) != 0)
            {
                parts.Add("Shift");
            }

            if ((modifiers & NativeMethods.ModWin) != 0)
            {
                parts.Add("Win");
            }

            string keyText = GetKeyDisplayText(virtualKey);
            if (!string.IsNullOrEmpty(keyText))
            {
                parts.Add(keyText);
            }

            return string.Join(" + ", parts.ToArray());
        }

        public static string GetKeyDisplayText(uint virtualKey)
        {
            if ((virtualKey >= (uint)Keys.A && virtualKey <= (uint)Keys.Z)
                || (virtualKey >= (uint)Keys.D0 && virtualKey <= (uint)Keys.D9))
            {
                return ((char)virtualKey).ToString();
            }

            if (virtualKey >= (uint)Keys.F1 && virtualKey <= (uint)Keys.F24)
            {
                return "F" + (virtualKey - (uint)Keys.F1 + 1).ToString(CultureInfo.InvariantCulture);
            }

            if (virtualKey >= (uint)Keys.NumPad0 && virtualKey <= (uint)Keys.NumPad9)
            {
                return "Num " + (virtualKey - (uint)Keys.NumPad0).ToString(CultureInfo.InvariantCulture);
            }

            switch ((Keys)virtualKey)
            {
                case Keys.Back: return "Backspace";
                case Keys.Tab: return "Tab";
                case Keys.Enter: return "Enter";
                case Keys.Escape: return "Esc";
                case Keys.Space: return "Space";
                case Keys.Insert: return "Insert";
                case Keys.Delete: return "Delete";
                case Keys.Home: return "Home";
                case Keys.End: return "End";
                case Keys.PageUp: return "Page Up";
                case Keys.PageDown: return "Page Down";
                case Keys.Left: return "Left";
                case Keys.Right: return "Right";
                case Keys.Up: return "Up";
                case Keys.Down: return "Down";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Multiply: return "Num *";
                case Keys.Divide: return "Num /";
                case Keys.Decimal: return "Num .";
                case Keys.Oem3: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.Oem5: return "\\";
                case Keys.Oem1: return ";";
                case Keys.Oem7: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                default: return null;
            }
        }

        public override string ToString()
        {
            return SelectionText;
        }
    }

    internal sealed class ShortcutSettingsStore
    {
        private readonly string settingsPath;

        public ShortcutSettingsStore()
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerticalAppSwitcher");
            settingsPath = Path.Combine(directory, "settings.json");
        }

        public ShortcutSettingsData Load()
        {
            if (!File.Exists(settingsPath))
            {
                return new ShortcutSettingsData
                {
                    ShortcutId = ShortcutDefinition.Default.Id,
                    SetupCompleted = false
                };
            }

            try
            {
                using (var stream = new FileStream(
                    settingsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read))
                {
                    var serializer =
                        new DataContractJsonSerializer(typeof(ShortcutSettingsData));
                    var settings =
                        (ShortcutSettingsData)serializer.ReadObject(stream);
                    if (settings == null)
                    {
                        throw new SerializationException(
                            "The shortcut settings file contained no data.");
                    }

                    if (ShortcutDefinition.Find(settings.ShortcutId) == null)
                    {
                        throw new SerializationException(
                            "The shortcut settings file contains an unknown shortcut.");
                    }

                    return settings;
                }
            }
            catch (SerializationException exception)
            {
                throw new InvalidDataException(
                    "The shortcut settings file is invalid: " + settingsPath,
                    exception);
            }
        }

        public void Save(ShortcutSettingsData settings)
        {
            if (settings == null)
            {
                throw new ArgumentNullException("settings");
            }

            if (ShortcutDefinition.Find(settings.ShortcutId) == null)
            {
                throw new InvalidOperationException(
                    "The selected shortcut cannot be saved.");
            }

            string directory = Path.GetDirectoryName(settingsPath);
            Directory.CreateDirectory(directory);
            string temporaryPath = settingsPath + ".tmp";

            try
            {
                using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None))
                {
                    var serializer =
                        new DataContractJsonSerializer(typeof(ShortcutSettingsData));
                    serializer.WriteObject(stream, settings);
                    stream.Flush();
                }

                if (File.Exists(settingsPath))
                {
                    File.Replace(temporaryPath, settingsPath, null);
                }
                else
                {
                    File.Move(temporaryPath, settingsPath);
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
    }

    [DataContract]
    internal sealed class ShortcutSettingsData
    {
        [DataMember(Name = "shortcutId")]
        public string ShortcutId { get; set; }

        [DataMember(Name = "setupCompleted")]
        public bool SetupCompleted { get; set; }
    }
}
