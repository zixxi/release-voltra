using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace Voltra.Linux.Views;

internal static class InterfaceMotion
{
    internal static async Task<bool> IsReducedAsync()
    {
        if (Environment.GetEnvironmentVariable("VOLTRA_REDUCED_MOTION") is "1" or "true") return true;
        string desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        using var limit = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            if (desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase) ||
                desktop.Contains("Cinnamon", StringComparison.OrdinalIgnoreCase) ||
                desktop.Contains("Unity", StringComparison.OrdinalIgnoreCase))
            {
                string schema = desktop.Contains("Cinnamon", StringComparison.OrdinalIgnoreCase)
                    ? "org.cinnamon.desktop.interface" : "org.gnome.desktop.interface";
                return (await ProcessCommand.RunAsync("gsettings", limit.Token, "get", schema, "enable-animations")).Trim() == "false";
            }
            if (desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase))
            {
                string config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ??
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
                return File.Exists(Path.Combine(config, "kdeglobals")) &&
                    File.ReadLines(Path.Combine(config, "kdeglobals")).Any(line =>
                        line.Trim().StartsWith("AnimationDurationFactor=", StringComparison.Ordinal) &&
                        double.TryParse(line.Split('=', 2)[1], System.Globalization.CultureInfo.InvariantCulture, out double factor) &&
                        factor == 0);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or
            OperationCanceledException or System.ComponentModel.Win32Exception) { }
        return false;
    }

    internal static async Task PlayAsync(Visual view, TranslateTransform transform, double startOpacity,
        double endOpacity, double startY, double endY, int milliseconds, CancellationToken token)
    {
        var motion = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0),
                    Setters = { new Setter(Visual.OpacityProperty, startOpacity), new Setter(TranslateTransform.YProperty, startY) }
                },
                new KeyFrame
                {
                    Cue = new Cue(1),
                    Setters = { new Setter(Visual.OpacityProperty, endOpacity), new Setter(TranslateTransform.YProperty, endY) }
                }
            }
        };
        view.Opacity = endOpacity;
        transform.Y = endY;
        try { await motion.RunAsync(view, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
