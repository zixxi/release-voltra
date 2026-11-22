using System.Drawing;
using System.Windows.Forms;

namespace Voltra;

internal sealed partial class MixerForm
{
    private void ManageProfiles()
    {
        using var dialog = new Form
        {
            Text = "Profiles",
            ClientSize = new Size(520, 320),
            MinimumSize = new Size(500, 320),
            Font = Font,
            BackColor = palette.Background,
            ForeColor = palette.Text,
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96)
        };
        dialog.HandleCreated += (_, _) => SetTitleBar(dialog.Handle, palette.Dark);

        // Derive dialog colors from the active theme (dark, light and custom accent).
        var surface = palette.Field;
        var selectedSurface = palette.Selected;
        var secondaryText = palette.Muted;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = palette.Background,
            Padding = new Padding(20, 16, 20, 16),
            ColumnCount = 1,
            RowCount = 3
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        dialog.Controls.Add(root);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        header.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        header.Controls.Add(new Label
        {
            Text = "Profiles", Font = new Font(Font.FontFamily, 15, FontStyle.Bold),
            ForeColor = palette.Text, Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        header.Controls.Add(new Label
        {
            Text = "Manage your knob mapping profiles.", ForeColor = palette.Muted,
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft
        }, 0, 1);
        root.Controls.Add(header, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(body, 0, 1);

        var profiles = new ListBox
        {
            Dock = DockStyle.Fill,
            IntegralHeight = false,
            BorderStyle = BorderStyle.None,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = 40,
            BackColor = surface,
            ForeColor = palette.Text,
            Margin = new Padding(0, 0, 12, 8),
            AccessibleName = "Saved profiles"
        };
        body.Controls.Add(profiles, 0, 0);

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Margin = new Padding(8, 0, 0, 0)
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(editor, 1, 0);

        var name = Entry("", "Profile name");
        name.Dock = DockStyle.Fill;
        // EditField normally reserves 6 px on the right; keep its edges aligned
        // with the two action-button rows immediately underneath.
        name.Margin = Padding.Empty;
        name.Editor.MaxLength = MappingProfiles<Channel>.MaxName;
        editor.Controls.Add(Caption("Profile name", 10), 0, 0);
        editor.Controls.Add(name, 0, 1);

        var store = config.Profiles;
        MappingProfile<Channel>? Selected() => profiles.SelectedItem as MappingProfile<Channel>;
        MixerButton? delete = null;
        MixerButton? activate = null;

        void RefreshProfiles(string? preferredId)
        {
            profiles.BeginUpdate();
            try
            {
                profiles.Items.Clear();
                foreach (var item in store.Items) profiles.Items.Add(item);
                int index = Array.FindIndex(store.Items, p => p.Id == preferredId);
                if (index < 0) index = Array.FindIndex(store.Items, p => p.Id == store.ActiveId);
                if (index < 0 && profiles.Items.Count > 0) index = 0;
                profiles.SelectedIndex = index;
            }
            finally { profiles.EndUpdate(); }
            profiles.Invalidate();
        }

        profiles.DrawItem += (_, e) =>
        {
            if (e.Index < 0 || e.Index >= profiles.Items.Count) return;
            var item = (MappingProfile<Channel>)profiles.Items[e.Index]!;
            bool isSelected = (e.State & DrawItemState.Selected) != 0;
            bool isActive = item.Id == store.ActiveId;
            using var background = new SolidBrush(isSelected ? selectedSurface : surface);
            e.Graphics.FillRectangle(background, e.Bounds);
            if (isSelected)
            {
                using var stripe = new SolidBrush(palette.Accent);
                e.Graphics.FillRectangle(stripe, e.Bounds.Left, e.Bounds.Top + 6, 3, e.Bounds.Height - 12);
            }
            var nameRect = new Rectangle(e.Bounds.X + 13, e.Bounds.Y, e.Bounds.Width - 26, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, item.Name, profiles.Font, nameRect, palette.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (isActive)
            {
                var activeRect = new Rectangle(e.Bounds.Right - 59, e.Bounds.Y, 53, e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, "Active", profiles.Font, activeRect, secondaryText,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            e.DrawFocusRectangle();
        };

        var error = Caption("", 9, muted: true);
        error.Dock = DockStyle.Fill;

        void UpdateSelection()
        {
            var item = Selected();
            if (item != null) name.Editor.Text = item.Name;
            if (delete != null) delete.Enabled = item != null && store.Items.Length > 1;
            if (activate != null) activate.Enabled = item != null;
        }
        profiles.SelectedIndexChanged += (_, _) => UpdateSelection();

        void Apply(Func<MappingProfiles<Channel>> change, bool keepSelection = false)
        {
            var before = Selected()?.Id;
            try
            {
                store = change();
                Persist(config.WithProfiles(store));
                RefreshProfiles(keepSelection ? before : store.ActiveId);
                error.Text = "";
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        }

        var create = ActionButton("New", () => Apply(() => store.Create(name.Editor.Text,
            Enumerable.Range(1, 5).Select(i => new Channel($"Knob {i}", "")).ToArray())), 92);
        var duplicate = ActionButton("Duplicate", () =>
        {
            var selected = Selected();
            if (selected != null) Apply(() => store.Create(name.Editor.Text, selected.Channels));
        }, 96);
        var rename = ActionButton("Rename", () =>
        {
            var selected = Selected();
            if (selected != null) Apply(() => store.Rename(selected.Id, name.Editor.Text), true);
        }, 96);
        delete = ActionButton("Delete", () =>
        {
            var selected = Selected();
            if (selected == null || store.Items.Length <= 1) return;
            if (ConfirmProfileDeletion(dialog, selected.Name))
                Apply(() => store.Delete(selected.Id));
        }, 96);
        // Use equal-width cells, rather than an auto-sized FlowLayoutPanel.
        // The shared MixerButton draws action text centered already.
        TableLayoutPanel ActionPair(MixerButton first, MixerButton second)
        {
            var pair = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
            };
            pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pair.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            pair.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            first.Dock = second.Dock = DockStyle.Fill;
            first.Margin = new Padding(0, 2, 5, 3);
            second.Margin = new Padding(5, 2, 0, 3);
            pair.Controls.Add(first, 0, 0);
            pair.Controls.Add(second, 1, 0);
            return pair;
        }
        editor.Controls.Add(ActionPair(create, duplicate), 0, 2);
        editor.Controls.Add(ActionPair(rename, delete), 0, 3);
        editor.Controls.Add(error, 0, 4);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 7, 0, 0)
        };
        root.Controls.Add(footer, 0, 2);
        activate = ActionButton("Activate", () =>
        {
            var selected = Selected();
            if (selected == null) return;
            Apply(() => store.Activate(selected.Id));
            dialog.Close();
        }, 104, true);
        var close = ActionButton("Close", dialog.Close, 90);
        // Identical dimensions and a consistent baseline for footer actions.
        activate.Size = close.Size = new Size(108, UiMetrics.FieldHeight);
        activate.Margin = new Padding(6, 0, 0, 0);
        close.Margin = new Padding(6, 0, 0, 0);
        footer.Controls.Add(activate);
        footer.Controls.Add(close);

        RefreshProfiles(store.ActiveId);
        UpdateSelection();
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }

    /// <summary>Theme-consistent confirmation; cancellation never changes saved profiles.</summary>
    private bool ConfirmProfileDeletion(Form owner, string profileName)
    {
        using var confirmation = new Form
        {
            Text = "Delete profile",
            ClientSize = new Size(390, 190),
            Font = Font,
            BackColor = palette.Background,
            ForeColor = palette.Text,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            ShowInTaskbar = false,
            MinimizeBox = false,
            MaximizeBox = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96)
        };
        confirmation.HandleCreated += (_, _) => SetTitleBar(confirmation.Handle, palette.Dark);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(22, 16, 22, 14),
            Margin = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 47));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        confirmation.Controls.Add(layout);

        layout.Controls.Add(Caption("Delete profile?", 15, bold: true), 0, 0);
        var message = Caption($"Delete \"{profileName}\"? This action cannot be undone.", 10);
        message.AutoEllipsis = false;
        layout.Controls.Add(message, 0, 1);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = new Padding(0, 4, 0, 0)
        };
        layout.Controls.Add(footer, 0, 3);

        var deleteButton = ActionButton("Delete", () =>
        {
            confirmation.DialogResult = DialogResult.Yes;
            confirmation.Close();
        }, 102, selected: true);
        var cancelButton = ActionButton("Cancel", () =>
        {
            confirmation.DialogResult = DialogResult.Cancel;
            confirmation.Close();
        }, 102);
        deleteButton.Margin = new Padding(8, 0, 0, 0);
        cancelButton.Margin = Padding.Empty;
        footer.Controls.Add(deleteButton);
        footer.Controls.Add(cancelButton);
        confirmation.CancelButton = cancelButton;
        confirmation.Shown += (_, _) => cancelButton.Focus();

        return confirmation.ShowDialog(owner) == DialogResult.Yes;
    }

}
