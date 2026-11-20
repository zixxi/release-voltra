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

        firmwareFlashing = true;
        RefreshFirmwareControls();
        try
        {
            using var dialog = new FirmwareDialog(this);
            dialog.ShowProgress(release.Version, "Preparing the firmware download…");
            Task? workflow = null;
            dialog.Shown += (_, _) => workflow = RunFirmwareWorkflowAsync(dialog, device, release);
            dialog.ShowDialog(this);
            if (workflow is not null) await workflow;
        }
        finally
        {
            firmwareFlashing = false;
            if (!exiting) RefreshFirmwareControls();
        }
    }

    private async Task RunFirmwareWorkflowAsync(FirmwareDialog dialog, DeviceFirmware device, FirmwareRelease release)
    {
        var token = lifetime.Token;
        bool retry = false;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var diagnostics = new System.Text.StringBuilder();
                void Log(string message)
                {
                    MixerDiagnostics.Log("Firmware: " + message);
                    if (diagnostics.Length < 12_000)
                        diagnostics.AppendLine(message[..Math.Min(message.Length, 12_000 - diagnostics.Length)]);
                }

                try
                {
                    Log($"Controller firmware {device.Version} → {release.Version}");
                    if (retry)
                    {
                        dialog.ShowProgress(release.Version, "Waiting for the controller to return…");
                        await WaitForFirmwareAsync(device, device.Version, TimeSpan.FromSeconds(8), dialog, token);
                    }
                    if (!await InstallFirmwareAsync(dialog, device, release, Log, token)) return;
                    await dialog.ShowSuccessAsync(release.Version, token);
                    return;
                }
                catch (Exception ex) when (!token.IsCancellationRequested &&
                    (IsUpdateError(ex) || ex is TimeoutException or Win32Exception))
                {
                    Log(ex.GetBaseException().Message);
                    string reason = ex is TimeoutException ? "Voltra timed out while releasing the serial connection."
                        : ex is Win32Exception ? "Windows could not start or stop the bundled flashing tool."
                        : UpdateErrorMessage(ex);
                    SetFirmwareMessage("Firmware update failed. " + reason);
                    retry = await dialog.ShowFailureAsync(reason, diagnostics.ToString(), token);
                    if (!retry) return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (!dialog.IsDisposed) dialog.Finish(); }
    }

    private async Task<bool> InstallFirmwareAsync(FirmwareDialog dialog, DeviceFirmware device, FirmwareRelease release,
        Action<string> log, CancellationToken token)
    {
        string directory = Path.Combine(Path.GetTempPath(), "VoltraFirmware", Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, release.Package.Name);
        try
        {
            using var download = CancellationTokenSource.CreateLinkedTokenSource(token);
            dialog.ShowProgress(release.Version, "Downloading firmware…", download.Cancel);
            try
            {
                await FirmwareFlasher.VerifyToolAsync(download.Token);
                Directory.CreateDirectory(directory);
                var progress = new Progress<int>(percent =>
                {
                    if (percent < 100 && !download.IsCancellationRequested)
                        dialog.ReportProgress("Downloading firmware…", percent);
                });
                IProgress<string> verification = new Progress<string>(stage => dialog.ReportProgress(stage));
                await updates.DownloadFirmwareAsync(release, device, path, progress,
                    () => verification.Report("Verifying firmware…"), download.Token);
                download.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (download.IsCancellationRequested && !token.IsCancellationRequested)
            {
                SetFirmwareMessage("Firmware download canceled.");
                return false;
            }

            if (!await dialog.ConfirmUpdateAsync(device, release, token))
            {
                SetFirmwareMessage("");
                return false;
            }

            dialog.ShowProgress(release.Version, "Preparing the controller…");
            using var session = await link.HoldForFirmwareAsync(device, token);
            if (!await dialog.ShowBootInstructionsAsync(token))
            {
                SetFirmwareMessage("Firmware update canceled. If you entered download mode, tap RESET with BOOT released.");
                return false;
            }

            dialog.ShowProgress(release.Version, "Waiting for the ESP32 bootloader…");
            var flashProgress = new Progress<(string Stage, int? Percent)>(update =>
            {
                string? text = update.Stage switch
                {
                    "waiting" => "Waiting for the ESP32 bootloader…",
                    "connecting" => "Connecting to the bootloader…",
                    "checking-layout" => "Checking the controller and flash layout…",
                    "writing" => "Writing firmware… Keep USB connected.",
                    "verifying-flash" => "Verifying flash…",
                    "restarting" => "Restarting the ESP32…",
                    "reset-needed" => "Flash verified. Tap RESET with BOOT released.",
                    _ => null
                };
                if (text is not null && update.Percent is not 100)
                    dialog.ReportProgress(text, update.Percent);
            });
            bool manualReset = await FirmwareFlasher.RunAsync(device, release, path, session.PreviousPort, flashProgress, log, token);
            session.Resume();
            dialog.ReportProgress(manualReset
                ? "Tap RESET with BOOT released to reconnect."
                : "Reconnecting to the controller… If needed, tap RESET.");
            var (port, installed) = await WaitForFirmwareAsync(device, release.Version, TimeSpan.FromSeconds(40), dialog, token);
            if (config.PreferredPort.Length > 0 && !config.PreferredPort.Equals(port, StringComparison.OrdinalIgnoreCase))
                Persist(config with { PreferredPort = port });
            firmwareConnected = true;
            firmwareDevice = installed;
            SetFirmwareMessage($"Firmware update completed · {installed.Version}");
            log($"Confirmed {installed.Version} from the selected controller on {port}.");
            return true;
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private async Task<(string Port, DeviceFirmware Firmware)> WaitForFirmwareAsync(DeviceFirmware device, Version expected,
        TimeSpan timeout, FirmwareDialog dialog, CancellationToken token)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            var state = link.State;
            if (state.ConnectedPort is { } port && state.Firmware is { } current && current.Mac == device.Mac)
            {
                dialog.ReportProgress("Confirming the installed firmware version…");
                if (!FirmwareRelease.Supports(current) || current.Version != expected)
                    throw new InvalidDataException($"The controller reported firmware {current.Version}. Expected {expected} on a supported layout.");
                return (port, current);
            }
            await Task.Delay(150, token);
        }
        throw new IOException("Voltra could not confirm the firmware version. Tap RESET with BOOT released and check the USB connection.");
    }
}
