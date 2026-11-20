using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Voltra.Linux.Views;

public sealed partial class OledView : UserControl
{
    public OledView() => InitializeComponent();

    internal void Setup(MainViewModel model, WriteableBitmap preview)
    {
        PreviewImage.Source = preview;
        PageMenu.ItemsSource = new[] { "Mixer levels", "Clock & date", "Weather", "Image / GIF", "PC uptime" };
        SeparatorMenu.ItemsSource = new[] { "Automatic", "Before channel five", "Off" };
        PageMenu.SelectedIndex = (int)model.Settings.Page;
        SeparatorMenu.SelectedIndex = (int)model.Settings.Separator;
        ClockFormat.IsChecked = model.Settings.TwelveHour;
        City.Text = model.Settings.City;
        UpdateSections();
        PageMenu.SelectionChanged += (_, _) =>
        {
            if (PageMenu.SelectedIndex < 0) return;
            model.Change(model.Settings with { Page = (DisplayPage)PageMenu.SelectedIndex });
            UpdateSections();
        };
        SeparatorMenu.SelectionChanged += (_, _) =>
        {
            if (SeparatorMenu.SelectedIndex >= 0) model.Change(model.Settings with { Separator = (SeparatorMode)SeparatorMenu.SelectedIndex });
        };
        ClockFormat.IsCheckedChanged += (_, _) => model.Change(model.Settings with { TwelveHour = ClockFormat.IsChecked == true });
        ApplyCity.Click += (_, _) => model.Change(model.Settings with { City = (City.Text ?? "").Trim() });
        ChooseMedia.Click += async (_, _) =>
        {
            try
            {
                var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (storage is null || !storage.CanOpen) { model.Report("File picker unavailable on this desktop."); return; }
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Choose an OLED image or GIF",
                    FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.bmp", "*.webp"] }]
                });
                if (files.Count == 0) return;
                string? path = files[0].TryGetLocalPath();
                if (path is null) { model.Report("Choose a local image file."); return; }
                model.Change(model.Settings with { Media = path });
                UpdateSections();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            { model.Report($"Could not choose an image: {ex.Message}"); }
        };

        void UpdateSections()
        {
            var config = model.Settings;
            ClockRow.IsVisible = config.Page == DisplayPage.Clock;
            WeatherRow.IsVisible = config.Page == DisplayPage.Weather;
            MediaRow.IsVisible = config.Page == DisplayPage.Image;
            SeparatorRow.IsVisible = config.Page == DisplayPage.Mixer;
            MediaName.Text = config.Media.Length == 0 ? "No file selected" : Path.GetFileName(config.Media);
        }
    }

    internal void Refresh(DeviceSnapshot device, DisplaySnapshot frame) =>
        DisplayStatus.Text = frame.Message.Length == 0 ? device.Display : frame.Message;

    internal void InvalidatePreview() => PreviewImage.InvalidateVisual();
}
