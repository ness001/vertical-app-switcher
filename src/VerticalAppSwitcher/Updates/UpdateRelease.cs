using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VerticalAppSwitcher
{
    internal sealed class UpdateRelease
    {
        public const string RepositoryUrl = "https://github.com/ness001/vertical-app-switcher";
        public const string ApiUrl = "https://api.github.com/repos/ness001/vertical-app-switcher/releases/latest";
        public Version Version { get; private set; }
        public string Runtime { get; private set; }
        public string PackageName { get; private set; }
        public Uri PackageUrl { get; private set; }
        public Uri ChecksumUrl { get; private set; }
        public string PageUrl { get { return RepositoryUrl + "/releases/tag/v" + Version; } }

        public static string CurrentVersion
        {
            get
            {
                return typeof(UpdateRelease).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                    .InformationalVersion.Split('+')[0];
            }
        }

        public static string CurrentRuntime
        {
            get
            {
                return RuntimeInformation.ProcessArchitecture switch
                {
                    Architecture.X64 => "win-x64",
                    Architecture.Arm64 => "win-arm64",
                    _ => throw new NotSupportedException("Updates support only x64 and Arm64 apps.")
                };
            }
        }

        public static Version ParseVersion(string text)
        {
            if (text == null || !Regex.IsMatch(text,
                @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z")
                || !Version.TryParse(text, out Version version)
                || version.Major > 65534 || version.Minor > 65534 || version.Build > 65534)
            {
                throw new FormatException("The release version is not supported.");
            }

            return version;
        }

        public static UpdateRelease Parse(string json, string currentVersion, string runtime)
        {
            if (runtime != "win-x64" && runtime != "win-arm64")
            {
                throw new NotSupportedException("The app architecture is not supported.");
            }

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
            {
                return null;
            }

            string tag = root.GetProperty("tag_name").GetString();
            if (tag == null || !tag.StartsWith("v", StringComparison.Ordinal))
            {
                throw new FormatException("The release tag must start with v.");
            }

            Version version = ParseVersion(tag.Substring(1));
            string current = currentVersion.Split('+')[0];
            Version installed = ParseVersion(current.Split('-')[0]);
            if (version < installed || (version == installed && !current.Contains('-')))
            {
                return null;
            }

            string package = "VerticalAppSwitcher-" + version + "-" + runtime + ".zip";
            JsonElement[] assets = root.GetProperty("assets").EnumerateArray().ToArray();
            return new UpdateRelease
            {
                Version = version,
                Runtime = runtime,
                PackageName = package,
                PackageUrl = FindAsset(assets, tag, package),
                ChecksumUrl = FindAsset(assets, tag, package + ".sha256")
            };
        }

        private static Uri FindAsset(JsonElement[] assets, string tag, string name)
        {
            JsonElement[] matches = assets.Where(asset =>
                asset.GetProperty("name").GetString() == name).ToArray();
            string expected = RepositoryUrl + "/releases/download/" + tag + "/" + name;
            if (matches.Length != 1
                || matches[0].GetProperty("browser_download_url").GetString() != expected)
            {
                throw new InvalidOperationException("The release is missing a trusted asset: " + name);
            }

            return new Uri(expected);
        }
    }
}
