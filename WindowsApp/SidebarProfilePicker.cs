using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Voltra;

/// <summary>Compact, theme-aware profile selector for the Windows sidebar.</summary>
internal sealed class SidebarProfilePicker : Control
{
    private readonly ThemePalette theme;
    private bool hovering;
    private string profileName = "Default";
    [System.ComponentModel.DesignerSerializationVisibility(
        System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal string ProfileName
    {
        get => profileName;
        set { profileName = value; Invalidate(); }
    }

    internal SidebarProfilePicker(ThemePalette theme)
    {
        this.theme = theme;
        DoubleBuffered = true;
        ResizeRedraw = true;
        TabStop = true;
        Cursor = Cursors.Hand;
        BackColor = theme.Sidebar;
        ForeColor = theme.Text;
        AccessibleRole = AccessibleRole.ComboBox;
        AccessibleName = "Active profile";
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        hovering = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        hovering = false;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space or Keys.Down || (e.Alt && e.KeyCode == Keys.Down))
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            OnClick(EventArgs.Empty);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(theme.Sidebar);
        var rect = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (var path = FieldChrome.Rounded(rect, LogicalToDeviceUnits(7)))
        using (var background = new SolidBrush(hovering ? FieldChrome.Blend(theme.Field, theme.Hover, 0.45f) : theme.Field))
        using (var outline = new Pen(Focused ? theme.Accent : theme.Line))
        {
            g.FillPath(background, path);
            g.DrawPath(outline, path);
        }

        float scale = DeviceDpi / 96f;
        float center = Height / 2f;
        using (var iconPen = new Pen(theme.Accent, 1.4f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var dot = new SolidBrush(theme.Accent))
        {
            for (int i = 0; i < 3; i++)
            {
                float y = center + (i - 1) * 5 * scale;
                float x = (i == 1 ? 20 : i == 0 ? 15 : 18) * scale;
                g.DrawLine(iconPen, 12 * scale, y, 26 * scale, y);
                g.FillEllipse(dot, x - 2 * scale, y - 2 * scale, 4 * scale, 4 * scale);
            }
        }

        var label = new Rectangle(LogicalToDeviceUnits(35), 0, Math.Max(0, Width - LogicalToDeviceUnits(65)), Height);
        TextRenderer.DrawText(g, profileName, Font, label, theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        using var arrow = new Pen(theme.Muted, 1.6f * scale) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width - 17 * scale;
        g.DrawLines(arrow, [new PointF(cx - 4 * scale, center - 2 * scale), new PointF(cx, center + 2 * scale), new PointF(cx + 4 * scale, center - 2 * scale)]);
    }
}
