using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace VerticalAppSwitcher
{
    internal sealed class UpdatePreferences
    {
        public bool AutomaticChecks { get; set; } = true;
        public DateTimeOffset PostponedUntil { get; set; }
    }

    internal sealed class UpdateController : IDisposable
    {
        private readonly UpdateClient client = new UpdateClient();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer timer;
        private readonly NotifyIcon tray;
        private readonly Action exit;
        private readonly Func<bool> canShow;
        private readonly ToolStripMenuItem checkItem;
        private readonly ToolStripMenuItem automaticItem;
        private readonly string settingsPath;
        private UpdatePreferences preferences;
        private UpdateRelease release;
        private string workspace;
        private string status = "Check GitHub Releases for a newer stable version.";
        private UpdateForm form;
        private bool busy;
        private bool installing;
        private bool disposed;

        public UpdateController(ContextMenuStrip menu, NotifyIcon tray, Action exit, Func<bool> canShow)
        {
            this.tray = tray;
            this.exit = exit;
            this.canShow = canShow;
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VerticalAppSwitcher", "updates.json");
            preferences = LoadPreferences();
            checkItem = new ToolStripMenuItem("Check for updates...");
            checkItem.Click += async delegate { await OpenAsync(); };
            automaticItem = new ToolStripMenuItem("Check for updates automatically") { Checked = preferences.AutomaticChecks };
            automaticItem.Click += delegate
            {
                preferences.AutomaticChecks = !preferences.AutomaticChecks;
                automaticItem.Checked = preferences.AutomaticChecks;
                SavePreferences();
            };
            int index = menu.Items.Count - 2;
            menu.Items.Insert(index, checkItem);
            menu.Items.Insert(index + 1, automaticItem);
            tray.BalloonTipClicked += OnBalloonClick;
            timer = new System.Windows.Forms.Timer { Interval = 20000 };
            timer.Tick += async delegate
            {
                if (!canShow()) return;
                timer.Interval = 12 * 60 * 60 * 1000;
                if (preferences.AutomaticChecks && DateTimeOffset.UtcNow >= preferences.PostponedUntil)
                {
                    await CheckAsync(false);
                }
            };
            timer.Start();
        }

        private async void OnBalloonClick(object sender, EventArgs eventArgs)
        {
            if (workspace != null) await OpenAsync();
        }

        private async Task OpenAsync()
        {
            if (disposed || !canShow()) return;
            if (form != null)
            {
                form.Activate();
                return;
            }

            form = new UpdateForm(UpdateRelease.CurrentVersion, tray.Icon);
            form.InstallRequested += Install;
            form.PostponeRequested += delegate
            {
                preferences.PostponedUntil = DateTimeOffset.UtcNow.AddDays(1);
                SavePreferences();
            };
            form.FormClosed += delegate { form = null; };
            RefreshForm();
            form.Show();
            if (!busy) await CheckAsync(true);
        }

        private async Task CheckAsync(bool manual)
        {
            if (disposed || busy) return;
            busy = true;
            status = "Checking GitHub Releases...";
            RefreshForm();
            try
            {
                UpdateRelease available = await client.CheckAsync(lifetime.Token);
                if (disposed) return;
                if (available == null)
                {
                    DeleteDownload();
                    release = null;
                    checkItem.Text = "Check for updates...";
                    status = "You have the latest stable version available to this app.";
                    return;
                }

                if (workspace == null || release == null || release.Version != available.Version)
                {
                    DeleteDownload();
                    release = available;
                    status = "Downloading and checking version " + release.Version + "...";
                    RefreshForm();
                    workspace = await client.DownloadAsync(release, lifetime.Token);
                }
                if (disposed)
                {
                    DeleteDownload();
                    return;
                }
                status = "Version " + release.Version + " is ready. Update and restart when convenient."
                    + "\n\nSettings, pins, history, and Start with Windows stay unchanged."
                    + "\nThe package is checked with SHA-256. The executable is not code-signed.";
                checkItem.Text = "Update to " + release.Version + "...";
                if (form == null && canShow() && DateTimeOffset.UtcNow >= preferences.PostponedUntil)
                {
                    tray.ShowBalloonTip(5000, "Update available", "Version " + release.Version
                        + " is ready. Use the tray menu to update or postpone.", ToolTipIcon.Info);
                }
            }
            catch (OperationCanceledException) when (disposed) { }
            catch (Exception exception)
            {
                status = "Could not prepare an update. " + exception.Message
                    + "\n\nKeep using this version. Close this window and choose Check for updates to retry.";
                DiagnosticLog.Write("Update check: " + exception.Message);
            }
            finally
            {
                busy = false;
                if (!disposed) RefreshForm();
            }
        }

        private async void Install()
        {
            if (disposed || busy || workspace == null || !canShow()) return;
            busy = true;
            installing = true;
            status = "Preparing to restart...";
            RefreshForm();
            try
            {
                await Task.Run(() => UpdateInstaller.Start(workspace, release));
                if (!disposed) exit();
            }
            catch (Exception exception)
            {
                installing = false;
                status = "The update could not start. " + exception.Message + "\nThe current app is still open.";
                DiagnosticLog.Write("Update install: " + exception.Message);
                busy = false;
                if (!disposed) RefreshForm();
            }
        }

        private void RefreshForm()
        {
            form?.SetStatus(status, workspace != null && !busy);
        }

        private UpdatePreferences LoadPreferences()
        {
            try
            {
                return File.Exists(settingsPath)
                    ? JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(settingsPath)) ?? new UpdatePreferences()
                    : new UpdatePreferences();
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is JsonException)
            {
                DiagnosticLog.Write("Update preferences: " + exception.Message);
                return new UpdatePreferences();
            }
        }

        private void SavePreferences()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllText(settingsPath + ".tmp", JsonSerializer.Serialize(preferences));
                File.Move(settingsPath + ".tmp", settingsPath, true);
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                DiagnosticLog.Write("Update preferences: " + exception.Message);
                tray.ShowBalloonTip(4000, "Update setting not saved", "The change applies only until the app exits.", ToolTipIcon.Warning);
            }
        }

        private void DeleteDownload()
        {
            if (workspace == null || installing) return;
            try { Directory.Delete(workspace, true); }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                DiagnosticLog.Write("Update cleanup: " + exception.Message);
            }
            workspace = null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            timer.Stop();
            timer.Dispose();
            lifetime.Cancel();
            tray.BalloonTipClicked -= OnBalloonClick;
            form?.Dispose();
            client.Dispose();
            lifetime.Dispose();
            DeleteDownload();
        }
    }

    internal sealed class UpdateForm : Form
    {
        private readonly Label status;
        private readonly Button install;
        public event Action InstallRequested;
        public event Action PostponeRequested;

        public UpdateForm(string version, Icon icon)
        {
            Text = "Vertical App Switcher updates";
            AccessibleName = Text;
            Icon = icon;
            Font = SystemFonts.MessageBoxFont;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(500, 270);
            MinimumSize = new Size(480, 290);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 4, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "Installed version: " + version, AutoSize = true, Margin = new Padding(0, 0, 0, 12) });
            status = new Label { Dock = DockStyle.Fill, AutoSize = true, AccessibleName = "Update status" };
            layout.Controls.Add(status);
            var releaseLink = new LinkLabel { Text = "Open GitHub Releases", AutoSize = true, Margin = new Padding(0, 8, 0, 12) };
            releaseLink.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo(UpdateRelease.RepositoryUrl + "/releases") { UseShellExecute = true }); }
                catch (Exception exception) { MessageBox.Show(this, exception.Message, Text); }
            };
            layout.Controls.Add(releaseLink);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var later = new Button { Text = "Later", AutoSize = true, MinimumSize = new Size(90, 32), DialogResult = DialogResult.Cancel };
            later.Click += delegate { PostponeRequested?.Invoke(); Close(); };
            install = new Button { Text = "Update and restart", AutoSize = true, MinimumSize = new Size(155, 32), Enabled = false };
            install.Click += delegate { InstallRequested?.Invoke(); };
            buttons.Controls.Add(later);
            buttons.Controls.Add(install);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            CancelButton = later;
        }

        public void SetStatus(string text, bool canInstall)
        {
            status.Text = text;
            status.AccessibleName = text;
            install.Enabled = canInstall;
        }
    }
}
