using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace Voltra.Linux.Views;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel model = null!;
    private readonly DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly WriteableBitmap preview = new(new PixelSize(128, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
    private readonly int[] row = new int[128];
    private byte[]? painted;

    public MainWindow() => InitializeComponent();

    internal MainWindow(MainViewModel model) : this()
    {
        this.model = model;
        VersionText.Text = $"Voltra {LinuxUpdateService.Version}";
        Dashboard.Setup(model, preview);
        Oled.Setup(model, preview);
        Preferences.Setup(model);
        DashboardButton.Click += (_, _) => Navigate(Dashboard, DashboardButton);
        OledButton.Click += (_, _) => Navigate(Oled, OledButton);
        SettingsButton.Click += (_, _) => Navigate(Preferences, SettingsButton);
        Refresh();
        refresh.Tick += (_, _) => Refresh();
        refresh.Start();
        Closed += (_, _) => { refresh.Stop(); preview.Dispose(); };
    }

    private void Navigate(Control visiblePage, Button selected)
    {
        foreach (var page in new Control[] { Dashboard, Oled, Preferences }) page.IsVisible = page == visiblePage;
        foreach (var button in new[] { DashboardButton, OledButton, SettingsButton })
            button.Classes.Set("selected", button == selected);
    }

    private void Refresh()
    {
        var device = model.Device.Snapshot;
        var audio = model.Audio.Snapshot;
        if (Dashboard.IsVisible) Dashboard.Refresh(device, audio);
        if (Preferences.IsVisible) Preferences.Refresh(device);
        var frame = model.Oled.Snapshot;
        if (Oled.IsVisible) Oled.Refresh(device, frame);
        if (!ReferenceEquals(painted, frame.Pixels) && (Dashboard.IsVisible || Oled.IsVisible)) PaintFrame(frame.Pixels);
        Notice.Text = model.Message;
        Notice.IsVisible = !string.IsNullOrEmpty(model.Message);
    }

    private void PaintFrame(byte[] frame)
    {
        using (var buffer = preview.Lock())
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < row.Length; x++)
                    row[x] = (frame[y * 16 + x / 8] & (0x80 >> (x & 7))) == 0 ? unchecked((int)0xFF090B10) : unchecked((int)0xFFE9EDFF);
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
