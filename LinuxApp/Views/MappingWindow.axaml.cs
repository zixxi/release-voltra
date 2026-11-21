using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Voltra.Linux.Views;

public sealed partial class MappingWindow : Window
{
    private sealed record Choice(string Name, string Target)
    {
        public override string ToString() => Name;
    }

    private readonly TextBox[] names = new TextBox[5];
    private readonly ComboBox[] targets = new ComboBox[5];

    public MappingWindow() => InitializeComponent();

    internal MappingWindow(Channel[] channels, AudioApplication[] applications, bool formatAppNames) : this()
    {
        const string master = "System volume";
        Choice[] apps = [new(master, Settings.Master), .. applications
            .Where(item => item.Target != Settings.Master)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select(item => new Choice(group.Count() == 1
                ? item.Name : $"{item.Name} · {item.Target}", item.Target)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)];
        for (int i = 0; i < names.Length; i++)
        {
            var text = new TextBlock { Text = (i + 1).ToString(), VerticalAlignment = VerticalAlignment.Center };
            var target = targets[i] = new ComboBox
            {
                IsEditable = true, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Center,
                ItemsSource = apps, PlaceholderText = "Application ID", IsTextSearchEnabled = false
            };
            var name = names[i] = new TextBox { Text = channels[i].Label, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(target, $"Knob {i + 1} application");
            AutomationProperties.SetName(name, $"Knob {i + 1} display name");
            var matched = Array.Find(apps, item => item.Target.Equals(channels[i].Target, StringComparison.OrdinalIgnoreCase));
            if (matched is not null) target.SelectedItem = matched;
            target.Text = matched?.Name ?? channels[i].Target;
            target.SelectionChanged += (_, _) =>
            {
                if (target.SelectedItem is not Choice selected) return;
                string suggestion = selected.Target == Settings.Master ? "Master" : formatAppNames ? selected.Name : selected.Target;
                if (formatAppNames && suggestion.Length > 0) suggestion = char.ToUpperInvariant(suggestion[0]) + suggestion[1..];
                name.Text = suggestion[..Math.Min(suggestion.Length, 18)];
            };
            Grid.SetColumn(target, 1);
            Grid.SetColumn(name, 2);
            var row = new Grid { ColumnDefinitions = new("58,51*,49*"), ColumnSpacing = 9, Height = 40 };
            row.Children.Add(text);
            row.Children.Add(target);
            row.Children.Add(name);
            var rule = new Border();
            rule.Classes.Add("rule");
            Rows.Children.Add(rule);
            Rows.Children.Add(row);
        }
        CancelMapping.Click += (_, _) => Close();
        SaveMapping.Click += (_, _) =>
        {
            var result = new Channel[5];
            for (int i = 0; i < result.Length; i++)
            {
                string app = (targets[i].Text ?? "").Trim();
                string label = (names[i].Text ?? "").Trim();
                string target = Array.Find(apps, item => item.Name.Equals(app, StringComparison.OrdinalIgnoreCase))?.Target ?? app;
                result[i] = new(label.Length == 0 ? $"Knob {i + 1}" : label, target);
            }
            Close(result);
        };
    }
}
