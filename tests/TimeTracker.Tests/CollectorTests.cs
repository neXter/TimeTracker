using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Tests;

public sealed class FakeSignals : ISignals
{
    public long Now;
    public bool Locked;
    public double Idle;
    public bool Fullscreen;
    public WindowInfo? Fg;
    public List<WindowInfo> Open = new();
    public long NowMs => Now;
    public bool IsLocked => Locked;
    public double IdleSeconds => Idle;
    public bool ForegroundIsFullscreen => Fullscreen;
    public WindowInfo? Foreground() => Fg;
    public IReadOnlyList<WindowInfo> OpenWindows() => Open;
}

public sealed class FakeNames : INameDetector
{
    public (string, string) Detect(string exePath)
    {
        var n = Path.GetFileNameWithoutExtension(exePath);
        return n.StartsWith("named", StringComparison.OrdinalIgnoreCase) || n is "chrome" or "WINWORD"
            ? (n == "chrome" ? "Google Chrome" : n == "WINWORD" ? "Microsoft Word" : n, "version-info")
            : (n, "fallback");
    }
}

public static class T
{
    public static long Ms(string iso) => DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal).ToUnixTimeMilliseconds();
    public static readonly WindowInfo Chrome = new(1, @"C:\Program Files\Google\Chrome\chrome.exe", "Tab");
    public static readonly WindowInfo Word = new(2, @"C:\Office\WINWORD.EXE", "Doc");
    public static readonly WindowInfo Game = new(3, @"D:\Games\Hollow\game_x64.exe", "Hollow");
}

public class CollectorTests
{
    private static (Db db, FakeSignals sig, Collector col, Settings set) Make(long start)
    {
        var db = Db.InMemory();
        var sig = new FakeSignals { Now = start };
        var set = new Settings(db);
        var col = new Collector(db, sig, new FakeNames(), set);
        col.Start();
        return (db, sig, col, set);
    }

    private static void Run(FakeSignals s, Collector c, int seconds)
    {
        for (int i = 0; i < seconds; i++) { c.Tick(); s.Now += 1000; }
    }

    private static double Sum(Db db, string sql, params object?[] a) => (db.Scalar<long?>(sql, a) ?? 0) / 1000.0;

    [Fact]
    public void Active_foreground_and_open_are_recorded()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, _) = Make(t0);
        s.Fg = T.Chrome; s.Open.Add(T.Chrome); s.Open.Add(T.Word);
        Run(s, c, 60);
        c.Stop("exit");

        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='active'"), 58, 62);
        Assert.InRange(Sum(db, "SELECT SUM(f.end_utc-f.start_utc) FROM foreground_interval f JOIN app a ON a.id=f.app_id WHERE a.detected_name='Google Chrome'"), 58, 62);
        Assert.Equal(2, db.Scalar<long>("SELECT COUNT(*) FROM open_interval"));
        Assert.Equal(0, db.Scalar<long>("SELECT COUNT(*) FROM foreground_interval f JOIN app a ON a.id=f.app_id WHERE a.detected_name='Microsoft Word'"));
    }

    [Fact]
    public void Foreground_switch_creates_new_interval_and_foreground_sums_to_active()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, _) = Make(t0);
        s.Fg = T.Chrome; Run(s, c, 30);
        s.Fg = T.Word; Run(s, c, 30);
        c.Stop("exit");
        double active = Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='active'");
        double fg = Sum(db, "SELECT SUM(end_utc-start_utc) FROM foreground_interval");
        Assert.InRange(Math.Abs(active - fg), 0, 2);
        Assert.Equal(2, db.Scalar<long>("SELECT COUNT(*) FROM foreground_interval"));
    }

    [Fact]
    public void Becomes_idle_after_threshold_and_stops_foreground_time()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, set) = Make(t0);
        Assert.Equal(5, set.IdleMinutes);
        s.Fg = T.Chrome;
        s.Idle = 0; Run(s, c, 20);
        s.Idle = 301; Run(s, c, 40);
        c.Stop("exit");
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='active'"), 19, 21);
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='idle'"), 39, 41);
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM foreground_interval"), 19, 21);
    }

    [Fact]
    public void Fullscreen_foreground_prevents_idle_when_enabled()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, set) = Make(t0);
        s.Fg = T.Game; s.Fullscreen = true; s.Idle = 1000;
        Run(s, c, 30);
        Assert.Equal(0, db.Scalar<long>("SELECT COUNT(*) FROM device_interval WHERE state='idle'"));
        set.Set("fullscreenActive", false);
        Run(s, c, 30);
        c.Stop("exit");
        Assert.True(db.Scalar<long>("SELECT COUNT(*) FROM device_interval WHERE state='idle'") > 0);
    }

    [Fact]
    public void Locked_session_closes_foreground_and_open_windows()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, _) = Make(t0);
        s.Fg = T.Chrome; s.Open.Add(T.Chrome);
        Run(s, c, 20);
        s.Locked = true; Run(s, c, 40);
        s.Locked = false; Run(s, c, 20);
        c.Stop("exit");
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='locked'"), 39, 41);
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM foreground_interval"), 38, 42);
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM open_interval"), 38, 42);
    }

    [Fact]
    public void Gap_after_a_crash_is_untracked_and_after_shutdown_is_locked()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var db = Db.InMemory();
        var s = new FakeSignals { Now = t0, Fg = T.Chrome };
        var set = new Settings(db);
        var c1 = new Collector(db, s, new FakeNames(), set); c1.Start();
        Run(s, c1, 120);
        // crash: no Stop(); 10 minutes later a new process starts
        s.Now += 600_000;
        var c2 = new Collector(db, s, new FakeNames(), set); c2.Start();
        Run(s, c2, 30);
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='untracked'"), 590, 720);

        // clean shutdown, then boot 8 hours later
        c2.Stop("shutdown");
        s.Now += 8 * 3600_000;
        var c3 = new Collector(db, s, new FakeNames(), set); c3.Start();
        Run(s, c3, 10);
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='locked'"), 8 * 3600 - 5, 8 * 3600 + 5);
    }

    [Fact]
    public void Pause_records_nothing_and_leaves_an_untracked_gap()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, _) = Make(t0);
        s.Fg = T.Chrome;
        Run(s, c, 20);
        c.Pause(s.Now + 120_000);
        Assert.True(c.IsPaused);
        Run(s, c, 120);
        Run(s, c, 20);
        c.Stop("exit");
        Assert.False(c.IsPaused);
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='active'"), 38, 42);
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='untracked'"), 118, 125);
    }

    [Fact]
    public void Title_scoped_rule_splits_one_exe_into_separate_apps()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, _) = Make(t0);
        var java = new WindowInfo(9, @"C:\Java\bin\javaw.exe", "Minecraft 1.21");
        var other = new WindowInfo(9, @"C:\Java\bin\javaw.exe", "SomeTool");
        s.Fg = java; s.Open.Add(java); Run(s, c, 10);
        s.Fg = other; s.Open.Clear(); s.Open.Add(other); Run(s, c, 10);
        c.Stop("exit");
        var names = db.Query("SELECT detected_name FROM app ORDER BY id", r => r.GetString(0));
        Assert.Contains("Minecraft", names);
        Assert.Contains("javaw", names);
    }

    [Fact]
    public void Window_titles_are_only_stored_when_enabled()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, set) = Make(t0);
        s.Fg = T.Chrome; Run(s, c, 5);
        Assert.Null(db.Scalar<string>("SELECT title FROM foreground_interval LIMIT 1"));
        set.Set("recordTitles", true);
        s.Fg = new WindowInfo(1, T.Chrome.ExePath, "Homework - Google Docs"); Run(s, c, 5);
        c.Stop("exit");
        Assert.Equal("Homework - Google Docs", db.Scalar<string>("SELECT title FROM foreground_interval WHERE title IS NOT NULL LIMIT 1"));
    }

    [Fact]
    public void Suspend_and_resume_marks_the_sleep_as_locked()
    {
        long t0 = T.Ms("2026-11-02T10:00:00Z");
        var (db, s, c, _) = Make(t0);
        s.Fg = T.Chrome; Run(s, c, 30);
        c.OnSuspend();
        s.Now += 3600_000;
        c.OnResume();
        Run(s, c, 10);
        c.Stop("exit");
        Assert.InRange(Sum(db, "SELECT SUM(end_utc-start_utc) FROM device_interval WHERE state='locked'"), 3590, 3610);
    }
}
