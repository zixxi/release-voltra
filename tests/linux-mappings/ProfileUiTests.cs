using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Voltra.Linux;
using Voltra.Linux.Views;

internal static class ProfileUiTests
{
    internal static void Run()
    {
        var folder = Directory.CreateTempSubdirectory();
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", folder.FullName);
        Exception? failure = null;
        try
        {
            AppBuilder.Configure<App>().UsePlatformDetect().AfterSetup(builder =>
            {
                var desktop = (IClassicDesktopStyleApplicationLifetime)builder.Instance!.ApplicationLifetime!;
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        var main = (MainWindow)desktop.MainWindow!;
                        main.Width = main.MinWidth;
                        main.Height = main.MinHeight;
                        await Task.Delay(250);
                        var picker = main.FindControl<ComboBox>("ProfilePicker")!;
                        var manage = main.FindControl<Button>("ManageProfiles")!;
                        foreach (string page in new[] { "OledButton", "SettingsButton", "DashboardButton" })
                        {
                            Click(main, page);
                            await Task.Delay(250);
                            Point top = picker.TranslatePoint(default, main)!.Value;
                            Point bottom = manage.TranslatePoint(new(manage.Bounds.Width, manage.Bounds.Height), main)!.Value;
                            if (!picker.IsEffectivelyVisible || top.Y < 0 || bottom.Y > main.ClientSize.Height ||
                                bottom.X > ((Grid)main.FindControl<Border>("NavMarker")!.Parent!).Bounds.Width + 18)
                                throw new Exception($"Profile sidebar out of bounds on {page}: {top}, {bottom}");
                        }
                        Click(main, "ManageProfiles");
                        var dialog = desktop.Windows.OfType<ProfileWindow>().Single();
                        dialog.FindControl<TextBox>("ProfileName")!.Text = "Streaming";
                        Click(dialog, "CreateProfile");
                        Click(dialog, "CloseDialog");
                        await Task.Delay(100);
                        if (picker.SelectedItem?.ToString() != "Streaming") throw new Exception("Creation did not activate profile");
                        var dashboard = main.FindControl<DashboardView>("Dashboard")!;
                        async Task Edit(string label, string action)
                        {
                            dashboard.FindControl<Button>("EditMapping")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                            var edit = desktop.Windows.OfType<MappingWindow>().Single();
                            var field = edit.GetVisualDescendants().OfType<TextBox>()
                                .Single(box => AutomationProperties.GetName(box) == "Knob 1 display name");
                            field.Text = label;
                            Click(edit, action);
                            await Task.Delay(350);
                        }
                        await Edit("Voice", "SaveMapping");
                        await Edit("Discard this", "CancelMapping");
                        picker.SelectedIndex = 0;
                        await Task.Delay(200);
                        var saved = LinuxSettingsService.Read(out var error);
                        if (error != null || saved.Profiles.Items.Length != 2 || saved.Profiles.Active.Name != "Default" ||
                            saved.Channels[0].Label != "Spotify" || saved.Profiles.Items[1].Channels[0].Label != "Voice")
                            throw new Exception("Sidebar selection did not persist");
                        Console.WriteLine("PASS profile manager, selector and all three pages at minimum window size");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { desktop.TryShutdown(); }
                }, DispatcherPriority.ApplicationIdle);
            }).StartWithClassicDesktopLifetime([]);
        }
        finally { folder.Delete(true); }
        if (failure != null) throw new Exception("Profile UI regression", failure);
    }

    private static void Click(Window window, string name) =>
        window.FindControl<Button>(name)!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
}
