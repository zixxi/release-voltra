using System.Security;
using Microsoft.Win32;

namespace Voltra;

internal static class WindowsStartup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Voltra";
    private const string UninstallKey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{605834B7-403B-4FAC-9BF3-04D5C5A90AE7}_is1";
    private const string InstallRequired = "Open an installed copy of Voltra to change Windows startup.";

    internal static (bool Installed, bool? Registered, string? Message) Read()
    {
        bool installed = false;
        try
        {
            string? executable = InstalledExecutable();
            if (executable is null) return (false, false, InstallRequired);
            installed = true;
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            object? value = run?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            return (true, IsRegistered(value, executable), null);
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            return (installed, null, "Could not read Windows startup registration. " + ex.Message);
        }
    }

    internal static string? Synchronize(bool enable, Action? savePreference = null)
    {
        try
        {
            string? executable = InstalledExecutable();
            if (executable is null) return InstallRequired;
            string command = Command(executable);
            if (enable && command.Length > 260)
                return "Voltra's installed path is too long for Windows startup.";

            using var run = enable ? Registry.CurrentUser.CreateSubKey(RunKey)
                : Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            object? previous = run?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (enable ? IsRegistered(previous, executable) : previous is null)
            {
                savePreference?.Invoke();
                return null;
            }

            RegistryValueKind kind = previous is null ? RegistryValueKind.Unknown : run!.GetValueKind(ValueName);
            if (enable)
                (run ?? throw new IOException("The Windows startup key is unavailable."))
                    .SetValue(ValueName, command, RegistryValueKind.String);
            else
                run!.DeleteValue(ValueName, throwOnMissingValue: false);

            try { savePreference?.Invoke(); }
            catch (Exception ex) when (IsSettingsError(ex))
            {
                try
                {
                    if (previous is null) run!.DeleteValue(ValueName, throwOnMissingValue: false);
                    else run!.SetValue(ValueName, previous, kind);
                }
                catch (Exception rollback) when (IsSettingsError(rollback))
                {
                    MixerDiagnostics.Log("Could not restore Windows startup registration: " + rollback.Message);
                    return "Could not save the startup preference or restore its previous registration.";
                }
                return "Could not save the startup preference. " + ex.Message;
            }
            return null;
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            return "Could not update Windows startup. " + ex.Message;
        }
    }

    internal static int SyncForSetup(string[] args)
    {
        if (!string.Equals(args[0], "--sync-startup", StringComparison.OrdinalIgnoreCase))
            return 2;
        string? replyPath = null;
        if (args.Length != 1)
        {
            if (args.Length != 3 || args[1] != "--result-file" || !Path.IsPathFullyQualified(args[2]))
                return 2;
            replyPath = args[2];
        }

        int exitCode;
        try
        {
            string? error = Synchronize(MixerSettings.ReadStartupPreference());
            exitCode = error is null ? 0 : 1;
        }
        catch (Exception ex) when (IsSettingsError(ex) || ex is System.Text.Json.JsonException)
        {
            exitCode = 1;
        }

        try
        {
            if (replyPath is not null)
            {
                // Setup owns the temporary directory. Never overwrite an existing file.
                using var result = new FileStream(replyPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                result.WriteByte((byte)('0' + exitCode));
            }
        }
        catch (Exception ex) when (IsSettingsError(ex))
        {
            return 1;
        }
        return exitCode;
    }

    internal static string? InstalledExecutable()
    {
        if (Environment.ProcessPath is not { } executable ||
            !string.Equals(Path.GetFileName(executable), "Voltra.exe", StringComparison.OrdinalIgnoreCase))
            return null;

        // Read both views so upgrades also recognize the existing 32-bit Inno uninstall record.
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
            using var uninstall = currentUser.OpenSubKey(UninstallKey);
            if (uninstall?.GetValue("InstallLocation") is not string location || !Path.IsPathFullyQualified(location))
                continue;
            string installed = Path.GetFullPath(Path.Combine(location, "Voltra.exe"));
            if (string.Equals(installed, executable, StringComparison.OrdinalIgnoreCase) && File.Exists(installed))
                return installed;
        }
        return null;
    }

    internal static bool IsSettingsError(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or ArgumentException;

    private static bool IsRegistered(object? value, string executable) =>
        value is string command && string.Equals(command, Command(executable), StringComparison.OrdinalIgnoreCase);

    private static string Command(string executable) => $"\"{executable}\" --minimized";
}
