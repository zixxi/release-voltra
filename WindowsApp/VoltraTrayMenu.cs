using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace Voltra;

internal sealed class VoltraTrayMenu : ContextMenuStrip
{
    private const int LogicalDpi = 96;
    private const int MenuWidth = 208;
    private const int EdgeInset = 6;
    private const int CornerRadius = 11;
    private readonly Func<ThemePalette> currentPalette;
    private readonly ToolStripLabel header = new("Voltra") { AutoSize = false, Margin = Padding.Empty };
    private readonly ToolStripSeparator separator = new() { AutoSize = false, Margin = Padding.Empty };
    private readonly TrayCommand openItem;
    private readonly TrayCommand exitItem;
    private ThemePalette theme;
    private int menuDpi = LogicalDpi;
    private Point? openingLocation;
    private Font headerFont = new("Segoe UI Semibold", 13, GraphicsUnit.Pixel);
    private Font itemFont = new("Segoe UI", 14, GraphicsUnit.Pixel);

    internal VoltraTrayMenu(Func<ThemePalette> currentPalette, Action open, Action exit)
    {
        this.currentPalette = currentPalette;
        theme = currentPalette();
        openItem = new TrayCommand(this, "Open Voltra", open);
        exitItem = new TrayCommand(this, "Exit", exit);

        SuspendLayout();
        AutoSize = false;
        ShowImageMargin = false;
        DropShadowEnabled = false;
        BackColor = theme.Background;
        ForeColor = theme.Text;
        Renderer = new TrayRenderer(this);
        Items.AddRange([header, openItem, separator, exitItem]);
        ApplyLayout();
        ResumeLayout(true);
    }

    protected override Padding DefaultPadding => new(0, Px(EdgeInset), 0, Px(EdgeInset));

    protected override void OnOpening(CancelEventArgs e)
    {
        theme = currentPalette();
        BackColor = theme.Background;
        ForeColor = theme.Text;

        Point cursor = Cursor.Position;
        Rectangle workArea = Screen.FromPoint(cursor).WorkingArea;
        IntPtr monitor = MonitorFromPoint(cursor, 2); // MONITOR_DEFAULTTONEAREST
        int dpi = GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? (int)dpiX : DeviceDpi;
        if (dpi != menuDpi)
        {
            menuDpi = dpi;
            headerFont.Dispose();
            itemFont.Dispose();
            headerFont = new("Segoe UI Semibold", 13 * DpiScale, GraphicsUnit.Pixel);
            itemFont = new("Segoe UI", 14 * DpiScale, GraphicsUnit.Pixel);
            ApplyLayout();
        }

        // Anchor after sizing for the monitor under the cursor, including secondary taskbars.
        int x = cursor.X - Width;
        int y = cursor.Y - Height;
        if (x < workArea.Left) x = cursor.X;
        if (y < workArea.Top) y = cursor.Y;
        openingLocation = new Point(
            Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width)),
            Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height)));
        Location = openingLocation.Value;

        using var outline = FieldChrome.Rounded(ClientRectangle, Px(CornerRadius));
        Region = new Region(outline);
        base.OnOpening(e);
    }

    protected override void SetVisibleCore(bool visible)
    {
        try { base.SetVisibleCore(visible); }
        finally { openingLocation = null; }
    }

    protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
    {
        // WinForms reapplies the original tray location when the menu becomes visible.
        if (openingLocation is Point anchor)
            (x, y) = (anchor.X, anchor.Y);
        base.SetBoundsCore(x, y, width, height, specified);
    }

    private void ApplyLayout()
    {
        SuspendLayout();
        int width = Px(MenuWidth);
        header.Size = new(width, Px(28));
        openItem.Size = new(width, Px(36));
        separator.Height = Px(10);
        exitItem.Size = new(width, Px(36));
        Padding = DefaultPadding;
        Size = new(width, Padding.Vertical + header.Height + openItem.Height + separator.Height + exitItem.Height);
        ResumeLayout(true);
    }

    protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
    {
        openItem.ResetHighlight();
        exitItem.ResetHighlight();
        base.OnClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            headerFont.Dispose();
            itemFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private float DpiScale => (menuDpi == 0 ? LogicalDpi : menuDpi) / (float)LogicalDpi;
    private int Px(int value) => (int)Math.Round(value * DpiScale);

    private sealed class TrayCommand : ToolStripMenuItem
    {
        private readonly UiMotion highlight;

        internal TrayCommand(VoltraTrayMenu menu, string text, Action action) : base(text)
        {
            AutoSize = false;
            Margin = Padding.Empty;
            ShowShortcutKeys = false;
            highlight = new UiMotion(menu, 120, () => Invalidate());
            Click += (_, _) => action();
        }

        internal float GetHighlight()
        {
            highlight.Move(Selected || Pressed ? 1 : 0);
            return highlight.Value;
        }

        internal void ResetHighlight() => highlight.Move(0, immediate: true);

        protected override void Dispose(bool disposing)
        {
            if (disposing) highlight.Dispose();
            base.Dispose(disposing);
        }
    }

    private sealed class TrayRenderer(VoltraTrayMenu menu) : ToolStripRenderer
    {
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var outline = FieldChrome.Rounded(
                new Rectangle(0, 0, menu.Width - 1, menu.Height - 1), menu.Px(CornerRadius));
            using var pen = new Pen(menu.theme.Line);
            e.Graphics.DrawPath(pen, outline);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item is not TrayCommand item) return;
            float amount = item.GetHighlight();
            if (amount <= 0 && !item.Pressed) return;

            Rectangle bounds = new(Point.Empty, item.Size);
            bounds.Inflate(-menu.Px(EdgeInset), -menu.Px(2));
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = FieldChrome.Rounded(bounds, menu.Px(7));
            Color target = item.Pressed ? menu.theme.Pressed : menu.theme.Selected;
            using var fill = new SolidBrush(FieldChrome.Blend(menu.theme.Background, target, item.Pressed ? 1 : amount));
            e.Graphics.FillPath(fill, shape);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool isHeader = e.Item == menu.header;
            int start = menu.Px(36);
            var textBounds = new Rectangle(start, 0, e.Item.Width - start - menu.Px(12), e.Item.Height);
            TextRenderer.DrawText(e.Graphics, e.Text, isHeader ? menu.headerFont : menu.itemFont, textBounds,
                isHeader ? menu.theme.Accent : menu.theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);

            var state = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TranslateTransform(menu.Px(14), (e.Item.Height - 14 * menu.DpiScale) / 2);
            e.Graphics.ScaleTransform(menu.DpiScale, menu.DpiScale);
            using var pen = new Pen(e.Item.Selected ? menu.theme.Accent : menu.theme.Muted, 1.2f)
            {
                StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
            };
            if (isHeader)
            {
                using var accent = new SolidBrush(menu.theme.Accent);
                e.Graphics.FillEllipse(accent, 4, 4, 6, 6);
            }
            else if (e.Item == menu.openItem)
            {
                using var window = FieldChrome.Rounded(new Rectangle(1, 2, 12, 10), 2);
                e.Graphics.DrawPath(pen, window);
                e.Graphics.DrawLine(pen, 1, 5, 13, 5);
            }
            else
            {
                e.Graphics.DrawLines(pen, [new Point(6, 2), new Point(2, 2), new Point(2, 12), new Point(6, 12)]);
                e.Graphics.DrawLine(pen, 5, 7, 13, 7);
                e.Graphics.DrawLines(pen, [new Point(10, 4), new Point(13, 7), new Point(10, 10)]);
            }
            e.Graphics.Restore(state);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = menu.Px(12);
            int y = e.Item.Height / 2;
            using var pen = new Pen(menu.theme.Line);
            e.Graphics.DrawLine(pen, inset, y, e.Item.Width - inset - 1, y);
        }
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromPoint(Point point, uint flags);

    [DllImport("shcore.dll", ExactSpelling = true)]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
