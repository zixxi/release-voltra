using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;

namespace Voltra.Linux.Views;

public sealed partial class RuleWindow : Window
{
    private sealed record Choice(string Name, string Id)
    {
        public override string ToString() => $"{Name}  ·  {Id}";
    }
    public RuleWindow() => InitializeComponent();

    internal RuleWindow(string title, string description, string[] rules, AudioApplication[] apps) : this()
    {
        Title = Heading.Text = title;
        Description.Text = description;
        var entries = new ObservableCollection<string>(rules);
        Entries.ItemsSource = entries;
        AddEntry.IsEnabled = RemoveEntry.IsEnabled = false;
        Choices.SelectionChanged += (_, _) => AddEntry.IsEnabled = Choices.SelectedItem is Choice;
        Entries.SelectionChanged += (_, _) => RemoveEntry.IsEnabled = Entries.SelectedItem is string;
        void Add(string id)
        {
            if (!entries.Contains(id)) entries.Add(id);
            Entries.SelectedItem = id;
        }
        AddEntry.Click += (_, _) => { if (Choices.SelectedItem is Choice choice) Add(choice.Id); };
        RemoveEntry.Click += (_, _) => { if (Entries.SelectedItem is string id) entries.Remove(id); };
        Browse.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Choose a Linux executable" });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not string path) return;
            try
            {
                var file = new FileInfo(path);
                if (file.ResolveLinkTarget(true) is { } target) file = new(target.FullName);
                if (!OperatingSystem.IsLinux() || !file.Exists || file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    (File.GetUnixFileMode(file.FullName) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0)
                { Description.Text = "Choose an executable Linux file."; return; }
                Add("exe:" + file.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Description.Text = ex.Message; }
        };
        Cancel.Click += (_, _) => Close();
        Save.Click += (_, _) => Close(entries.Order(StringComparer.Ordinal).ToArray());
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !Choices.IsDropDownOpen) Close(); };
        var running = apps.SelectMany(app => (app.RuleIds ?? []).Select(id => new Choice(app.Name, id))).ToArray();
        Choices.ItemsSource = running;
        Opened += async (_, _) =>
        {
            var installed = await Task.Run(Installed);
            Choices.ItemsSource = running.Concat(installed).DistinctBy(c => c.Id)
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        };
    }

    private static List<Choice> Installed()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string user = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(home, ".local/share");
        string system = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share";
        var result = new List<Choice>();
        foreach (var root in system.Split(':').Prepend(user).Where(Path.IsPathFullyQualified))
        {
            try
            {
                var folder = Path.Combine(root, "applications");
                if (!Directory.Exists(folder)) continue;
                foreach (var file in Directory.GetFiles(folder, "*.desktop"))
                {
                    try
                    {
                        var lines = File.ReadLines(file).SkipWhile(l => l != "[Desktop Entry]").Skip(1)
                            .TakeWhile(l => !l.StartsWith('[')).ToArray();
                        if (lines.Contains("Hidden=true") || lines.Contains("NoDisplay=true") || !lines.Contains("Type=Application")) continue;
                        if (lines.FirstOrDefault(l => l.StartsWith("Name=", StringComparison.Ordinal)) is { Length: > 5 } name)
                            result.Add(new(name[5..], "app:" + Path.GetFileNameWithoutExtension(file)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
}
