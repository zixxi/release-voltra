using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Voltra;

internal sealed partial class MixerForm
{
    private readonly UpdateService updates = new();
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 12_000 };
    private Label? updateStatus;
    private MixerButton? updateCheck;
    private string updateMessage = "Updates have not been checked.";
    private bool checkingUpdates;
    private UpdateRelease? pendingUpdate;
    private Version? lastShownUpdate;
    private Form? updateDialog;
    internal InnoUpdateInstaller? PendingInstaller { get; private set; }

    private void StartUpdateChecks()
    {
        if (updates.RepositoryUrl is null) return;
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = (int)TimeSpan.FromHours(6).TotalMilliseconds;
            await CheckUpdatesAsync(manual: false);
        };
        updateTimer.Start();
    }

    private Control BuildAbout()
    {
        var repository = updates.RepositoryUrl;
        updateStatus = Caption("", 10, muted: true);
        updateCheck = ActionButton("Check for updates", () => _ = CheckUpdatesAsync(manual: true), 152);
        var actions = Inline(updateCheck);
        if (repository is not null)
            actions.Controls.Add(ActionButton("View on GitHub", () =>
            {
                try { Process.Start(new ProcessStartInfo(repository.AbsoluteUri) { UseShellExecute = true }); }
                catch (System.ComponentModel.Win32Exception ex) { SetUpdateMessage(ex.Message); }
            }, 135));

        var about = Stack(Divider(), Caption("About", UiMetrics.Section, bold: true, height: 37),
            Caption("Voltra", 13, bold: true),
            Caption($"Desktop Audio Controller  ·  Version {BuildInfo.Version}", 10, muted: true),
            updateStatus, actions);
        if (BuildInfo.Get("ProjectLicense") is { Length: > 0 } license)
            about.Controls.Add(Caption($"License · {license}", 10, muted: true));
        about.Dock = DockStyle.None;
        about.Height = 208;
        UpdateAboutStatus();
        return about;
    }

    private void SetUpdateMessage(string message)
    {
        updateMessage = message;
        UpdateAboutStatus();
    }

    private void UpdateAboutStatus()
    {
        bool configured = updates.RepositoryUrl is not null;
        if (updateStatus is not null)
            updateStatus.Text = configured ? updateMessage : "Update repository is not configured for this build.";
        if (updateCheck is not null) updateCheck.Enabled = configured && !checkingUpdates;
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdates || exiting || updateDialog is not null || updates.RepositoryUrl is null) return;
        checkingUpdates = true;
        SetUpdateMessage("Checking for updates…");
        try
        {
            var release = await updates.CheckAsync(lifetime.Token);
            if (exiting) return;
            pendingUpdate = release is not null && (manual || release.Version != lastShownUpdate) ? release : null;
            SetUpdateMessage(release is null ? "You're up to date." : $"Update available · {release.Version}");
            ShowPendingUpdate();
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) when (IsUpdateError(ex))
        {
            if (!exiting) SetUpdateMessage("Unable to check for updates. " + UpdateErrorMessage(ex));
        }
        finally
        {
            checkingUpdates = false;
            if (!exiting) UpdateAboutStatus();
        }
    }

    private void ShowPendingUpdate()
    {
        if (exiting || !Visible || !Enabled || updateDialog is not null || pendingUpdate is not { } release) return;
        if (OwnedForms.Any(form => form.Visible)) return;
        pendingUpdate = null;
        lastShownUpdate = release.Version;

        var dialog = new Form
        {
            Text = "Update available", Icon = Icon, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new(530, 405),
            MinimumSize = new(476, 396), AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = palette.Background, ForeColor = palette.Text, Font = Font
        };
        updateDialog = dialog;
        dialog.HandleCreated += (_, _) => SetTitleBar(dialog.Handle, palette.Dark);
        var grid = Grid(1, 7);
        grid.Padding = new(22, 15, 22, 15);
        grid.RowStyles.Add(new(SizeType.Absolute, 36));
        grid.RowStyles.Add(new(SizeType.Absolute, 30));
        grid.RowStyles.Add(new(SizeType.Absolute, 24));
        grid.RowStyles.Add(new(SizeType.Percent, 100));
        grid.RowStyles.Add(new(SizeType.Absolute, 49));
        grid.RowStyles.Add(new(SizeType.Absolute, 12));
        grid.RowStyles.Add(new(SizeType.Absolute, 40));

        grid.Controls.Add(Caption($"Voltra {release.Version} is available", UiMetrics.Section, bold: true), 0, 0);
        grid.Controls.Add(Caption($"Current version · {BuildInfo.Version}    New version · {release.Version}", 10, muted: true), 0, 1);
        grid.Controls.Add(Caption("What's new", 10, bold: true), 0, 2);
        var notes = new TextBox
        {
            Dock = DockStyle.Fill, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle, BackColor = palette.Field, ForeColor = palette.Text,
            Text = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes were provided." : release.Notes,
            AccessibleName = "Release notes"
        };
        grid.Controls.Add(notes, 0, 3);

        bool canDownload = updates.CanDownload(release);
        bool canInstall = canDownload && WindowsStartup.Read().Installed;
        var status = Caption(!canDownload ? "A signed package for this build is unavailable."
            : canInstall ? "Voltra will close before starting the installer."
            : "Open the installed copy of Voltra to update automatically.", 10, muted: true);
        status.AutoEllipsis = false;
        var progress = new ProgressBar { Dock = DockStyle.Fill, Visible = false, AccessibleName = "Download progress" };
        grid.Controls.Add(status, 0, 4);
        grid.Controls.Add(progress, 0, 5);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var install = ActionButton("Update now", () => { }, 110, canInstall);
        install.Enabled = canInstall;
        var download = ActionButton("Download update", () => { }, 141, canDownload);
        download.Enabled = canDownload;
        CancellationTokenSource? downloadToken = null;
        var later = ActionButton("Later", () =>
        {
            if (downloadToken is null) dialog.Close();
            else downloadToken.Cancel();
        }, 108);
        download.Click += (_, _) => _ = GetUpdateAsync(installAfterDownload: false);
        install.Click += (_, _) => _ = GetUpdateAsync(installAfterDownload: true);
        buttons.Controls.AddRange([install, download, later]);
        grid.Controls.Add(buttons, 0, 6);
        dialog.Controls.Add(grid);
        dialog.CancelButton = later;
        dialog.FormClosing += (_, args) =>
        {
            if (downloadToken is null) return;
            downloadToken.Cancel();
            args.Cancel = args.CloseReason == CloseReason.UserClosing && !exiting;
        };
        dialog.FormClosed += (_, _) => updateDialog = null;
        dialog.Show(this);

        async Task GetUpdateAsync(bool installAfterDownload)
        {
            if (downloadToken is not null || PendingInstaller is not null || exiting) return;
            if (installAfterDownload && firmwareFlashing)
            {
                status.Text = "Wait for the ESP32 firmware operation to finish before updating Voltra.";
                return;
            }

            string? savedPath = null;
            if (!installAfterDownload)
            {
                using var save = new SaveFileDialog
                {
                    Title = "Save verified Voltra update", FileName = release.Package!.Name,
                    Filter = "Windows installer (*.exe)|*.exe", DefaultExt = "exe", AddExtension = true
                };
                if (save.ShowDialog(dialog) != DialogResult.OK) return;
                savedPath = save.FileName;
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            downloadToken = cancellation;
            install.Enabled = download.Enabled = false;
            later.Text = "Cancel download";
            progress.Value = 0;
            progress.Visible = true;
            status.Text = "Verifying release information…";
            string? stage = null;
            InnoUpdateInstaller? prepared = null;
            try
            {
                if (installAfterDownload)
                    stage = InnoUpdateInstaller.CreateStage();
                string destination = savedPath ?? Path.Combine(stage!, release.Package!.Name);
                var verified = await updates.DownloadAsync(release, destination, new Progress<int>(value =>
                {
                    if (dialog.IsDisposed || downloadToken != cancellation) return;
                    progress.Value = value;
                    status.Text = value == 100 ? "Verifying downloaded file…" : $"Downloading… {value}%";
                }), cancellation.Token);
                if (dialog.IsDisposed || exiting) return;
                if (!installAfterDownload)
                {
                    status.Text = $"Verified package saved · {Path.GetFileName(verified.Path)}";
                    return;
                }

                status.Text = "Checking the staged installer…";
                prepared = await InnoUpdateInstaller.PrepareAsync(verified, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (dialog.IsDisposed || exiting) return;
                if (firmwareFlashing)
                {
                    status.Text = "Wait for the ESP32 firmware operation to finish before updating Voltra.";
                    return;
                }
                PendingInstaller = prepared;
                prepared = null;
                stage = null;
                ExitFromTray();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!dialog.IsDisposed) status.Text = "Download canceled.";
            }
            catch (Exception ex) when (IsUpdateError(ex))
            {
                if (!dialog.IsDisposed) status.Text = "Unable to prepare the update. " + UpdateErrorMessage(ex);
            }
            finally
            {
                downloadToken = null;
                prepared?.Dispose();
                if (stage is not null) InnoUpdateInstaller.DeleteStage(stage);
                if (!dialog.IsDisposed)
                {
                    progress.Visible = false;
                    later.Text = "Later";
                    download.Enabled = canDownload;
                    install.Enabled = canInstall;
                }
            }
        }
    }

    private static bool IsUpdateError(Exception ex) => ex is HttpRequestException or IOException or
        UnauthorizedAccessException or CryptographicException or OperationCanceledException or
        FormatException or JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException or
        System.Security.SecurityException;

    private static string UpdateErrorMessage(Exception ex) => ex switch
    {
        OperationCanceledException => "The request timed out.",
        HttpRequestException { StatusCode: null, HttpRequestError: not HttpRequestError.Unknown } =>
            "Check your internet connection and try again.",
        JsonException or KeyNotFoundException or FormatException => "The release information is invalid.",
        _ => ex.Message
    };
}
