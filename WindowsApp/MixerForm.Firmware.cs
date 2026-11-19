using System.ComponentModel;

namespace Voltra;

internal sealed partial class MixerForm
{
    private Label? firmwareStatus;
    private MixerButton? firmwareCheck, firmwareInstall;
    private FirmwareRelease? firmwareRelease;
    private DeviceFirmware? firmwareDevice;
    private string firmwareMessage = "";
    private bool checkingFirmware, firmwareFlashing, firmwareConnected;
    private DateTimeOffset nextFirmwareCheck;
    private (string Mac, Version Version)? firmwareNotice;

    private Control BuildFirmwareSection()
    {
        firmwareStatus = Caption("", 10, muted: true, height: 66);
        firmwareStatus.AutoEllipsis = false;
        firmwareStatus.AccessibleName = "Device firmware information";
        firmwareCheck = ActionButton("Check for updates", () => _ = CheckDeviceFirmwareAsync(manual: true), 152);
        firmwareInstall = ActionButton("Update firmware", () => _ = ConfirmFirmwareUpdateAsync(), 152);
        var section = Stack(Divider(), Caption("Device firmware", UiMetrics.Section, bold: true, height: 37),
            firmwareStatus, Inline(firmwareCheck, firmwareInstall));
        section.Dock = DockStyle.None;
        section.Height = 166;
        RefreshFirmwareControls();
        return section;
    }

    private void ObserveFirmware(MixerState state)
    {
        if (firmwareFlashing) return;
        bool connected = state.ConnectedPort is not null;
        var device = connected ? state.Firmware : null;
        if (firmwareDevice != device || firmwareConnected != connected)
        {
            firmwareConnected = connected;
            firmwareDevice = device;
            firmwareMessage = "";
            nextFirmwareCheck = DateTimeOffset.MinValue;
            RefreshFirmwareControls();
        }
        if (!checkingFirmware && device is not null && FirmwareRelease.Supports(device) &&
            updates.FirmwareConfigured && DateTimeOffset.UtcNow >= nextFirmwareCheck)
            _ = CheckDeviceFirmwareAsync(manual: false);
    }

    private async Task CheckDeviceFirmwareAsync(bool manual)
    {
        if (checkingFirmware || firmwareFlashing || exiting || !updates.FirmwareConfigured ||
            firmwareDevice is not { } device || !FirmwareRelease.Supports(device)) return;
        nextFirmwareCheck = DateTimeOffset.UtcNow.AddMinutes(20);
        checkingFirmware = true;
        SetFirmwareMessage("Checking firmware releases…");
        try
        {
            firmwareRelease = await updates.CheckFirmwareAsync(manual, lifetime.Token);
            if (exiting) return;
            if (firmwareDevice == device)
            {
                nextFirmwareCheck = DateTimeOffset.UtcNow.AddHours(6);
                firmwareMessage = "";
                if (firmwareRelease.CanInstall(device) && firmwareNotice != (device.Mac, firmwareRelease.Version))
                {
                    firmwareNotice = (device.Mac, firmwareRelease.Version);
                    tray.ShowBalloonTip(3500, "Voltra firmware update", $"Firmware {firmwareRelease.Version} is available. Open Settings to review it.",
                        ToolTipIcon.Info);
                }
            }
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) when (IsUpdateError(ex))
        {
            if (!exiting && firmwareDevice == device)
            {
                firmwareRelease = null;
                firmwareMessage = "Unable to check firmware. " + UpdateErrorMessage(ex);
            }
        }
        finally
        {
            checkingFirmware = false;
            if (!exiting) RefreshFirmwareControls();
        }
    }

    private void SetFirmwareMessage(string message)
    {
        firmwareMessage = message;
        RefreshFirmwareControls();
    }

    private void RefreshFirmwareControls()
    {
        bool connected = link.State.ConnectedPort is not null;
        var device = firmwareDevice;
        var release = firmwareRelease;
        bool supported = device is not null && FirmwareRelease.Supports(device);
        bool newer = supported && release is not null && release.Version > device!.Version;
        string status = firmwareMessage;
        if (status.Length == 0)
        {
            status = !connected ? "Connect your Voltra controller."
                : device is null ? "Firmware version unknown. Install the current Voltra sketch once via USB."
                : !supported ? "This hardware, protocol or running partition cannot be updated in Voltra."
                : !updates.FirmwareConfigured ? "The firmware signing key is not configured for this build."
                : release is null ? "Firmware updates have not been checked."
                : !newer ? "Your firmware is up to date."
                : release.MinimumAppVersion > BuildInfo.Version ? $"Update Voltra to version {release.MinimumAppVersion} or later first."
                : !FirmwareFlasher.IsPackaged ? "Update available. This build does not include the flashing tool."
                : "Firmware update available.";
        }
        if (firmwareStatus is not null)
        {
            firmwareStatus.Text = $"Installed · {device?.Version.ToString() ?? "—"}    Latest · {release?.Version.ToString() ?? "—"}{Environment.NewLine}{status}";
            tips.SetToolTip(firmwareStatus, status);
        }
        if (firmwareCheck is not null)
            firmwareCheck.Enabled = connected && supported && updates.FirmwareConfigured && !checkingFirmware && !firmwareFlashing;
        if (firmwareInstall is not null)
            firmwareInstall.Enabled = connected && device is not null && release?.CanInstall(device) == true &&
                FirmwareFlasher.IsPackaged && !checkingFirmware && !firmwareFlashing;
    }

    private async Task ConfirmFirmwareUpdateAsync()
    {
        if (firmwareFlashing || checkingFirmware || firmwareDevice is not { } device ||
            firmwareRelease is not { } release || !release.CanInstall(device) || !FirmwareFlasher.IsPackaged) return;
        if (!ConfirmFirmware(device, release)) return;

        firmwareFlashing = true;
        string directory = Path.Combine(Path.GetTempPath(), "VoltraFirmware", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, release.Package.Name);
        using var downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            await FirmwareFlasher.VerifyToolAsync(downloadCancellation.Token);
            Directory.CreateDirectory(directory);
            SetFirmwareMessage("Downloading signed firmware…");
            await updates.DownloadFirmwareAsync(release, device, path,
                new Progress<int>(percent =>
                {
                    if (firmwareFlashing && !exiting)
                        SetFirmwareMessage(percent == 100 ? "Verifying firmware…" : $"Downloading firmware · {percent}%");
                }), downloadCancellation.Token);

            SetFirmwareMessage("Preparing the controller…");
            using var session = await link.HoldForFirmwareAsync(device, downloadCancellation.Token);
            if (MessageBox.Show(this, "Hold BOOT on the ESP32, tap RESET, then release BOOT. Keep the USB cable connected. Click OK when ready.",
                "Enter USB download mode", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
            {
                SetFirmwareMessage("Firmware update canceled. If you pressed BOOT, tap RESET once.");
                return;
            }

            SetFirmwareMessage("Entering USB download mode…");
            await FirmwareFlasher.RunAsync(device, release, path,
                new Progress<string>(message => { if (firmwareFlashing && !exiting) SetFirmwareMessage(message); }));
            session.Resume();
            SetFirmwareMessage("Reconnecting and checking the version. If needed, tap RESET once.");
            string connectedPort = await ConfirmRebootAsync(device, release.Version, lifetime.Token);
            if (config.PreferredPort.Length > 0 && !config.PreferredPort.Equals(connectedPort, StringComparison.OrdinalIgnoreCase))
                Persist(config with { PreferredPort = connectedPort });
            firmwareDevice = link.State.Firmware;
            SetFirmwareMessage($"Firmware update completed · {release.Version}");
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) when (IsUpdateError(ex) || ex is TimeoutException or Win32Exception)
        {
            if (!exiting) SetFirmwareMessage("Firmware update stopped. " + UpdateErrorMessage(ex));
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            firmwareFlashing = false;
            if (!exiting) RefreshFirmwareControls();
        }
    }

    private async Task<string> ConfirmRebootAsync(DeviceFirmware device, Version expected, CancellationToken token)
    {
        long deadline = Environment.TickCount64 + 40_000;
        while (Environment.TickCount64 < deadline)
        {
            var state = link.State;
            if (state.ConnectedPort is { } port && state.Firmware is { } current && current.Mac == device.Mac)
            {
                if (!FirmwareRelease.Supports(current) || current.Version != expected)
                    throw new InvalidDataException($"The controller returned firmware {current.Version}. Expected {expected}.");
                return port;
            }
            await Task.Delay(200, token);
        }
        throw new IOException("The new version could not be confirmed. Tap RESET without BOOT, then see USB recovery in RELEASING.md.");
    }

    private bool ConfirmFirmware(DeviceFirmware device, FirmwareRelease release)
    {
        using var dialog = new Form
        {
            Text = "Firmware update available", Icon = Icon, ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent, ClientSize = new(530, 320),
            MinimumSize = new(465, 290), AutoScaleMode = AutoScaleMode.Dpi,
            BackColor = palette.Background, ForeColor = palette.Text, Font = Font
        };
        dialog.HandleCreated += (_, _) => SetTitleBar(dialog.Handle, palette.Dark);
        var grid = Grid(1, 5);
        grid.Padding = new(22, 15, 22, 15);
        grid.RowStyles.Add(new(SizeType.Absolute, 35));
        grid.RowStyles.Add(new(SizeType.Absolute, 30));
        grid.RowStyles.Add(new(SizeType.Absolute, 25));
        grid.RowStyles.Add(new(SizeType.Percent, 100));
        grid.RowStyles.Add(new(SizeType.Absolute, 43));
        grid.Controls.Add(Caption($"Voltra firmware {release.Version}", UiMetrics.Section, bold: true), 0, 0);
        grid.Controls.Add(Caption($"Installed · {device.Version}    New version · {release.Version}", 10, muted: true), 0, 1);
        grid.Controls.Add(Caption("What's new", 10, bold: true), 0, 2);
        grid.Controls.Add(new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = palette.Field, ForeColor = palette.Text,
            Text = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes were provided." : release.Notes,
            AccessibleName = "Firmware release notes"
        }, 0, 3);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var update = ActionButton("Update firmware", () => { }, 145, true);
        update.DialogResult = DialogResult.OK;
        var later = ActionButton("Later", () => { }, 90);
        later.DialogResult = DialogResult.Cancel;
        actions.Controls.AddRange([update, later]);
        grid.Controls.Add(actions, 0, 4);
        dialog.Controls.Add(grid);
        dialog.CancelButton = later;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }
}
