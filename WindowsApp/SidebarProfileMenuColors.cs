using System.Drawing.Drawing2D;

namespace Voltra;

internal sealed class SidebarProfileMenu : ContextMenuStrip
{
    private int ownerDpi = 96;

    internal SidebarProfileMenu(ThemePalette theme)
    {
        DoubleBuffered = true;
        ShowImageMargin = ShowCheckMargin = DropShadowEnabled = AutoSize = false;
        BackColor = theme.Field;
        ForeColor = theme.Text;
        Renderer = new SidebarProfileMenuRenderer(theme);
    }

    internal int Px(int logical) => (int)Math.Round(logical * (ownerDpi > 0 ? ownerDpi : 96) / 96d);

    // ToolStripDropDownMenu recalculates Padding from DefaultPadding when it
    // measures items. Keep the rows full-width even after those layout passes.
    protected override Padding DefaultPadding => new(0, Px(3), 0, Px(3));

    internal void ShowFor(Control owner)
    {
        ownerDpi = owner.DeviceDpi;
        SuspendLayout();
        try
        {
            Padding = DefaultPadding;
            int height = Padding.Vertical;
            foreach (ToolStripItem item in Items)
            {
                item.AutoSize = false;
                item.Margin = Padding.Empty;
                item.Size = new(owner.Width, item is ToolStripSeparator ? Px(7) : Math.Max(Px(32), item.Font.Height + Px(8)));
                height += item.Height;
            }
            Size = new(owner.Width, Math.Min(height, Screen.FromControl(owner).WorkingArea.Height));
        }
        finally { ResumeLayout(true); }
        Show(owner, new Point(0, owner.Height + Px(4)));
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (Width < 2 || Height < 2) return;
        using var path = FieldChrome.Rounded(ClientRectangle, Px(9));
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }
}

internal sealed class SidebarProfileMenuRenderer(ThemePalette theme) : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
        e.Graphics.Clear(theme.Field);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var menu = (SidebarProfileMenu)e.ToolStrip;
        if (menu.Width < 3 || menu.Height < 3) return;
        using var path = FieldChrome.Rounded(new(0, 0, menu.Width - 1, menu.Height - 1), menu.Px(9));
        using var pen = new Pen(theme.Line);
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.DrawPath(pen, path);
        e.Graphics.Restore(state);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled || e.ToolStrip is not SidebarProfileMenu menu) return;
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        rect.Inflate(-menu.Px(5), -menu.Px(2));
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var path = FieldChrome.Rounded(rect, menu.Px(5));
        using var fill = new SolidBrush(theme.Hover);
        var state = e.Graphics.Save();
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.FillPath(fill, path);
        e.Graphics.Restore(state);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var menu = (SidebarProfileMenu)e.ToolStrip!;
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        rect.Inflate(-menu.Px(12), 0);
        e.TextRectangle = rect;
        e.TextFormat = TextFormatFlags.Left | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | (e.TextFormat & TextFormatFlags.HidePrefix);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var menu = (SidebarProfileMenu)e.ToolStrip!;
        int inset = menu.Px(10), y = e.Item.Height / 2;
        using var pen = new Pen(theme.Line);
        e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset - 1, y);
    }
}
