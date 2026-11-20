using System.Drawing.Drawing2D;

namespace Voltra;

internal sealed class PageScroller : Panel
{
    private readonly ScrollThumb thumb;
    private int offset, maximumOffset, wheelRemainder;
    private bool arranging;

    internal PageScroller(ThemePalette theme)
    {
        BackColor = theme.Background;
        thumb = new(this, theme);
        Controls.Add(thumb);
    }

    internal Control? Page { get; private set; }
    internal int Offset => offset;

    internal void ApplyTheme(ThemePalette theme)
    {
        BackColor = theme.Background;
        thumb.ApplyTheme(theme);
    }

    internal void SetPage(Control? content)
    {
        if (Page is { } previous)
        {
            TrackFocus(previous, attach: false);
            previous.SizeChanged -= PageSizeChanged;
            Page = null;
            Controls.Remove(previous);
        }

        offset = wheelRemainder = 0;
        Page = content;
        if (content is not null)
        {
            content.TabIndex = 0;
            content.SizeChanged += PageSizeChanged;
            TrackFocus(content, attach: true);
            Controls.Add(content);
        }
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (arranging || Disposing || thumb is null) return;
        arranging = true;
        try
        {
            int gutter = LogicalToDeviceUnits(18);
            maximumOffset = Page is { } content ? Math.Max(0, content.Height + Padding.Vertical - ClientSize.Height) : 0;
            offset = Math.Clamp(offset, 0, maximumOffset);
            if (Page is { } body)
                body.Bounds = new(Padding.Left, Padding.Top - offset,
                    Math.Max(0, ClientSize.Width - Padding.Left - Math.Max(gutter, Padding.Right)), body.Height);
            thumb.Bounds = new(Math.Max(0, ClientSize.Width - gutter), 0, gutter, ClientSize.Height);
            thumb.Visible = maximumOffset > 0;
            thumb.Invalidate();
        }
        finally { arranging = false; }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        wheelRemainder = 0;
        PerformLayout();
    }

    internal void ScrollTo(int position)
    {
        int next = Math.Clamp(position, 0, maximumOffset);
        if (next == offset) return;
        offset = next;
        if (Page is { } content) content.Top = Padding.Top - offset;
        thumb.Invalidate();
    }

    internal void ScrollWheel(MouseEventArgs e)
    {
        if (maximumOffset == 0 || e is HandledMouseEventArgs { Handled: true }) return;
        int lines = SystemInformation.MouseWheelScrollLines;
        if (lines == 0 || (ModifierKeys & (Keys.Control | Keys.Shift | Keys.Alt)) != Keys.None) return;

        int step = lines < 0 ? PageStep : LogicalToDeviceUnits(18) * lines;
        int movement = wheelRemainder + e.Delta * step;
        wheelRemainder = movement % SystemInformation.MouseWheelScrollDelta;
        ScrollTo(offset - movement / SystemInformation.MouseWheelScrollDelta);
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ScrollWheel(e);
        base.OnMouseWheel(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (maximumOffset > 0 && keyData is Keys.PageUp or Keys.PageDown)
        {
            ScrollTo(offset + (keyData == Keys.PageUp ? -PageStep : PageStep));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private int PageStep => Math.Max(1, ClientSize.Height - LogicalToDeviceUnits(24));

    private void PageSizeChanged(object? sender, EventArgs e) => PerformLayout();

    private void TrackFocus(Control control, bool attach)
    {
        if (attach)
        {
            control.Enter += RevealFocusedControl;
            control.ControlAdded += TrackAddedControl;
            control.ControlRemoved += TrackRemovedControl;
        }
        else
        {
            control.Enter -= RevealFocusedControl;
            control.ControlAdded -= TrackAddedControl;
            control.ControlRemoved -= TrackRemovedControl;
        }
        foreach (Control child in control.Controls)
            TrackFocus(child, attach);
    }

    private void TrackAddedControl(object? sender, ControlEventArgs e)
    {
        if (e.Control is { } child) TrackFocus(child, attach: true);
    }

    private void TrackRemovedControl(object? sender, ControlEventArgs e)
    {
        if (e.Control is { } child) TrackFocus(child, attach: false);
    }

    private void RevealFocusedControl(object? sender, EventArgs e)
    {
        if (maximumOffset == 0 || sender is not Control { TabStop: true, CanSelect: true } control) return;
        var bounds = RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        int inset = LogicalToDeviceUnits(8);
        if (bounds.Top < inset || bounds.Height + inset * 2 > ClientSize.Height)
            ScrollTo(offset + bounds.Top - inset);
        else if (bounds.Bottom > ClientSize.Height - inset)
            ScrollTo(offset + bounds.Bottom - ClientSize.Height + inset);
    }

    private sealed class ScrollThumb : Control
    {
        private readonly PageScroller owner;
        private ThemePalette theme;
        private readonly UiMotion hover;
        private int? grabOffset;

        internal ScrollThumb(PageScroller owner, ThemePalette theme)
        {
            this.owner = owner;
            this.theme = theme;
            hover = new(this, 110, Invalidate);
            SetStyle(ControlStyles.Selectable, true);
            DoubleBuffered = true;
            ResizeRedraw = true;
            TabStop = true;
            TabIndex = 1;
            Visible = false;
            AccessibleName = "Page scroll";
            AccessibleRole = AccessibleRole.ScrollBar;
        }

        internal void ApplyTheme(ThemePalette value)
        {
            theme = value;
            Invalidate();
        }

        private Rectangle Lane
        {
            get
            {
                int inset = LogicalToDeviceUnits(5);
                int width = LogicalToDeviceUnits(6);
                return new((ClientSize.Width - width) / 2, inset, width, Math.Max(0, ClientSize.Height - inset * 2));
            }
        }

        private Rectangle Thumb
        {
            get
            {
                var lane = Lane;
                int viewport = owner.ClientSize.Height;
                int maximum = owner.maximumOffset;
                if (maximum == 0 || lane.Height == 0) return Rectangle.Empty;
                int limit = Math.Max(1, lane.Height - 1);
                int minimum = Math.Min(limit, LogicalToDeviceUnits(28));
                int proportional = (int)Math.Round(lane.Height * (double)viewport / (viewport + maximum));
                int height = Math.Clamp(proportional, minimum, limit);
                int top = (int)Math.Round((lane.Height - height) * (double)owner.Offset / maximum);
                return new(lane.X, lane.Top + top, lane.Width, height);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var bounds = Thumb;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            Color idle = FieldChrome.Blend(theme.Background, theme.Muted, theme.Dark ? 0.57f : 0.76f);
            Color fill = Focused && ShowFocusCues ? theme.Accent : FieldChrome.Blend(idle, theme.Muted, hover.Value * 0.45f);
            if (SystemInformation.HighContrast && !(Focused && ShowFocusCues))
                fill = theme.Text;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(fill);
            using var shape = FieldChrome.Rounded(bounds, (bounds.Width + 1) / 2);
            e.Graphics.FillPath(brush, shape);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover.Move(1);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (grabOffset is null) hover.Move(0);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || owner.maximumOffset == 0) return;
            var bounds = Thumb;
            Focus();
            if (e.Y >= bounds.Top && e.Y < bounds.Bottom)
            {
                grabOffset = e.Y - bounds.Top;
                Capture = true;
            }
            else owner.ScrollTo(owner.Offset + (e.Y < bounds.Top ? -owner.PageStep : owner.PageStep));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (grabOffset is not { } grab || !Capture) return;
            var lane = Lane;
            int travel = lane.Height - Thumb.Height;
            if (travel > 0)
            {
                double position = Math.Clamp((e.Y - grab - lane.Top) / (double)travel, 0, 1);
                owner.ScrollTo((int)Math.Round(position * owner.maximumOffset));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && grabOffset is not null) Capture = false;
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || grabOffset is null) return;
            grabOffset = null;
            hover.Move(ClientRectangle.Contains(PointToClient(MousePosition)) ? 1 : 0);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            int step = LogicalToDeviceUnits(18);
            int next = keyData switch
            {
                Keys.Up => owner.Offset - step,
                Keys.Down => owner.Offset + step,
                Keys.Home => 0,
                Keys.End => owner.maximumOffset,
                _ => -1
            };
            if (keyData is not (Keys.Up or Keys.Down or Keys.Home or Keys.End))
                return base.ProcessCmdKey(ref msg, keyData);
            owner.ScrollTo(next);
            return true;
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnChangeUICues(UICuesEventArgs e) { base.OnChangeUICues(e); Invalidate(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) hover.Dispose();
            base.Dispose(disposing);
        }
    }
}
