using Avalonia.Controls;

namespace Voltra.Linux.Views;

public sealed partial class ProfileWindow : Window
{
    private sealed record ProfileRow(MappingProfile<Channel> Profile, bool Active)
    {
        public string Name => Profile.Name;
    }

    public ProfileWindow() => InitializeComponent();

    internal ProfileWindow(MainViewModel model) : this()
    {
        var store = model.Settings.Profiles;
        MappingProfile<Channel> Selected() => ((ProfileRow)Picker.SelectedItem!).Profile;
        void Select(string id)
        {
            var rows = store.Items.Select(p => new ProfileRow(p, p.Id == store.ActiveId)).ToArray();
            Picker.ItemsSource = rows;
            Picker.SelectedItem = rows.First(row => row.Profile.Id == id);
            DeleteProfile.IsEnabled = store.Items.Length > 1;
        }
        Picker.SelectionChanged += (_, _) =>
        {
            Feedback.Text = "";
            if (Picker.SelectedItem is ProfileRow p) ProfileName.Text = p.Name;
        };
        Select(store.ActiveId);
        void Apply(Func<MappingProfiles<Channel>> change, bool keep = false)
        {
            string id = Selected().Id;
            try
            {
                store = change();
                model.Change(model.Settings.WithProfiles(store));
                Select(keep ? id : store.ActiveId);
            }
            catch (ArgumentException ex) { Feedback.Text = ex.Message; }
        }
        CreateProfile.Click += (_, _) => Apply(() => store.Create(ProfileName.Text ?? "",
            Enumerable.Range(1, 5).Select(i => new Channel($"Knob {i}", "")).ToArray()));
        DuplicateProfile.Click += (_, _) => Apply(() => store.Create(ProfileName.Text ?? "", Selected().Channels));
        RenameProfile.Click += (_, _) => Apply(() => store.Rename(Selected().Id, ProfileName.Text ?? ""), true);
        DeleteProfile.Click += async (_, _) =>
        {
            var profile = Selected();
            var confirm = new Window
            {
                Title = "Delete profile", Width = 320, SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false
            };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            var delete = new Button { Content = "Delete" };
            cancel.Click += (_, _) => confirm.Close(false);
            delete.Click += (_, _) => confirm.Close(true);
            confirm.Content = new StackPanel
            {
                Margin = new(16), Spacing = 12,
                Children =
                {
                    new TextBlock { Text = $"Delete profile “{profile.Name}”?",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 6,
                        Children = { cancel, delete } }
                }
            };
            if (await confirm.ShowDialog<bool>(this)) Apply(() => store.Delete(profile.Id));
        };
        ActivateProfile.Click += (_, _) => { Apply(() => store.Activate(Selected().Id)); Close(); };
        CloseDialog.Click += (_, _) => Close();
    }
}
