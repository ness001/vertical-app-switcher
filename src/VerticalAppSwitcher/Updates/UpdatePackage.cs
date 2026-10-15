using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VerticalAppSwitcher
{
    internal static class UpdatePackage
    {
        public const long MaximumDownloadSize = 64 * 1024 * 1024;
        public static readonly string[] Files =
        {
            "VerticalAppSwitcher.exe", "LICENSE", "NOTICE", "README.txt", "update.json"
        };

        public static void VerifyChecksum(string archive, string checksum, string packageName)
        {
            Match match = Regex.Match(checksum.Trim(), @"\A([0-9a-fA-F]{64})  (.+)\z");
            if (!match.Success || match.Groups[2].Value != packageName)
            {
                throw new InvalidDataException("The update checksum file is invalid.");
            }

            using FileStream stream = File.OpenRead(archive);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(actual, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The update checksum does not match. No files were replaced.");
            }
        }

        public static void Extract(string archive, string destination, string version, string runtime)
        {
            UpdateRelease.ParseVersion(version);
            string prefix = "VerticalAppSwitcher-" + version + "-" + runtime + "/";
            using ZipArchive zip = ZipFile.OpenRead(archive);
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            long total = 0;
            foreach (ZipArchiveEntry entry in zip.Entries)
            {
                if (entry.FullName == prefix && entry.Length == 0)
                {
                    continue;
                }

                string name = entry.FullName.StartsWith(prefix, StringComparison.Ordinal)
                    ? entry.FullName.Substring(prefix.Length) : string.Empty;
                // Never extract arbitrary paths, links, or additional executable content.
                if (!Files.Contains(name, StringComparer.Ordinal) || !entries.TryAdd(name, entry)
                    || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000
                    || entry.Length <= 0 || entry.Length > MaximumDownloadSize)
                {
                    throw new InvalidDataException("The update package contains an unexpected entry.");
                }

                total += entry.Length;
                if (total > MaximumDownloadSize)
                {
                    throw new InvalidDataException("The update package is too large.");
                }
            }

            if (entries.Count != Files.Length)
            {
                throw new InvalidDataException("This release does not support in-app updates. Download it manually from GitHub Releases.");
            }

            Directory.CreateDirectory(destination);
            foreach (string name in Files)
            {
                entries[name].ExtractToFile(Path.Combine(destination, name), false);
            }

            ValidateManifest(destination, version, runtime);
            string executablePath = Path.Combine(destination, Files[0]);
            string productVersion = FileVersionInfo.GetVersionInfo(executablePath).ProductVersion;
            if (productVersion == null || productVersion.Split('+')[0] != version)
            {
                throw new InvalidDataException("The update executable has the wrong version.");
            }

            using FileStream executable = File.OpenRead(executablePath);
            using var pe = new PEReader(executable);
            Machine expected = runtime == "win-x64" ? Machine.Amd64 : Machine.Arm64;
            if (pe.PEHeaders.CoffHeader.Machine != expected)
            {
                throw new InvalidDataException("The update executable has the wrong architecture.");
            }
        }

        public static void ValidateManifest(string directory, string version, string runtime)
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "update.json")));
            JsonElement root = document.RootElement;
            if (root.GetProperty("schemaVersion").GetInt32() != 1
                || root.GetProperty("version").GetString() != version
                || root.GetProperty("runtime").GetString() != runtime)
            {
                throw new InvalidDataException("The update manifest does not match this release.");
            }

            string framework = root.GetProperty("desktopRuntime").GetString();
            Version required = UpdateRelease.ParseVersion(framework);
            if (!IsDesktopRuntimeInstalled(required, runtime))
            {
                throw new InvalidOperationException("This release requires .NET " + framework
                    + " Desktop Runtime (" + runtime + "). Install it from https://dotnet.microsoft.com/download/dotnet before updating.");
            }
        }

        internal static bool IsDesktopRuntimeInstalled(Version required, string runtime)
        {
            // SDK and per-user installations need not register shared frameworks in HKLM.
            if (runtime == UpdateRelease.CurrentRuntime)
            {
                string core = RuntimeEnvironment.GetRuntimeDirectory();
                string shared = Directory.GetParent(Path.TrimEndingDirectorySeparator(core))?.Parent?.FullName;
                string desktop = shared == null ? null : Path.Combine(shared, "Microsoft.WindowsDesktop.App");
                if (desktop != null && Directory.Exists(desktop)
                    && Directory.GetDirectories(desktop).Any(path =>
                        Version.TryParse(Path.GetFileName(path), out Version installed)
                        && installed.Major == required.Major && installed.Minor == required.Minor
                        && installed >= required && File.Exists(Path.Combine(path, "System.Windows.Forms.dll"))))
                {
                    return true;
                }
            }

            string architecture = runtime == "win-x64" ? "x64" : "arm64";
            foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                using RegistryKey machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using RegistryKey key = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\"
                    + architecture + @"\sharedfx\Microsoft.WindowsDesktop.App");
                if (key != null && key.GetValueNames().Any(name =>
                    Version.TryParse(name, out Version installed)
                    && installed.Major == required.Major && installed.Minor == required.Minor
                    && installed >= required)) return true;
            }
            return false;
        }
    }
}
