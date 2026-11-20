namespace Voltra.Linux;

internal static class LinuxStartupService
{
    private static string DesktopFile => Path.Combine(LinuxSettingsService.ConfigHome, "autostart", "voltra.desktop");

    internal static Task ApplyAsync(bool enabled) => Task.Run(() =>
    {
        if (!enabled)
        {
            File.Delete(DesktopFile);
            return;
        }
        string executable = Environment.ProcessPath ?? throw new IOException("Could not locate Voltra.");
        // A framework-hosted development launch needs both the dotnet executable and the DLL.
        string invocation = Path.GetFileName(executable) == "dotnet"
            ? $"{Quote(executable)} {Quote(typeof(LinuxStartupService).Assembly.Location)}" : Quote(executable);
        LinuxSettingsService.AtomicWrite(DesktopFile, "[Desktop Entry]\nType=Application\nName=Voltra\n" +
            $"Exec={invocation} --minimized\nTerminal=false\n");
    });

    internal static async Task<bool> IsTrayAvailableAsync(CancellationToken token)
    {
        try
        {
            string result = await ProcessCommand.RunAsync("busctl", token, "--user", "--no-pager", "--timeout=1s",
                "get-property", "org.kde.StatusNotifierWatcher", "/StatusNotifierWatcher",
                "org.kde.StatusNotifierWatcher", "IsStatusNotifierHostRegistered");
            return result.Trim() == "b true";
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or TimeoutException)
        { return false; }
    }

    private static string Quote(string value)
    {
        if (value.Any(char.IsControl)) throw new IOException("The executable path contains control characters.");
        // Escape the Exec command first, then the desktop entry string containing it.
        string quoted = value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("`", "\\`")
            .Replace("$", "\\$").Replace("%", "%%");
        return "\"" + quoted.Replace("\\", "\\\\") + "\"";
    }
}
