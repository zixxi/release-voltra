using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
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

    public MainWindow() => InitializeComponent();

    internal MainWindow(MainViewModel model) : this()
    {
        this.model = model;
        Dashboard.Setup(model, preview, () => Navigate(1));
        Oled.Setup(model, preview);
        Preferences.Setup(model);
        DashboardButton.Click += (_, _) => Navigate(0);
        OledButton.Click += (_, _) => Navigate(1);
        SettingsButton.Click += (_, _) => Navigate(2);
        Refresh();
        refresh.Tick += (_, _) => { if (IsVisible) Refresh(); };
        refresh.Start();
        Closed += (_, _) => { refresh.Stop(); preview.Dispose(); };
    }

    private void Navigate(int page)
    {
        Dashboard.IsVisible = page == 0;
        Oled.IsVisible = page == 1;
        Preferences.IsVisible = page == 2;
        DashboardButton.Classes.Set("selected", page == 0);
        OledButton.Classes.Set("selected", page == 1);
        SettingsButton.Classes.Set("selected", page == 2);
        PageHeading.Text = page switch { 1 => "OLED", 2 => "Settings", _ => "Dashboard" };
        NavMarker.Margin = new Thickness(1, 6 + page * 44, 0, 0);
        PageScroll.Offset = default;
        Refresh();
    }

    private void Refresh()
    {
        var device = model.Device.Snapshot;
        var audio = model.Audio.Snapshot;
        var config = model.Settings;
        ObserveMovement(device.Levels, config.NoiseThreshold);

        var (label, color) = device.Connection switch
        {
            ControllerConnection.Connecting => ("Connecting", "Pending"),
            ControllerConnection.Connected => ("Connected", "Online"),
            ControllerConnection.Error => ("Connection error", "Error"),
            _ => ("Disconnected", "Muted")
        };
        ConnectionTitle.Text = label;
        if (Application.Current!.TryGetResource(color, ActualThemeVariant, out var value) && value is IBrush brush)
        {
            ConnectionDot.Fill = device.Connection == ControllerConnection.Disconnected ? Brushes.Transparent : brush;
            ConnectionDot.Stroke = brush;
            ConnectionTitle.Foreground = brush;
        }
        ConnectionDetail.Text = device.Port ?? device.Status;
        AudioStatus.Text = audio.Status;
        ToolTip.SetTip(ConnectionDetail, device.Status);
        ToolTip.SetTip(AudioStatus, audio.Status);
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
