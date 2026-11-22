namespace Voltra;

internal sealed partial class MixerForm
{
    private void ManageProfiles()
    {
        using var dialog = new Form
        {
            Text = "Manage profiles", ClientSize = new(416, 248), Font = Font,
            BackColor = palette.Background, ForeColor = palette.Text, ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new(96, 96)
        };
        dialog.HandleCreated += (_, _) => SetTitleBar(dialog.Handle, palette.Dark);
        var grid = Grid(1, 7);
        grid.Padding = new(16, 8, 16, 8);
        grid.ColumnStyles.Add(new(SizeType.Percent, 100));
        dialog.Controls.Add(grid);
        var store = config.Profiles;
        var picker = Choice([], -1, 380);
        picker.AccessibleName = "Profile to manage";
        var name = Entry("", "Profile name");
        name.Editor.MaxLength = MappingProfiles<Channel>.MaxName;
        picker.Dock = name.Dock = DockStyle.Fill;
        var error = Caption("", 9, muted: true);
        MappingProfile<Channel> Selected() => (MappingProfile<Channel>)picker.SelectedItem!;
        MixerButton? delete = null;
        void Select(string id)
        {
            picker.SetOptions([.. store.Items], Array.FindIndex(store.Items, p => p.Id == id));
            name.Editor.Text = Selected().Name;
            if (delete != null) delete.Enabled = store.Items.Length > 1;
        }
        picker.Committed += (_, _) => name.Editor.Text = Selected().Name;
        Select(store.ActiveId);
        void Apply(Func<MappingProfiles<Channel>> change, bool keep = false)
        {
            string id = Selected().Id;
            try
            {
                store = change();
                Persist(config.WithProfiles(store));
                Select(keep ? id : store.ActiveId);
                error.Text = "";
            }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        }
        var create = ActionButton("Create blank", () => Apply(() => store.Create(name.Editor.Text,
            Enumerable.Range(1, 5).Select(i => new Channel($"Knob {i}", "")).ToArray())), 118);
        var duplicate = ActionButton("Duplicate", () => Apply(() => store.Create(name.Editor.Text, Selected().Channels)), 108);
        var rename = ActionButton("Rename", () => Apply(() => store.Rename(Selected().Id, name.Editor.Text), true), 100);
        delete = ActionButton("Delete", () =>
        {
            if (store.Items.Length > 1 && MessageBox.Show(dialog, $"Delete profile “{Selected().Name}”?", "Delete profile",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                Apply(() => store.Delete(Selected().Id));
        }, 88);
        delete.Enabled = store.Items.Length > 1;
        var activate = ActionButton("Activate", () => { Apply(() => store.Activate(Selected().Id)); dialog.Close(); }, 100, true);
        var close = ActionButton("Close", dialog.Close, 88);
        var controls = new Control[] { Caption("Profile", 10), picker, Caption("Name for create, duplicate or rename", 10),
            name, Inline(create, duplicate, rename), error, Inline(delete, close, activate) };
        for (int i = 0; i < controls.Length; i++)
        {
            grid.RowStyles.Add(new(SizeType.Percent, i is 0 or 2 or 5 ? 10 : 17.5f));
            controls[i].Dock = DockStyle.Fill;
            grid.Controls.Add(controls[i], 0, i);
        }
        dialog.CancelButton = close;
        dialog.ShowDialog(this);
    }
}
