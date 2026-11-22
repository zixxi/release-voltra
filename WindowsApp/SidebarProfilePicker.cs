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
        using (var path = Rounded(rect, 7))
        using (var background = new SolidBrush(hovering ? FieldChrome.Blend(theme.Field, theme.Hover, 0.45f) : theme.Field))
        using (var outline = new Pen(Focused ? theme.Accent : theme.Line))
        {
            g.FillPath(background, path);
            g.DrawPath(outline, path);
        }

        float center = Height / 2f;
        using (var iconPen = new Pen(theme.Accent, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        using (var dot = new SolidBrush(theme.Accent))
        {
            for (int i = 0; i < 3; i++)
            {
                float y = center - 5 + i * 5;
                float x = i == 1 ? 20 : i == 0 ? 15 : 18;
                g.DrawLine(iconPen, 12, y, 26, y);
                g.FillEllipse(dot, x - 2, y - 2, 4, 4);
            }
        }

        var label = new Rectangle(35, 0, Math.Max(0, Width - 65), Height);
        TextRenderer.DrawText(g, profileName, Font, label, theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);

        using var arrow = new Pen(theme.Muted, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width - 17;
        g.DrawLines(arrow, [new PointF(cx - 4, center - 2), new PointF(cx, center + 2), new PointF(cx + 4, center - 2)]);
    }

    private static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        int diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}


/// <summary>Draws the profile menu consistently across dark, light and custom accent themes.</summary>
internal sealed class VoltraConsistentProfileMenuRenderer : ToolStripProfessionalRenderer
{
    private readonly ThemePalette theme;

    internal VoltraConsistentProfileMenuRenderer(ThemePalette theme)
    {
        this.theme = theme;
        RoundedEdges = false; // The parent ContextMenuStrip uses its own rounded Region.
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var fill = new SolidBrush(theme.Field);
        e.Graphics.FillRectangle(fill, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var bounds = e.ToolStrip.ClientRectangle;
        if (bounds.Width < 4 || bounds.Height < 4) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outline = new Pen(theme.Line);
        using var path = FieldChrome.Rounded(
            new Rectangle(0, 0, bounds.Width - 1, bounds.Height - 1), 9);
        e.Graphics.DrawPath(outline, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var rect = new Rectangle(2, 2, Math.Max(1, e.Item.Width - 5), Math.Max(1, e.Item.Height - 4));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var background = new SolidBrush(FieldChrome.Blend(theme.Field, theme.Selected, 0.55f));
        using var path = FieldChrome.Rounded(rect, 5);
        e.Graphics.FillPath(background, path);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var bounds = e.Item.Bounds;
        int y = bounds.Height / 2;
        using var pen = new Pen(theme.Line);
        e.Graphics.DrawLine(pen, 6, y, Math.Max(6, bounds.Width - 7), y);
    }
}
