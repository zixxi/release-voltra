using Avalonia.Controls;

namespace Voltra.Linux.Views;

public sealed partial class ProfileWindow : Window
{
    public ProfileWindow() => InitializeComponent();

    internal ProfileWindow(MainViewModel model) : this()
    {
        var store = model.Settings.Profiles;
        string? confirmDelete = null;
        MappingProfile<Channel> Selected() => (MappingProfile<Channel>)Picker.SelectedItem!;
        void Select(string id)
        {
            Picker.ItemsSource = store.Items;
            Picker.SelectedItem = store.Items.First(p => p.Id == id);
            DeleteProfile.IsEnabled = store.Items.Length > 1;
        }
        Picker.SelectionChanged += (_, _) =>
        {
            confirmDelete = null;
            Feedback.Text = "";
            DeleteProfile.Content = "Delete";
            if (Picker.SelectedItem is MappingProfile<Channel> p) ProfileName.Text = p.Name;
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
        DeleteProfile.Click += (_, _) =>
        {
            if (confirmDelete == Selected().Id) Apply(() => store.Delete(Selected().Id));
            else
            {
                confirmDelete = Selected().Id;
                Feedback.Text = $"Delete profile “{Selected().Name}”?";
                DeleteProfile.Content = "Confirm delete";
            }
        };
        ActivateProfile.Click += (_, _) => { Apply(() => store.Activate(Selected().Id)); Close(); };
        CloseDialog.Click += (_, _) => Close();
    }
}
