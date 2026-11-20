using System.Runtime.InteropServices;

namespace Voltra;

internal sealed partial class MixerForm
{
    private sealed class FirmwareDialog : Form
    {
        private const int DialogWidth = 514;
        private const int Edge = 24;
        private readonly MixerForm owner;
        private Control? screen;
        private Label? stageLabel;
        private FirmwareProgressBar? progressBar;
        private Action? dismiss;
        private bool finished;

        internal FirmwareDialog(MixerForm owner)
        {
            this.owner = owner;
            Text = "Voltra · Device firmware";
            Icon = owner.Icon;
            Font = owner.Font;
            BackColor = owner.palette.Background;
            ForeColor = owner.palette.Text;
            ShowInTaskbar = false;
            MaximizeBox = MinimizeBox = false;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            HandleCreated += (_, _) => SetTitleBar(Handle, owner.palette.Dark);
        }

        internal Task<bool> ConfirmUpdateAsync(DeviceFirmware device, FirmwareRelease release, CancellationToken token)
        {
            string notes = string.IsNullOrWhiteSpace(release.Notes) ? "No release notes were provided." : release.Notes.Trim();
            const int notesHorizontalPadding = 12 + 20;
            int notesHeight = WrappedHeight(notes, DialogWidth - Edge * 2 - notesHorizontalPadding) + 2;
            var content = Grid(1, 2);
            content.RowStyles.Add(new(SizeType.Absolute, 26));
            var notesView = new PageScroller(owner.palette with { Background = owner.palette.Field })
            {
                Dock = DockStyle.Fill, Padding = new(12, 8, 20, 8), AccessibleName = "Firmware release notes",
                Margin = new(0)
            };
            notesView.SetPage(new Label
            {
                Text = notes, Height = notesHeight, UseMnemonic = false, ForeColor = owner.palette.Text,
                AccessibleName = "Release notes", TextAlign = ContentAlignment.TopLeft
            });
            content.Controls.Add(owner.Caption("What's new", UiMetrics.Body, bold: true), 0, 0);
            content.Controls.Add(notesView, 0, 1);
            return AskAsync("Firmware update available", $"Voltra firmware {release.Version}  ·  Installed version {device.Version}",
                content, 26 + Math.Min(156, notesHeight + 16), "Later", "Update firmware", token);
        }

        internal Task<bool> ShowBootInstructionsAsync(CancellationToken token)
        {
            const string instructions = "1. Hold BOOT.\n2. Press and release RESET.\n3. Release BOOT.\n4. Select Continue.";
            return AskAsync("Enter firmware update mode", "Keep the Voltra controller connected by USB.",
                Paragraph(instructions), WrappedHeight(instructions) + 12, "Cancel", "Continue", token);
        }

        internal void ShowProgress(Version version, string stage, Action? cancel = null)
        {
            var content = Grid(1, 2);
            content.RowStyles.Add(new(SizeType.Absolute, 35));
            content.RowStyles.Add(new(SizeType.Absolute, 12));
            var label = owner.Caption(stage, UiMetrics.Body);
            var bar = new FirmwareProgressBar(owner.palette) { Dock = DockStyle.Fill, Margin = new(0) };
            content.Controls.Add(label, 0, 0);
            content.Controls.Add(bar, 0, 1);
            MixerButton? button = cancel is null ? null : owner.ActionButton("Cancel", cancel, 96);
            ShowScreen("Updating Voltra firmware", $"Waveshare ESP32-S3-Zero  ·  Target version {version}",
                content, 54, null, button, null);
            stageLabel = label;
            progressBar = bar;
            dismiss = cancel;
        }

        internal void ReportProgress(string stage, int? percent = null)
        {
            if (IsDisposed || stageLabel is null || progressBar is null) return;
            string text = percent is int value ? $"{stage}  {value}%" : stage;
            if (stageLabel.Text != text) stageLabel.Text = text;
            progressBar.SetValue(percent);
        }

        internal async Task ShowSuccessAsync(Version version, CancellationToken token)
        {
            var connected = Paragraph("Device connection · Connected");
            connected.ForeColor = owner.palette.Online;
            await AskAsync("Firmware updated successfully", $"Your Voltra controller is now running firmware {version}.",
                connected, 27, null, "Done", token);
        }

        internal Task<bool> ShowFailureAsync(string reason, string details, CancellationToken token)
        {
            const string recovery = "If the controller stays in download mode, tap RESET with BOOT released. If it still won't reconnect, use the USB recovery steps in RELEASING.md.";
            string message = reason + "\n\n" + recovery;
            var body = Paragraph(message);
            MixerButton? copy = null;
            copy = owner.ActionButton("Copy details", () =>
            {
                try
                {
                    Clipboard.SetText(reason + Environment.NewLine + details);
                    copy!.Text = "Copied";
                }
                catch (ExternalException) { copy!.Text = "Try copying again"; }
            }, 134);
            return AskAsync("Firmware update failed", null, body, WrappedHeight(message) + 10, "Close", "Retry", token, copy);
        }

        private async Task<bool> AskAsync(string title, string? subtitle, Control body, int bodyHeight,
            string? secondary, string primary, CancellationToken token, MixerButton? extra = null)
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var accept = owner.ActionButton(primary, () => answer.TrySetResult(true),
                primary == "Update firmware" ? 152 : 108, selected: true);
            var decline = secondary is null ? null : owner.ActionButton(secondary, () => answer.TrySetResult(false), 96);
            ShowScreen(title, subtitle, body, bodyHeight, extra, decline, accept);
            dismiss = () => answer.TrySetResult(secondary is null);
            (decline ?? accept).Select();
            try { return await answer.Task.WaitAsync(token); }
            finally { dismiss = null; }
        }

        private Label Paragraph(string text) => new()
        {
            Dock = DockStyle.Fill, Margin = new(0), Text = text, UseMnemonic = false, ForeColor = owner.palette.Text,
            TextAlign = ContentAlignment.TopLeft
        };

        private int WrappedHeight(string text, int width = DialogWidth - Edge * 2)
        {
            using var graphics = CreateGraphics();
            var size = TextRenderer.MeasureText(graphics, text, Font, new Size(LogicalToDeviceUnits(width), 32_767),
                TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
            return (int)Math.Ceiling(size.Height * 96d / DeviceDpi);
        }

        private void ShowScreen(string title, string? subtitle, Control body, int bodyHeight,
            MixerButton? extra, MixerButton? secondary, MixerButton? primary)
        {
            SuspendLayout();
            BackColor = owner.palette.Background;
            ForeColor = owner.palette.Text;
            if (IsHandleCreated) SetTitleBar(Handle, owner.palette.Dark);
            stageLabel = null;
            progressBar = null;
            screen?.Dispose();
            var grid = Grid(1, 6);
            grid.Padding = new(Edge, 18, Edge, 16);
            grid.RowStyles.Add(new(SizeType.Absolute, 37));
            grid.RowStyles.Add(new(SizeType.Absolute, subtitle is null ? 0 : 26));
            grid.RowStyles.Add(new(SizeType.Absolute, 9));
            grid.RowStyles.Add(new(SizeType.Percent, 100));
            grid.RowStyles.Add(new(SizeType.Absolute, 16));
            bool hasActions = extra is not null || secondary is not null || primary is not null;
            grid.RowStyles.Add(new(SizeType.Absolute, hasActions ? 39 : 0));
            grid.Controls.Add(owner.Caption(title, 17, bold: true), 0, 0);
            if (subtitle is not null) grid.Controls.Add(owner.Caption(subtitle, UiMetrics.Body, muted: true), 0, 1);
            grid.Controls.Add(body, 0, 3);
            if (hasActions)
            {
                var footer = Grid(2, 1);
                footer.ColumnStyles.Add(new(SizeType.Percent, 44));
                footer.ColumnStyles.Add(new(SizeType.Percent, 56));
                if (extra is not null) footer.Controls.Add(extra, 0, 0);
                var actions = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill, Margin = new(0), WrapContents = false, FlowDirection = FlowDirection.RightToLeft
                };
                if (primary is not null)
                {
                    primary.Margin = new(0, 2, 0, 2);
                    actions.Controls.Add(primary);
                }
                if (secondary is not null)
                {
                    secondary.Margin = primary is null ? new(0, 2, 0, 2) : new(0, 2, 8, 2);
                    actions.Controls.Add(secondary);
                }
                footer.Controls.Add(actions, 1, 0);
                grid.Controls.Add(footer, 0, 5);
            }
            int height = 96 + (subtitle is null ? 0 : 26) + bodyHeight + (hasActions ? 39 : 0);
            float scale = IsHandleCreated ? DeviceDpi / 96f : 1;
            if (scale != 1) grid.Scale(new SizeF(scale, scale));
            ClientSize = new Size((int)Math.Round(DialogWidth * scale), (int)Math.Round(height * scale));
            Controls.Add(screen = grid);
            AcceptButton = primary;
            ResumeLayout(true);
        }

        internal void Finish()
        {
            finished = true;
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!finished && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                dismiss?.Invoke();
            }
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                dismiss?.Invoke();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    private sealed class FirmwareProgressBar : Control
    {
        private readonly ThemePalette theme;
        private readonly System.Windows.Forms.Timer repaint = new() { Interval = 33 };
        private int? value;

        internal FirmwareProgressBar(ThemePalette theme)
        {
            this.theme = theme;
            DoubleBuffered = true;
            ResizeRedraw = true;
            AccessibleRole = AccessibleRole.ProgressBar;
            AccessibleName = "Firmware update progress";
            TabStop = false;
            repaint.Tick += (_, _) => Invalidate();
        }

        internal void SetValue(int? percent)
        {
            int? next = percent.HasValue ? Math.Max(percent.Value, value ?? 0) : null;
            if (next == value) return;
            value = next;
            UpdateAnimation();
            Invalidate();
        }

        private void UpdateAnimation() => repaint.Enabled = Visible && !value.HasValue && UiMotion.AnimationsEnabled();

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Disposing && !IsDisposed) UpdateAnimation();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int height = LogicalToDeviceUnits(5);
            var track = new Rectangle(0, (Height - height) / 2, Width, height);
            using var background = new SolidBrush(theme.Line);
            using var foreground = new SolidBrush(theme.Accent);
            e.Graphics.FillRectangle(background, track);
            if (value is int percent)
                e.Graphics.FillRectangle(foreground, 0, track.Y, track.Width * percent / 100, track.Height);
            else
            {
                int length = track.Width / 4;
                double phase = repaint.Enabled ? Environment.TickCount64 % 1500 / 1500d : 0.5;
                int x = (int)((track.Width + length) * phase) - length;
                e.Graphics.FillRectangle(foreground, Math.Max(0, x), track.Y,
                    Math.Max(0, Math.Min(track.Width, x + length) - Math.Max(0, x)), track.Height);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) repaint.Dispose();
            base.Dispose(disposing);
        }
    }
}
