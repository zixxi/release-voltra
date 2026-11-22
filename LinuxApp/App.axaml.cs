using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Voltra.Linux.Views;

namespace Voltra.Linux;

public sealed partial class App : Application
{
    private string accentHex = Settings.DefaultAccent;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ActualThemeVariantChanged += (_, _) => UpdatePalette();
        UpdatePalette();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            _ = StartAsync(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var model = new MainViewModel();
        ApplyAppearance(model.Settings);
        var window = new MainWindow(model);
        desktop.MainWindow = window;
        bool nativeTray = false, exiting = false;
        desktop.ShutdownRequested += (_, e) =>
        {
            if (exiting) return;
            e.Cancel = true;
            _ = ExitAsync();
        };
        var startup = model.RestoreStartupAsync();
        window.Closing += (_, e) =>
        {
            if (exiting) return;
            e.Cancel = true;
            if (model.FirmwareDialogOpen) return;
            if (nativeTray) window.Hide();
            else _ = ExitAsync();
        };
        bool minimized = desktop.Args?.Contains("--minimized", StringComparer.Ordinal) == true;
        // The lifetime starts the main window itself. Delay assigning it until a tray probe
        // has completed for a minimized launch so no window flashes in supported sessions.
        if (minimized) desktop.MainWindow = null;
        nativeTray = await LinuxStartupService.IsTrayAvailableAsync(CancellationToken.None);
        if (exiting) return;

        if (nativeTray)
        {
            try
            {
                using var icon = AssetLoader.Open(new Uri("avares://LinuxApp/Assets/Voltra.ico"));
                var menu = new NativeMenu();
                var open = new NativeMenuItem("Open Voltra");
                var exit = new NativeMenuItem("Exit");
                open.Click += (_, _) => OpenWindow();
                exit.Click += (_, _) => _ = ExitAsync();
                menu.Items.Add(open);
                menu.Items.Add(exit);
                TrayIcon.SetIcons(this, new TrayIcons { new TrayIcon
                {
                    Icon = new WindowIcon(icon), ToolTipText = "Voltra", Menu = menu, IsVisible = true
                }});
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or NotSupportedException)
            {
                nativeTray = false;
                model.Report($"System tray unavailable: {ex.Message}");
            }
        }
        desktop.MainWindow = window;
        if (!minimized || !nativeTray)
        {
            window.Show();
            if (minimized) model.Report("System tray unavailable. Voltra opened normally.");
        }
        await startup;

        void OpenWindow()
        {
            window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        async Task ExitAsync()
        {
            if (exiting || model.FirmwareDialogOpen || model.Firmware.Installing) return;
            exiting = true;
            window.StopRefresh();
            try { await model.DisposeAsync(); }
            catch (Exception ex) { Console.Error.WriteLine($"Voltra shutdown: {ex.Message}"); }
            desktop.Shutdown();
        }
    }

    internal void ApplyAppearance(Settings settings)
    {
        accentHex = settings.Accent;
        RequestedThemeVariant = settings.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
        UpdatePalette();
    }

    private void UpdatePalette()
    {
        bool dark = ActualThemeVariant == ThemeVariant.Dark;
        var background = Color.Parse(dark ? "#17181D" : "#F8F9FB");
        var field = Color.Parse(dark ? "#22242B" : "#FFFFFF");
        var foreground = Color.Parse(dark ? "#F0F1F4" : "#20232B");
        var muted = Color.Parse(dark ? "#ADB2C0" : "#565D6A");
        if (!LinuxSettingsService.TryAccent(accentHex, out var accent))
            accent = Color.Parse(Settings.DefaultAccent);
        for (int step = 0; step < 20 && (Contrast(accent, field) < 4.5 || Contrast(accent, background) < 4.5); step++)
            accent = Blend(accent, dark ? Colors.White : Colors.Black, 0.15f);
        var accentForeground = Contrast(accent, Colors.Black) > Contrast(accent, Colors.White) ? Colors.Black : Colors.White;

        void Brush(string key, Color color) => Resources[key] = new SolidColorBrush(color);
        Brush("Workspace", background);
        Brush("Sidebar", Color.Parse(dark ? "#121318" : "#EEF0F4"));
        Brush("Surface", field);
        Brush("Text", foreground);
        Brush("Muted", muted);
        Brush("Line", Color.Parse(dark ? "#3C414D" : "#C6CAD3"));
        Brush("VoltraAccent", accent);
        Brush("Selected", Blend(background, accent, dark ? 0.18f : 0.12f));
        Brush("ControlHover", Blend(field, accent, 0.09f));
        Brush("ControlPressed", Blend(field, accent, 0.25f));
        Brush("PrimaryHover", Blend(accent, accentForeground, 0.12f));
        Brush("PrimaryPressed", Blend(accent, accentForeground, 0.22f));
        Brush("AccentForeground", accentForeground);
        Brush("Online", Color.Parse(dark ? "#83D6A5" : "#1C7045"));
        Brush("Pending", Color.Parse(dark ? "#E4BE78" : "#805B16"));
        Brush("Error", Color.Parse(dark ? "#FF9AA8" : "#A3213B"));
        Brush("ScrollThumb", Blend(background, muted, dark ? 0.57f : 0.76f));
        Resources["SystemAccentColor"] = accent;
    }

    private static Color Blend(Color from, Color to, float amount) => Color.FromRgb(
        (byte)(from.R + (to.R - from.R) * amount),
        (byte)(from.G + (to.G - from.G) * amount),
        (byte)(from.B + (to.B - from.B) * amount));

    private static double Contrast(Color first, Color second)
    {
        static double Linear(byte value)
        {
            double channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => Linear(color.R) * 0.2126 + Linear(color.G) * 0.7152 + Linear(color.B) * 0.0722;
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}
