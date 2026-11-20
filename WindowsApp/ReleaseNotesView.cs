using System.Runtime.InteropServices;

namespace Voltra;

internal sealed class ReleaseNotesView : Panel
{
    private readonly PageScroller scroller;
    private readonly NotesText text;
    private readonly string markdown;
    private ThemePalette theme;

    internal ReleaseNotesView(ThemePalette theme, string markdown)
    {
        this.theme = theme;
        this.markdown = markdown;
        Margin = Padding.Empty;
        BackColor = theme.Field;
        scroller = new PageScroller(theme with { Background = theme.Field })
        {
            Dock = DockStyle.Fill, Padding = new(14, 11, 24, 11)
        };
        text = new NotesText(scroller)
        {
            AccessibleName = "Release notes",
            Height = 24, ReadOnly = true, BorderStyle = BorderStyle.None,
            ScrollBars = RichTextBoxScrollBars.None, DetectUrls = false,
            BackColor = theme.Field, ForeColor = theme.Text
        };
        Controls.Add(scroller);
        scroller.SetPage(text);
        text.Rtf = ReleaseNotesDocument.ToRtf(markdown, theme.Text, theme.Background);
        text.Select(0, 0);
    }

    internal void ApplyTheme(ThemePalette value)
    {
        var previous = theme;
        if (value == previous) return;
        theme = value;
        BackColor = value.Field;
        scroller.ApplyTheme(value with { Background = value.Field });
        text.BackColor = value.Field;
        text.ForeColor = value.Text;
        if (value.Text == previous.Text && value.Background == previous.Background) return;
        int start = text.SelectionStart, length = text.SelectionLength, offset = scroller.Offset;
        text.Rtf = ReleaseNotesDocument.ToRtf(markdown, value.Text, value.Background);
        text.Select(start, length);
        scroller.ScrollTo(offset);
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (ClientSize.Width < 3 || ClientSize.Height < 3) return;
        using var outline = FieldChrome.Rounded(ClientRectangle, LogicalToDeviceUnits(8));
        Region = new Region(outline);
    }

    private sealed class NotesText(PageScroller scroller) : RichTextBox
    {
        private const int RequestResize = 0x0400 + 65; // EM_REQUESTRESIZE
        private int measuredWidth;
        private bool layoutPending;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            MeasureContent();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated && Width != measuredWidth) MeasureContent();
        }

        private void MeasureContent()
        {
            if (Width < 1) return;
            measuredWidth = Width;
            SendMessage(Handle, RequestResize, 0, 0);
        }

        protected override void OnContentsResized(ContentsResizedEventArgs e)
        {
            base.OnContentsResized(e);
            if (Width < 1 || e.NewRectangle.Height < 1) return;
            int height = e.NewRectangle.Height + LogicalToDeviceUnits(3);
            if (Height == height) return;
            Height = height;
            if (!IsHandleCreated || Disposing || layoutPending) return;
            // A width change can report a new height within the scroller's layout pass.
            layoutPending = true;
            BeginInvoke((Action)(() =>
            {
                layoutPending = false;
                if (!IsDisposed) scroller.PerformLayout();
            }));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroller.ScrollWheel(e);
            base.OnMouseWheel(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Capture || e.Button != MouseButtons.Left) return;
            int y = Top + e.Y;
            int step = LogicalToDeviceUnits(18);
            if (y < 0) scroller.ScrollTo(scroller.Offset - step);
            else if (y >= scroller.ClientSize.Height) scroller.ScrollTo(scroller.Offset + step);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            IButtonControl? action = keyData switch
            {
                Keys.Enter => FindForm()?.AcceptButton,
                Keys.Escape => FindForm()?.CancelButton,
                _ => null
            };
            if (action is null) return base.ProcessCmdKey(ref msg, keyData);
            action.PerformClick();
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode is not (Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End)) return;
            int top = Top + GetPositionFromCharIndex(SelectionStart).Y;
            int line = Font.Height;
            if (top < line) scroller.ScrollTo(scroller.Offset + top - line);
            else if (top + line > scroller.ClientSize.Height)
                scroller.ScrollTo(scroller.Offset + top + line - scroller.ClientSize.Height);
        }

        [DllImport("user32.dll", EntryPoint = "SendMessageW", ExactSpelling = true)]
        private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
    }
}
