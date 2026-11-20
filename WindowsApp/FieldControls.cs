using System.Drawing.Drawing2D;

namespace Voltra;

internal sealed class PickerField : UserControl
{
    private readonly ThemePalette theme;
    private readonly Button arrow;
    private readonly TextBox? editor;
    private readonly ContextMenuStrip menu;
    private object[] options;
    private int selectedIndex;

    internal event EventHandler? Opening;
    internal event EventHandler? Committed;
    internal bool IsOpen => menu.Visible;
    internal int Selection => selectedIndex;
    internal object? SelectedItem => selectedIndex >= 0 ? options[selectedIndex] : null;
    internal string Value => editor?.Text ?? SelectedItem?.ToString() ?? "";

    internal PickerField(ThemePalette theme, object[] options, int index, bool editable = false)
    {
        this.theme = theme;
        this.options = options;
        selectedIndex = index;
        Height = UiMetrics.FieldHeight;
        Margin = new(0, 0, 8, 0);
        BackColor = theme.Field;
        ForeColor = theme.Text;
        AccessibleRole = AccessibleRole.ComboBox;
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = false;
        arrow = new Button
        {
            FlatStyle = FlatStyle.Flat, TabStop = !editable, AccessibleName = "Open choices",
            Margin = new(0), BackColor = theme.Field, ForeColor = theme.Text,
            UseVisualStyleBackColor = false
        };
        arrow.FlatAppearance.BorderSize = 0;
        arrow.FlatAppearance.MouseOverBackColor = theme.Hover;
        arrow.Paint += PaintButton;
        arrow.Click += (_, _) => OpenMenu();
        arrow.Enter += (_, _) => Invalidate(true);
        arrow.Leave += (_, _) => Invalidate(true);
        arrow.KeyDown += OpenWithKey;
        Controls.Add(arrow);
        if (editable)
        {
            editor = new TextBox
            {
                BackColor = theme.Field, ForeColor = theme.Text, BorderStyle = BorderStyle.None,
                Margin = new(0), Text = SelectedItem?.ToString() ?? ""
            };
            editor.Enter += (_, _) => Invalidate();
            editor.Leave += (_, _) => Invalidate();
            editor.KeyDown += OpenWithKey;
            Controls.Add(editor);
        }
        menu = new ContextMenuStrip
        {
            ShowImageMargin = false, BackColor = theme.Field, ForeColor = theme.Text,
            Renderer = new ToolStripProfessionalRenderer(new MenuColors(theme))
        };
        menu.Closed += (_, _) => Invalidate(true);
        SyncLabel();
    }

    internal void SetOptions(object[] values, int index)
    {
        options = values;
        selectedIndex = index;
        SyncLabel();
        Invalidate(true);
    }

    internal void SetValue(string value)
    {
        if (editor is not null) editor.Text = value;
    }

    private void SyncLabel()
    {
        arrow.AccessibleName = editor is null ? $"Select {AccessibleName ?? "option"}, {Value}" : "Open app list";
    }

    private void OpenWithKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode != Keys.F4 && !(e.Alt && e.KeyCode == Keys.Down)) return;
        e.SuppressKeyPress = true;
        OpenMenu();
    }

    private void OpenMenu()
    {
        Opening?.Invoke(this, EventArgs.Empty);
        if (!Enabled) return;
        while (menu.Items.Count > 0)
        {
            var item = menu.Items[0];
            menu.Items.RemoveAt(0);
            item.Dispose();
        }
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(options[i].ToString()?.Replace("&", "&&"))
            {
                ForeColor = i == selectedIndex ? theme.Accent : theme.Text,
                AutoSize = false, Width = Math.Max(Width - 4, 138), Height = 27
            };
            item.Click += (_, _) =>
            {
                selectedIndex = index;
                if (editor is not null) editor.Text = SelectedItem?.ToString() ?? "";
                SyncLabel();
                Invalidate(true);
                Committed?.Invoke(this, EventArgs.Empty);
            };
            menu.Items.Add(item);
        }
        if (menu.Items.Count == 0) return;
        menu.Show(this, new Point(0, Height + 2));
        if (selectedIndex >= 0) menu.Items[selectedIndex].Select();
        Invalidate();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (arrow is null) return;
        int buttonWidth = editor is null ? Math.Max(0, Width - 6) : 29;
        arrow.Bounds = new(Width - buttonWidth - 3, 3, buttonWidth, Math.Max(0, Height - 6));
        if (editor is not null)
            editor.Bounds = new(9, Math.Max(1, (Height - editor.PreferredHeight) / 2),
                Math.Max(0, Width - 43), editor.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        FieldChrome.Draw(e.Graphics, ClientRectangle, theme.Background, theme.Field,
            ContainsFocus || IsOpen ? theme.Accent : theme.Line);

    }

    private void PaintButton(object? sender, PaintEventArgs e)
    {
        bool hover = arrow.ClientRectangle.Contains(arrow.PointToClient(MousePosition));
        e.Graphics.Clear(hover ? theme.Hover : theme.Field);
        if (editor is null)
            TextRenderer.DrawText(e.Graphics, Value, Font, new Rectangle(10, 0, Math.Max(0, arrow.Width - 36), arrow.Height),
                Enabled ? theme.Text : theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        int x = arrow.Width - 15, y = arrow.Height / 2;
        using var pen = new Pen(theme.Muted, 1.35f);
        e.Graphics.DrawLines(pen, new Point[] { new(x - 4, y - 2), new(x, y + 2), new(x + 4, y - 2) });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) menu?.Dispose();
        base.Dispose(disposing);
    }

    private sealed class MenuColors(ThemePalette theme) : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => theme.Field;
        public override Color MenuBorder => theme.Line;
        public override Color MenuItemBorder => theme.Line;
        public override Color MenuItemSelected => theme.Selected;
    }
}

internal sealed class ChoiceMark : CheckBox
{
    private readonly ThemePalette theme;
    private readonly bool radio;
    private readonly UiMotion? selection;
    private bool hover, pressed;

    internal ChoiceMark(ThemePalette theme, string label, bool selected, bool radio = false)
    {
        this.theme = theme;
        this.radio = radio;
        Text = label;
        Checked = selected;
        AutoCheck = !radio;
        AutoSize = false;
        Size = new(355, radio ? 34 : 43);
        Margin = new(0);
        Cursor = Cursors.Hand;
        selection = new(this, radio ? 145 : 185, Invalidate);
        selection.Move(selected ? 1 : 0, immediate: true);
        AccessibleRole = radio ? AccessibleRole.RadioButton : AccessibleRole.CheckButton;
        DoubleBuffered = true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Enabled && pressed ? theme.Pressed : Enabled && hover ? theme.Hover : theme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        int y = Height / 2;
        float scale = DeviceDpi / 96f;
        int Px(int logical) => (int)Math.Round(logical * scale);
        using var border = new Pen(Checked ? theme.Accent : theme.Muted, 1.3f);
        using var accent = new SolidBrush(theme.Accent);
        if (radio)
        {
            g.DrawEllipse(border, Px(2), y - Px(8), Px(16), Px(16));
            float size = Px(8) * (selection?.Value ?? 0);
            if (size > 0.1f) g.FillEllipse(accent, Px(10) - size / 2, y - size / 2, size, size);
        }
        else
        {
            float position = selection?.Value ?? 0;
            int width = Px(UiMetrics.SwitchWidth), height = Px(UiMetrics.SwitchHeight), thumbSize = Px(UiMetrics.SwitchThumb);
            int offset = (height - thumbSize) / 2;
            Color off = Enabled ? theme.Line : theme.Field;
            using var track = new SolidBrush(Enabled ? FieldChrome.Blend(off, theme.Accent, position) : off);
            using var pill = FieldChrome.Rounded(new Rectangle(Px(3), y - height / 2, width, height), height / 2 + 1);
            g.FillPath(track, pill);
            using var thumb = new SolidBrush(Enabled ? FieldChrome.Blend(theme.Text, theme.AccentForeground, position) : theme.Muted);
            g.FillEllipse(thumb, Px(3) + offset + (width - thumbSize - 2 * offset) * position,
                y - height / 2 + offset, thumbSize, thumbSize);
        }

        int left = Px(radio ? 28 : 63);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(left, 0, Math.Max(0, Width - left), Height), Enabled ? theme.Text : theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues)
        {
            using var focus = FieldChrome.Rounded(new Rectangle(0, 1, Width - 1, Height - 3), 5);
            g.DrawPath(border, focus);
        }
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        selection?.Move(Checked ? 1 : 0);
        base.OnCheckedChanged(e);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void Dispose(bool disposing)
    {
        if (disposing) selection?.Dispose();
        base.Dispose(disposing);
    }
}

internal static class FieldChrome
{
    internal static GraphicsPath Rounded(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        int size = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.Left, bounds.Top, size, size, 180, 90);
        path.AddArc(bounds.Right - size, bounds.Top, size, size, 270, 90);
        path.AddArc(bounds.Right - size, bounds.Bottom - size, size, size, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - size, size, size, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void Draw(Graphics g, Rectangle bounds, Color background, Color fill, Color? outline, int radius = UiMetrics.Radius)
    {
        g.Clear(background);
        if (bounds.Width < 3 || bounds.Height < 3) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1), radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
        if (outline is Color color)
        {
            using var pen = new Pen(color);
            g.DrawPath(pen, path);
        }
    }

    internal static Color Blend(Color start, Color end, float amount) => Color.FromArgb(
        (int)(start.R + (end.R - start.R) * amount),
        (int)(start.G + (end.G - start.G) * amount),
        (int)(start.B + (end.B - start.B) * amount));
}

internal sealed class MixerButton : Button
{
    private ThemePalette theme;
    private readonly UiMotion feedback;
    private readonly UiMotion selection;
    internal bool Navigation;
    internal bool Segment;
    internal bool Primary;
    internal Color? Swatch;
    private bool hover;
    private bool pressed;
    private bool selected;

    internal MixerButton(ThemePalette theme, bool selected)
    {
        this.theme = theme;
        this.selected = selected;
        feedback = new(this, 130, Invalidate);
        selection = new(this, 180, Invalidate);
        selection.Move(selected ? 1 : 0, immediate: true);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    internal void SetSelected(bool value)
    {
        selected = value;
        selection.Move(value ? 1 : 0);
        Invalidate();
    }

    internal void ApplyTheme(ThemePalette value)
    {
        theme = value;
        BackColor = value.Field;
        ForeColor = value.Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        bool focused = Focused && ShowFocusCues;
        Color background = Navigation ? theme.Sidebar : theme.Background;
        Color resting = Primary ? theme.Accent : FieldChrome.Blend(BackColor, theme.Selected, selection.Value);
        Color interactive = Primary ? FieldChrome.Blend(theme.Accent, theme.AccentForeground, pressed ? 0.22f : 0.12f)
            : pressed ? theme.Pressed : theme.Hover;
        Color fill = Enabled ? FieldChrome.Blend(resting, interactive, feedback.Value) : theme.Background;
        Color? outline = focused || (selected && !Navigation && !Segment) ? theme.Accent
            : Navigation || Segment || Primary ? null : theme.Line;
        FieldChrome.Draw(e.Graphics, ClientRectangle, Segment ? theme.Field : background, fill, outline, Segment ? 4 : UiMetrics.Radius);
        if (Swatch is Color color && Width > 12 && Height > 12)
        {
            using var brush = new SolidBrush(color);
            using var path = FieldChrome.Rounded(new Rectangle(5, 5, Width - 11, Height - 11), 4);
            e.Graphics.FillPath(brush, path);
        }

        var bounds = new Rectangle(Padding.Left, 0, Math.Max(0, Width - Padding.Horizontal), Height);
        Color foreground = Primary ? theme.AccentForeground : Navigation
            ? FieldChrome.Blend(theme.Muted, theme.Text, Math.Max(selection.Value, feedback.Value)) : theme.Text;
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, Enabled ? foreground : theme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
            (Navigation ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter));
    }

    private void UpdateFeedback() => feedback.Move(Enabled && (hover || pressed) ? 1 : 0);

    protected override void OnMouseEnter(EventArgs e) { hover = true; UpdateFeedback(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; UpdateFeedback(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { pressed = e.Button == MouseButtons.Left; UpdateFeedback(); Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; UpdateFeedback(); Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { feedback.Dispose(); selection.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class EditField : UserControl
{
    private readonly ThemePalette theme;
    internal readonly TextBox Editor;

    internal EditField(ThemePalette theme, string text, string label)
    {
        this.theme = theme;
        Height = UiMetrics.FieldHeight;
        Margin = new(0, 2, 6, 2);
        ResizeRedraw = true;
        DoubleBuffered = true;
        TabStop = false;
        Editor = new TextBox
        {
            Text = text, AccessibleName = label, BorderStyle = BorderStyle.None,
            BackColor = theme.Field, ForeColor = theme.Text, Margin = new(0)
        };
        Editor.Enter += (_, _) => Invalidate();
        Editor.Leave += (_, _) => Invalidate();
        Controls.Add(Editor);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (Editor is not null)
            Editor.Bounds = new(9, Math.Max(1, (Height - Editor.PreferredHeight) / 2),
                Math.Max(0, Width - 18), Editor.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e) =>
        FieldChrome.Draw(e.Graphics, ClientRectangle, theme.Background, theme.Field,
            ContainsFocus ? theme.Accent : theme.Line);
}
