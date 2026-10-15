using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal sealed class UpdatePlan
    {
        public int ParentId { get; set; }
        public long ParentStartTime { get; set; }
        public string Target { get; set; }
        public string Version { get; set; }
        public string Runtime { get; set; }
        public string ReadyEvent { get; set; }
    }

    internal static class UpdateInstaller
    {
        public const string ReadyPrefix = @"Local\VerticalAppSwitcher.UpdateReady.";
        public const string GateName = @"Local\VerticalAppSwitcher.UpdateInProgress";

        public static bool IsUpdateInProgress()
        {
            if (!Mutex.TryOpenExisting(GateName, out Mutex gate)) return false;
            gate.Dispose();
            return true;
        }

        public static string Start(string workspace, UpdateRelease release)
        {
            string target = Environment.ProcessPath;
            if (!string.Equals(Path.GetFileName(target), "VerticalAppSwitcher.exe", StringComparison.OrdinalIgnoreCase)
                || File.Exists(Path.ChangeExtension(target, ".runtimeconfig.json")))
            {
                throw new InvalidOperationException("In-app installation requires the single-file app. Use Build.ps1 or a release ZIP.");
            }

            // Fail before shutdown if the installation folder is not writable.
            string probe = Path.Combine(Path.GetDirectoryName(target), ".update-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                1, FileOptions.DeleteOnClose)) { }

            // Each attempt owns its helper files; a timed-out helper is never overwritten.
            string attempt = Path.Combine(Path.GetDirectoryName(workspace), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(attempt);
            File.Copy(Path.Combine(workspace, release.PackageName), Path.Combine(attempt, release.PackageName));
            File.Copy(Path.Combine(workspace, "checksum.txt"), Path.Combine(attempt, "checksum.txt"));
            workspace = attempt;
            string token = Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyPrefix + token);
            using Process current = Process.GetCurrentProcess();
            var plan = new UpdatePlan
            {
                ParentId = current.Id,
                ParentStartTime = current.StartTime.ToUniversalTime().Ticks,
                Target = target,
                Version = release.Version.ToString(),
                Runtime = release.Runtime,
                ReadyEvent = token
            };
            string planPath = Path.Combine(workspace, "plan.json");
            File.WriteAllText(planPath, JsonSerializer.Serialize(plan));
            string helper = Path.Combine(workspace, "UpdateHelper.exe");
            File.Copy(target, helper, false);
            var start = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = workspace };
            start.ArgumentList.Add("--apply-update");
            start.ArgumentList.Add(planPath);
            using Process process = Process.Start(start);
            if (process == null || !ready.WaitOne(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException("The update helper could not start. The app will stay open.");
            }
            File.WriteAllText(Path.Combine(workspace, "approved"), token);
            return workspace;
        }

        public static int Run(string planPath)
        {
            string workspace = Path.GetDirectoryName(Path.GetFullPath(planPath));
            string log = Path.Combine(workspace, "update.log");
            try
            {
                UpdatePlan plan = JsonSerializer.Deserialize<UpdatePlan>(File.ReadAllText(planPath));
                ValidatePlan(plan, workspace);
                using var gate = new Mutex(false, GateName, out bool newGate);
                if (!newGate) throw new InvalidOperationException("Another update is in progress. Wait for it to finish before retrying.");
                using Process parent = Process.GetProcessById(plan.ParentId);
                if (parent.StartTime.ToUniversalTime().Ticks != plan.ParentStartTime
                    || !string.Equals(parent.MainModule.FileName, plan.Target, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The update target process does not match.");
                }

                string packageName = "VerticalAppSwitcher-" + plan.Version + "-" + plan.Runtime + ".zip";
                string archive = Path.Combine(workspace, packageName);
                UpdatePackage.VerifyChecksum(archive, File.ReadAllText(Path.Combine(workspace, "checksum.txt")), packageName);
                string verified = Path.Combine(workspace, "verified-" + Guid.NewGuid().ToString("N"));
                UpdatePackage.Extract(archive, verified, plan.Version, plan.Runtime);
                using (EventWaitHandle ready = EventWaitHandle.OpenExisting(ReadyPrefix + plan.ReadyEvent))
                {
                    ready.Set();
                }

                if (!parent.WaitForExit(30000))
                {
                    throw new InvalidOperationException("The app did not exit normally. No files were replaced.");
                }

                string approval = Path.Combine(workspace, "approved");
                if (!File.Exists(approval) || File.ReadAllText(approval) != plan.ReadyEvent)
                {
                    throw new InvalidOperationException("Update startup was not confirmed. No files were replaced.");
                }

                string backup = Install(verified, plan.Target, TryRestart);
                File.AppendAllText(log, "Update installed. Previous files: " + backup + Environment.NewLine);
                return 0;
            }
            catch (Exception exception)
            {
                try { File.AppendAllText(log, exception + Environment.NewLine); }
                catch (Exception) { }
                MessageBox.Show(exception.Message + "\n\nUpdate details: " + log,
                    "Vertical App Switcher update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
        }

        private static void ValidatePlan(UpdatePlan plan, string workspace)
        {
            string updatesRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerticalAppSwitcher", "Updates") + Path.DirectorySeparatorChar;
            if (plan == null || !workspace.StartsWith(updatesRoot, StringComparison.OrdinalIgnoreCase)
                || !Guid.TryParseExact(Path.GetFileName(workspace), "N", out _)
                || !Guid.TryParseExact(plan.ReadyEvent, "N", out _)
                || !Path.IsPathFullyQualified(plan.Target)
                || Path.GetFileName(plan.Target) != "VerticalAppSwitcher.exe"
                || plan.Runtime != UpdateRelease.CurrentRuntime)
            {
                throw new InvalidDataException("The update request is invalid.");
            }

            UpdateRelease.ParseVersion(plan.Version);
        }

        // Only the five release files are changed. Profile files and startup registration stay untouched.
        internal static string Install(string package, string target, Func<string, bool> restart)
        {
            string directory = Path.GetDirectoryName(target);
            string backup = Path.Combine(directory, ".update-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backup);
            var changed = new List<string>();
            try
            {
                foreach (string name in UpdatePackage.Files)
                {
                    string destination = Path.Combine(directory, name);
                    string staged = Path.Combine(directory, ".update-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        File.Copy(Path.Combine(package, name), staged, false);
                        if (File.Exists(destination))
                        {
                            File.Replace(staged, destination, Path.Combine(backup, name));
                        }
                        else
                        {
                            File.Move(staged, destination);
                        }

                        changed.Add(name);
                    }
                    finally
                    {
                        if (File.Exists(staged)) File.Delete(staged);
                    }
                }

                if (!restart(target))
                {
                    throw new InvalidOperationException("The new app did not start. Restoring the previous files.");
                }

                return backup;
            }
            catch (UpdateStillRunningException)
            {
                // Never replace files under a process that may still be starting.
                throw new InvalidOperationException("The new app did not confirm startup. Close it normally before restoring files from " + backup);
            }
            catch (Exception failure)
            {
                try
                {
                    for (int index = changed.Count - 1; index >= 0; index--)
                    {
                        string name = changed[index];
                        string destination = Path.Combine(directory, name);
                        string original = Path.Combine(backup, name);
                        if (File.Exists(original)) File.Copy(original, destination, true);
                        else File.Delete(destination);
                    }

                    if (!restart(target))
                    {
                        throw new IOException("The previous app could not restart. Start it manually.");
                    }
                }
                catch (Exception recovery)
                {
                    throw new IOException("Update recovery needs attention. Previous files: " + backup
                        + ". " + recovery.Message, failure);
                }

                throw new IOException("The update failed. Previous files were restored. " + failure.Message, failure);
            }
        }

        private static bool TryRestart(string target)
        {
            string token = Guid.NewGuid().ToString("N");
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ReadyPrefix + token);
            var start = new ProcessStartInfo(target) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(target) };
            start.ArgumentList.Add("--update-ready");
            start.ArgumentList.Add(token);
            using Process process = Process.Start(start);
            if (process == null) return false;
            for (int attempt = 0; attempt < 200; attempt++)
            {
                if (ready.WaitOne(100)) return true;
                if (process.HasExited) return false;
            }

            throw new UpdateStillRunningException();
        }

        private sealed class UpdateStillRunningException : Exception { }
    }
}
