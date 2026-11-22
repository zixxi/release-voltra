using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Voltra;

internal sealed class SidebarProfileMenuColors(ThemePalette theme) : ProfessionalColorTable
{
    public override Color ToolStripDropDownBackground => theme.Field;
    public override Color MenuBorder => theme.Line;
    public override Color MenuItemBorder => theme.Line;
    public override Color MenuItemSelected => theme.Selected;
    public override Color MenuItemSelectedGradientBegin => theme.Selected;
    public override Color MenuItemSelectedGradientEnd => theme.Selected;
    public override Color SeparatorDark => theme.Line;
    public override Color SeparatorLight => theme.Line;
}

internal sealed class SidebarProfileMenuRenderer(ThemePalette theme)
    : ToolStripProfessionalRenderer(new SidebarProfileMenuColors(theme))
{
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem item || !item.Selected) return;
        if (item.Width <= 8 || item.Height <= 6) return;

        // Inset the highlight equally on both sides and keep it away from row edges.
        var bounds = new Rectangle(4, 3, item.Width - 8, item.Height - 6);
        var graphics = e.Graphics;
        var oldSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var path = Rounded(bounds, 6);
            using var brush = new SolidBrush(Blend(theme.Field, theme.Selected, 0.55f));
            graphics.FillPath(brush, path);
        }
        finally
        {
            graphics.SmoothingMode = oldSmoothing;
        }
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var rect = new Rectangle(4, 0, 22, e.Item.Height);
        TextRenderer.DrawText(e.Graphics, "\u2713", e.Item.Font, rect, theme.Accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        if (e.ToolStrip is not ToolStripDropDown)
        {
            base.OnRenderToolStripBorder(e);
            return;
        }

        var bounds = new Rectangle(0, 0,
            Math.Max(1, e.ToolStrip.Width - 1), Math.Max(1, e.ToolStrip.Height - 1));
        var oldSmoothing = e.Graphics.SmoothingMode;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        try
        {
            using var path = Rounded(bounds, 8);
            using var pen = new Pen(theme.Line);
            e.Graphics.DrawPath(pen, path);
        }
        finally
        {
            e.Graphics.SmoothingMode = oldSmoothing;
        }
    }

    private static Color Blend(Color start, Color end, float amount) => Color.FromArgb(
        (int)Math.Round(start.R + (end.R - start.R) * amount),
        (int)Math.Round(start.G + (end.G - start.G) * amount),
        (int)Math.Round(start.B + (end.B - start.B) * amount));

    private static GraphicsPath Rounded(Rectangle rectangle, int radius)
    {
        int diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        var path = new GraphicsPath();
        path.AddArc(rectangle.X, rectangle.Y, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Y, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.X, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
