namespace TimeTracker.Core;

/// <summary>Everything the collector needs from Windows. Implemented natively in the app and faked in tests.</summary>
public interface ISignals
{
    long NowMs { get; }
    bool IsLocked { get; }
    /// <summary>Seconds since the last keyboard/mouse/touch/gamepad input.</summary>
    double IdleSeconds { get; }
    bool ForegroundIsFullscreen { get; }
    WindowInfo? Foreground();
    IReadOnlyList<WindowInfo> OpenWindows();
}

public interface INameDetector
{
    /// <summary>Friendly name for an executable. source: version-info | steam | package | fallback</summary>
    (string Name, string Source) Detect(string exePath);
}

/// <summary>
/// Turns signals into intervals (DESIGN §2, §3.1). Call <see cref="Tick"/> about once per second.
/// States: active / idle / locked (off) / untracked (the tracker wasn't running).
/// </summary>
public sealed class Collector
{
    public const long HeartbeatMs = 15_000;
    public const long OpenScanMs = 5_000;
    public const long GapMinMs = 30_000;
    public const long TickJumpMs = 20_000;

    private readonly Db _db;
    private readonly ISignals _s;
    private readonly INameDetector _names;
    private readonly Settings _set;

    private AliasIndex _aliases;
    private volatile bool _aliasesDirty;
    private readonly Dictionary<string, long> _appCache = new();

    private long _lastTick, _lastBeat, _lastOpenScan, _pausedUntil;
    private long _devId; private string? _devState;
    private long _fgId; private string? _fgKey; private string? _fgTitle;
    private readonly Dictionary<string, long> _open = new();   // app key -> open_interval id
    private string _gapState = "untracked";
    private volatile bool _suspended;

    public Collector(Db db, ISignals signals, INameDetector names, Settings settings)
    {
        _db = db; _s = signals; _names = names; _set = settings;
        _aliases = AliasIndex.Load(db);
    }

    public bool IsPaused => _pausedUntil > _s.NowMs;
    public long PausedUntilMs => _pausedUntil;
    public string? CurrentState => _devState;

    /// <summary>Call after custom names changed so title-scoped rules take effect.</summary>
    public void InvalidateAliases() => _aliasesDirty = true;

    // ---------- lifecycle ----------

    /// <summary>Call once at startup. Decides how the time since the last run is classified.</summary>
    public void Start()
    {
        var reason = _db.GetMeta("stop_reason");
        _gapState = reason is "shutdown" or "suspend" ? "locked" : "untracked";
        _db.SetMeta("stop_reason", "running");   // overwritten on any clean stop; "running" at next start means a crash/kill
    }

    /// <summary>Clean stop (exit, logoff, shutdown). reason: exit | shutdown</summary>
    public void Stop(string reason)
    {
        CloseAll(_s.NowMs);
        _db.SetMeta("stop_reason", reason);
    }

    public void OnSuspend()
    {
        _suspended = true;
        CloseAll(_s.NowMs);
        _gapState = "locked";
        _db.SetMeta("stop_reason", "suspend");
    }

    public void OnResume()
    {
        _db.SetMeta("stop_reason", "running");
        _lastTick = 0;
        _suspended = false;
    }

    public void Pause(long untilMs)
    {
        CloseAll(_s.NowMs);
        _pausedUntil = untilMs;
        _gapState = "untracked";
    }

    public void Resume() { _pausedUntil = 0; }

    /// <summary>Drops all running-interval state and caches (used after the database was emptied).</summary>
    public void ForgetState()
    {
        _devState = null; _devId = 0; _fgKey = null; _fgTitle = null; _fgId = 0;
        _open.Clear(); _appCache.Clear(); _lastOpenScan = 0; _lastBeat = 0;
    }

    // ---------- main loop ----------

    public void Tick()
    {
        if (_suspended) return;
        long now = _s.NowMs;
        if (_aliasesDirty) { _aliases = AliasIndex.Load(_db); _aliasesDirty = false; }

        // A big jump means the machine slept or the process was frozen without telling us.
        if (_lastTick != 0 && now - _lastTick > TickJumpMs) CloseAll(Math.Max(_lastBeat, 0));
        _lastTick = now;

        if (_pausedUntil != 0)
        {
            if (_pausedUntil > now) { CloseAll(now); return; }
            _pausedUntil = 0;
        }

        string state;
        if (_s.IsLocked) state = "locked";
        else
        {
            bool idle = _s.IdleSeconds >= _set.IdleMinutes * 60.0;
            if (idle && _set.FullscreenActive && _s.ForegroundIsFullscreen) idle = false;
            state = idle ? "idle" : "active";
        }

        SetDevice(state, now);

        if (state == "locked")
        {
            CloseForeground(now);
            CloseAllOpen(now);
        }
        else
        {
            SyncOpen(now);
            if (state == "active") SyncForeground(now); else CloseForeground(now);
        }

        if (now - _lastBeat >= HeartbeatMs) Heartbeat(now);
    }

    // ---------- device state ----------

    private void SetDevice(string state, long now)
    {
        if (_devState == state) return;
        if (_devState != null) _db.Exec("UPDATE device_interval SET end_utc=MAX(end_utc,$0) WHERE id=$1", now, _devId);
        else FillGap(now);
        _devId = _db.Insert("INSERT INTO device_interval(state,start_utc,end_utc) VALUES($0,$1,$1)", state, now);
        _devState = state;
        _lastBeat = now;
    }

    /// <summary>Records the time between the previous run/pause/sleep and now, classified by why we weren't recording.</summary>
    private void FillGap(long now)
    {
        var lastEnd = _db.Scalar<long?>("SELECT MAX(end_utc) FROM device_interval");
        if (lastEnd != null && now - lastEnd.Value > GapMinMs)
            _db.Exec("INSERT INTO device_interval(state,start_utc,end_utc) VALUES($0,$1,$2)", _gapState, lastEnd.Value, now);
        _gapState = "untracked";
    }

    // ---------- foreground ----------

    private void SyncForeground(long now)
    {
        var w = _s.Foreground();
        string? key = null; string? title = null;
        if (w != null && !string.IsNullOrEmpty(w.ExePath))
        {
            key = KeyFor(w, out _);
            if (_set.RecordTitles) title = w.Title;
        }
        bool changed = key != _fgKey || (_set.RecordTitles && key != null && title != _fgTitle);
        if (!changed) return;
        CloseForeground(now);
        if (key == null || w == null) return;
        long appId = AppIdFor(w);
        _fgId = _db.Insert("INSERT INTO foreground_interval(app_id,start_utc,end_utc,title) VALUES($0,$1,$1,$2)", appId, now, title);
        _fgKey = key; _fgTitle = title;
    }

    private void CloseForeground(long now)
    {
        if (_fgKey == null) return;
        _db.Exec("UPDATE foreground_interval SET end_utc=MAX(end_utc,$0) WHERE id=$1", now, _fgId);
        _fgKey = null; _fgTitle = null; _fgId = 0;
    }

    // ---------- open windows ----------

    private void SyncOpen(long now)
    {
        if (now - _lastOpenScan < OpenScanMs && _lastOpenScan != 0) return;
        _lastOpenScan = now;
        var seen = new Dictionary<string, WindowInfo>();
        foreach (var w in _s.OpenWindows())
        {
            if (string.IsNullOrEmpty(w.ExePath)) continue;
            seen.TryAdd(KeyFor(w, out _), w);
        }
        foreach (var kv in _open.Where(kv => !seen.ContainsKey(kv.Key)).ToList())
        {
            _db.Exec("UPDATE open_interval SET end_utc=MAX(end_utc,$0) WHERE id=$1", now, kv.Value);
            _open.Remove(kv.Key);
        }
        foreach (var kv in seen.Where(kv => !_open.ContainsKey(kv.Key)))
        {
            long appId = AppIdFor(kv.Value);
            _open[kv.Key] = _db.Insert("INSERT INTO open_interval(app_id,start_utc,end_utc) VALUES($0,$1,$1)", appId, now);
        }
    }

    private void CloseAllOpen(long now)
    {
        foreach (var id in _open.Values) _db.Exec("UPDATE open_interval SET end_utc=MAX(end_utc,$0) WHERE id=$1", now, id);
        _open.Clear();
        _lastOpenScan = 0;
    }

    // ---------- apps ----------

    private string KeyFor(WindowInfo w, out long ruleId)
    {
        var rule = _aliases.TitleRule(w.ExePath, w.Title);
        ruleId = rule?.Id ?? 0;
        return w.ExePath.ToLowerInvariant() + "|" + ruleId;
    }

    private long AppIdFor(WindowInfo w)
    {
        string key = KeyFor(w, out long ruleId);
        if (_appCache.TryGetValue(key, out var id)) return id;
        var existing = _db.Scalar<long?>("SELECT id FROM app WHERE exe_path=$0 AND title_rule_id=$1", w.ExePath, ruleId);
        if (existing == null)
        {
            string name, source;
            if (ruleId > 0) { name = _aliases.ById(ruleId)!.DisplayName; source = "alias"; }
            else (name, source) = SafeDetect(w.ExePath);
            existing = _db.Insert(
                "INSERT INTO app(exe_path,exe_name,detected_name,source,title_rule_id,first_seen_utc) VALUES($0,$1,$2,$3,$4,$5)",
                w.ExePath, Path.GetFileName(w.ExePath), name, source, ruleId, _s.NowMs);
        }
        _appCache[key] = existing.Value;
        return existing.Value;
    }

    private (string, string) SafeDetect(string path)
    {
        try { return _names.Detect(path); }
        catch { return (Path.GetFileNameWithoutExtension(path), "fallback"); }
    }

    // ---------- bookkeeping ----------

    private void Heartbeat(long now)
    {
        _db.Transaction(() =>
        {
            if (_devState != null) _db.Exec("UPDATE device_interval SET end_utc=MAX(end_utc,$0) WHERE id=$1", now, _devId);
            if (_fgKey != null) _db.Exec("UPDATE foreground_interval SET end_utc=MAX(end_utc,$0) WHERE id=$1", now, _fgId);
            foreach (var id in _open.Values) _db.Exec("UPDATE open_interval SET end_utc=MAX(end_utc,$0) WHERE id=$1", now, id);
        });
        _lastBeat = now;
    }

    /// <summary>Closes every running interval at <paramref name="endMs"/> and forgets them.</summary>
    private void CloseAll(long endMs)
    {
        if (_devState != null) _db.Exec("UPDATE device_interval SET end_utc=MAX(end_utc,$0) WHERE id=$1", endMs, _devId);
        CloseForeground(endMs);
        CloseAllOpen(endMs);
        _devState = null; _devId = 0;
    }

    /// <summary>Total active seconds since the given instant (cheap; used for the tray tooltip).</summary>
    public static long ActiveSecondsSince(Db db, long sinceMs, long nowMs)
    {
        var ms = db.Scalar<long?>(
            "SELECT SUM(MIN(end_utc,$1)-MAX(start_utc,$0)) FROM device_interval WHERE state='active' AND end_utc>$0 AND start_utc<$1",
            sinceMs, nowMs) ?? 0;
        return ms / 1000;
    }
}
