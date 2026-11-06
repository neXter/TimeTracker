using System.Diagnostics;
using Microsoft.Win32;
using TimeTracker.Core;

namespace TimeTracker;

/// <summary>The background app: tray icon, collector loop, and the windows opened from the tray.</summary>
internal sealed partial class TrayApp : ApplicationContext
{
    public readonly Db Db;
    public readonly Settings Settings;
    public readonly Collector Collector;
    public readonly StatsService Stats;
    public readonly AliasService Aliases;

    private readonly WindowsSignals _signals = new();
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _slow = new() { Interval = 30_000 };
    private readonly Control _ui = new();
    private readonly List<WebWindow> _windows = new();
    private readonly Icon _iconNormal, _iconPaused;
    private WebWindow? _flyoutWin;               // created once, kept hidden, shown on demand
    private DateTime _flyoutClosedAt = DateTime.MinValue;
    private DateTime _lastScheduledCheck = DateTime.MinValue;
    private bool _exiting;

    public string Lang => I18n.Resolve(Settings.Language);
    public string T(string key, params object?[] args) => I18n.T(Lang, key, args);

    public TrayApp(bool showFlyoutOnStart)
    {
        Directory.CreateDirectory(Installer.DataDir);
        Db = new Db(Path.Combine(Installer.DataDir, "tracker.db"));
        Settings = new Settings(Db);
        Collector = new Collector(Db, _signals, new AppNamer(), Settings);
        Stats = new StatsService(Db, Installer.DeviceName);
        Aliases = new AliasService(Db);
        Collector.Start();

        _ = _ui.Handle;   // create the handle so background threads can marshal to the UI thread
        var size = SystemInformation.SmallIconSize;
        _iconNormal = AppIcons.Load(size);
        _iconPaused = AppIcons.WithPausedBadge(size);

        _tray = new NotifyIcon { Icon = _iconNormal, Visible = true, Text = "TimeTracker", ContextMenuStrip = new ContextMenuStrip() };
        _tray.ContextMenuStrip.Opening += (_, _) => BuildMenu();
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ToggleFlyout(); };

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();
        _slow.Tick += (_, _) => OnSlowTick();
        _slow.Start();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.SessionEnding += OnSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.UserPreferenceChanged += (_, _) => _ui.BeginInvoke(() => _tray.Icon = Collector.IsPaused ? _iconPaused : _iconNormal);

        ListenFor(Installer.ShowEventName, () => ShowDashboard());
        ListenFor(Installer.SettingsEventName, () => ShowSettings());
        ListenFor(Installer.FlyoutEventName, ToggleFlyout);
        ListenFor(Installer.QuitEventName, () => ExitApp());

        OnSlowTick();
        // Load the flyout in the background a few seconds after start so the first click is instant.
        _ = Task.Delay(showFlyoutOnStart ? 1500 : 5000).ContinueWith(_ => _ui.BeginInvoke(() => { if (showFlyoutOnStart) ToggleFlyout(); else WarmFlyout(); }), TaskScheduler.Default);
        Log.Write($"started v{Installer.VersionText}, device {Installer.DeviceName}, lang {Lang}");
    }

    private void ListenFor(string name, Action action)
    {
        var ev = new EventWaitHandle(false, EventResetMode.AutoReset, name);
        new Thread(() =>
        {
            while (true)
            {
                try { ev.WaitOne(); _ui.BeginInvoke(action); }
                catch (ObjectDisposedException) { return; }
                catch { /* ignore and keep listening */ }
            }
        }) { IsBackground = true, Name = "TimeTracker " + name }.Start();
    }

    // ---------- collector loop ----------

    // TIMETRACKER_NO_COLLECT=1 stops live recording (used with demo data for screenshots, so real apps never show up).
    private static readonly bool NoCollect = Environment.GetEnvironmentVariable("TIMETRACKER_NO_COLLECT") == "1";

    private void OnTick()
    {
        if (NoCollect) return;
        try { Collector.Tick(); }
        catch (Exception ex) { Log.Write("tick failed: " + ex.Message); }
    }

    private void OnSlowTick()
    {
        try
        {
            UpdateTooltip();
            if (Collector.IsPaused != ReferenceEquals(_tray.Icon, _iconPaused)) _tray.Icon = Collector.IsPaused ? _iconPaused : _iconNormal;
            // Scheduled reports: once at start, then at most once an hour (a pending period is only ever written once).
            if (DateTime.Now - _lastScheduledCheck > TimeSpan.FromMinutes(60))
            {
                _lastScheduledCheck = DateTime.Now;
                string lang = Lang;
                _ = Task.Run(() =>
                {
                    try { ReportBuilder.RunScheduled(Stats, Db, Settings, lang); }
                    catch (Exception ex) { Log.Write("scheduled report failed: " + ex.Message); }
                });
            }
        }
        catch (Exception ex) { Log.Write("slow tick failed: " + ex.Message); }
    }

    public void UpdateTooltip()
    {
        long start = Stats.DayStartMs(Stats.Today());
        long sec = Collector.ActiveSecondsSince(Db, start, Time.NowMs());
        string text = T(Collector.IsPaused ? "tray.tooltipPaused" : "tray.tooltip", I18n.FormatDuration(Lang, sec));
        _tray.Text = text.Length > 120 ? text[..120] : text;
    }

    // ---------- Windows events ----------

    private void OnSessionSwitch(object? s, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.SessionLogoff:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _signals.Locked = true; break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _signals.Locked = false; break;
        }
    }

    private void OnSessionEnding(object? s, SessionEndingEventArgs e)
    {
        if (_exiting) return;
        try { Collector.Stop("shutdown"); } catch { }
    }

    private void OnPowerModeChanged(object? s, PowerModeChangedEventArgs e)
    {
        _ui.BeginInvoke(() =>
        {
            try
            {
                if (e.Mode == PowerModes.Suspend) Collector.OnSuspend();
                else if (e.Mode == PowerModes.Resume) Collector.OnResume();
            }
            catch (Exception ex) { Log.Write("power event failed: " + ex.Message); }
        });
        if (e.Mode == PowerModes.Suspend) { try { Collector.OnSuspend(); } catch { } }   // must finish before the machine sleeps
    }

    // ---------- tray menu ----------

    private void BuildMenu()
    {
        var m = _tray.ContextMenuStrip!;
        m.Items.Clear();
        var open = new ToolStripMenuItem(T("tray.dashboard"), null, (_, _) => ShowDashboard()) { Font = new Font(m.Font, FontStyle.Bold) };
        m.Items.Add(open);

        var report = new ToolStripMenuItem(T("tray.report"));
        foreach (var k in new[] { "thisWeek", "lastWeek", "thisMonth", "lastMonth", "custom" })
        {
            string kind = k;
            report.DropDownItems.Add(new ToolStripMenuItem(T("rep." + k), null, (_, _) => GenerateReport(kind)));
        }
        m.Items.Add(report);
        m.Items.Add(new ToolStripSeparator());

        if (Collector.IsPaused)
            m.Items.Add(new ToolStripMenuItem(T("tray.resume"), null, (_, _) => ResumeTracking()));
        else
        {
            var pause = new ToolStripMenuItem(T("tray.pause"));
            pause.DropDownItems.Add(new ToolStripMenuItem(T("pause.15"), null, (_, _) => PauseTracking(TimeSpan.FromMinutes(15))));
            pause.DropDownItems.Add(new ToolStripMenuItem(T("pause.60"), null, (_, _) => PauseTracking(TimeSpan.FromHours(1))));
            pause.DropDownItems.Add(new ToolStripMenuItem(T("pause.tomorrow"), null, (_, _) => PauseTracking(DateTime.Today.AddDays(1) - DateTime.Now)));
            m.Items.Add(pause);
        }
        m.Items.Add(new ToolStripMenuItem(T("tray.settings"), null, (_, _) => ShowSettings()));
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add(new ToolStripMenuItem(T("tray.exit"), null, (_, _) => { if (RequirePin()) ExitApp(); }));
    }

    public bool RequirePin() => Prompts.RequirePin(Settings, Lang);

    public void PauseTracking(TimeSpan span)
    {
        if (!RequirePin()) return;
        Collector.Pause(Time.NowMs() + (long)span.TotalMilliseconds);
        _tray.Icon = _iconPaused;
        UpdateTooltip(); BroadcastRefresh();
    }

    public void ResumeTracking()
    {
        Collector.Resume();
        _tray.Icon = _iconNormal;
        UpdateTooltip(); BroadcastRefresh();
    }

    // ---------- windows ----------

    /// <summary>Creates the flyout window hidden and off-screen so WebView2 and the page are loaded before the first click.</summary>
    private void WarmFlyout()
    {
        if (_flyoutWin is { IsDisposed: false } || _exiting) return;
        _flyoutWin = new WebWindow(this, "flyout");
        _windows.Add(_flyoutWin);
        _flyoutWin.Show();                         // transparent and off-screen; it hides itself after the first render
    }

    public void ToggleFlyout()
    {
        if (_flyoutWin is { IsFlyoutVisible: true }) { _flyoutWin.HideFlyout(); return; }
        // Clicking the tray icon deactivates (and hides) an open flyout first; don't instantly reopen it.
        if ((DateTime.UtcNow - _flyoutClosedAt).TotalMilliseconds < 400) return;
        if (_flyoutWin == null || _flyoutWin.IsDisposed) WarmFlyout();
        _flyoutWin!.RequestShowFlyout();
    }

    public void FlyoutHidden() => _flyoutClosedAt = DateTime.UtcNow;

    public void ShowDashboard(IEnumerable<long>? renameIds = null)
    {
        _flyoutWin?.HideFlyout();
        var existing = _windows.FirstOrDefault(w => w.Page == "dashboard");
        if (existing != null)
        {
            if (existing.WindowState == FormWindowState.Minimized) existing.WindowState = FormWindowState.Normal;
            existing.Activate();
            existing.BringToFront();
            if (renameIds != null) existing.Post(new { @event = "rename", appIds = renameIds.ToArray() });
            return;
        }
        OpenWindow("dashboard", renameIds != null ? "rename=" + string.Join(",", renameIds) : null);
    }

    public void ShowSettings()
    {
        _flyoutWin?.HideFlyout();
        var existing = _windows.FirstOrDefault(w => w.Page == "settings");
        if (existing != null) { existing.Activate(); existing.BringToFront(); return; }
        if (!RequirePin()) return;
        OpenWindow("settings", null);
    }

    private void OpenWindow(string page, string? query)
    {
        var w = new WebWindow(this, page, query);
        _windows.Add(w);
        w.Show();
        if (page != "flyout") { w.Activate(); }
    }

    public void WindowClosed(WebWindow w)
    {
        _windows.Remove(w);
        if (w == _flyoutWin) _flyoutWin = null;
    }

    public void BroadcastRefresh()
    {
        foreach (var w in _windows.ToList()) w.Post(new { @event = "refresh" });
    }

    // ---------- reports ----------

    public void GenerateReport(string kind)
    {
        var today = Stats.Today();
        PeriodKind period; DateOnly anchor = today; DateOnly? end = null;
        switch (kind)
        {
            case "thisWeek": period = PeriodKind.Week; break;
            case "lastWeek": period = PeriodKind.Week; anchor = today.AddDays(-7); break;
            case "thisMonth": period = PeriodKind.Month; break;
            case "lastMonth": period = PeriodKind.Month; anchor = new DateOnly(today.Year, today.Month, 1).AddMonths(-1); break;
            case "custom":
                var r = Prompts.AskRange(Lang);
                if (r == null) return;
                period = PeriodKind.Range; anchor = r.Value.From; end = r.Value.To; break;
            default: return;
        }
        GenerateReport(period, anchor, end, open: true);
    }

    public string? GenerateReport(PeriodKind period, DateOnly anchor, DateOnly? end, bool open)
    {
        if (!Stats.Get(period, anchor, end).HasData)
        {
            Prompts.Info(T("report.noData"), Lang);
            return null;
        }
        string path = ReportBuilder.Generate(Stats, period, anchor, end, Lang, Settings.ReportDir);
        if (open) OpenWithShell(path);
        return path;
    }

    /// <summary>Opens a file, folder or URI with the default handler. TIMETRACKER_NO_OPEN=1 suppresses it (automated tests).</summary>
    public static void OpenWithShell(string target, string? args = null)
    {
        if (Environment.GetEnvironmentVariable("TIMETRACKER_NO_OPEN") == "1") { Log.Write("(suppressed) open " + target); return; }
        try { Process.Start(args == null ? new ProcessStartInfo(target) { UseShellExecute = true } : new ProcessStartInfo(target, args) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Write("open failed: " + ex.Message); }
    }

    // ---------- exit ----------

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        try { Collector.Stop("exit"); } catch { }
        _tick.Stop(); _slow.Stop();
        foreach (var w in _windows.ToList()) { try { w.Close(); } catch { } }
        _tray.Visible = false;
        _tray.Dispose();
        try { Db.Dispose(); } catch { }
        Log.Write("exited");
        ExitThread();
        Application.Exit();
    }
}
