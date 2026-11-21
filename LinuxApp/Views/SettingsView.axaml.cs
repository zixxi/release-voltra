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
    private string? lastAutoPort;
    private bool refreshingPorts, writingStartup, gesture;
    private readonly CancellationTokenSource checks = new();
    private LinuxRelease? available;

    public SettingsView() => InitializeComponent();

    internal void Setup(MainViewModel model)
    {
        this.model = model;
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
        VersionLine.Text = $"Desktop Audio Controller  ·  Version {LinuxUpdateService.Version}  ·  Linux x64";
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
            if (!refreshingPorts && index >= 0 && index <= ports.Length)
                model.Change(model.Settings with { Port = index == 0 ? "" : ports[index - 1] });
        };
        CheckUpdates.Click += async (_, _) => await CheckAsync();
        OpenRelease.Click += async (_, _) =>
        {
            if (available is null) return;
            try
            {
                var launcher = TopLevel.GetTopLevel(this)?.Launcher;
                if (launcher is null || !await launcher.LaunchUriAsync(available.Package))
                    UpdateStatus.Text = available.Package.AbsoluteUri;
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            { UpdateStatus.Text = $"Browser unavailable: {available.Package.AbsoluteUri}"; }
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
        DeviceStatus.Text = device.Status;
        if (device.Firmware is { } firmware) DeviceStatus.Text += $" · Firmware {firmware}";
        var requested = model.Settings.Port;
        string[] current = requested.Length == 0 || device.Ports.Contains(requested)
            ? device.Ports : [.. device.Ports, requested];
        string? automaticPort = requested.Length == 0 ? device.Port : null;
        if (PortMenu.IsDropDownOpen ||
            (lastAutoPort == automaticPort && ports.AsSpan().SequenceEqual(current) && PortMenu.ItemsSource is not null)) return;
        lastAutoPort = automaticPort;
        ports = current;
        refreshingPorts = true;
        PortMenu.ItemsSource = new[] { automaticPort is not null ? $"Auto · {System.IO.Path.GetFileName(automaticPort)}" : "Auto" }.Concat(ports).ToArray();
        PortMenu.SelectedIndex = requested.Length == 0 ? 0 : Array.IndexOf(ports, requested) + 1;
        refreshingPorts = false;
    }

    private async Task CheckAsync()
    {
        CheckUpdates.IsEnabled = false;
        UpdateStatus.Text = "Checking for Linux releases…";
        try
        {
            available = await LinuxUpdateService.CheckAsync(checks.Token);
            UpdateStatus.Text = available is null ? "No newer Linux package found." : $"Voltra {available.Version} is available.";
            OpenRelease.IsVisible = available is not null;
            ReleaseNotes.Text = available?.Notes ?? "";
            NotesViewer.IsVisible = !string.IsNullOrWhiteSpace(available?.Notes);
        }
        catch (OperationCanceledException) when (checks.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or
            InvalidOperationException or OperationCanceledException or KeyNotFoundException)
        { UpdateStatus.Text = $"Could not check for updates: {ex.Message}"; }
        finally { CheckUpdates.IsEnabled = true; }
    }

    internal void CancelChecks() => checks.Cancel();
}
