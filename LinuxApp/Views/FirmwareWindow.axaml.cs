using Avalonia.Controls;
using Avalonia.Input;
using Voltra;

namespace Voltra.Linux.Views;

public sealed partial class FirmwareWindow : Window
{
    private CancellationTokenSource? attempt;
    private TaskCompletionSource<bool>? writeDecision;
    private bool critical;

    public FirmwareWindow() => InitializeComponent();

    internal FirmwareWindow(MainViewModel model, DeviceSnapshot target, FirmwareRelease release) : this()
    {
        var identity = target.Identity!;
        Controller.Text = $"Waveshare ESP32-S3-Zero · ID ending {identity.Mac[^6..]}";
        Versions.Text = $"Installed {identity.Version}   →   Available {release.Version}";
        Notes.Text = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes provided." : release.Notes;
        Stage.Text = "Keep USB connected. Prepare the update, then use BOOT and RESET when prompted. You will confirm before writing.";
        Dismiss.Click += (_, _) => Close();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closing += (_, e) =>
        {
            if (attempt is null) return;
            e.Cancel = true;
            if (critical) return;
            attempt.Cancel();
            writeDecision?.TrySetResult(false);
        };
        Continue.Click += async (_, _) =>
        {
            if (writeDecision is not null)
            {
                critical = true;
                Dismiss.IsEnabled = Continue.IsEnabled = false;
                Continue.IsVisible = false;
                writeDecision.TrySetResult(true);
                return;
            }
            if (attempt is not null) return;
            var current = model.Device.Snapshot;
            if (current.Identity != identity || current.UsbSocket != target.UsbSocket)
            {
                Stage.Text = "Reconnect the original controller and check its installed version in Settings.";
                return;
            }
            using var cts = new CancellationTokenSource();
            attempt = cts;
            Continue.IsVisible = Details.IsVisible = NotesHeading.IsVisible = NotesPanel.IsVisible = false;
            Heading.Text = "Updating controller firmware";
            bool canceled = false;
            try
            {
                await model.Firmware.InstallAsync(model, current, release, UpdateProgress, ConfirmWrite, cts.Token);
                Heading.Text = "Firmware updated successfully";
                Stage.Text = $"Confirmed version {release.Version} on the reconnected controller.";
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { canceled = true; }
            catch (Exception ex)
            {
                Heading.Text = "Firmware update failed";
                Stage.Text = "Keep USB connected. If the device is in download mode, tap RESET with BOOT released.";
                Details.Text = ex.Message;
                Details.IsVisible = true;
                Console.Error.WriteLine($"Firmware update: {ex.Message}");
                Continue.Content = "Retry";
                Continue.IsVisible = true;
            }
            finally
            {
                Progress.IsVisible = critical = false;
                attempt = null;
                Dismiss.Content = "Close";
                Dismiss.IsEnabled = Continue.IsEnabled = true;
                if (canceled) Close();
            }
        };

        async Task<bool> ConfirmWrite()
        {
            writeDecision = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Stage.Text = "The bootloader matches this controller. Select Write firmware to install the verified image. Keep USB connected until verification finishes.";
            Progress.IsVisible = false;
            Continue.Content = "Write firmware";
            Continue.IsVisible = Dismiss.IsEnabled = true;
            try { return await writeDecision.Task; }
            finally { writeDecision = null; }
        }

        void UpdateProgress(FirmwareProgress progress)
        {
            if (attempt is null || writeDecision is not null) return;
            Stage.Text = progress.Message;
            Progress.IsVisible = true;
            Progress.IsIndeterminate = !progress.Percent.HasValue;
            if (progress.Percent.HasValue) Progress.Value = progress.Percent.Value;
        }
    }
}
