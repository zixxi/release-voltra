using System.Drawing.Drawing2D;

namespace Voltra;

internal sealed class AccentColorPicker : Form, IMessageFilter
{
    private const int PopupWidth = 320;
    private const int PopupHeight = 326;
    private readonly Control anchor;
    private readonly ThemePalette theme;
    private readonly float scale;
    private readonly EditField hex;
    private readonly ColorTrack plane, hue;
    private readonly ColorSample preview;
    private readonly Label error;
    private readonly MixerButton apply;
    private readonly Font bodyFont, titleFont;
    private AccentColor selection;
    private bool settingHex, closing;

    internal AccentColorPicker(Control anchor, ThemePalette theme, string initialHex, Action<string> commit)
    {
        this.anchor = anchor;
        this.theme = theme;
        Rectangle anchorBounds = anchor.RectangleToScreen(anchor.ClientRectangle);
        Rectangle workArea = Screen.FromRectangle(anchorBounds).WorkingArea;
        scale = Math.Min(anchor.DeviceDpi / 96f,
            Math.Min(workArea.Width / (float)PopupWidth, workArea.Height / (float)PopupHeight));
        selection = AccentColor.FromRgb(ColorTranslator.FromHtml(initialHex));

        Text = "Custom accent";
        AccessibleName = Text;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = theme.Background;
        ForeColor = theme.Text;
        bodyFont = new Font("Segoe UI", 14 * scale, GraphicsUnit.Pixel);
        titleFont = new Font("Segoe UI Semibold", 16 * scale, GraphicsUnit.Pixel);
        Font = bodyFont;
        ClientSize = new(Px(PopupWidth), Px(PopupHeight));
        Location = PopupLocation(anchorBounds, workArea, Size, Px(6));
        using var shape = FieldChrome.Rounded(ClientRectangle, Px(12));
        Region = new Region(shape);

        var title = new Label { Text = Text, Font = titleFont, TextAlign = ContentAlignment.MiddleLeft };
        plane = new ColorTrack(this, hueOnly: false);
        hue = new ColorTrack(this, hueOnly: true);
        var hexLabel = new Label { Text = "HEX", ForeColor = theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
        hex = new EditField(theme, selection.ToHex(), "Accent HEX");
        hex.Editor.MaxLength = 32;
        hex.Editor.TextChanged += (_, _) => HexChanged();
        preview = new ColorSample(theme, selection.ToRgb(), Px(6));
        error = new Label { ForeColor = theme.Error, TextAlign = ContentAlignment.MiddleLeft };
        var cancel = CreateButton("Cancel");
        apply = CreateButton("Apply");
        apply.Primary = true;
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) =>
        {
            if (!ThemePalette.IsHex(hex.Editor.Text.Trim())) return;
            string value = selection.ToHex();
            Close();
            commit(value);
        };
        AcceptButton = apply;
        CancelButton = cancel;

        Place(title, 14, 10, 292, 24);
        Place(plane, 14, 40, 292, 142);
        Place(hue, 14, 189, 292, 26);
        Place(hexLabel, 14, 222, 36, 34);
        Place(hex, 51, 222, 132, 34);
        Place(preview, 193, 222, 113, 34);
        Place(error, 14, 258, 292, 20);
        Place(cancel, 131, 279, 82, 33);
        Place(apply, 221, 279, 85, 33);
        Shown += (_, _) => Application.AddMessageFilter(this);
    }

    internal static Point PopupLocation(Rectangle anchor, Rectangle workArea, Size size, int gap)
    {
        int left = Math.Clamp(anchor.Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - size.Width));
        int top = anchor.Bottom + gap;
        if (top + size.Height > workArea.Bottom) top = anchor.Top - gap - size.Height;
        return new(left, Math.Clamp(top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size.Height)));
    }

    private int Px(int value) => Math.Max(1, (int)Math.Round(value * scale));

    private void Place(Control control, int left, int top, int width, int height)
    {
        control.Bounds = new(Px(left), Px(top), Px(width), Px(height));
        Controls.Add(control);
    }

    private MixerButton CreateButton(string text) => new(theme, false)
    {
        Text = text, BackColor = theme.Field, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false
    };

    private void HexChanged()
    {
        if (settingHex) return;
        string value = hex.Editor.Text.Trim();
        bool valid = ThemePalette.IsHex(value);
        SetHexValidity(valid);
        if (valid) UpdateSelection(AccentColor.FromRgb(ColorTranslator.FromHtml(value), selection.Hue));
    }

    private void SelectColor(AccentColor color)
    {
        UpdateSelection(color);
        string value = color.ToHex();
        if (hex.Editor.Text != value)
        {
            settingHex = true;
            hex.Editor.Text = value;
            settingHex = false;
        }
        SetHexValidity(true);
    }

    private void SetHexValidity(bool valid)
    {
        if (apply.Enabled != valid) apply.Enabled = valid;
        string message = valid ? "" : "Enter a color in #RRGGBB format.";
        if (error.Text != message) error.Text = message;
    }

    private void UpdateSelection(AccentColor color)
    {
        if (selection == color) return;
        var previous = selection;
        selection = color;
        plane.SelectionChanged(previous, color);
        hue.SelectionChanged(previous, color);
        preview.SetColor(color.ToRgb());
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = FieldChrome.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), Px(12));
        using var pen = new Pen(theme.Line);
        e.Graphics.DrawPath(pen, outline);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (Visible && !closing) Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        closing = true;
        base.OnFormClosing(e);
        closing = !e.Cancel;
    }

    public bool PreFilterMessage(ref Message message)
    {
        // Do not reopen the picker when the user dismisses it by clicking its trigger.
        if (closing || message.Msg is not (0x201 or 0x204 or 0x207 or 0x20B or 0xA1 or 0xA4 or 0xA7 or 0xAB))
            return false;
        Point cursor = Cursor.Position;
        if (Bounds.Contains(cursor)) return false;
        bool clickedAnchor = anchor.RectangleToScreen(anchor.ClientRectangle).Contains(cursor);
        Close();
        return clickedAnchor;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Application.RemoveMessageFilter(this);
        base.Dispose(disposing);
        if (disposing)
        {
            bodyFont.Dispose();
            titleFont.Dispose();
        }
    }

    private sealed class ColorSample : Control
    {
        private readonly Color background;
        private readonly int radius;
        private readonly SolidBrush fill;
        private readonly Pen border;
        private GraphicsPath? outline;

        internal ColorSample(ThemePalette theme, Color initial, int radius)
        {
            background = theme.Background;
            this.radius = radius;
            fill = new SolidBrush(initial);
            border = new Pen(theme.Line);
            AccessibleName = "Color preview";
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque, true);
        }

        internal void SetColor(Color color)
        {
            if (fill.Color == color) return;
            fill.Color = color;
            Invalidate();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            outline?.Dispose();
            outline = null;
            base.OnSizeChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(background);
            if (Width < 3 || Height < 3) return;
            outline ??= FieldChrome.Rounded(new Rectangle(0, 0, Width - 1, Height - 1), radius);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(fill, outline);
            e.Graphics.DrawPath(border, outline);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                outline?.Dispose();
                fill.Dispose();
                border.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    private sealed class ColorTrack : Control
    {
        private static readonly Color[] Spectrum =
            [Color.Red, Color.Yellow, Color.Lime, Color.Cyan, Color.Blue, Color.Magenta, Color.Red];
        private static readonly float[] SpectrumStops = [0, 1f / 6, 2f / 6, 0.5f, 4f / 6, 5f / 6, 1];
        private readonly AccentColorPicker picker;
        private readonly bool hueOnly;
        private readonly Pen border, ring, edge;
        private readonly SolidBrush markerFill = new(Color.Black);
        private GraphicsPath? outline;
        private Bitmap? gradient;
        private Color cachedHue;

        internal ColorTrack(AccentColorPicker picker, bool hueOnly)
        {
            this.picker = picker;
            this.hueOnly = hueOnly;
            border = new Pen(picker.theme.Line);
            ring = new Pen(Color.White, picker.Px(2));
            edge = new Pen(Color.FromArgb(195, Color.Black), picker.Px(1));
            AccessibleName = hueOnly ? "Hue" : "Saturation and brightness";
            AccessibleRole = AccessibleRole.Slider;
            DoubleBuffered = true;
            ResizeRedraw = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.Opaque, true);
            TabStop = true;
            Cursor = hueOnly ? Cursors.Hand : Cursors.Cross;
        }

        private Rectangle Track => hueOnly
            ? new(picker.Px(7), picker.Px(6), Width - 2 * picker.Px(7), Height - 2 * picker.Px(6))
            : Rectangle.Inflate(ClientRectangle, -1, -1);

        private static Color PureHue(AccentColor color) => new AccentColor(color.Hue, 1, 1).ToRgb();

        internal void SelectionChanged(AccentColor previous, AccentColor current)
        {
            if (hueOnly)
            {
                if (previous.Hue == current.Hue) return;
            }
            else
            {
                if (previous.Hue != current.Hue && PureHue(previous) != PureHue(current))
                {
                    Invalidate();
                    return;
                }
                if (previous.Saturation == current.Saturation && previous.Brightness == current.Brightness) return;
            }

            var dirty = Rectangle.Ceiling(RectangleF.Union(Marker(Track, previous), Marker(Track, current)));
            int inset = picker.Px(4);
            dirty.Inflate(inset, inset);
            Invalidate(dirty);
        }

        private void EnsureGradient(Rectangle bounds, Color pureHue)
        {
            if (gradient is not null && cachedHue == pureHue) return;
            gradient ??= new Bitmap(Width, Height);
            outline ??= FieldChrome.Rounded(bounds, picker.Px(hueOnly ? 7 : 8));
            using var graphics = Graphics.FromImage(gradient);
            graphics.Clear(picker.theme.Background);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var saturation = new LinearGradientBrush(bounds, Color.White, pureHue, LinearGradientMode.Horizontal);
            if (hueOnly)
                saturation.InterpolationColors = new ColorBlend { Colors = Spectrum, Positions = SpectrumStops };
            graphics.FillPath(saturation, outline);
            if (!hueOnly)
            {
                using var shade = new LinearGradientBrush(bounds, Color.FromArgb(0, Color.Black),
                    Color.Black, LinearGradientMode.Vertical);
                graphics.FillPath(shade, outline);
            }
            cachedHue = pureHue;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle bounds = Track;
            var graphics = e.Graphics;
            if (bounds.Width < 2 || bounds.Height < 2)
            {
                graphics.Clear(picker.theme.Background);
                return;
            }

            var color = picker.selection;
            Color pureHue = PureHue(color);
            EnsureGradient(bounds, hueOnly ? Color.Red : pureHue);
            graphics.DrawImageUnscaled(gradient!, 0, 0);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            border.Color = Focused && ShowFocusCues ? picker.theme.Accent : picker.theme.Line;
            graphics.DrawPath(border, outline!);
            var marker = Marker(bounds, color);
            markerFill.Color = hueOnly ? pureHue : color.ToRgb();
            graphics.FillEllipse(markerFill, marker);
            graphics.DrawEllipse(ring, marker);
            graphics.DrawEllipse(edge, RectangleF.Inflate(marker, picker.Px(2), picker.Px(2)));
        }

        private RectangleF Marker(Rectangle bounds, AccentColor color)
        {
            float x = bounds.Left + (float)(hueOnly ? color.Hue : color.Saturation) * (bounds.Width - 1);
            float y = hueOnly ? bounds.Top + bounds.Height / 2f
                : bounds.Top + (float)(1 - color.Brightness) * (bounds.Height - 1);
            int radius = picker.Px(6);
            x = Math.Clamp(x, radius + 1, Math.Max(radius + 1, Width - radius - 2));
            y = Math.Clamp(y, radius + 1, Math.Max(radius + 1, Height - radius - 2));
            return new RectangleF(x - radius, y - radius, radius * 2, radius * 2);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            Capture = true;
            Pick(e.Location);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Capture && e.Button == MouseButtons.Left) Pick(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !Capture) return;
            Pick(e.Location);
            Capture = false;
        }

        private void Pick(Point point)
        {
            Rectangle bounds = Track;
            double x = Math.Clamp((point.X - bounds.Left) / (double)Math.Max(1, bounds.Width - 1), 0, 1);
            double y = Math.Clamp((point.Y - bounds.Top) / (double)Math.Max(1, bounds.Height - 1), 0, 1);
            var color = picker.selection;
            picker.SelectColor(hueOnly ? color with { Hue = x } : color with { Saturation = x, Brightness = 1 - y });
        }

        protected override bool IsInputKey(Keys keyData) =>
            (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            double step = e.Shift ? 0.05 : 0.01;
            double horizontal = e.KeyCode == Keys.Right ? step : e.KeyCode == Keys.Left ? -step : 0;
            double vertical = e.KeyCode == Keys.Up ? step : e.KeyCode == Keys.Down ? -step : 0;
            if (horizontal == 0 && vertical == 0) return;
            var color = picker.selection;
            picker.SelectColor(hueOnly
                ? color with { Hue = Math.Clamp(color.Hue + horizontal + vertical, 0, 1) }
                : color with
                {
                    Saturation = Math.Clamp(color.Saturation + horizontal, 0, 1),
                    Brightness = Math.Clamp(color.Brightness + vertical, 0, 1)
                });
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        protected override void OnSizeChanged(EventArgs e)
        {
            ReleaseGradient();
            base.OnSizeChanged(e);
        }

        private void ReleaseGradient()
        {
            gradient?.Dispose();
            gradient = null;
            outline?.Dispose();
            outline = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ReleaseGradient();
                border.Dispose();
                ring.Dispose();
                edge.Dispose();
                markerFill.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
