using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Voltra;

internal sealed partial class MixerForm : Form
{
    private static readonly string[] PageNames = ["Mixer levels", "Clock & date", "Weather", "Image or GIF", "PC uptime"];
    private static readonly string[] Presets = [MixerSettings.DefaultAccent, "#6596EF", "#26B6BC", "#64BA6C", "#E5AA51", "#E6809D"];
    private static readonly string[] PresetNames = ["Lilac", "Blue", "Teal", "Green", "Amber", "Rose"];
    private readonly System.Windows.Forms.Timer refresh = new() { Interval = 45 };
    private readonly OledRenderer renderer = new();
    private readonly WeatherService weatherService = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly Icon brandIcon = new(typeof(MixerForm), "Brand.ico");
    private readonly NotifyIcon tray = new();
    private readonly ToolTip tips = new();
    private readonly VoltraTrayMenu trayMenu;
    private readonly DeviceLink link;
    private MixerSettings config;
    private ThemePalette palette;
    private TableLayoutPanel? shell;
    private PageScroller? scroll;
    private UiMotion? navigationMotion;
    private MixerButton[] navigation = [];
    private byte[]? idleFrame;
    private long nextFrame;
    private FramePreview? preview;
    private KnobReadout[] meters = [];
    private Label? connection, connectionDetail, oledDetail, weatherDetail;
    private Control? settingsBody;
    private bool buildingSettings;
    private PickerField? portPicker;
    private string[]? shownPorts;
    private string? shownPort;
    private WeatherReading? weather;
    private long nextWeather;
    private string? weatherMessage;
    private bool fetchingWeather, exiting;
    private int page;
    private Task settingsSave = Task.CompletedTask;
    private string? startupError;

    private readonly InstanceActivation activation;
    private FormWindowState previousWindowState = FormWindowState.Normal;

    internal MixerForm(InstanceActivation activation)
    {
        this.activation = activation;
        config = MixerSettings.Load();
        startupError = WindowsStartup.Synchronize(config.LaunchAtWindowsStartup);
        palette = ThemePalette.From(config);
        link = new(config);
        Text = "Voltra";
        Icon = brandIcon;
        Font = new("Segoe UI", UiMetrics.Body);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        DoubleBuffered = true;
        MinimumSize = new Size(940, 650);
        ClientSize = new Size(1100, 690);
        StartPosition = FormStartPosition.CenterScreen;
        if (config.MediaPath.Length > 0) TryLoadMedia(config.MediaPath, false);
        trayMenu = new VoltraTrayMenu(() => ThemePalette.From(config), RestoreAndActivateMainWindow, ExitFromTray);
        SetupTray();
        BuildShell();
        SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        refresh.Tick += (_, _) =>
        {
            if (activation.TakeRequest()) RestoreAndActivateMainWindow();
            UpdateView();
        };
        refresh.Start();
        Shown += (_, _) => BeginInvoke((Action)PrewarmSettings);
        StartUpdateChecks();
        Activated += (_, _) => ShowPendingUpdate();
    }

    private void BuildShell()
    {
        int scrollOffset = scroll?.Offset ?? 0;
        SuspendLayout();
        navigationMotion?.Dispose();
        if (settingsBody?.Parent is null) settingsBody?.Dispose();
        settingsBody = null;
        updateStatus = null;
        firmwareStatus = null;
        firmwareCheck = firmwareInstall = null;
        updateCheck = null;
        portPicker = null;
        shell?.Dispose();
        palette = ThemePalette.From(config);
        BackColor = palette.Background;
        ForeColor = palette.Text;
        UpdateTitleBar();
        refreshUpdateDialogTheme?.Invoke();

        shell = Grid(3, 1);
        shell.ColumnStyles.Add(new(SizeType.Percent, 16));
        shell.ColumnStyles.Add(new(SizeType.Absolute, 1));
        shell.ColumnStyles.Add(new(SizeType.Percent, 84));
        shell.RowStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(shell);
        shell.Controls.Add(BuildSidebar(), 0, 0);
        shell.Controls.Add(new Panel { BackColor = palette.Line, Dock = DockStyle.Fill, Margin = new(0) }, 1, 0);

        scroll = new PageScroller(palette) { Dock = DockStyle.Fill, Padding = new(32, 18, 32, 22) };
        shell.Controls.Add(scroll, 2, 0);
        BuildPage(scale: false);
        ScaleNewContent(shell);
        ResumeLayout(true);
        scroll.ScrollTo(scrollOffset);
        UpdateView();
    }

    private void ScaleNewContent(Control control)
    {
        float scale = IsHandleCreated && AutoScaleDimensions.Width > 0 ? AutoScaleDimensions.Width / 96f : 1;
        if (scale != 1) control.Scale(new SizeF(scale, scale));
    }

    private void BuildPage(bool scale)
    {
        if (scroll is null) return;
        scroll.SuspendLayout();
        var previous = scroll.Page;
        scroll.SetPage(null);
        if (previous != settingsBody) previous?.Dispose();
        preview = null;
        meters = [];
        oledDetail = weatherDetail = null;
        bool cached = page == 2 && settingsBody is not null;
        var body = cached ? settingsBody! : CreatePage(page);
        scroll.SetPage(body);
        if (scale && !cached) ScaleNewContent(body);
        if (page == 2) settingsBody = body;
        scroll.ResumeLayout(true);
        UpdateView();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        // Windows can send the wheel to the focused sidebar control instead of
        // the page under the pointer, depending on the user's mouse settings.
        if (scroll is { IsHandleCreated: true } viewport &&
            viewport.ClientRectangle.Contains(viewport.PointToClient(PointToScreen(e.Location))))
            viewport.ScrollWheel(e);
        base.OnMouseWheel(e);
    }

    private void PrewarmSettings()
    {
        if (exiting || settingsBody is not null) return;
        settingsBody = CreatePage(2);
        ScaleNewContent(settingsBody);
    }

    private Control CreatePage(int target)
    {
        var body = Grid(1, 2);
        body.Dock = DockStyle.None;
        body.Height = target == 0 ? 634 : 600;
        body.RowStyles.Add(new(SizeType.Absolute, 60));
        body.RowStyles.Add(new(SizeType.Percent, 100));
        var title = Caption(new[] { "Dashboard", "OLED", "Settings" }[target], UiMetrics.Page, bold: true);
        body.Controls.Add(title, 0, 0);
        buildingSettings = target == 2;
        try
        {
            var content = target switch { 0 => Dashboard(), 1 => OledSettings(), _ => AppSettings() };
            body.Controls.Add(content, 0, 1);
            if (buildingSettings)
                FitSettingsToContent(body, content);
        }
        finally { buildingSettings = false; }
        return body;
    }

    internal static void FitSettingsToContent(TableLayoutPanel body, Control content)
    {
        // PageScroller uses the body's height as its scroll extent. Include the
        // scaled title row and every visible settings row, including nested sections.
        void Fit()
        {
            int rows = content.GetPreferredSize(new Size(Math.Max(1, content.ClientSize.Width), 0)).Height;
            int height = (int)Math.Ceiling(body.RowStyles[0].Height) + rows + content.Margin.Vertical;
            if (body.Height != height) body.Height = height;
        }
        content.Layout += (_, _) => Fit();
        Fit();
    }

    private Control BuildSidebar()
    {
        var sidebar = Grid(1, 4);
        sidebar.BackColor = palette.Sidebar;
        sidebar.Padding = new(10, 20, 10, 14);
        sidebar.ColumnStyles.Add(new(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new(SizeType.Absolute, 132));
        sidebar.RowStyles.Add(new(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new(SizeType.Absolute, 25));
        sidebar.RowStyles.Add(new(SizeType.Absolute, 24));
        var menu = new Panel { Dock = DockStyle.Fill, Margin = new(0), BackColor = palette.Sidebar };
        sidebar.Controls.Add(menu, 0, 0);
        var list = Grid(1, 3);
        menu.Controls.Add(list);
        navigation = new MixerButton[3];
        string[] names = ["Dashboard", "OLED", "Settings"];
        for (int i = 0; i < names.Length; i++)
        {
            int destination = i;
            list.RowStyles.Add(new(SizeType.Percent, 100f / 3));
            var button = ActionButton(names[i], () => Navigate(destination), selected: page == i);
            button.Dock = DockStyle.Fill;
            button.BackColor = palette.Sidebar;
            button.Navigation = true;
            button.Margin = new(0, 2, 0, 2);
            button.Padding = new(14, 0, 9, 0);
            navigation[i] = button;
            list.Controls.Add(button, 0, i);
        }
        var marker = new Panel { Width = 3, BackColor = palette.Accent, Enabled = false, TabStop = false };
        menu.Controls.Add(marker);
        marker.BringToFront();
        navigationMotion = new(menu, 205, () =>
        {
            if (navigationMotion is not { } motion) return;
            int row = menu.ClientSize.Height / 3;
            int inset = Math.Max(5, row / 6);
            marker.Bounds = new(1, Math.Clamp((int)(motion.Value * row) + inset, 0, menu.Height),
                Math.Max(2, menu.DeviceDpi / 32), Math.Max(0, row - inset * 2));
        });
        navigationMotion.Move(page, immediate: true);
        menu.SizeChanged += (_, _) =>
        {
            int row = menu.ClientSize.Height / 3;
            marker.SetBounds(1, (int)((navigationMotion?.Value ?? page) * row) + row / 6,
                Math.Max(2, menu.DeviceDpi / 32), Math.Max(0, row * 2 / 3));
        };
        connection = Caption("Disconnected", 10.5f);
        connection.Padding = new(10, 0, 0, 0);
        connectionDetail = Caption("", 8.5f, muted: true);
        connectionDetail.Padding = new(10, 0, 0, 0);
        sidebar.Controls.Add(connection, 0, 2);
        sidebar.Controls.Add(connectionDetail, 0, 3);
        return sidebar;
    }

    private void Navigate(int destination)
    {
        if (destination == page) return;
        long started = Stopwatch.GetTimestamp();
        page = destination;
        for (int i = 0; i < navigation.Length; i++) navigation[i].SetSelected(i == page);
        navigationMotion?.Move(page);
        navigation[page].Focus();
        BuildPage(scale: true);
        MixerDiagnostics.Log($"Navigation: page {destination} built and laid out in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms.");
    }

    private Control Dashboard()
    {
        var layout = Grid(1, 5);
        layout.RowStyles.Add(new(SizeType.Absolute, 260));
        layout.RowStyles.Add(new(SizeType.Absolute, 22));
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        layout.RowStyles.Add(new(SizeType.Absolute, 224));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        var top = Split();
        layout.Controls.Add(top, 0, 0);
        top.Controls.Add(PreviewColumn("OLED preview"), 0, 0);
        var quick = Stack(Caption("OLED", UiMetrics.Section, bold: true), Row("Idle page", IdlePicker()),
            Caption("Knob overlay returns to idle after 3 seconds.", 9, muted: true, height: 34),
            ActionButton("OLED settings", () => Navigate(1)));
        quick.Padding = new(24, 0, 0, 0);
        top.Controls.Add(quick, 2, 0);
        var separator = Divider();
        separator.Dock = DockStyle.Fill;
        layout.Controls.Add(separator, 0, 1);
        var titleRow = Grid(2, 1);
        titleRow.ColumnStyles.Add(new(SizeType.Percent, 100));
        titleRow.ColumnStyles.Add(new(SizeType.Absolute, 148));
        titleRow.Controls.Add(Caption("Live mixer", UiMetrics.Section, bold: true), 0, 0);
        var edit = ActionButton("Edit mappings", EditMappings);
        edit.Anchor = AnchorStyles.Right;
        titleRow.Controls.Add(edit, 1, 0);
        layout.Controls.Add(titleRow, 0, 2);

        var meterRow = Grid(5, 1);
        meters = new KnobReadout[5];
        for (int i = 0; i < meters.Length; i++)
        {
            meterRow.ColumnStyles.Add(new(SizeType.Percent, 20));
            meters[i] = new KnobReadout(palette, i) { Dock = DockStyle.Fill, Margin = new(2) };
            meterRow.Controls.Add(meters[i], i, 0);
        }
        layout.Controls.Add(meterRow, 0, 3);
        return layout;
    }

    private Control OledSettings()
    {
        var top = Split();
        top.Controls.Add(PreviewColumn("Live preview", true), 0, 0);
        var options = Stack(Caption("Idle page", UiMetrics.Section, bold: true));
        options.Padding = new(24, 0, 0, 0);
        top.Controls.Add(options, 2, 0);
        FlowLayoutPanel? idleContext = null;
        var choices = Stack();
        choices.Dock = DockStyle.None;
        choices.Height = 161;
        options.Controls.Add(choices);
        for (int i = 0; i < PageNames.Length; i++)
        {
            var value = (OledPage)i;
            var radio = new ChoiceMark(palette, PageNames[i], config.Page == value, radio: true)
            {
                Height = 32,
                UseMnemonic = false
            };
            radio.Click += (_, _) =>
            {
                if (config.Page == value) return;
                SelectIdle(value);
                foreach (var mark in choices.Controls.OfType<ChoiceMark>())
                    mark.Checked = ReferenceEquals(mark, radio);
                if (idleContext is not null)
                {
                    PopulateOledContext(idleContext);
                    foreach (Control control in idleContext.Controls) ScaleNewContent(control);
                }
            };
            choices.Controls.Add(radio);
        }
        options.Controls.Add(Divider());
        var context = Stack();
        idleContext = context;
        context.Dock = DockStyle.None;
        context.Height = 182;
        options.Controls.Add(context);
        PopulateOledContext(context);
        return top;
    }

    private void PopulateOledContext(FlowLayoutPanel context)
    {
        context.SuspendLayout();
        while (context.Controls.Count > 0) context.Controls[0].Dispose();
        weatherDetail = null;
        if (config.Page == OledPage.Mixer)
        {
            var separator = Choice(["Automatic (Master)", "Always separate channel 5", "Disabled"], (int)config.Separator, 214);
            separator.AccessibleName = "Mixer channel separator";
            separator.Committed += (_, _) => Persist(config with { Separator = (MixerSeparator)separator.Selection });
            context.Controls.Add(Caption("Mixer channel separator", UiMetrics.Body));
            context.Controls.Add(separator);
        }
        if (config.Page == OledPage.Clock)
        {
            context.Controls.Add(Row("Clock format", Segments(["12-hour", "24-hour"], config.TwelveHourClock ? 0 : 1,
                index => SetClock(index == 0), 168)));
        }
        if (config.Page == OledPage.Media)
        {
            context.Controls.Add(ActionButton("Choose image or GIF…", ChooseMedia, 176));
            context.Controls.Add(Caption(renderer.HasMedia ? Path.GetFileName(config.MediaPath)
                : "PNG, JPEG, BMP, or GIF", 9, muted: true, height: 30));
        }
        if (config.Page == OledPage.Weather)
        {
            var city = Entry(config.City, "City");
            var apply = ActionButton("Set city", () =>
            {
                weather = null;
                weatherMessage = null;
                nextWeather = 0;
                Persist(config with { City = city.Editor.Text.Trim() });
            }, 77);
            city.Editor.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { apply.PerformClick(); e.SuppressKeyPress = true; } };
            var cityFlow = Inline(city, apply);
            city.Width = 174;
            context.Controls.Add(Caption("City", 10));
            context.Controls.Add(cityFlow);
            weatherDetail = Caption("", 9, muted: true);
            context.Controls.Add(weatherDetail);
            var credit = ActionButton("Weather by Open-Meteo", () =>
                Process.Start(new ProcessStartInfo("https://open-meteo.com/") { UseShellExecute = true }), 180);
            context.Controls.Add(credit);
        }
        context.ResumeLayout(true);
    }

    private Control PreviewColumn(string heading, bool detailed = false)
    {
        var column = Grid(1, 5);
        column.Padding = new(0, 0, 18, 0);
        column.RowStyles.Add(new(SizeType.Absolute, 35));
        column.RowStyles.Add(new(SizeType.Absolute, detailed ? 212 : 192));
        column.RowStyles.Add(new(SizeType.Absolute, 25));
        column.RowStyles.Add(new(SizeType.Absolute, detailed ? 38 : 0));
        column.RowStyles.Add(new(SizeType.Percent, 100));
        column.Controls.Add(Caption(heading, UiMetrics.Section, bold: true), 0, 0);
        preview = new FramePreview(palette) { Dock = DockStyle.Fill, Margin = new(0), AccessibleName = "128 by 64 OLED preview" };
        column.Controls.Add(preview, 0, 1);
        oledDetail = Caption("", 9, muted: true);
        column.Controls.Add(oledDetail, 0, 2);
        if (detailed)
            column.Controls.Add(Caption("Knob overlay: 3 seconds", 10, muted: true), 0, 3);
        return column;
    }

    private Control AppSettings()
    {
        var startup = BuildStartupSetting();
        var theme = Segments(Enum.GetNames<AppTheme>(), (int)config.Theme, index =>
        {
            if (config.Theme == (AppTheme)index) return;
            Persist(config with { Theme = (AppTheme)index });
            RebuildAfterInput();
        }, 258);

        var accents = Inline();
        for (int i = 0; i < Presets.Length; i++)
        {
            string hex = Presets[i];
            var swatch = ActionButton("", () =>
            {
                Persist(config with { Accent = hex });
                RebuildAfterInput();
            }, 32, string.Equals(hex, config.Accent, StringComparison.OrdinalIgnoreCase));
            swatch.AccessibleName = PresetNames[i] + " accent";
            swatch.Swatch = ColorTranslator.FromHtml(hex);
            tips.SetToolTip(swatch, PresetNames[i]);
            accents.Controls.Add(swatch);
        }
        var custom = Entry(config.Accent, "#RRGGBB");
        custom.Width = 125;
        var sample = ActionButton("", () => { }, 32);
        sample.Swatch = ColorTranslator.FromHtml(config.Accent);
        sample.AccessibleName = "Choose custom accent color";
        sample.Cursor = Cursors.Hand;
        tips.SetToolTip(sample, "Choose custom accent color");
        var accentError = Caption("", 9, height: 23);
        accentError.ForeColor = palette.Error;

        void ApplyAccent(string hex)
        {
            custom.Editor.Text = hex;
            sample.Swatch = ColorTranslator.FromHtml(hex);
            sample.Invalidate();
            if (config.Accent == hex) return;
            Persist(config with { Accent = hex });
            RebuildAfterInput();
        }

        AccentColorPicker? picker = null;
        sample.Click += (_, _) =>
        {
            if (picker is not null) return;
            picker = new AccentColorPicker(sample, palette, config.Accent, ApplyAccent);
            picker.FormClosed += (_, _) => picker = null;
            picker.Show(this);
        };
        sample.Disposed += (_, _) => picker?.Close();

        var apply = ActionButton("Apply", () =>
        {
            var hex = custom.Editor.Text.Trim();
            if (!ThemePalette.IsHex(hex))
            {
                accentError.Text = "Enter six hexadecimal digits, for example #B78BFA.";
                custom.Editor.Focus();
                return;
            }
            ApplyAccent(hex.ToUpperInvariant());
        }, 80);
        custom.Editor.TextChanged += (_, _) => accentError.Text = "";
        custom.Editor.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { apply.PerformClick(); e.SuppressKeyPress = true; } };

        var formatting = Check("Automatically format app names", config.FormatAppNames);
        formatting.CheckedChanged += (_, _) => Persist(config with { FormatAppNames = formatting.Checked });
        var sync = Check("Windows Volume Sync", config.WindowsVolumeSync);
        var takeover = Segments(["Immediate", "Soft takeover"], config.TakeoverMode == KnobTakeover.Immediate ? 0 : 1,
            index => Persist(config with { TakeoverMode = index == 0 ? KnobTakeover.Immediate : KnobTakeover.Soft }), 256);
        var threshold = Choice(["1%", "2%", "3%", "4%", "5%"], config.KnobNoiseThreshold - 1, 86);
        threshold.Committed += (_, _) => Persist(config with { KnobNoiseThreshold = threshold.Selection + 1 });
        takeover.Enabled = threshold.Enabled = sync.Checked;
        sync.CheckedChanged += (_, _) =>
        {
            takeover.Enabled = threshold.Enabled = sync.Checked;
            Persist(config with { WindowsVolumeSync = sync.Checked });
        };
        var detect = Check("Automatic port detection", config.AutoDetect);
        detect.CheckedChanged += (_, _) =>
        {
            Persist(config with { AutoDetect = detect.Checked });
            PopulatePorts();
        };
        var preferred = Choice([], -1, 200);
        portPicker = preferred;
        PopulatePorts();
        preferred.Opening += (_, _) => PopulatePorts();
        preferred.Committed += (_, _) =>
        {
            if (preferred.SelectedItem is not PortChoice choice) return;
            Persist(config with { PreferredPort = choice.Port });
        };
        var rescan = ActionButton("Rescan", () => { link.Rescan(); PopulatePorts(); }, 80);
        return Stack(
            Caption("General", UiMetrics.Section, bold: true, height: 37), startup,
            Divider(), Caption("Appearance", UiMetrics.Section, bold: true, height: 37), Row("Theme", theme),
            Row("Accent library", accents), Row("Custom accent", Inline(custom, sample, apply)), accentError,
            Divider(), Caption("Knob mapping", UiMetrics.Section, bold: true, height: 37), formatting,
            Caption("Example: spotify.exe → Spotify", 9, muted: true),
            sync, Caption("Preserve Windows volume changes until the physical knob takes control again.", 9, muted: true),
            Row("Knob takeover mode", takeover), Row("Knob movement threshold", threshold),
            Divider(), Caption("Dynamic audio mappings", UiMetrics.Section, bold: true, height: 37),
            MappingRuleRow("Game whitelist", "These executables are recognized alongside automatically detected games.", () => config.RegisteredGames, paths => Persist(config with { RegisteredGames = paths })),
            MappingRuleRow("Game blacklist", "Exclude these applications even when automatically recognized as games.", () => config.ExcludedGames, paths => Persist(config with { ExcludedGames = paths })),
            MappingRuleRow("Focused app whitelist", "Leave empty to allow every identifiable focused application with audio.", () => config.FocusedAllow, paths => Persist(config with { FocusedAllow = paths })),
            MappingRuleRow("Focused app blacklist", "Exclude these applications even when their executable is on the whitelist.", () => config.FocusedDeny, paths => Persist(config with { FocusedDeny = paths })),
            Divider(), Caption("Device connection", UiMetrics.Section, bold: true, height: 37), detect,
            Row("Preferred port", Inline(preferred, rescan)),
            Caption("A specific port overrides automatic detection.", 9, muted: true), BuildAbout(), BuildFirmwareSection());
    }

    private Control BuildStartupSetting()
    {
        var startup = Check("Launch Voltra at Windows startup", false);
        var status = Caption("", 9, muted: true, height: 38);
        var section = Stack(startup, status);
        section.Dock = DockStyle.None;
        bool refreshing = false;

        void RefreshStartupSetting()
        {
            refreshing = true;
            var state = WindowsStartup.Read();
            startup.Checked = state.Registered ?? false;
            startup.Enabled = state.Installed;
            status.Text = startupError ?? state.Message ?? "";
            bool showStatus = status.Text.Length > 0;
            status.Visible = showStatus;
            section.Height = startup.Height + (showStatus ? status.Height : 0);
            refreshing = false;
        }

        RefreshStartupSetting();
        startup.CheckedChanged += (_, _) =>
        {
            if (refreshing) return;
            var updated = config with { LaunchAtWindowsStartup = startup.Checked };
            settingsSave.GetAwaiter().GetResult();
            startupError = WindowsStartup.Synchronize(updated.LaunchAtWindowsStartup, updated.Save);
            if (startupError is null) config = updated;
            RefreshStartupSetting();
        };
        return section;
    }

    private void RebuildAfterInput() => BeginInvoke((Action)BuildShell);

    private void PopulatePorts()
    {
        if (portPicker is not { } picker) return;
        string connected = link.State.ConnectedPort ?? "";
        string[] ports = shownPorts = link.AvailablePorts;
        if (config.PreferredPort.Length > 0 && !ports.Contains(config.PreferredPort, StringComparer.OrdinalIgnoreCase))
            ports = [.. ports, config.PreferredPort];
        List<PortChoice> choices =
        [
            new("", config.AutoDetect ? connected.Length > 0 && config.PreferredPort.Length == 0
                ? $"Auto · {connected} detected" : "Auto" : "Select a port")
        ];
        choices.AddRange(ports.Select(port => new PortChoice(port, port)));
        picker.SetOptions([.. choices], Math.Max(0, choices.FindIndex(choice =>
            choice.Port.Equals(config.PreferredPort, StringComparison.OrdinalIgnoreCase))));
        shownPort = connected;
    }

    private sealed record PortChoice(string Port, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SpecialMapping(string Id, string Title)
    {
        public override string ToString() => Title;
    }

    private void EditMappings()
    {
        using var dialog = new Form
        {
            Text = "Edit mappings", StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            ShowInTaskbar = false, BackColor = palette.Background, ForeColor = palette.Text,
            Font = Font
        };
        dialog.SuspendLayout();
        dialog.AutoScaleDimensions = new SizeF(96, 96);
        dialog.AutoScaleMode = AutoScaleMode.Dpi;
        dialog.ClientSize = new(608, 368);
        dialog.HandleCreated += (_, _) => SetTitleBar(dialog.Handle, palette.Dark);
        var grid = Grid(3, 10);
        grid.Padding = new(20, 14, 20, 14);
        grid.ColumnStyles.Add(new(SizeType.Absolute, 46));
        grid.ColumnStyles.Add(new(SizeType.Percent, 53));
        grid.ColumnStyles.Add(new(SizeType.Percent, 47));
        grid.RowStyles.Add(new(SizeType.Absolute, 49));
        grid.RowStyles.Add(new(SizeType.Absolute, 25));
        grid.RowStyles.Add(new(SizeType.Absolute, 6));
        var heading = Grid(1, 2);
        heading.RowStyles.Add(new(SizeType.Absolute, 28));
        heading.RowStyles.Add(new(SizeType.Percent, 100));
        heading.Controls.Add(Caption("Knob assignments", 15, bold: true), 0, 0);
        heading.Controls.Add(Caption("Assign an application or mapping mode to each knob.", 10, muted: true), 0, 1);
        grid.Controls.Add(heading, 0, 0);
        grid.SetColumnSpan(heading, 3);
        grid.Controls.Add(Caption("Knob", 9, bold: true, muted: true), 0, 1);
        grid.Controls.Add(Caption("Application / mode", 9, bold: true, muted: true), 1, 1);
        grid.Controls.Add(Caption("Display name", 9, bold: true, muted: true), 2, 1);
        void AddRule(int row)
        {
            var rule = Divider();
            rule.Dock = DockStyle.Fill;
            grid.Controls.Add(rule, 0, row);
            grid.SetColumnSpan(rule, 3);
        }
        AddRule(2);
        var names = new EditField[5];
        var targets = new PickerField[5];
        var specials = new SpecialMapping[] { new(Channel.SystemVolume, "System volume"),
            new(Channel.Game, "Game"), new(Channel.Focused, "Currently Focused") };
        object[] available = [.. specials, .. link.State.Apps
            .Concat(config.Channels.Select(channel => channel.Processes))
            .Where(name => name.Length > 0 && !name.StartsWith('@'))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            grid.RowStyles.Add(new(SizeType.Absolute, 40));
            grid.Controls.Add(Caption((i + 1).ToString(), 10), 0, i + 3);
            int selected = Array.FindIndex(specials, option => option.Id == config.Channels[i].Processes);
            var target = new PickerField(palette, available, selected, editable: true);
            if (selected < 0) target.SetValue(config.Channels[i].Processes);
            target.AccessibleName = $"Knob {i + 1} app";
            targets[i] = target;
            var name = Entry(config.Channels[i].Label, $"Knob {i + 1} display name");
            name.Editor.MaxLength = 18;
            name.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            target.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            target.Dock = DockStyle.None;
            target.Margin = new(0, 0, 12, 0);
            name.Margin = Padding.Empty;
            target.TabIndex = 2 * i;
            name.TabIndex = 2 * i + 1;
            names[i] = name;
            target.Committed += (_, _) =>
                names[index].Editor.Text = SuggestedName(target.Value, specials[0].Title);
            grid.Controls.Add(target, 1, i + 3);
            grid.Controls.Add(name, 2, i + 3);
        }

        grid.RowStyles.Add(new(SizeType.Absolute, 14));
        AddRule(8);
        grid.RowStyles.Add(new(SizeType.Percent, 100));
        var footer = Grid(3, 1);
        footer.TabIndex = 10;
        footer.ColumnStyles.Add(new(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new(SizeType.Absolute, 84));
        footer.ColumnStyles.Add(new(SizeType.Absolute, 122));
        var cancel = ActionButton("Cancel", () => { }, 76);
        cancel.Anchor = AnchorStyles.Right;
        cancel.Margin = new(0, 0, 8, 0);
        cancel.DialogResult = DialogResult.Cancel;
        var save = ActionButton("Save mappings", () => { }, 122, true);
        save.Anchor = AnchorStyles.Right;
        save.Margin = Padding.Empty;
        save.Primary = true;
        save.DialogResult = DialogResult.OK;
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(save, 2, 0);
        grid.Controls.Add(footer, 0, 9);
        grid.SetColumnSpan(footer, 3);
        dialog.Controls.Add(grid);
        dialog.ResumeLayout(true);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        Persist(config with
        {
            Channels = targets.Select((target, index) =>
            {
                string app = target.Value.Trim();
                string label = names[index].Editor.Text.Trim();
                if (target.SelectedItem is SpecialMapping special && app == special.Title) app = special.Id;
                else if (app.Equals(specials[0].Title, StringComparison.OrdinalIgnoreCase)) app = Channel.SystemVolume;
                return new Channel(label.Length == 0 ? $"Knob {index + 1}" : label, app);
            }).ToArray()
        });
        UpdateView();
    }

    private Control MappingRuleRow(string title, string hint, Func<string[]> read, Action<string[]> save) =>
        Row(title, ActionButton("Manage…", () => EditMappingRules(title, hint, read(), save), 96));

    private void EditMappingRules(string title, string hint, string[] saved, Action<string[]> save)
    {
        var games = new SortedSet<string>(saved, StringComparer.OrdinalIgnoreCase);
        using var dialog = new Form
        {
            Text = title, StartPosition = FormStartPosition.CenterParent,
            ClientSize = new(550, 275), MinimumSize = new(500, 245), ShowInTaskbar = false,
            BackColor = palette.Background, ForeColor = palette.Text, Font = Font, AutoScaleMode = AutoScaleMode.Dpi
        };
        dialog.HandleCreated += (_, _) => SetTitleBar(dialog.Handle, palette.Dark);
        var layout = Grid(1, 3);
        layout.Padding = new(16);
        layout.RowStyles.Add(new(SizeType.Absolute, 42));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.Controls.Add(Caption(hint, 10), 0, 0);
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = palette.Field, ForeColor = palette.Text,
            BorderStyle = BorderStyle.FixedSingle, HorizontalScrollbar = true, IntegralHeight = false,
            AccessibleName = title + " executable paths" };
        list.Items.AddRange(games.Cast<object>().ToArray());
        layout.Controls.Add(list, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new(0, 6, 0, 0) };
        void Choose(bool replace)
        {
            string? previous = replace ? list.SelectedItem as string : null;
            if (replace && previous == null) return;
            using var file = new OpenFileDialog { Filter = "Application (*.exe)|*.exe", CheckFileExists = true };
            if (file.ShowDialog(dialog) != DialogResult.OK) return;
            string path = Path.GetFullPath(file.FileName);
            if (AudioApplication.SharedHost(path))
            {
                MessageBox.Show(dialog, "This executable hosts multiple applications. Choose an application-specific executable.", title);
                return;
            }
            if (games.Contains(path) && !StringComparer.OrdinalIgnoreCase.Equals(path, previous))
            {
                MessageBox.Show(dialog, "This executable is already listed.", title);
                return;
            }
            if (previous != null) { games.Remove(previous); list.Items.Remove(previous); }
            games.Add(path);
            list.SelectedIndex = list.Items.Add(path);
        }
        actions.Controls.Add(ActionButton("Add…", () => Choose(false), 72));
        actions.Controls.Add(ActionButton("Edit…", () => Choose(true), 72));
        actions.Controls.Add(ActionButton("Remove", () =>
        {
            if (list.SelectedItem is string path) { games.Remove(path); list.Items.Remove(path); }
        }, 82));
        var cancel = ActionButton("Cancel", () => { }, 76);
        cancel.DialogResult = DialogResult.Cancel;
        var done = ActionButton("Save", () => { }, 76, true);
        done.DialogResult = DialogResult.OK;
        actions.Controls.Add(cancel);
        actions.Controls.Add(done);
        layout.Controls.Add(actions, 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = done;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) == DialogResult.OK) save([.. games]);
    }

    private string SuggestedName(string app, string master)
    {
        if (app == master) return "Master";
        if (config.FormatAppNames)
        {
            var executable = app.Split([',', ';'], 2)[0].Trim();
            app = Path.GetFileNameWithoutExtension(executable);
            if (app.Length > 0) app = char.ToUpperInvariant(app[0]) + app[1..];
        }
        return app[..Math.Min(app.Length, 18)];
    }

    private void SelectIdle(OledPage idle)
    {
        if (config.Page == idle) return;
        Persist(config with { Page = idle });

    }

    private void SetClock(bool twelveHour)
    {
        if (config.TwelveHourClock == twelveHour) return;
        Persist(config with { TwelveHourClock = twelveHour });
    }

    private PickerField IdlePicker()
    {
        var picker = Choice(PageNames, (int)config.Page);
        picker.AccessibleName = "OLED idle page";
        picker.Committed += (_, _) => SelectIdle((OledPage)picker.Selection);
        return picker;
    }

    private void ChooseMedia()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Images and GIFs|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All files|*.*",
            CheckFileExists = true
        };
        // The owned dialog stays centered; keep the main window visible while editing.
        if (dialog.ShowDialog(this) != DialogResult.OK || !TryLoadMedia(dialog.FileName, true)) return;
        Persist(config with { MediaPath = dialog.FileName, Page = OledPage.Media });
        RebuildAfterInput();
    }

    private bool TryLoadMedia(string path, bool showError)
    {
        try
        {
            renderer.LoadMedia(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or
            ExternalException or OutOfMemoryException or NotSupportedException)
        {
            if (showError) MessageBox.Show(this, "Could not open the image or GIF. " + ex.Message, Text);
            return false;
        }
    }

    private async Task FetchWeather(string city)
    {
        fetchingWeather = true;
        nextWeather = Environment.TickCount64 + 900_000;
        try
        {
            var result = await weatherService.ReadAsync(city, lifetime.Token);
            if (!exiting && config.City == city)
            {
                weather = result;
                weatherMessage = null;
            }
        }
        catch (OperationCanceledException) when (exiting) { }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or
            System.Text.Json.JsonException or KeyNotFoundException or OperationCanceledException)
        {
            if (!exiting && config.City == city)
            {
                weatherMessage = ex is InvalidOperationException ? ex.Message : "Weather update failed.";
                nextWeather = Environment.TickCount64 + 60_000;
            }
        }
        finally { fetchingWeather = false; }
    }

    private async void Persist(MixerSettings updated)
    {
        config = updated;
        nextFrame = 0;
        link.Configure(updated);
        Task previous = settingsSave;
        var save = Task.Run(async () =>
        {
            await previous;
            try { updated.Save(); return null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex; }
        });
        settingsSave = save;
        UpdateView();
        var error = await save;
        if (error is not null && !exiting && ReferenceEquals(config, updated))
            MessageBox.Show(this, "Settings are active for this session. Saving failed. " + error.Message, Text);
    }

    private void UpdateView()
    {
        if (exiting) return;
        var state = link.State;
        ObserveFirmware(state);
        long now = Environment.TickCount64;
        if (config.Page == OledPage.Weather && config.City.Length > 0 && !fetchingWeather && now >= nextWeather)
            _ = FetchWeather(config.City);

        if (idleFrame is null || now >= nextFrame)
        {
            idleFrame = renderer.Render(config, state, weather);
            link.SetFrame(idleFrame, config, config.Page == OledPage.Media ? renderer.MediaId : 0);
            nextFrame = now + 170;
        }
        byte[] idle = idleFrame;
        if (!Visible) return;

        bool connected = state.ConnectedPort is not null;
        if (connection is not null)
        {
            (string label, Color color) = state.Connection switch
            {
                ConnectionState.Connected => ("●  Connected", palette.Online),
                ConnectionState.Connecting => ("●  Connecting", palette.Pending),
                ConnectionState.Error => ("●  Connection error", palette.Error),
                _ => ("○  Disconnected", palette.Muted)
            };
            if (connection.Text != label) connection.Text = label;
            if (connection.ForeColor != color) connection.ForeColor = color;
        }
        if (connectionDetail is not null)
        {
            string detail = connected ? "" : state.Status;
            if (connectionDetail.Text != detail)
            {
                connectionDetail.Text = detail;
                tips.SetToolTip(connectionDetail, detail);
            }
        }
        if (oledDetail is not null)
            oledDetail.Text = state.DisplayStatus == "OLED ready"
                ? "128 × 64 · Monochrome"
                : $"128 × 64 · {state.DisplayStatus}";
        if (weatherDetail is not null)
            weatherDetail.Text = weatherMessage ?? (weather is null ? "" : $"{weather.City} · {weather.Temperature:0} °C");
        if (page == 2)
        {
            if (portPicker is { IsOpen: false } && (shownPort != (state.ConnectedPort ?? "") ||
                !ReferenceEquals(shownPorts, link.AvailablePorts)))
                PopulatePorts();
        }

        int active = connected && state.MovedChannel is >= 0 and < 5 && now - state.LastMovement < 3000
            ? state.MovedChannel : -1;
        preview?.ShowFrame(idle, active < 0 ? null : state.TargetNames?[active] ?? config.Channels[active].Label,
            active < 0 ? 0 : state.Levels[active]);
        for (int i = 0; i < meters.Length; i++)
            meters[i].Set(config.Channels[i].Label, state.WindowsLevels?[i], active == i,
                connected && state.WaitingForKnob?[i] == true ? state.Levels[i] : null,
                config.Channels[i].Dynamic ? state.TargetNames?[i] ?? config.Channels[i].Inactive : null);
    }

    private void SetupTray()
    {
        tray.Text = Text;
        tray.Icon = Icon;
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => RestoreAndActivateMainWindow();
        tray.Visible = true;
    }

    private void RestoreAndActivateMainWindow()
    {
        if (exiting || IsDisposed || Disposing) return;
        if (WindowState == FormWindowState.Minimized) WindowState = previousWindowState;
        if (!Visible) Show();

        // Keep a mapping editor, update prompt or native file dialog in front
        // of its disabled owner when the user relaunches from a shortcut.
        nint target = IsWindowEnabled(Handle) ? Handle : GetLastActivePopup(Handle);
        if (target == 0 || !IsWindowVisible(target)) target = Handle;
        if (GetForegroundWindow() == target || SetForegroundWindow(target)) return;
        var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = Handle, Flags = 2, Count = 3 };
        FlashWindowEx(ref flash);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        internal uint Size;
        internal nint Window;
        internal uint Flags, Count, Timeout;
    }

    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FlashInfo info);

    private void ExitFromTray()
    {
        if (handingOffUpdate && PendingInstaller is null) return;
        exiting = true;
        Close();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide();
        else previousWindowState = WindowState;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing &&
            (firmwareFlashing || (handingOffUpdate && PendingInstaller is null)))
        {
            exiting = false;
            e.Cancel = true;
            return;
        }
        if (!exiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            exiting = true;
            activation.Stop();
            refresh.Stop();
            lifetime.Cancel();
            tray.Visible = false;
        }
        base.OnFormClosing(e);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
        refresh.Dispose();
        navigationMotion?.Dispose();
        if (settingsBody?.Parent is null) settingsBody?.Dispose();
        tips.Dispose();
        link.Dispose();
        settingsSave.GetAwaiter().GetResult();
        renderer.Dispose();
        weatherService.Dispose();
        updateTimer.Dispose();
        updates.Dispose();
        lifetime.Dispose();
        tray.Dispose();
        trayMenu.Dispose();
        Icon = null;
        brandIcon.Dispose();
        base.OnFormClosed(e);
    }

    private void SystemThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (IsHandleCreated && !IsDisposed && !exiting)
            BeginInvoke((Action)(() =>
            {
                if (!exiting && config.Theme == AppTheme.System && ThemePalette.From(config) != palette)
                    BuildShell();
            }));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateTitleBar();
    }

    private void UpdateTitleBar()
    {
        if (IsHandleCreated) SetTitleBar(Handle, palette.Dark);
    }

    private static void SetTitleBar(IntPtr window, bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18985)) return;
        int value = dark ? 1 : 0;
        DwmSetWindowAttribute(window, 20, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    private static TableLayoutPanel Grid(int columns, int rows) => new()
    {
        Dock = DockStyle.Fill, ColumnCount = columns, RowCount = rows, Margin = new(0)
    };

    private TableLayoutPanel Split()
    {
        var grid = Grid(3, 1);
        grid.ColumnStyles.Add(new(SizeType.Percent, 49));
        grid.ColumnStyles.Add(new(SizeType.Absolute, 1));
        grid.ColumnStyles.Add(new(SizeType.Percent, 51));
        grid.RowStyles.Add(new(SizeType.Percent, 100));
        grid.Controls.Add(new Panel { BackColor = palette.Line, Dock = DockStyle.Fill, Margin = new(0, 4, 0, 9) }, 1, 0);
        return grid;
    }

    private static FlowLayoutPanel Stack(params Control[] children)
    {
        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new(0)
        };
        void SizeRows()
        {
            int width = layout.ClientSize.Width - layout.Padding.Horizontal - 4;
            if (width <= 0) return;
            foreach (Control child in layout.Controls)
                if (child is Panel or Label)
                    child.Width = width;
        }
        layout.Controls.AddRange(children);
        layout.SizeChanged += (_, _) => SizeRows();
        layout.PaddingChanged += (_, _) => SizeRows();
        layout.ControlAdded += (_, _) => SizeRows();
        SizeRows();
        return layout;
    }

    private static FlowLayoutPanel Inline(params Control[] children)
    {
        var row = new FlowLayoutPanel { Height = 39, Width = 320, WrapContents = false, Margin = new(0) };
        row.Controls.AddRange(children);
        return row;
    }

    private Control Row(string text, Control control)
    {
        var row = Grid(2, 1);
        row.Dock = DockStyle.None;
        row.Height = 43;
        row.RowStyles.Add(new(SizeType.Percent, 100));
        row.ColumnStyles.Add(new(SizeType.Absolute, buildingSettings ? 188 : 108));
        row.ColumnStyles.Add(new(SizeType.Percent, 100));
        var label = Caption(text, UiMetrics.Body);
        control.Anchor = AnchorStyles.Left;
        if (control is FlowLayoutPanel) control.Anchor |= AnchorStyles.Right;
        if (control.AccessibleName is null) control.AccessibleName = text;
        row.Controls.Add(label, 0, 0);
        row.Controls.Add(control, 1, 0);
        return row;
    }

    private Panel Divider()
    {
        var rule = new Panel { Height = 19, Margin = new(0) };
        rule.Paint += (_, e) =>
        {
            using var pen = new Pen(palette.Line);
            e.Graphics.DrawLine(pen, 0, rule.Height / 2, rule.Width, rule.Height / 2);
        };
        return rule;
    }

    private Label Caption(string text, float size, bool bold = false, bool muted = false, int height = 28) => new()
    {
        Text = text, Dock = DockStyle.Fill, Height = height, Width = 280, Margin = new(0),
        AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, ForeColor = muted ? palette.Muted : palette.Text,
        Font = new Font(bold ? "Segoe UI Semibold" : "Segoe UI", size)
    };

    private MixerButton ActionButton(string text, Action action, int width = 134, bool selected = false)
    {
        var button = new MixerButton(palette, selected)
        {
            Text = text, Size = new(width, UiMetrics.FieldHeight), Margin = new(0, 2, 6, 2),
            FlatStyle = FlatStyle.Flat, BackColor = selected ? palette.Selected : palette.Field,
            ForeColor = palette.Text, UseVisualStyleBackColor = false
        };
        button.Click += (_, _) => action();
        return button;
    }

    private Control Segments(string[] labels, int selected, Action<int> changed, int width)
    {
        var group = new Panel
        {
            Size = new(width, UiMetrics.FieldHeight), Padding = new(3), Margin = new(0, 2, 0, 2),
            BackColor = palette.Field, AccessibleRole = AccessibleRole.Grouping
        };
        group.Paint += (_, e) => FieldChrome.Draw(e.Graphics, group.ClientRectangle, palette.Background,
            palette.Field, palette.Line);
        var row = Grid(labels.Length, 1);
        row.BackColor = palette.Field;
        var buttons = new MixerButton[labels.Length];
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            row.ColumnStyles.Add(new(SizeType.Percent, 100f / labels.Length));
            var button = ActionButton(labels[i], () =>
            {
                for (int k = 0; k < buttons.Length; k++) buttons[k].SetSelected(k == index);
                changed(index);
            }, selected: i == selected);
            button.BackColor = palette.Field;
            button.Segment = true;
            button.Dock = DockStyle.Fill;
            button.Margin = new(0);
            button.PreviewKeyDown += (_, e) =>
            {
                if (e.KeyCode is Keys.Left or Keys.Right) e.IsInputKey = true;
            };
            button.KeyDown += (_, e) =>
            {
                int direction = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
                if (direction == 0) return;
                int next = (index + direction + buttons.Length) % buttons.Length;
                buttons[next].Focus();
                buttons[next].PerformClick();
                e.SuppressKeyPress = true;
            };
            buttons[i] = button;
            row.Controls.Add(button, i, 0);
        }
        group.Controls.Add(row);
        return group;
    }

    private PickerField Choice(string[] options, int index, int width = 158) =>
        new(palette, options, index) { Width = width };

    private EditField Entry(string text, string label) => new(palette, text, label);

    private ChoiceMark Check(string text, bool value) => new(palette, text, value);
}
