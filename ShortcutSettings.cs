using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

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

            return null;
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
