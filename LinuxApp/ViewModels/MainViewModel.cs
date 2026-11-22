namespace Voltra.Linux;

internal sealed class MainViewModel : IAsyncDisposable
{
    private Settings settings;
    private Task save = Task.CompletedTask;
    private readonly SemaphoreSlim startup = new(1, 1);
    private string? message;

    internal Settings Settings => Volatile.Read(ref settings);
    internal string? Message => Volatile.Read(ref message);
    internal LinuxFirmwareUpdater Firmware { get; } = new();
    internal bool FirmwareDialogOpen { get; set; }
    internal LinuxDeviceService Device { get; }
    internal PipeWireAudioService Audio { get; }
    internal OledDisplayService Oled { get; }

    internal MainViewModel()
    {
        settings = LinuxSettingsService.Read(out message);
        Device = new LinuxDeviceService(() => Settings);
        Audio = new PipeWireAudioService(() => Settings, () => Device.Snapshot);
        Oled = new OledDisplayService(() => Settings, Device, Audio);
    }

    internal void Report(string? value) => Volatile.Write(ref message, value);

    internal void Change(Settings next)
    {
        Volatile.Write(ref settings, next);
        save = SaveAfterAsync(save, next);
    }

    private async Task SaveAfterAsync(Task previous, Settings value)
    {
        await previous;
        try { await LinuxSettingsService.SaveAsync(value); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Report($"Could not save preferences: {ex.Message}"); }
    }

    internal async Task<bool> SetStartupAsync(bool enabled)
    {
        await startup.WaitAsync();
        try
        {
            await LinuxStartupService.ApplyAsync(enabled);
            Change(Settings with { Startup = enabled });
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Could not update startup: {ex.Message}");
            return false;
        }
        finally { startup.Release(); }
    }

    internal async Task RestoreStartupAsync()
    {
        if (!Settings.Startup) return;
        await startup.WaitAsync();
        try { await LinuxStartupService.ApplyAsync(Settings.Startup); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Report($"Could not restore startup: {ex.Message}"); }
        finally { startup.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await startup.WaitAsync();
        try { await Task.WhenAll(save, Oled.DisposeAsync().AsTask(), Audio.DisposeAsync().AsTask(), Device.DisposeAsync().AsTask()); }
        finally { Firmware.Dispose(); startup.Release(); }
    }
}
