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
        VerifyCaptionPlacement(window);
        VerifyButtonAndMenuLayout(window);
        VerifySettingsScroll(window);
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

    // Repeat on each monitor scale to exercise its actual GDI font metrics.
    private static void VerifyCaptionPlacement(Form window)
    {
        var palette = ThemePalette.From(new MixerSettings());
        using var button = new MixerButton(palette, false) { TabStop = false };
        window.Controls.Add(button);
        button.ApplyTheme(palette);
        int Px(int value) => window.LogicalToDeviceUnits(value);
        button.Padding = new Padding(Px(13), 0, Px(7), 0);
        foreach (string label in new[] { "Edit mappings", "Dark", "Light", "System", "Immediate",
            "Soft takeover", "12-hour", "24-hour", "Rename", "Activate", "Dashboard" })
        {
            button.Navigation = label == "Dashboard";
            button.Segment = label is "Dark" or "Light" or "System" or "Immediate" or "Soft takeover" or "12-hour" or "24-hour";
            button.Size = new Size(Px(125), Px(button.Segment ? 28 : 35));
            var area = button.ClientRectangle;
            using var blank = new Bitmap(area.Width, area.Height);
            using var actual = new Bitmap(area.Width, area.Height);
            button.Text = "";
            button.DrawToBitmap(blank, area);
            button.Text = label;
            button.DrawToBitmap(actual, area);
            using var expected = (Bitmap)blank.Clone();
            var bounds = button.Navigation ? Rectangle.FromLTRB(button.Padding.Left, 0,
                area.Right - button.Padding.Right, area.Bottom) : area;
            using (var g = Graphics.FromImage(expected))
                TextRenderer.DrawText(g, label, button.Font, bounds, button.Navigation ? palette.Muted : palette.Text,
                    TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                    (button.Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
            var ink = ChangedBounds(actual, blank);
            var reference = ChangedBounds(expected, blank);
            if (ink.IsEmpty || ink != reference)
                throw new Exception($"{label} at {window.DeviceDpi} DPI: ink {ink}, expected {reference}");
        }
        Console.WriteLine($"PASS button caption placement at {window.DeviceDpi} DPI.");
    }

    private static Rectangle ChangedBounds(Bitmap painted, Bitmap background)
    {
        var bounds = Rectangle.Empty;
        for (int y = 0; y < painted.Height; y++)
        for (int x = 0; x < painted.Width; x++)
        {
            if (((painted.GetPixel(x, y).ToArgb() ^ background.GetPixel(x, y).ToArgb()) & 0x00ffffff) == 0) continue;
            var pixel = new Rectangle(x, y, 1, 1);
            bounds = bounds.IsEmpty ? pixel : Rectangle.Union(bounds, pixel);
        }
        return bounds;
    }

    private static void VerifyButtonAndMenuLayout(Form window)
    {
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light, AppTheme.System })
        foreach (var accent in new[] { "#B78BFA", "#EE3333", "#6596EF" })
        {
            var palette = ThemePalette.From(new MixerSettings { Theme = theme, Accent = accent });
            foreach (var labels in new[] { new[] { "Dark", "Light", "System" },
                new[] { "Immediate", "Soft takeover" }, new[] { "12-hour", "24-hour" } })
            {
                using var row = new SegmentedButtonRow(palette, 258);
                window.Controls.Add(row);
                foreach (var label in labels)
                    row.Controls.Add(new MixerButton(palette, label == labels[0]) { Text = label, Segment = true });
                foreach (int dpi in new[] { 96, 120, 144, 192 })
                {
                    int Px(int value) => (int)Math.Round(value * dpi / 96d);
                    row.Size = new(Px(labels[0] == "12-hour" ? 168 : 258), Px(34));
                    row.Padding = new(Px(3));
                    row.PerformLayout();
                    int x = row.Padding.Left;
                    foreach (Control button in row.Controls)
                    {
                        if (button.Left != x || button.Top != row.Padding.Top ||
                            button.Bottom != row.Height - row.Padding.Bottom ||
                            Math.Abs(button.Width - row.DisplayRectangle.Width / (double)labels.Length) >= 1)
                            throw new Exception($"Segment clipped at {dpi} DPI: {button.Text} {button.Bounds}");
                        x = button.Right;
                    }
                    if (x != row.Width - row.Padding.Right) throw new Exception("Unexpected segment gutter");
                }
            }
            using var picker = new SidebarProfilePicker(palette) { Width = window.LogicalToDeviceUnits(157) };
            window.Controls.Add(picker);
            using var menu = new SidebarProfileMenu(palette);
            menu.Items.Add("Default");
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Manage profiles...");
            menu.ShowFor(picker);
            try
            {
                var item = menu.Items[0];
                item.Select();
                if (menu.Width != picker.Width || item.Bounds.Left != menu.Width - item.Bounds.Right)
                    throw new Exception($"Asymmetric profile popup: {item.Bounds} in {menu.ClientSize}");
                using var bitmap = new Bitmap(item.Width, item.Height);
                using var g = Graphics.FromImage(bitmap);
                g.Clear(palette.Field);
                menu.Renderer.DrawMenuItemBackground(new(g, item));
                int y = item.Height / 2;
                int[] painted = Enumerable.Range(0, item.Width)
                    .Where(x => bitmap.GetPixel(x, y).ToArgb() == palette.Hover.ToArgb()).ToArray();
                if (painted.Length == 0 || painted[0] == 0 || Math.Abs(painted[0] - (item.Width - 1 - painted[^1])) > 1)
                    throw new Exception($"Uneven profile highlight for {theme}, {accent}");
            }
            finally { menu.Close(); }
        }
        Console.WriteLine("PASS segment geometry and profile menu bounds and hover rendering");
    }

    private static void VerifySettingsScroll(Form window)
    {
        using var viewport = new PageScroller(ThemePalette.From(new MixerSettings()))
            { Dock = DockStyle.Fill, Padding = new(32, 18, 32, 22) };
        window.Controls.Add(viewport);
        var body = new TableLayoutPanel { RowCount = 2, ColumnCount = 1 };
        body.RowStyles.Add(new(SizeType.Absolute, 60));
        body.RowStyles.Add(new(SizeType.Percent, 100));
        var rows = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false,
            FlowDirection = FlowDirection.TopDown, Margin = Padding.Empty };
        body.Controls.Add(rows, 0, 1);
        var options = new Panel { Height = 1310, Margin = Padding.Empty };
        var firmware = new Button { Text = "Update firmware", Height = 36 };
        rows.Controls.AddRange([options, firmware]);
        MixerForm.FitSettingsToContent(body, rows);
        viewport.SetPage(body);
        foreach (var size in new[] { new Size(940, 650), new Size(1280, 900), new Size(1000, 700) })
        {
            window.Size = size;
            foreach (var height in new[] { 1310, 1480, 1170 })
            {
                options.Height = height;
                Application.DoEvents();
                viewport.ScrollTo(int.MaxValue);
                var bounds = viewport.RectangleToClient(firmware.RectangleToScreen(firmware.ClientRectangle));
                if (!viewport.ClientRectangle.Contains(bounds) || viewport.Offset <= 0 ||
                    Math.Abs(viewport.ClientSize.Height - viewport.Padding.Bottom - bounds.Bottom) > 12)
                    throw new InvalidOperationException($"Firmware action clipped or excess scroll at {size}, {height}: {bounds}");
            }
        }
        Console.WriteLine("PASS Settings content extent and last action after resizing");
    }
}
