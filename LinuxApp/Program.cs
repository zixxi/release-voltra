using Avalonia;

namespace Voltra.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.Error.WriteLine("Use WindowsApp for Windows. This build requires a Linux desktop session.");
            return;
        }

        FileStream instance;
        try
        {
            Directory.CreateDirectory(LinuxSettingsService.Folder);
            instance = File.Open(Path.Combine(LinuxSettingsService.Folder, "instance.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Unable to open the Voltra settings directory: {ex.Message}");
            return;
        }
        using (instance)
        {
            try { instance.Lock(0, 1); }
            catch (IOException)
            {
                Console.Error.WriteLine("Voltra is already running.");
                return;
            }

            AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
    }
}
