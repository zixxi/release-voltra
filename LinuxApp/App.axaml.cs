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
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

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
            if (exiting) return;
            exiting = true;
            window.StopRefresh();
            try { await model.DisposeAsync(); }
            catch (Exception ex) { Console.Error.WriteLine($"Voltra shutdown: {ex.Message}"); }
            desktop.Shutdown();
        }
    }

    internal void ApplyAppearance(Settings settings)
    {
        RequestedThemeVariant = settings.Theme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
        LinuxSettingsService.TryAccent(settings.Accent, out Color accent);
        static double Linear(byte value) => value / 255d <= .04045 ? value / 255d / 12.92 :
            Math.Pow((value / 255d + .055) / 1.055, 2.4);
        double luminance = .2126 * Linear(accent.R) + .7152 * Linear(accent.G) + .0722 * Linear(accent.B);
        Resources["VoltraAccent"] = new SolidColorBrush(accent);
        Resources["AccentForeground"] = new SolidColorBrush(luminance > .179 ? Colors.Black : Colors.White);
        Resources["SystemAccentColor"] = accent;
    }
}
