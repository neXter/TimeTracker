using System.Diagnostics;
using Microsoft.Win32;

namespace TimeTracker;

/// <summary>
/// Per-user "installer" built into the exe: copies it to %LOCALAPPDATA%\Programs\TimeTracker, registers autostart and an
/// Apps &amp; features entry. No admin rights, no Start-menu or desktop shortcut (see DESIGN §5).
/// </summary>
internal static class Installer
{
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string DataDir => Environment.GetEnvironmentVariable("TIMETRACKER_DATA") ?? Path.Combine(LocalAppData, "TimeTracker");
    public static string InstallDir => Path.Combine(LocalAppData, "Programs", "TimeTracker");
    public static string InstalledExe => Path.Combine(InstallDir, "TimeTracker.exe");
    public static string CurrentExe => Environment.ProcessPath ?? InstalledExe;
    /// <summary>The Windows computer name. TIMETRACKER_DEVICE_NAME overrides it (used to make demo screenshots).</summary>
    public static string DeviceName => Environment.GetEnvironmentVariable("TIMETRACKER_DEVICE_NAME") is { Length: > 0 } n ? n : Environment.MachineName;

    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TimeTracker";
    /// <summary>
    /// Empty normally. When TIMETRACKER_DATA points at another data folder (development, tests, demo screenshots) the
    /// instance gets its own lock and event names, so it can run next to a real installed copy without touching it.
    /// </summary>
    public static string InstanceSuffix
    {
        get
        {
            var d = Environment.GetEnvironmentVariable("TIMETRACKER_DATA");
            if (string.IsNullOrEmpty(d)) return "";
            var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(d).ToLowerInvariant()));
            return "." + Convert.ToHexString(hash)[..8];
        }
    }
    public static string MutexName => @"Local\TimeTracker.SingleInstance" + InstanceSuffix;
    public static string QuitEventName => @"Local\TimeTracker.Quit" + InstanceSuffix;
    public static string ShowEventName => @"Local\TimeTracker.Show" + InstanceSuffix;
    public static string SettingsEventName => @"Local\TimeTracker.Settings" + InstanceSuffix;
    public static string FlyoutEventName => @"Local\TimeTracker.Flyout" + InstanceSuffix;

    public static string VersionText => typeof(Installer).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Version of the installed copy (from the Apps & features entry), or null when not installed.</summary>
    public static System.Version? InstalledVersion
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(UninstallKey);
                return System.Version.TryParse(k?.GetValue("DisplayVersion") as string, out var v) ? v : null;
            }
            catch { return null; }
        }
    }

    public static System.Version CurrentVersion => System.Version.TryParse(VersionText, out var v) ? v : new System.Version(0, 0);

    public static bool IsInstalledCopy => SamePath(CurrentExe, InstalledExe);
    public static bool IsInstalled => File.Exists(InstalledExe) && Registry.CurrentUser.OpenSubKey(UninstallKey) != null;

    /// <summary>Running processes of the installed program file (the one an update or uninstall is about to replace).</summary>
    private static List<Process> InstalledCopyProcesses()
    {
        var me = Environment.ProcessId;
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName("TimeTracker"))
        {
            try { if (p.Id != me && p.MainModule?.FileName is { } f && SamePath(f, InstalledExe)) list.Add(p); }
            catch { /* process ended or is not accessible */ }
        }
        return list;
    }

    /// <summary>Asks the installed copy to exit, then ends it if it does not. Other copies (portable, development) are left alone.</summary>
    public static void StopOthers()
    {
        try { using var quit = EventWaitHandle.OpenExisting(QuitEventName); quit.Set(); } catch { }
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (InstalledCopyProcesses().Count == 0) return;
            Thread.Sleep(250);
        }
        foreach (var p in InstalledCopyProcesses())
        {
            try { p.Kill(true); p.WaitForExit(3000); } catch { }
        }
    }

    public static void Install()
    {
        StopOthers();
        Directory.CreateDirectory(InstallDir);
        string srcDir = Path.GetDirectoryName(CurrentExe)!;
        bool multiFile = File.Exists(Path.Combine(srcDir, "TimeTracker.dll"));
        if (!SamePath(srcDir, InstallDir))
        {
            if (multiFile) CopyDir(srcDir, InstallDir);
            else Retry(() => File.Copy(CurrentExe, InstalledExe, true));
        }

        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            long size = 0;
            try { size = new FileInfo(InstalledExe).Length / 1024; } catch { }
            k.SetValue("DisplayName", "TimeTracker");
            k.SetValue("DisplayVersion", VersionText);
            k.SetValue("Publisher", "TimeTracker");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("DisplayIcon", InstalledExe);
            k.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            k.SetValue("EstimatedSize", (int)size, RegistryValueKind.DWord);
        }
        if (Autostart.State() == "disabled") Autostart.Enable(InstalledExe);
        else Autostart.Enable(InstalledExe);   // keep the entry pointing at the installed exe (a "blocked" flag stays untouched)
    }

    public static void Launch(string args)
    {
        Process.Start(new ProcessStartInfo(InstalledExe, args) { UseShellExecute = false, WorkingDirectory = InstallDir });
    }

    /// <summary>Removes autostart and the Apps entry, then deletes the program files after this process has exited.</summary>
    public static void Uninstall(bool deleteData)
    {
        Autostart.Disable();
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        string dir = InstallDir;
        string cmd = $"/c ping 127.0.0.1 -n 4 >nul & rmdir /s /q \"{dir}\"" + (deleteData ? $" & rmdir /s /q \"{DataDir}\"" : "");
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }

    private static void Retry(Action a)
    {
        for (int i = 0; ; i++)
        {
            try { a(); return; }
            catch (IOException) when (i < 20) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) when (i < 20) { Thread.Sleep(300); }
        }
    }

    private static void CopyDir(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) Retry(() => File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true));
        foreach (var d in Directory.GetDirectories(src)) CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
    }
}

internal static class Log
{
    private static readonly object L = new();
    public static void Write(string msg)
    {
        try
        {
            lock (L)
            {
                var p = Path.Combine(Installer.DataDir, "log.txt");
                Directory.CreateDirectory(Installer.DataDir);
                if (File.Exists(p) && new FileInfo(p).Length > 512 * 1024) File.Delete(p);
                File.AppendAllText(p, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
