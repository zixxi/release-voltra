using System.Text.Json;
using Voltra.Linux;

var settings = LinuxSettingsService.Read(out string? error);
if (error is not null) throw new InvalidOperationException(error);

var discover = new LinuxSerialDiscovery(args[0], args[1]);
await using var device = new LinuxDeviceService(() => Volatile.Read(ref settings), discover.Scan);
device.SetFrame(new DisplayFrame(Enumerable.Repeat((byte)0xAA, 1024).ToArray(), DisplayPage.Mixer, 0, 0));
IDisposable? held = null;
while (await Console.In.ReadLineAsync() is { } command && command != "quit")
{
    if (command == "hold") held = await device.HoldForFirmwareAsync(device.Snapshot, CancellationToken.None);
    if (command == "resume") { held?.Dispose(); held = null; }
    if (command == "automatic" || command.StartsWith("manual "))
    {
        var next = command == "automatic" ? settings with { AutoDetect = true } :
            settings with { AutoDetect = false, Port = command[7..] };
        Volatile.Write(ref settings, next);
        await LinuxSettingsService.SaveAsync(next);
    }
    var state = device.Snapshot;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        state.Status, state.Port, state.Firmware, state.Levels, state.Ports,
        Displayed = state.Displayed is not null, settings.AutoDetect, Preferred = settings.Port,
        device.Diagnostics
    }));
}
