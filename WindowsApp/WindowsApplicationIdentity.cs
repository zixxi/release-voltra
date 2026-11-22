using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Voltra;

internal static class WindowsApplicationIdentity
{
    [ThreadStatic] private static StringBuilder? className;

    internal static uint ForegroundPid(out nint window, out string windowClass)
    {
        windowClass = "";
        window = GetForegroundWindow();
        if (window == 0 || window == GetDesktopWindow() || window == GetShellWindow()) return 0;
        GetWindowThreadProcessId(window, out uint pid);
        if (pid == Environment.ProcessId) return 0;
        // Common dialogs and the desktop can be hosted by otherwise audible apps.
        var name = className ??= new StringBuilder(64);
        if (GetClassNameW(window, name, name.Capacity) == 0) return 0;
        windowClass = name.ToString();
        switch (windowClass)
        {
            case "#32770": case "Progman": case "WorkerW": case "Shell_TrayWnd": return 0;
            case "ApplicationFrameWindow":
                nint child = FindWindowExW(window, 0, "Windows.UI.Core.CoreWindow", null);
                if (child == 0) return 0;
                GetWindowThreadProcessId(child, out pid);
                break;
        }
        return pid;
    }

    internal static AudioApplication? Read(uint pid)
    {
        if (pid == 0 || pid == Environment.ProcessId) return null;
        using var handle = OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) return null;
        int size = 512;
        var buffer = new StringBuilder(size);
        if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size))
        {
            if (Marshal.GetLastWin32Error() != 122) return null;
            size = 32768;
            buffer.EnsureCapacity(size);
            if (!QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return null;
        }
        string path = buffer.ToString();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(windows + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        string stem = Path.GetFileNameWithoutExtension(path);
        string key = path;
        uint length = 0;
        if (AudioApplication.SharedHost(path))
            key += "#" + pid; // A shared runtime does not imply a shared application.
        else if (GetApplicationUserModelId(handle, ref length, null) == 122 && length is > 1 and < 4096)
        {
            var id = new StringBuilder((int)length);
            if (GetApplicationUserModelId(handle, ref length, id) == 0) key = "app:" + id;
        }
        string name = stem;
        string image = path;
        try
        {
            var metadata = FileVersionInfo.GetVersionInfo(path);
            if (key == path && CompanionOwner(path, metadata) is string owner) key = image = owner;
            string? product = metadata.ProductName?.Trim();
            if (!string.IsNullOrWhiteSpace(product) && product.Length <= 100) name = product;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        return new(key, path, name, HasGameSignature(image));
    }

    // Some desktop apps ship an adjacent ProductAudio.exe / ProductHelper.exe.
    // Require a matching product AND company on the actual parent image. A
    // common folder or similarly named executable alone cannot establish identity.
    private static readonly string[] CompanionSuffixes = ["audio", "renderer", "helper"];
    internal static string? CompanionOwner(string path, FileVersionInfo metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata.ProductName) || string.IsNullOrWhiteSpace(metadata.CompanyName)) return null;
        string stem = Path.GetFileNameWithoutExtension(path);
        foreach (string suffix in CompanionSuffixes)
        {
            if (!stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            string parent = stem[..^suffix.Length].TrimEnd('-', '_', ' ');
            if (parent.Length == 0) return null;
            string image = Path.Combine(Path.GetDirectoryName(path)!, parent + ".exe");
            if (!File.Exists(image) || AudioApplication.SharedHost(image)) return null;
            var candidate = FileVersionInfo.GetVersionInfo(image);
            return string.Equals(candidate.ProductName, metadata.ProductName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.CompanyName, metadata.CompanyName, StringComparison.OrdinalIgnoreCase) ? image : null;
        }
        return null;
    }

    internal static bool MinecraftWindow(nint window, string windowClass, AudioApplication app)
    {
        if (windowClass is not ("GLFW30" or "LWJGL")) return false;
        string stem = Path.GetFileNameWithoutExtension(app.Path);
        if (!stem.Equals("javaw", StringComparison.OrdinalIgnoreCase) &&
            !stem.Equals("java", StringComparison.OrdinalIgnoreCase)) return false;
        var title = new StringBuilder(96);
        if (GetWindowTextW(window, title, title.Capacity) == 0) return false;
        string text = title.ToString();
        return text == "Minecraft" || text.StartsWith("Minecraft ", StringComparison.Ordinal) ||
            text.StartsWith("Minecraft*", StringComparison.Ordinal);
    }

    internal static bool HasGameSignature(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory)) return false;
        return !IsUtility(stem) && (
            (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) &&
             Directory.Exists(Path.Combine(directory, stem + "_Data"))) ||
            (stem.EndsWith("-Win64-Shipping", StringComparison.OrdinalIgnoreCase) &&
             Path.GetFileName(directory).Equals("Win64", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "Binaries", StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly string[] Utilities = [ "launcher", "crash", "updater", "helper", "editor", "server", "discord", "spotify",
            "chrome", "firefox", "msedge", "browser", "steamwebhelper" ];

    private static bool IsUtility(string name) => Utilities.Any(word => name.Contains(word, StringComparison.OrdinalIgnoreCase));

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowTextW(nint window, StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string name, string? title);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, [Out] StringBuilder name, int size);
    [DllImport("kernel32.dll")] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetApplicationUserModelId(SafeProcessHandle process, ref uint size, StringBuilder? name);
}
