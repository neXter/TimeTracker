using Microsoft.Win32;

namespace TimeTracker;

/// <summary>Start at sign-in via the per-user Run key (no admin rights). See DESIGN §5.</summary>
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string ValueName = "TimeTracker";

    public static string Command(string exe) => $"\"{exe}\" --background";

    /// <summary>enabled | disabled (entry removed) | blocked (switched off in Windows Settings → Apps → Startup)</summary>
    public static string State()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        if (run?.GetValue(ValueName) is not string) return "disabled";
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
        if (approved?.GetValue(ValueName) is byte[] b && b.Length > 0 && (b[0] & 1) == 1) return "blocked";
        return "enabled";
    }

    public static string? CurrentCommand()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey);
        return run?.GetValue(ValueName) as string;
    }

    public static void Enable(string exePath)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        run.SetValue(ValueName, Command(exePath), RegistryValueKind.String);
    }

    public static void Disable()
    {
        using var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        run?.DeleteValue(ValueName, throwOnMissingValue: false);
        using var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
        approved?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
