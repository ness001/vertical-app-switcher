using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using VerticalAppSwitcher;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] == "--apply-update")
            return UpdateInstaller.Run(arguments[1]);
        if (arguments.Length == 2 && arguments[0] == "--update-ready")
        {
            using EventWaitHandle ready = EventWaitHandle.OpenExisting(UpdateInstaller.ReadyPrefix + arguments[1]);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fixture-ready.txt"), "Replacement app started");
            ready.Set();
            Thread.Sleep(500);
            return 0;
        }
        if (arguments.Length == 1 && arguments[0] == "--installer-fixture")
            return RunInstallerFixture();

        string root = Path.Combine(Path.GetTempPath(), "vertical-update-checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(string name, Action test)
        {
            test();
            passed++;
            Console.WriteLine("PASS " + name);
        }

        try
        {
            Check("stable release and architecture selection", () =>
            {
                UpdateRelease release = UpdateRelease.Parse(ReleaseJson("0.3.0"), "0.2.0", "win-arm64");
                Assert(release.PackageName.EndsWith("win-arm64.zip"), "Wrong architecture");
                Assert(UpdateRelease.Parse(ReleaseJson("0.2.0"), "0.2.0", "win-x64") == null, "Same version offered");
                Assert(UpdateRelease.Parse(ReleaseJson("0.1.0"), "0.2.0", "win-x64") == null, "Downgrade offered");
                Assert(UpdateRelease.Parse(ReleaseJson("0.2.0"), "0.2.0-rc.1", "win-x64") != null, "Stable upgrade missing");
                Assert(UpdateRelease.Parse(ReleaseJson("0.3.0", true), "0.2.0", "win-x64") == null, "Prerelease offered");
                Assert(UpdateRelease.Parse(ReleaseJson("0.3.0").Replace("\"draft\":false", "\"draft\":true"), "0.2.0", "win-x64") == null, "Draft offered");
            });
            Check("untrusted assets and malformed versions rejected", () =>
            {
                Throws(() => UpdateRelease.Parse(ReleaseJson("0.3.0").Replace("https://github.com/", "https://example.com/"), "0.2.0", "win-x64"));
                foreach (string version in new[] { "01.2.3", "1.2", "1.2.3.4", "1.2.3-rc.1", "65535.0.0", "../1.2.3" })
                    Throws(() => UpdateRelease.ParseVersion(version));
            });
            Check("checksum acceptance and tamper rejection", () =>
            {
                string file = Path.Combine(root, "test.zip");
                File.WriteAllText(file, "fixture");
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
                UpdatePackage.VerifyChecksum(file, hash + "  test.zip\n", "test.zip");
                Throws(() => UpdatePackage.VerifyChecksum(file, hash + "  other.zip", "test.zip"));
                File.AppendAllText(file, "tampered");
                Throws(() => UpdatePackage.VerifyChecksum(file, hash + "  test.zip", "test.zip"));
            });
            Check("ZIP traversal extra files and duplicate files rejected", () =>
            {
                foreach (string[] names in new[]
                {
                    new[] { "../outside.exe" },
                    new[] { "VerticalAppSwitcher-0.3.0-win-x64/../outside.exe" },
                    new[] { "VerticalAppSwitcher-0.3.0-win-x64/extra.dll" },
                    new[] { "VerticalAppSwitcher-0.3.0-win-x64/LICENSE", "VerticalAppSwitcher-0.3.0-win-x64/LICENSE" }
                })
                {
                    string zipPath = Path.Combine(root, Guid.NewGuid() + ".zip");
                    using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                        foreach (string name in names)
                            using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("fixture");
                    Throws(() => UpdatePackage.Extract(zipPath, Path.Combine(root, Guid.NewGuid().ToString()), "0.3.0", "win-x64"));
                }
            });
            Check("update gate prevents another normal startup", () =>
            {
                Assert(!UpdateInstaller.IsUpdateInProgress(), "Unexpected existing update");
                using (var gate = new Mutex(false, UpdateInstaller.GateName))
                    Assert(UpdateInstaller.IsUpdateInProgress(), "Active update not detected");
                Assert(!UpdateInstaller.IsUpdateInProgress(), "Update gate retained");
            });
            Check("runtime requirements checked", () =>
            {
                Assert(UpdatePackage.IsDesktopRuntimeInstalled(new Version(10, 0, 0), UpdateRelease.CurrentRuntime), "Current runtime missing");
                Assert(!UpdatePackage.IsDesktopRuntimeInstalled(new Version(99, 0, 0), UpdateRelease.CurrentRuntime), "Unknown runtime accepted");
            });
            Check("replacement preserves unrelated files and creates backup", () =>
            {
                (string package, string target) = Fixture(root);
                string backup = UpdateInstaller.Install(package, target, _ => true);
                Assert(File.ReadAllText(target) == "new VerticalAppSwitcher.exe", "Not updated");
                Assert(File.ReadAllText(Path.Combine(backup, "VerticalAppSwitcher.exe")) == "old VerticalAppSwitcher.exe", "No backup");
                Assert(File.ReadAllText(Path.Combine(Path.GetDirectoryName(target), "state.json")) == "keep", "Profile altered");
            });
            Check("restart failure restores old files", () =>
            {
                (string package, string target) = Fixture(root);
                int attempts = 0;
                Throws(() => UpdateInstaller.Install(package, target, _ => ++attempts > 1));
                Assert(attempts == 2, "Old version not restarted");
                Assert(File.ReadAllText(target) == "old VerticalAppSwitcher.exe", "Rollback failed");
                Assert(!File.Exists(Path.Combine(Path.GetDirectoryName(target), "update.json")), "New file not removed");
            });
            Check("partial file failure restores changed files", () =>
            {
                (string package, string target) = Fixture(root);
                File.Delete(Path.Combine(package, "NOTICE"));
                Throws(() => UpdateInstaller.Install(package, target, _ => true));
                Assert(File.ReadAllText(target) == "old VerticalAppSwitcher.exe", "Partial rollback failed");
            });
            Check("HTTP failures and cancellation are safe", () =>
            {
                using var missing = new UpdateClient(new Handler(HttpStatusCode.NotFound));
                Assert(missing.CheckAsync(CancellationToken.None).GetAwaiter().GetResult() == null, "404 not handled");
                using var failed = new UpdateClient(new Handler(HttpStatusCode.Forbidden));
                Throws(() => failed.CheckAsync(CancellationToken.None).GetAwaiter().GetResult());
                using var canceled = new CancellationTokenSource();
                canceled.Cancel();
                Throws(() => missing.CheckAsync(canceled.Token).GetAwaiter().GetResult());
            });
            Check("postpone preferences and native controls", () =>
            {
                var settings = new UpdatePreferences { AutomaticChecks = false, PostponedUntil = DateTimeOffset.UtcNow.AddDays(1) };
                UpdatePreferences restored = JsonSerializer.Deserialize<UpdatePreferences>(JsonSerializer.Serialize(settings));
                Assert(!restored.AutomaticChecks && restored.PostponedUntil > DateTimeOffset.UtcNow, "Preferences lost");
                Application.EnableVisualStyles();
                using var form = new UpdateForm("0.2.0", System.Drawing.SystemIcons.Application);
                form.Show();
                Application.DoEvents();
                Button install = Descendants(form).OfType<Button>().Single(button => button.Text == "Update and restart");
                Button later = Descendants(form).OfType<Button>().Single(button => button.Text == "Later");
                Assert(!install.Enabled, "Install active before verification");
                bool postponed = false;
                form.PostponeRequested += () => postponed = true;
                form.SetStatus("Version 0.3.0 is ready.", true);
                Assert(install.Enabled, "Verified package cannot be installed");
                later.PerformClick();
                Application.DoEvents();
                Assert(postponed && !form.Visible, "Later failed");
            });
            if (arguments.Length == 2)
            {
                Check("real package checksum manifest version and architecture", () =>
                {
                    string archive = Path.GetFullPath(arguments[0]);
                    string runtime = arguments[1];
                    string name = Path.GetFileName(archive);
                    UpdatePackage.VerifyChecksum(archive, File.ReadAllText(archive + ".sha256"), name);
                    UpdatePackage.Extract(archive, Path.Combine(root, "real-package"), "0.2.0", runtime);
                });
            }
            Console.WriteLine(passed + " checks passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally { Directory.Delete(root, true); }
    }

    private static int RunInstallerFixture()
    {
        try
        {
            string directory = AppContext.BaseDirectory;
            string workspace = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerticalAppSwitcher", "Updates", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            string name = "VerticalAppSwitcher-0.2.0-" + UpdateRelease.CurrentRuntime;
            string package = Path.Combine(workspace, name);
            Directory.CreateDirectory(package);
            File.Copy(Environment.ProcessPath, Path.Combine(package, "VerticalAppSwitcher.exe"));
            foreach (string file in new[] { "LICENSE", "NOTICE", "README.txt" })
                File.WriteAllText(Path.Combine(package, file), "Disposable helper fixture");
            File.WriteAllText(Path.Combine(package, "update.json"), JsonSerializer.Serialize(new
            {
                schemaVersion = 1, version = "0.2.0", runtime = UpdateRelease.CurrentRuntime, desktopRuntime = "10.0.0"
            }));
            string archive = Path.Combine(workspace, name + ".zip");
            using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                foreach (string file in UpdatePackage.Files)
                    zip.CreateEntryFromFile(Path.Combine(package, file), name + "/" + file);
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive)));
            File.WriteAllText(Path.Combine(workspace, "checksum.txt"), hash + "  " + name + ".zip");
            UpdateRelease release = UpdateRelease.Parse(ReleaseJson("0.2.0"), "0.1.0", UpdateRelease.CurrentRuntime);
            string attempt = UpdateInstaller.Start(workspace, release);
            File.WriteAllText(Path.Combine(directory, "fixture-workspace.txt"), attempt);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static (string, string) Fixture(string root)
    {
        string folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
        string package = Path.Combine(folder, "package");
        string install = Path.Combine(folder, "install");
        Directory.CreateDirectory(package);
        Directory.CreateDirectory(install);
        foreach (string name in UpdatePackage.Files)
        {
            File.WriteAllText(Path.Combine(package, name), "new " + name);
            if (name != "update.json") File.WriteAllText(Path.Combine(install, name), "old " + name);
        }
        File.WriteAllText(Path.Combine(install, "state.json"), "keep");
        return (package, Path.Combine(install, "VerticalAppSwitcher.exe"));
    }

    private static string ReleaseJson(string version, bool prerelease = false)
    {
        var assets = new List<object>();
        foreach (string runtime in new[] { "win-x64", "win-arm64" })
        foreach (string suffix in new[] { ".zip", ".zip.sha256" })
        {
            string name = "VerticalAppSwitcher-" + version + "-" + runtime + suffix;
            assets.Add(new { name, browser_download_url = UpdateRelease.RepositoryUrl + "/releases/download/v" + version + "/" + name });
        }
        return JsonSerializer.Serialize(new { tag_name = "v" + version, draft = false, prerelease, assets });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Throws(Action action)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new Exception("Expected operation to fail.");
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;
        public Handler(HttpStatusCode status) { this.status = status; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(status) { RequestMessage = request });
        }
    }
}
