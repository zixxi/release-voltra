using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Voltra;

namespace Voltra.Linux.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel model = null!;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly WriteableBitmap preview = new(new PixelSize(OledFrame.Width, OledFrame.Height),
        new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    private readonly int[] row = new int[OledFrame.Width];
    private byte[]? painted;
    private int[]? previousLevels;
    private long lastMove;
    private int activeChannel = -1;
    private int currentPage;
    private bool reducedMotion = true;
    private CancellationTokenSource? pageAnimation;

    public MainWindow() => InitializeComponent();

    internal MainWindow(MainViewModel model) : this()
    {
        this.model = model;
        SizeChanged += (_, e) => PageContent.Margin = e.NewSize.Width < 800
            ? new Thickness(16, 10, 14, 10) : new Thickness(26, 16, 24, 16);
        Dashboard.Setup(model, preview, () => Navigate(1));
        Oled.Setup(model, preview);
        Preferences.Setup(model);
        DashboardButton.Click += (_, _) => Navigate(0);
        OledButton.Click += (_, _) => Navigate(1);
        SettingsButton.Click += (_, _) => Navigate(2);
        Refresh();
        refresh.Tick += (_, _) => { if (IsVisible) Refresh(); };
        refresh.Start();
        Opened += async (_, _) =>
        {
            reducedMotion = await InterfaceMotion.IsReducedAsync();
            if (reducedMotion) return;
            Classes.Add("motion");
            foreach (var button in new[] { DashboardButton, OledButton, SettingsButton })
                button.Classes.Add("motion");
            if (NavMarker.RenderTransform is TranslateTransform indicator)
                indicator.Transitions = [new DoubleTransition
                {
                    Property = TranslateTransform.YProperty, Duration = TimeSpan.FromMilliseconds(185), Easing = new CubicEaseOut()
                }];
        };
        Closed += (_, _) => { refresh.Stop(); pageAnimation?.Cancel(); preview.Dispose(); };
    }

    internal bool ReducedMotion => reducedMotion;

    private async void Navigate(int page)
    {
        if (currentPage == page) return;
        bool forward = page > currentPage;
        currentPage = page;
        pageAnimation?.Cancel();
        Dashboard.IsVisible = page == 0;
        Oled.IsVisible = page == 1;
        Preferences.IsVisible = page == 2;
        DashboardButton.Classes.Set("selected", page == 0);
        OledButton.Classes.Set("selected", page == 1);
        SettingsButton.Classes.Set("selected", page == 2);
        PageHeading.Text = page switch { 1 => "OLED", 2 => "Settings", _ => "Dashboard" };
        if (NavMarker.RenderTransform is TranslateTransform indicator)
            indicator.Y = (page switch { 1 => OledButton, 2 => SettingsButton, _ => DashboardButton }).Bounds.Y;
        PageScroll.Offset = default;
        Refresh();
        if (reducedMotion || PageContent.RenderTransform is not TranslateTransform movement) return;

        using var animation = new CancellationTokenSource();
        pageAnimation = animation;
        try
        {
            await InterfaceMotion.PlayAsync(PageContent, movement, .74, 1, forward ? 9 : -9, 0, 185, animation.Token);
        }
        finally
        {
            if (ReferenceEquals(pageAnimation, animation)) pageAnimation = null;
        }
    }

    private void Refresh()
    {
        var device = model.Device.Snapshot;
        var audio = model.Audio.Snapshot;
        var config = model.Settings;
        ObserveMovement(device.Levels, config.NoiseThreshold);

        var (label, color) = device.Connection switch
        {
            ControllerConnection.Connecting => ("Connecting…", "Pending"),
            ControllerConnection.Connected => ("Connected", "Online"),
            ControllerConnection.Error => ("Connection error", "Error"),
            _ => ("Disconnected", "Muted")
        };
        ConnectionTitle.Text = label;
        if (Application.Current!.TryGetResource(color, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            ConnectionDot.Fill = brush;
            ConnectionTitle.Foreground = brush;
        }
        ConnectionDetail.Text = device.Connection switch
        {
            ControllerConnection.Connected => "ESP32-S3-Zero",
            ControllerConnection.Connecting => "Searching for controller",
            ControllerConnection.Error => "See device settings",
            _ => "Connect Voltra device"
        };
        AudioStatus.Text = audio.Status switch
        {
            "PipeWire connected" => "Audio ready",
            "Connecting to PipeWire…" => "Audio starting…",
            _ => "PipeWire unavailable"
        };
        ToolTip.SetTip(ConnectionStatus, device.Port is { } path ? $"{path}\n{audio.Status}" : $"{device.Status}\n{audio.Status}");
        var frame = model.Oled.Snapshot;
        if (Dashboard.IsVisible) Dashboard.Refresh(config, device, audio, activeChannel);
        if (Oled.IsVisible) Oled.Refresh(config, device, frame);
        if (Preferences.IsVisible) Preferences.Refresh(device);
        byte[] shown = device.Displayed ?? frame.Pixels;
        if (!ReferenceEquals(painted, shown) && (Dashboard.IsVisible || Oled.IsVisible)) PaintFrame(shown);
        Notice.Text = model.Message;
        Notice.IsVisible = !string.IsNullOrEmpty(model.Message);
    }

    private void ObserveMovement(int[]? levels, int threshold)
    {
        long now = Environment.TickCount64;
        if (levels is null || now - lastMove >= 3000) activeChannel = -1;
        if (!ReferenceEquals(previousLevels, levels) && previousLevels is not null && levels is not null)
        {
            int movement = threshold - 1;
            for (int i = 0; i < levels.Length; i++)
            {
                int change = Math.Abs(levels[i] - previousLevels[i]);
                if (change <= movement) continue;
                movement = change;
                activeChannel = i;
                lastMove = now;
            }
        }
        previousLevels = levels;
    }

    private void PaintFrame(byte[] frame)
    {
        using (var buffer = preview.Lock())
            for (int y = 0; y < OledFrame.Height; y++)
            {
                for (int x = 0; x < row.Length; x++)
                    row[x] = (frame[y * (OledFrame.Width / 8) + x / 8] & (0x80 >> (x & 7))) == 0
                        ? unchecked((int)0xFF000000) : unchecked((int)0xFFFFFFFF);
                Marshal.Copy(row, 0, IntPtr.Add(buffer.Address, y * buffer.RowBytes), row.Length);
            }
        painted = frame;
        Dashboard.InvalidatePreview();
        Oled.InvalidatePreview();
    }

    internal void StopRefresh()
    {
        refresh.Stop();
        Preferences.CancelChecks();
    }
}
