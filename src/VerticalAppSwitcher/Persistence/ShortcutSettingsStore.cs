using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
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
}
