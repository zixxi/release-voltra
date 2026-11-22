using Voltra;

internal static class Program
{
    // Run from an interactive Windows desktop. This exercises the production
    // control without opening a serial port or changing any WASAPI volume.
    [STAThread]
    private static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Control.CheckForIllegalCrossThreadCalls = true;
        using var icon = new Icon(typeof(KnobReadout), "Brand.ico");
        using var window = new Form { ClientSize = new(420, 300), Icon = icon };
        window.Show();
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.System })
        {
            var palette = ThemePalette.From(new MixerSettings { Theme = theme });
            using var gauge = new KnobReadout(palette, 0);
            window.Controls.Add(gauge);
            using var bitmap = new Bitmap(420, 300);
            for (int i = 0; i < 360; i++)
            {
                if (i % 40 == 0) window.WindowState = FormWindowState.Minimized;
                if (i % 40 == 1) window.WindowState = FormWindowState.Normal;
                gauge.Size = (i % 8) switch
                {
                    0 => Size.Empty, 1 => new(29, 67), 2 => new(30, 68),
                    _ => new(100 + i % 180, 150 + i % 150)
                };
                gauge.Set("Currently Focused", i % 7 == 0 ? null : i % 103 - 1, i % 2 == 0,
                    i % 111 - 5, i % 3 == 0 ? "No audio session" : "Player");
                Application.DoEvents();
                if (gauge.Width > 0 && gauge.Height > 0) gauge.DrawToBitmap(bitmap, gauge.ClientRectangle);
            }
            window.Controls.Remove(gauge);
        }
        Console.WriteLine($"PASS WinForms gauge stress at {window.DeviceDpi} DPI.");
    }
}
