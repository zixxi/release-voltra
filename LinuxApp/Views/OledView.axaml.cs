using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace Voltra.Linux.Views;

public sealed partial class OledView : UserControl
{
    internal static readonly string[] PageNames = ["Mixer levels", "Clock & date", "Weather", "Image or GIF", "PC uptime"];
    private readonly RadioButton[] pages = new RadioButton[PageNames.Length];
    public OledView() => InitializeComponent();

    internal void Setup(MainViewModel model, WriteableBitmap preview)
    {
        Preview.Setup("Live preview", 212, preview);
        for (int i = 0; i < pages.Length; i++)
        {
            var page = (DisplayPage)i;
            var option = pages[i] = new RadioButton { GroupName = "OledPage", Content = PageNames[i] };
            option.Classes.Add("idle");
            option.IsCheckedChanged += (_, _) =>
            {
                if (option.IsChecked != true || model.Settings.Page == page) return;
                model.Change(model.Settings with { Page = page });
                UpdateSections(page);
            };
            PageChoices.Children.Add(option);
        }
        UpdateSections(model.Settings.Page);
        SeparatorMenu.ItemsSource = new[] { "Automatic (Master)", "Always separate channel 5", "Disabled" };
        SeparatorMenu.SelectedIndex = (int)model.Settings.Separator;
        TwelveHour.IsChecked = model.Settings.TwelveHour;
        TwentyFourHour.IsChecked = !model.Settings.TwelveHour;
        City.Text = model.Settings.City;
        SeparatorMenu.SelectionChanged += (_, _) =>
        {
            if (SeparatorMenu.SelectedIndex >= 0)
                model.Change(model.Settings with { Separator = (SeparatorMode)SeparatorMenu.SelectedIndex });
        };
        TwelveHour.IsCheckedChanged += (_, _) =>
        {
            if (TwelveHour.IsChecked == true) model.Change(model.Settings with { TwelveHour = true });
        };
        TwentyFourHour.IsCheckedChanged += (_, _) =>
        {
            if (TwentyFourHour.IsChecked == true) model.Change(model.Settings with { TwelveHour = false });
        };
        void SaveCity() => model.Change(model.Settings with { City = (City.Text ?? "").Trim() });
        ApplyCity.Click += (_, _) => SaveCity();
        City.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; SaveCity(); } };
        WeatherSource.Click += async (_, _) =>
        {
            var launcher = TopLevel.GetTopLevel(this)?.Launcher;
            if (launcher is null || !await launcher.LaunchUriAsync(new Uri("https://open-meteo.com/")))
                model.Report("Open https://open-meteo.com/ in your browser.");
        };
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
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            { model.Report($"Could not choose an image: {ex.Message}"); }
        };
    }

    private void UpdateSections(DisplayPage page)
    {
        pages[(int)page].IsChecked = true;
        ClockRow.IsVisible = page == DisplayPage.Clock;
        WeatherRow.IsVisible = page == DisplayPage.Weather;
        MediaRow.IsVisible = page == DisplayPage.Image;
        SeparatorRow.IsVisible = page == DisplayPage.Mixer;
    }

    internal void Refresh(Settings config, DeviceSnapshot device, DisplaySnapshot frame)
    {
        if (pages[(int)config.Page].IsChecked != true) UpdateSections(config.Page);
        Preview.Refresh(device);
        if (config.Page == DisplayPage.Weather) WeatherStatus.Text = frame.Message;
        if (config.Page == DisplayPage.Image)
            MediaName.Text = frame.Message.Length > 0 ? frame.Message :
                config.Media.Length == 0 ? "PNG, JPEG, BMP, GIF, or WebP" : Path.GetFileName(config.Media);
    }

    internal void InvalidatePreview() => Preview.InvalidatePixels();
}
