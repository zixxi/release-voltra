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
    private bool refreshingPorts, writingStartup, gesture;
    private readonly CancellationTokenSource checks = new();
    private LinuxRelease? available;

    public SettingsView() => InitializeComponent();

    internal void Setup(MainViewModel model)
    {
        this.model = model;
        var config = model.Settings;
        StartupToggle.IsChecked = config.Startup;
        ThemeMenu.ItemsSource = new[] { "Dark", "Light", "System" };
        ThemeMenu.SelectedItem = config.Theme;
        AccentInput.Text = config.Accent;
        SyncToggle.IsChecked = config.Sync;
        TakeoverMenu.ItemsSource = new[] { "Soft takeover", "Immediate" };
        TakeoverMenu.SelectedIndex = (int)config.Takeover;
        ThresholdMenu.ItemsSource = new[] { "1%", "2%", "3%", "4%", "5%" };
        ThresholdMenu.SelectedIndex = config.NoiseThreshold - 1;
        VersionLine.Text = $"Voltra {LinuxUpdateService.Version} · Linux x64";
        foreach (string value in new[] { "#B78BFA", "#6596EF", "#26B6BC", "#64BA6C", "#E5AA51", "#E6809D" })
        {
            var sample = new Button { Width = 27, Height = 27, Background = new SolidColorBrush(Color.Parse(value)) };
            ToolTip.SetTip(sample, value);
            sample.Click += (_, _) => SaveAccent(value);
            Presets.Children.Add(sample);
        }

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
        ThemeMenu.SelectionChanged += (_, _) =>
        {
            if (ThemeMenu.SelectedItem is not string theme) return;
            model.Change(model.Settings with { Theme = theme });
            ((App)Application.Current!).ApplyAppearance(model.Settings);
        };
        ApplyAccent.Click += (_, _) => SaveAccent(AccentInput.Text ?? "");
        OpenColor.Click += (_, _) =>
        {
            if (AccentPanel.IsVisible) { CancelColor(); return; }
            Picker.SetHex(LinuxSettingsService.TryAccent(AccentInput.Text, out _) ? AccentInput.Text! : model.Settings.Accent);
            AccentPanel.IsVisible = true;
        };
        CancelPicker.Click += (_, _) => CancelColor();
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !AccentPanel.IsVisible) return;
            CancelColor();
            e.Handled = true;
        };
        ApplyPicker.Click += (_, _) => SaveAccent(Picker.Hex);
        Picker.Changed += (_, _) =>
        {
            gesture = true;
            AccentInput.Text = Picker.Hex;
            gesture = false;
        };
        AccentInput.TextChanged += (_, _) =>
        {
            var text = AccentInput.Text;
            if (!LinuxSettingsService.TryAccent(text, out var color)) return;
            AccentSample.Background = new SolidColorBrush(color);
            if (!gesture && AccentPanel.IsVisible) Picker.SetHex(text!);
        };
        SyncToggle.IsCheckedChanged += (_, _) => model.Change(model.Settings with { Sync = SyncToggle.IsChecked == true });
        TakeoverMenu.SelectionChanged += (_, _) =>
        {
            if (TakeoverMenu.SelectedIndex >= 0) model.Change(model.Settings with { Takeover = (KnobTakeover)TakeoverMenu.SelectedIndex });
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

        void CancelColor()
        {
            AccentInput.Text = model.Settings.Accent;
            AccentPanel.IsVisible = false;
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
            AccentPanel.IsVisible = false;
            AccentInput.Text = text.ToUpperInvariant();
            model.Change(model.Settings with { Accent = AccentInput.Text });
            ((App)Application.Current!).ApplyAppearance(model.Settings);
        }
    }

    internal void Refresh(DeviceSnapshot device)
    {
        if (model is null) return;
        DeviceStatus.Text = device.Status;
        if (device.Firmware is { } firmware) DeviceStatus.Text += $" · Firmware {firmware}";
        var requested = model.Settings.Port;
        string[] current = requested.Length == 0 || device.Ports.Contains(requested)
            ? device.Ports : [.. device.Ports, requested];
        if (ports.AsSpan().SequenceEqual(current) && PortMenu.ItemsSource is not null) return;
        ports = current;
        refreshingPorts = true;
        PortMenu.ItemsSource = new[] { "Automatic (native Espressif USB)" }.Concat(ports).ToArray();
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
