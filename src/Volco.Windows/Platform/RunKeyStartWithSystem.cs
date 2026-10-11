using Microsoft.Win32;
using Volco.App;
using Volco.App.Platform;

namespace Volco.Windows.Platform;

/// <summary>
/// <see cref="IStartWithSystem"/> through the per-user Run key (no admin rights needed), the
/// same entry Task Manager's Startup apps and Settings > Apps > Startup list. The command adds
/// <see cref="LaunchOptions.BackgroundFlag"/>, so a sign-in start stays in the tray.
/// </summary>
internal sealed class RunKeyStartWithSystem : IStartWithSystem
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    // Where Task Manager turns an entry off without removing it: the first byte is odd when off.
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Volco";

    private readonly string _command;

    public RunKeyStartWithSystem(string exePath)
        => _command = $"\"{exePath}\" {LaunchOptions.BackgroundFlag}";

    public bool IsEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string) return false;
            using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;
        }
    }

    public void SetEnabled(bool enabled)
    {
        using (var run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true))
        {
            if (enabled) run.SetValue(ValueName, _command, RegistryValueKind.String);
            else run.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        // Turning it on here also overrides an earlier "off" in Task Manager.
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
