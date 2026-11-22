using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

namespace Voltra.Linux.Views;

public sealed partial class MappingWindow : Window
{
    private sealed record Choice(string Name, string? Target);
    private sealed record MappingRow(TextBox Name, ComboBox Picker, Func<string> ReadTarget);
    private const int PopupHeight = 178;

    private readonly MappingRow[] rows = new MappingRow[5];
    private readonly CancellationTokenSource animation = new();
    private Channel[]? result;
    private bool closing, allowClose;
    private bool reducedMotion = true;

    public MappingWindow() => InitializeComponent();

    internal MappingWindow(Channel[] channels, AudioApplication[] applications, bool formatAppNames, bool reducedMotion) : this()
    {
        this.reducedMotion = reducedMotion;
        var custom = new Choice("Enter application ID…", null);
        Choice[] apps = [new("System volume", Settings.Master), custom, .. applications
            .Where(item => item.Target != Settings.Master)
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.Select(item => new Choice(group.Count() == 1
                ? item.Name : $"{item.Name} · {item.Target}", item.Target)))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)];
        var textTemplate = new FuncDataTemplate<Choice>((choice, _) =>
        {
            var text = new TextBlock { Text = choice?.Name, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(text, choice?.Name);
            return text;
        });
        var fieldTheme = (ControlTheme)Resources["MappingTarget"]!;
        for (int i = 0; i < rows.Length; i++)
            rows[i] = AddRow(i + 1, channels[i], apps, custom, textTemplate, fieldTheme, formatAppNames);

        Opened += async (_, _) =>
        {
            if (!reducedMotion && DialogBody.RenderTransform is TranslateTransform shift)
                await InterfaceMotion.PlayAsync(DialogBody, shift, .25, 1, 8, 0, 170, animation.Token);
        };
        Closing += OnClosing;
        Closed += (_, _) => { animation.Cancel(); animation.Dispose(); };
        CancelMapping.Click += (_, _) => { if (!closing) Close(); };
        SaveMapping.Click += (_, _) =>
        {
            if (closing) return;
            result = rows.Select((row, i) =>
            {
                string name = (row.Name.Text ?? "").Trim();
                return new Channel(name.Length == 0 ? $"Knob {i + 1}" : name, row.ReadTarget());
            }).ToArray();
            Close(result);
        };
    }

    private MappingRow AddRow(int number, Channel channel, Choice[] apps, Choice custom, IDataTemplate textTemplate,
        ControlTheme fieldTheme, bool formatAppNames)
    {
        var index = new Border
        {
            Width = 25, Height = 25, CornerRadius = new CornerRadius(5),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = number.ToString(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        index.Bind(Border.BackgroundProperty, this.GetResourceObservable("Surface"));

        var input = new ComboBox
        {
            Theme = fieldTheme, ItemTemplate = textTemplate, HorizontalAlignment = HorizontalAlignment.Stretch,
            IsTextSearchEnabled = true, Height = 34, MaxDropDownHeight = PopupHeight
        };
        var editor = new TextBox { Watermark = "Application ID", Height = 34, IsVisible = false };
        var name = new TextBox { Text = channel.Label, Height = 34 };
        AutomationProperties.SetName(input, $"Knob {number} application");
        AutomationProperties.SetName(editor, $"Knob {number} application ID");
        AutomationProperties.SetName(name, $"Knob {number} display name");
        string target = channel.Target;
        bool updating = false;
        Popup? popup = null;
        input.TemplateApplied += (_, e) => popup = e.NameScope.Find<Popup>("PART_Popup");
        input.DropDownOpened += (_, _) =>
        {
            if (popup is null || input.TranslatePoint(default, DialogBody) is not Point anchor ||
                DialogFooter.TranslatePoint(default, DialogBody) is not Point footer) return;
            double above = anchor.Y - 4;
            double below = footer.Y - anchor.Y - input.Bounds.Height - 4;
            bool opensAbove = below < PopupHeight && above > below;
            popup.Placement = opensAbove ? PlacementMode.TopEdgeAlignedLeft : PlacementMode.BottomEdgeAlignedLeft;
            popup.VerticalOffset = opensAbove ? -4 : 4;
            double space = opensAbove ? above : below;
            int itemCount = Math.Clamp((int)(space - 8) / 34, 1, 5);
            input.MaxDropDownHeight = itemCount * 34 + 8;
        };
        SelectTarget();
        input.SelectionChanged += (_, _) =>
        {
            if (updating || input.SelectedItem is not Choice selected) return;
            if (selected == custom)
            {
                input.IsDropDownOpen = false;
                editor.Text = target;
                input.IsVisible = false;
                editor.IsVisible = true;
                editor.Focus();
                editor.SelectAll();
                return;
            }
            target = selected.Target!;
            string suggestion = target == Settings.Master ? "Master" : formatAppNames ? selected.Name : target;
            if (formatAppNames && suggestion.Length > 0) suggestion = char.ToUpperInvariant(suggestion[0]) + suggestion[1..];
            name.Text = suggestion[..Math.Min(suggestion.Length, 18)];
        };
        editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Escape)) return;
            e.Handled = true;
            if (e.Key == Key.Enter) target = (editor.Text ?? "").Trim();
            editor.IsVisible = false;
            input.IsVisible = true;
            SelectTarget();
            input.Focus();
        }, RoutingStrategies.Tunnel);
        var targetField = new Grid { Children = { input, editor } };
        Grid.SetColumn(targetField, 1);
        Grid.SetColumn(name, 2);
        var row = new Grid { ColumnDefinitions = new("42,54*,46*"), ColumnSpacing = 10, Height = 34 };
        row.Children.Add(index);
        row.Children.Add(targetField);
        row.Children.Add(name);
        Rows.Children.Add(row);
        return new MappingRow(name, input, () => editor.IsVisible ? (editor.Text ?? "").Trim() : target);

        void SelectTarget()
        {
            var selected = Array.Find(apps, item => item.Target?.Equals(target, StringComparison.OrdinalIgnoreCase) == true);
            updating = true;
            if (selected is null)
            {
                selected = new Choice(target.Length == 0 ? "Unassigned" : target, target);
                input.ItemsSource = new Choice[] { selected }.Concat(apps).ToArray();
            }
            else input.ItemsSource = apps;
            input.SelectedItem = selected;
            updating = false;
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose || reducedMotion || e.CloseReason is not (WindowCloseReason.WindowClosing or WindowCloseReason.Undefined) ||
            DialogBody.RenderTransform is not TranslateTransform shift) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        foreach (var row in rows) row.Picker.IsDropDownOpen = false;
        DialogBody.IsEnabled = false;
        DialogBody.IsHitTestVisible = false;
        double opacity = DialogBody.Opacity, offset = shift.Y;
        animation.Cancel();
        await InterfaceMotion.PlayAsync(DialogBody, shift, opacity, 0, offset, 7, 150, CancellationToken.None);
        if (!IsVisible) return;
        allowClose = true;
        Close(result);
    }
}
