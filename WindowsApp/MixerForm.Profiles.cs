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
            ClientSize = new Size(520, 340),
            MinimumSize = new Size(480, 340),
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

        var surface = Color.FromArgb(29, 31, 38);
        var hover = Color.FromArgb(48, 50, 60);
        var selectedSurface = Color.FromArgb(57, 48, 73);
        var secondaryText = Color.FromArgb(167, 170, 181);

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = palette.Background,
            Padding = new Padding(20, 16, 20, 16),
            ColumnCount = 1,
            RowCount = 3
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        dialog.Controls.Add(root);

        var heading = new Label
        {
            Text = "Profiles",
            Font = new Font(Font.FontFamily, 15, FontStyle.Bold),
            ForeColor = palette.Text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };
        root.Controls.Add(heading, 0, 0);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty
        };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
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
            Margin = new Padding(4, 0, 0, 0)
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        editor.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.Controls.Add(editor, 1, 0);

        var name = Entry("", "Profile name");
        name.Dock = DockStyle.Fill;
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
                using var stripe = new SolidBrush(Color.FromArgb(183, 139, 250));
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
            if (MessageBox.Show(dialog, $"Delete profile \"{selected.Name}\"?", "Delete profile",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2)
                == DialogResult.Yes)
                Apply(() => store.Delete(selected.Id));
        }, 96);
        editor.Controls.Add(Inline(create, duplicate), 0, 2);
        editor.Controls.Add(Inline(rename, delete), 0, 3);
        editor.Controls.Add(error, 0, 4);

        var footer = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 10, 0, 0)
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
        footer.Controls.Add(activate);
        footer.Controls.Add(close);

        RefreshProfiles(store.ActiveId);
        UpdateSelection();
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }
}
