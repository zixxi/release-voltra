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
    private Action? refreshUpdateDialogTheme;
    private bool handingOffUpdate;
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

        var updatePalette = palette;
        var dialog = new Form
        {
            Text = "Update available", Icon = Icon, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new(552, 424),
            MinimumSize = new(480, 368), MinimizeBox = false, MaximizeBox = false,
            AutoScaleDimensions = new(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = updatePalette.Background, ForeColor = updatePalette.Text, Font = Font
        };
        updateDialog = dialog;
        dialog.HandleCreated += (_, _) => SetTitleBar(dialog.Handle, updatePalette.Dark);
        var grid = Grid(1, 7);
        grid.Padding = new(24, 18, 24, 16);
        grid.RowStyles.Add(new(SizeType.Absolute, 40));
        grid.RowStyles.Add(new(SizeType.Absolute, 30));
        grid.RowStyles.Add(new(SizeType.Absolute, 29));
        grid.RowStyles.Add(new(SizeType.Percent, 100));
        grid.RowStyles.Add(new(SizeType.Absolute, 49));
        grid.RowStyles.Add(new(SizeType.Absolute, 9));
        grid.RowStyles.Add(new(SizeType.Absolute, 38));

        var heading = Caption($"Voltra {release.Version} is available", 18, bold: true);
        var versions = Caption($"Current version: {BuildInfo.Version}     New version: {release.Version}", UiMetrics.Body, muted: true);
        var notesHeading = Caption("What's new", 11, bold: true);
        var notes = new ReleaseNotesView(updatePalette, release.Notes) { Dock = DockStyle.Fill };
        grid.Controls.Add(heading, 0, 0);
        grid.Controls.Add(versions, 0, 1);
        grid.Controls.Add(notesHeading, 0, 2);
        grid.Controls.Add(notes, 0, 3);

        bool canDownload = updates.CanDownload(release);
        bool canInstall = canDownload && WindowsStartup.Read().Installed;
        var status = Caption(!canDownload ? "A signed package for this build is unavailable."
            : canInstall ? "Voltra will restart to complete the update."
            : "Open the installed copy of Voltra to update automatically.", UiMetrics.Body, muted: true);
        status.AutoEllipsis = false;
        status.Margin = new(0, 6, 0, 0);
        int percent = 0;
        bool statusIsError = false;
        var progress = new Panel { Dock = DockStyle.Fill, Margin = new(0), Visible = false,
            AccessibleName = "Download progress", AccessibleRole = AccessibleRole.ProgressBar };
        progress.Paint += (_, e) =>
        {
            var track = new Rectangle(0, 0, progress.Width, Math.Max(1, progress.LogicalToDeviceUnits(4)));
            using var background = new SolidBrush(updatePalette.Line);
            using var fill = new SolidBrush(updatePalette.Accent);
            e.Graphics.FillRectangle(background, track);
            e.Graphics.FillRectangle(fill, 0, 0, track.Width * percent / 100, track.Height);
        };
        grid.Controls.Add(status, 0, 4);
        grid.Controls.Add(progress, 0, 5);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Margin = new(0), WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft };
        CancellationTokenSource? downloadToken = null;
        var later = ActionButton("Later", () =>
        {
            if (downloadToken is null) dialog.Close();
            else if (!handingOffUpdate) downloadToken.Cancel();
        }, 128);
        var install = ActionButton("Update now", () => { }, 126);
        install.Click += (_, _) => _ = InstallAsync();
        install.Primary = true;
        install.Margin = new(0, 2, 0, 2);
        install.Enabled = canInstall;
        later.Margin = new(0, 2, 8, 2);
        // Tab order reads left to right; the flow layout anchors the primary action at the right.
        install.TabIndex = 1;
        later.TabIndex = 0;
        buttons.Controls.AddRange([install, later]);
        grid.Controls.Add(buttons, 0, 6);
        dialog.Controls.Add(grid);
        dialog.AcceptButton = canInstall ? install : later;
        dialog.CancelButton = later;
        dialog.FormClosing += (_, args) =>
        {
            if (downloadToken is null || PendingInstaller is not null) return;
            if (args.CloseReason == CloseReason.UserClosing && !exiting)
            {
                args.Cancel = true;
                if (!handingOffUpdate) downloadToken.Cancel();
            }
            else if (!handingOffUpdate || exiting)
                downloadToken.Cancel();
        };
        dialog.FormClosed += (_, _) =>
        {
            updateDialog = null;
            refreshUpdateDialogTheme = null;
        };
        refreshUpdateDialogTheme = () =>
        {
            if (dialog.IsDisposed || updatePalette == palette) return;
            updatePalette = palette;
            dialog.BackColor = updatePalette.Background;
            dialog.ForeColor = heading.ForeColor = notesHeading.ForeColor = updatePalette.Text;
            versions.ForeColor = updatePalette.Muted;
            status.ForeColor = statusIsError ? updatePalette.Error : updatePalette.Muted;
            notes.ApplyTheme(updatePalette);
            install.ApplyTheme(updatePalette);
            later.ApplyTheme(updatePalette);
            progress.Invalidate();
            if (dialog.IsHandleCreated) SetTitleBar(dialog.Handle, updatePalette.Dark);
        };
        dialog.Show(this);

        void SetStatus(string message, bool error = false)
        {
            if (statusIsError || error) tips.SetToolTip(status, error ? message : "");
            statusIsError = error;
            status.ForeColor = error ? updatePalette.Error : updatePalette.Muted;
            status.Text = message;
        }

        async Task InstallAsync()
        {
            if (!canInstall || downloadToken is not null || PendingInstaller is not null || exiting) return;
            if (firmwareFlashing)
            {
                SetStatus("Wait for the ESP32 firmware operation to finish before updating Voltra.", error: true);
                return;
            }

            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            downloadToken = cancellation;
            install.Enabled = false;
            later.Text = "Cancel download";
            percent = 0;
            progress.Visible = true;
            progress.Invalidate();
            SetStatus("Verifying release information…");
            string? stage = null;
            InnoUpdateInstaller? prepared = null;
            try
            {
                stage = InnoUpdateInstaller.CreateStage();
                string destination = Path.Combine(stage, release.Package!.Name);
                var verified = await updates.DownloadAsync(release, destination, new Progress<int>(value =>
                {
                    if (dialog.IsDisposed || downloadToken != cancellation || handingOffUpdate) return;
                    percent = value;
                    progress.Invalidate();
                    SetStatus(value == 100 ? "Verifying downloaded file…" : $"Downloading… {value}%");
                }), cancellation.Token);
                if (dialog.IsDisposed || exiting) return;

                cancellation.Token.ThrowIfCancellationRequested();
                handingOffUpdate = true;
                later.Enabled = false;
                SetStatus("Checking the staged installer…");
                prepared = await InnoUpdateInstaller.PrepareAsync(verified, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                if (dialog.IsDisposed || exiting) return;
                if (firmwareFlashing)
                {
                    SetStatus("Wait for the ESP32 firmware operation to finish before updating Voltra.", error: true);
                    return;
                }
                PendingInstaller = prepared;
                prepared = null;
                stage = null;
                ExitFromTray();
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                if (!dialog.IsDisposed) SetStatus("Download canceled.");
            }
            catch (Exception ex) when (IsUpdateError(ex))
            {
                if (!dialog.IsDisposed) SetStatus("Unable to prepare the update. " + UpdateErrorMessage(ex), error: true);
            }
            finally
            {
                handingOffUpdate = false;
                downloadToken = null;
                prepared?.Dispose();
                if (stage is not null) InnoUpdateInstaller.DeleteStage(stage);
                if (!dialog.IsDisposed)
                {
                    progress.Visible = false;
                    later.Text = "Later";
                    later.Enabled = true;
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
