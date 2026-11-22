using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Input;
using Voltra;

namespace Voltra.Linux.Views;

public sealed partial class SettingsView : UserControl
{
    private MainViewModel? model;
    private string[] ports = [];
    private string? lastAutoPort, lastManualPort;
    private bool refreshingPorts, writingStartup, gesture;
    private readonly CancellationTokenSource checks = new();
    private UpdateRelease? available;
    private PreparedUpdate? prepared;
    private CancellationTokenSource? desktopDownload;
    private FirmwareRelease? firmware;
    private bool firmwareChecking;
    private DateTimeOffset nextFirmwareCheck;
    private string firmwareMessage = "";

    public SettingsView() => InitializeComponent();

    internal void Setup(MainViewModel model)
    {
        this.model = model;
        FirmwareCheck.Click += async (_, _) => await CheckFirmwareAsync();
        FirmwareInstall.Click += async (_, _) =>
        {
            var snapshot = model.Device.Snapshot;
            if (model.DesktopUpdating || model.FirmwareDialogOpen || firmware is null || snapshot.Identity is not { } device ||
                snapshot.UsbSocket is null || !firmware.CanInstall(device) || TopLevel.GetTopLevel(this) is not Window parent) return;
            model.FirmwareDialogOpen = true;
            try { await new FirmwareWindow(model, snapshot, firmware).ShowDialog(parent); }
            finally { model.FirmwareDialogOpen = false; RefreshFirmware(model.Device.Snapshot); }
        };
        void Rules(Button button, string title, string description, Func<MappingRules, string[]> get,
            Func<MappingRules, string[], MappingRules> update)
        {
            button.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(this) is not Window parent) return;
                var editor = new RuleWindow(title, description, get(model.Settings.Rules), model.Audio.Snapshot.Applications);
                if (await editor.ShowDialog<string[]?>(parent) is { } entries)
                    model.Change(model.Settings with { Rules = update(model.Settings.Rules, entries) });
            };
        }
        Rules(GameAllow, "Game whitelist", "Recognize these applications as games alongside automatically detected games.", r => r.GameAllow, (r, values) => r with { GameAllow = values });
        Rules(GameDeny, "Game blacklist", "Exclude these applications from Game, even if they are on the whitelist.", r => r.GameDeny, (r, values) => r with { GameDeny = values });
        Rules(FocusAllow, "Focused app whitelist", "An empty whitelist allows every identifiable focused application with audio.", r => r.FocusAllow, (r, values) => r with { FocusAllow = values });
        Rules(FocusDeny, "Focused app blacklist", "Exclude these applications from Currently Focused, even if they are on the whitelist.", r => r.FocusDeny, (r, values) => r with { FocusDeny = values });
        var config = model.Settings;
        StartupToggle.IsChecked = config.Startup;
        ThemeDark.IsChecked = config.Theme == "Dark";
        ThemeLight.IsChecked = config.Theme == "Light";
        ThemeSystem.IsChecked = config.Theme == "System";
        AccentInput.Text = config.Accent;
        AccentSample.Background = Brush(config.Accent);
        FormatNames.IsChecked = config.FormatAppNames;
        FormatNames.IsCheckedChanged += (_, _) => model.Change(model.Settings with { FormatAppNames = FormatNames.IsChecked == true });
        SyncToggle.IsChecked = config.Sync;
        Immediate.IsChecked = config.Takeover == KnobTakeover.Immediate;
        SoftTakeover.IsChecked = config.Takeover == KnobTakeover.Soft;
        TakeoverModes.IsEnabled = ThresholdMenu.IsEnabled = config.Sync;
        ThresholdMenu.ItemsSource = new[] { "1%", "2%", "3%", "4%", "5%" };
        ThresholdMenu.SelectedIndex = config.NoiseThreshold - 1;
        VersionLine.Text = $"Desktop Audio Controller  ·  Version {LinuxUpdateService.DisplayVersion}  ·  Linux x64";
        string[] presetColors = [Settings.DefaultAccent, "#6596EF", "#26B6BC", "#64BA6C", "#E5AA51", "#E6809D"];
        string[] presetNames = ["Lilac", "Blue", "Teal", "Green", "Amber", "Rose"];
        var swatches = new Button[presetColors.Length];
        for (int i = 0; i < presetColors.Length; i++)
        {
            string hex = presetColors[i];
            var swatch = swatches[i] = new Button { Content = new Border
            {
                Background = Brush(hex), CornerRadius = new CornerRadius(4)
            },
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            swatch.Classes.Add("swatch");
            Avalonia.Automation.AutomationProperties.SetName(swatch, $"{presetNames[i]} accent");
            ToolTip.SetTip(swatch, presetNames[i]);
            swatch.Click += (_, _) => SaveAccent(hex);
            Presets.Children.Add(swatch);
        }
        MarkPreset(config.Accent);

        StartupToggle.IsCheckedChanged += async (_, _) =>
        {
            if (writingStartup) return;
            writingStartup = true;
            StartupToggle.IsEnabled = false;
            if (!await model.SetStartupAsync(StartupToggle.IsChecked == true))
                StartupToggle.IsChecked = model.Settings.Startup;
            StartupToggle.IsEnabled = true;
            writingStartup = false;
        };
        foreach (RadioButton button in new[] { ThemeDark, ThemeLight, ThemeSystem })
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked != true || button.Content is not string theme || model.Settings.Theme == theme) return;
                model.Change(model.Settings with { Theme = theme });
                ((App)Application.Current!).ApplyAppearance(model.Settings);
            };
        ApplyAccent.Click += (_, _) => SaveAccent(AccentInput.Text ?? "");
        AccentInput.TextChanged += (_, _) =>
        {
            AccentFeedback.IsVisible = false;
            if (LinuxSettingsService.TryAccent(AccentInput.Text, out var color))
                AccentSample.Background = new SolidColorBrush(color);
        };
        AccentInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            SaveAccent(AccentInput.Text ?? "");
        };
        OpenColor.Click += (_, _) =>
        {
            string hex = LinuxSettingsService.TryAccent(AccentInput.Text?.Trim(), out _) ? AccentInput.Text!.Trim() : model.Settings.Accent;
            PickerHex.Text = hex;
            UpdatePickerHex();
            ColorPopup.IsOpen = true;
        };
        ColorPopup.Opened += (_, _) => PickerHex.Focus();
        CancelPicker.Click += (_, _) => ColorPopup.IsOpen = false;
        PopupContent.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { ColorPopup.IsOpen = false; e.Handled = true; }
            if (e.Key == Key.Enter && ApplyPicker.IsEnabled) { SaveAccent(Picker.Hex); e.Handled = true; }
        };
        ApplyPicker.Click += (_, _) => SaveAccent(Picker.Hex);
        Picker.Changed += (_, _) =>
        {
            gesture = true;
            PickerHex.Text = Picker.Hex;
            gesture = false;
        };
        PickerHex.TextChanged += (_, _) => UpdatePickerHex();
        SyncToggle.IsCheckedChanged += (_, _) =>
        {
            bool enabled = SyncToggle.IsChecked == true;
            TakeoverModes.IsEnabled = ThresholdMenu.IsEnabled = enabled;
            model.Change(model.Settings with { Sync = enabled });
        };
        Immediate.IsCheckedChanged += (_, _) =>
        {
            if (Immediate.IsChecked == true) model.Change(model.Settings with { Takeover = KnobTakeover.Immediate });
        };
        SoftTakeover.IsCheckedChanged += (_, _) =>
        {
            if (SoftTakeover.IsChecked == true) model.Change(model.Settings with { Takeover = KnobTakeover.Soft });
        };
        ThresholdMenu.SelectionChanged += (_, _) =>
        {
            if (ThresholdMenu.SelectedIndex >= 0) model.Change(model.Settings with { NoiseThreshold = ThresholdMenu.SelectedIndex + 1 });
        };
        PortMenu.SelectionChanged += (_, _) =>
        {
            int index = PortMenu.SelectedIndex;
            if (refreshingPorts || index < 0 || index > ports.Length) return;
            var current = model.Settings;
            model.Change(index == 0 ? current with { AutoDetect = true } :
                current with { AutoDetect = false, Port = ports[index - 1] });
        };
        CopySerialDiagnostics.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            try { await clipboard.SetTextAsync(model.Device.Diagnostics); }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            { model.Report("Could not copy serial diagnostics."); }
        };
        CheckUpdates.Click += async (_, _) => await CheckAsync();
        OpenRelease.Click += async (_, _) => await UpdateApplicationAsync(model);
        CancelUpdate.Click += (_, _) =>
        {
            desktopDownload?.Cancel();
            if (prepared != null)
            {
                prepared.Dispose();
                prepared = null;
                UpdateStatus.Text = "Update canceled.";
                ResetUpdateControls(model);
            }
        };
        ManualRelease.Click += async (_, _) =>
        {
            var uri = new Uri($"https://github.com/{LinuxUpdateService.Repository}/releases");
            if (TopLevel.GetTopLevel(this)?.Launcher is not { } launcher || !await launcher.LaunchUriAsync(uri))
                UpdateStatus.Text = uri.AbsoluteUri;
        };

        ViewRepository.Click += async (_, _) =>
        {
            var uri = new Uri($"https://github.com/{LinuxUpdateService.Repository}");
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null || !await launcher.LaunchUriAsync(uri)) model.Report(uri.AbsoluteUri);
        };

        void MarkPreset(string hex)
        {
            for (int i = 0; i < swatches.Length; i++)
                swatches[i].Classes.Set("selected", presetColors[i].Equals(hex, StringComparison.OrdinalIgnoreCase));
        }

        void UpdatePickerHex()
        {
            string text = (PickerHex.Text ?? "").Trim();
            bool valid = LinuxSettingsService.TryAccent(text, out var color);
            ApplyPicker.IsEnabled = valid;
            PickerError.Text = valid ? "" : "Enter a color in #RRGGBB format.";
            if (!valid) return;
            PickerSample.Background = new SolidColorBrush(color);
            if (!gesture) Picker.SetHex(text);
        }

        void SaveAccent(string text)
        {
            text = text.Trim();
            if (!LinuxSettingsService.TryAccent(text, out _))
            {
                AccentFeedback.Text = "Enter six hexadecimal digits, such as #B78BFA.";
                AccentFeedback.IsVisible = true;
                return;
            }
            AccentFeedback.IsVisible = false;
            ColorPopup.IsOpen = false;
            string hex = text.ToUpperInvariant();
            AccentInput.Text = hex;
            MarkPreset(hex);
            if (model.Settings.Accent == hex) return;
            model.Change(model.Settings with { Accent = hex });
            ((App)Application.Current!).ApplyAppearance(model.Settings);
        }
    }

    private static IBrush Brush(string hex) => new SolidColorBrush(Color.Parse(hex));

    internal void Refresh(DeviceSnapshot device)
    {
        if (model is null) return;
        RefreshFirmware(device);
        DeviceStatus.Text = device.Status;
        if (device.Firmware is { } firmware) DeviceStatus.Text += $" · Firmware {firmware}";
        if (device.Port is { } connectedPath) DeviceStatus.Text += Environment.NewLine + connectedPath;
        string? requested = model.Settings.ManualPort;
        string[] current = string.IsNullOrEmpty(requested) || device.Ports.Contains(requested)
            ? device.Ports : [.. device.Ports, requested];
        string? automaticPort = requested is null ? device.Port : null;
        if (PortMenu.IsDropDownOpen ||
            (lastAutoPort == automaticPort && lastManualPort == requested &&
                ports.AsSpan().SequenceEqual(current) && PortMenu.ItemsSource is not null)) return;
        lastAutoPort = automaticPort;
        lastManualPort = requested;
        ports = current;
        refreshingPorts = true;
        PortMenu.ItemsSource = new[] { automaticPort is not null ? "Automatic · connected" : "Automatic" }.Concat(ports).ToArray();
        int manualIndex = Array.IndexOf(ports, requested);
        PortMenu.SelectedIndex = requested is null ? 0 : manualIndex < 0 ? -1 : manualIndex + 1;
        refreshingPorts = false;
    }

    private void RefreshFirmware(DeviceSnapshot snapshot)
    {
        if (model is null || model.FirmwareDialogOpen) return;
        model.Firmware.ObserveReconnection(snapshot);
        var device = snapshot.Identity;
        bool compatible = device is not null && FirmwareRelease.Supports(device) && snapshot.UsbSocket is not null;
        FirmwareVersions.Text = $"ESP32-S3-Zero   ·   Installed {device?.Version.ToString() ?? "—"}   ·   Available {firmware?.Version.ToString() ?? "—"}";
        FirmwareInstall.IsVisible = !firmwareChecking && compatible && firmware?.CanInstall(device!) == true;
        FirmwareCheck.IsEnabled = !firmwareChecking;
        string status = firmwareMessage.Length > 0 ? firmwareMessage :
            !compatible ? "Connect a supported controller using its native USB port." :
            firmware is null ? "Check for signed firmware." :
            firmware.MinimumAppVersion > LinuxUpdateService.Version ? $"Requires Voltra {firmware.MinimumAppVersion} or newer." :
            firmware.CanInstall(device!) ? "A verified update is available." : "Controller firmware is up to date.";
        FirmwareStatus.Text = model.Firmware.NeedsConfirmation
            ? "A previous write has no confirmed result. Tap RESET with BOOT released and check the installed version. " + status
            : status;
        if (!firmwareChecking && compatible && DateTimeOffset.UtcNow >= nextFirmwareCheck) _ = CheckFirmwareAsync();
    }

    private async Task CheckFirmwareAsync()
    {
        if (model is null || firmwareChecking || model.FirmwareDialogOpen) return;
        firmwareChecking = true;
        nextFirmwareCheck = DateTimeOffset.UtcNow.AddHours(6);
        firmware = null;
        firmwareMessage = "Checking the firmware release…";
        RefreshFirmware(model.Device.Snapshot);
        var verifying = new Progress<bool>(_ => { if (firmwareChecking) firmwareMessage = "Verifying the manifest signature…"; });
        try
        {
            firmware = await model.Firmware.CheckAsync(checks.Token, () => ((IProgress<bool>)verifying).Report(true));
            firmwareMessage = "";
        }
        catch (OperationCanceledException) when (checks.IsCancellationRequested) { }
        catch (Exception ex)
        {
            firmwareMessage = $"Firmware check failed. {ex.Message}";
            nextFirmwareCheck = DateTimeOffset.UtcNow.AddMinutes(10);
        }
        finally { firmwareChecking = false; RefreshFirmware(model.Device.Snapshot); }
    }

    private void ResetUpdateControls(MainViewModel model)
    {
        model.DesktopUpdating = false;
        CancelUpdate.IsVisible = false;
        CheckUpdates.IsEnabled = OpenRelease.IsEnabled = true;
        OpenRelease.Content = "Download verified update";
    }

    private async Task UpdateApplicationAsync(MainViewModel model)
    {
        if (available == null || model.FirmwareDialogOpen || model.Firmware.Installing) return;
        if (prepared != null)
        {
            try
            {
                prepared.Commit();
                prepared.Dispose();
                prepared = null;
                OpenRelease.IsEnabled = CancelUpdate.IsEnabled = false;
                UpdateStatus.Text = "Restarting to install the update…";
                model.RestartForUpdate!();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                prepared?.Dispose();
                prepared = null;
                ResetUpdateControls(model);
                UpdateStatus.Text = ex.Message;
            }
            return;
        }
        if (model.DesktopUpdating) return;
        model.DesktopUpdating = true;
        desktopDownload = CancellationTokenSource.CreateLinkedTokenSource(checks.Token);
        CheckUpdates.IsEnabled = OpenRelease.IsEnabled = false;
        CancelUpdate.IsVisible = true;
        UpdateStatus.Text = "Verifying the signed release manifest…";
        try
        {
            prepared = await LinuxUpdateService.PrepareAsync(available, new Progress<int>(p =>
                UpdateStatus.Text = $"Downloading update · {p}%"), () => UpdateStatus.Text = "Verified. Preparing installation…", desktopDownload.Token);
            UpdateStatus.Text = $"Voltra {available.Version} is ready. Restart to install.";
            OpenRelease.Content = "Restart and install";
            OpenRelease.IsEnabled = true;
        }
        catch (OperationCanceledException) { UpdateStatus.Text = "Update canceled or timed out."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or
            System.Security.Cryptography.CryptographicException or System.ComponentModel.Win32Exception or
            HttpRequestException or TimeoutException or FormatException or System.Text.Json.JsonException or KeyNotFoundException or InvalidDataException)
        {
            UpdateStatus.Text = $"Update failed. {ex.Message} You can extract the official release to a new user-owned directory.";
            ManualRelease.IsVisible = true;
        }
        finally
        {
            desktopDownload.Dispose();
            desktopDownload = null;
            if (prepared == null) ResetUpdateControls(model);
        }
    }

    private async Task CheckAsync()
    {
        CheckUpdates.IsEnabled = false;
        available = null;
        OpenRelease.IsVisible = ManualRelease.IsVisible = NotesViewer.IsVisible = false;
        UpdateStatus.Text = "Checking for desktop releases…";
        try
        {
            available = await LinuxUpdateService.CheckAsync(checks.Token);
            UpdateStatus.Text = available is null ? "No newer Linux package found." : $"Installed {LinuxUpdateService.DisplayVersion} · Available {available.Version}.";
            if (available != null)
            {
                bool complete = available.Package != null && available.Manifest != null && available.Signature != null;
                OpenRelease.IsVisible = complete && LinuxUpdateService.Portable;
                ManualRelease.IsVisible = !OpenRelease.IsVisible;
                if (!complete) UpdateStatus.Text += " This release has no complete signed Linux x64 package.";
                else if (!LinuxUpdateService.Portable) UpdateStatus.Text += " Extract the release to a dedicated user-owned folder to enable automatic updates.";
            }
            ReleaseNotes.Text = available?.Notes ?? "";
            NotesViewer.IsVisible = !string.IsNullOrWhiteSpace(available?.Notes);
        }
        catch (OperationCanceledException) when (checks.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or
            InvalidOperationException or OperationCanceledException or KeyNotFoundException or InvalidDataException or IOException)
        { UpdateStatus.Text = $"Could not check for updates: {ex.Message}"; }
        finally { CheckUpdates.IsEnabled = true; }
    }

    internal void CancelChecks() => checks.Cancel();
}
