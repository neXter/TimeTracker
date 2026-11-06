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
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\TimeTracker";
    public const string QuitEventName = @"Local\TimeTracker.Quit";
    public const string ShowEventName = @"Local\TimeTracker.Show";
    public const string SettingsEventName = @"Local\TimeTracker.Settings";
    public const string FlyoutEventName = @"Local\TimeTracker.Flyout";

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

    /// <summary>Asks other running instances to exit, then kills any that remain.</summary>
    public static void StopOthers()
    {
        try { using var quit = EventWaitHandle.OpenExisting(QuitEventName); quit.Set(); } catch { }
        var me = Environment.ProcessId;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            var others = Process.GetProcessesByName("TimeTracker").Where(p => p.Id != me).ToList();
            if (others.Count == 0) return;
            Thread.Sleep(250);
        }
        foreach (var p in Process.GetProcessesByName("TimeTracker").Where(p => p.Id != me))
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
