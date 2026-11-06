using System.Text.Json;

namespace TimeTracker.Core;

public enum PeriodKind { Day, Week, Month, Year, Range }

public sealed class BucketDto
{
    public string Key { get; set; } = "";          // yyyy-MM-dd (day buckets) or yyyy-MM (month buckets)
    public long StartMs { get; set; }
    public double Active { get; set; }
    public double Idle { get; set; }
    public Dictionary<string, double> Fg { get; set; } = new();
    public Dictionary<string, double> Open { get; set; } = new();
}

public sealed class GroupDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Exe { get; set; } = "";
    public string Path { get; set; } = "";
    public string DetectedName { get; set; } = "";
    public bool Custom { get; set; }
    public bool Unnamed { get; set; }
    public bool Hidden { get; set; }
    public long? AliasId { get; set; }
    public int Slot { get; set; }                  // 1..5 = categorical colour, 0 = "Other"
    public double Fg { get; set; }
    public double Open { get; set; }
    public int Sessions { get; set; }
    public long LastMs { get; set; }
    public List<long> AppIds { get; set; } = new();
}

public sealed class TilesDto
{
    public double ActiveSec { get; set; }
    public double IdleSec { get; set; }
    public double PrevActiveSec { get; set; }
    public double AvgDaySec { get; set; }
    public int DaysCounted { get; set; }
    public string? TopGroupId { get; set; }
    public double LongestSec { get; set; }
    public long LongestStartMs { get; set; }
    public string? LongestGroupId { get; set; }
}

public sealed class SegDto { public double S { get; set; } public double E { get; set; } public string? State { get; set; } public string? G { get; set; } }

public sealed class TimelineDto
{
    public long DayStartMs { get; set; }
    public double DaySeconds { get; set; }
    public List<SegDto> Device { get; set; } = new();
    public List<SegDto> Fg { get; set; } = new();
}

public sealed class StatsDto
{
    public string Device { get; set; } = "";
    public string Period { get; set; } = "";
    public string From { get; set; } = "";         // first day, yyyy-MM-dd
    public string To { get; set; } = "";           // last day (inclusive)
    public string Granularity { get; set; } = "day";
    public long GeneratedMs { get; set; }
    public bool HasData { get; set; }
    public List<BucketDto> Buckets { get; set; } = new();
    public List<GroupDto> Groups { get; set; } = new();
    public double[][] Heat { get; set; } = Array.Empty<double[]>();
    public TilesDto Tiles { get; set; } = new();
    public TimelineDto? Timeline { get; set; }
}

public sealed class UnnamedDto { public string Exe { get; set; } = ""; public string Path { get; set; } = ""; public List<long> AppIds { get; set; } = new(); }

public sealed class FlyoutDto
{
    public StatsDto Stats { get; set; } = new();
    public List<UnnamedDto> Unnamed { get; set; } = new();
    public long FirstActiveMs { get; set; }
    public long NowMs { get; set; }
}

public sealed class SessionDto { public long StartMs { get; set; } public long EndMs { get; set; } public string? Title { get; set; } }

public sealed class StatsService
{
    private readonly Db _db;
    private readonly TimeZoneInfo _tz;
    public string DeviceName { get; set; }
    public Func<long> NowMs { get; set; } = Time.NowMs;

    public StatsService(Db db, string deviceName, TimeZoneInfo? tz = null)
    {
        _db = db; DeviceName = deviceName; _tz = tz ?? TimeZoneInfo.Local;
    }

    // ---------- time helpers ----------

    public long DayStartMs(DateOnly d)
    {
        var dt = d.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        if (_tz.IsInvalidTime(dt)) dt = dt.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(dt, _tz), TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    private long LocalToMs(DateTime local)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (_tz.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, _tz), TimeSpan.Zero).ToUnixTimeMilliseconds();
    }

    public DateTime ToLocal(long ms) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, _tz);

    public DateOnly Today() => DateOnly.FromDateTime(ToLocal(NowMs()));

    public static DateOnly WeekStart(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public static (DateOnly From, DateOnly ToExcl) RangeOf(PeriodKind kind, DateOnly anchor, DateOnly? rangeEnd = null) => kind switch
    {
        PeriodKind.Day => (anchor, anchor.AddDays(1)),
        PeriodKind.Week => (WeekStart(anchor), WeekStart(anchor).AddDays(7)),
        PeriodKind.Month => (new DateOnly(anchor.Year, anchor.Month, 1), new DateOnly(anchor.Year, anchor.Month, 1).AddMonths(1)),
        PeriodKind.Year => (new DateOnly(anchor.Year, 1, 1), new DateOnly(anchor.Year + 1, 1, 1)),
        _ => (anchor, (rangeEnd ?? anchor).AddDays(1)),
    };

    private static (DateOnly, DateOnly) Previous(PeriodKind kind, DateOnly from, DateOnly toExcl) => kind switch
    {
        PeriodKind.Month => (from.AddMonths(-1), from),
        PeriodKind.Year => (from.AddYears(-1), from),
        _ => (from.AddDays(-(toExcl.DayNumber - from.DayNumber)), from),
    };

    // ---------- main query ----------

    private sealed class Grp
    {
        public string Id = ""; public string Name = ""; public string Exe = ""; public string Path = ""; public string Detected = "";
        public bool Custom, Unnamed, Hidden; public long? AliasId;
        public List<long> AppIds = new();
        public double Fg, Open; public int Sessions; public long LastMs;
    }

    public StatsDto Get(PeriodKind kind, DateOnly anchor, DateOnly? rangeEnd = null, bool dailyBuckets = false)
    {
        var (from, toExcl) = RangeOf(kind, anchor, rangeEnd);
        bool monthly = kind == PeriodKind.Year && !dailyBuckets;

        // buckets
        var bStart = new List<long>(); var bEnd = new List<long>(); var buckets = new List<BucketDto>();
        if (monthly)
        {
            for (var m = from; m < toExcl; m = m.AddMonths(1))
            {
                var next = m.AddMonths(1);
                bStart.Add(DayStartMs(m)); bEnd.Add(DayStartMs(next));
                buckets.Add(new BucketDto { Key = m.ToString("yyyy-MM"), StartMs = bStart[^1] });
            }
        }
        else
        {
            for (var d = from; d < toExcl; d = d.AddDays(1))
            {
                bStart.Add(DayStartMs(d)); bEnd.Add(DayStartMs(d.AddDays(1)));
                buckets.Add(new BucketDto { Key = d.ToString("yyyy-MM-dd"), StartMs = bStart[^1] });
            }
        }
        long fromMs = bStart[0], toMs = bEnd[^1];

        // groups
        var alias = AliasIndex.Load(_db);
        var apps = _db.Query("SELECT id,exe_path,exe_name,detected_name,source,title_rule_id,first_seen_utc FROM app",
            r => new AppRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
        var byName = new Dictionary<string, Grp>(StringComparer.OrdinalIgnoreCase);
        var byApp = new Dictionary<long, Grp>();
        foreach (var a in apps)
        {
            var v = alias.View(a);
            if (!byName.TryGetValue(v.Name, out var g))
            {
                g = new Grp { Name = v.Name, Exe = a.ExeName, Path = a.ExePath, Detected = a.DetectedName, Custom = v.Custom, Unnamed = v.Unnamed, Hidden = v.Hidden, AliasId = v.AliasId };
                byName[v.Name] = g;
            }
            else
            {
                g.Custom |= v.Custom; g.Unnamed &= v.Unnamed;
                if (!g.Exe.Equals(a.ExeName, StringComparison.OrdinalIgnoreCase) && !g.Exe.Contains("+")) g.Exe += " +";
            }
            g.AppIds.Add(a.Id);
            byApp[a.Id] = g;
        }
        foreach (var g in byName.Values) g.Id = "g" + g.AppIds.Min();

        void AddOverlap(long s, long e, Action<int, double> add)
        {
            s = Math.Max(s, fromMs); e = Math.Min(e, toMs);
            if (e <= s) return;
            int i = bEnd.BinarySearch(s);
            if (i < 0) i = ~i; else i++;              // first bucket whose end is > s
            for (; i < bStart.Count && bStart[i] < e; i++)
            {
                long o = Math.Min(e, bEnd[i]) - Math.Max(s, bStart[i]);
                if (o > 0) add(i, o / 1000.0);
            }
        }

        // foreground
        var fgList = new List<(Grp g, long s, long e)>();
        foreach (var (appId, s, e) in _db.Query("SELECT app_id,start_utc,end_utc FROM foreground_interval WHERE end_utc>$0 AND start_utc<$1",
                     r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), fromMs, toMs))
        {
            if (e - s < 1000 || !byApp.TryGetValue(appId, out var g) || g.Hidden) continue;
            fgList.Add((g, s, e));
            AddOverlap(s, e, (i, sec) =>
            {
                g.Fg += sec;
                buckets[i].Fg[g.Id] = buckets[i].Fg.GetValueOrDefault(g.Id) + sec;
            });
            g.LastMs = Math.Max(g.LastMs, Math.Min(e, toMs));
        }

        // open
        foreach (var (appId, s, e) in _db.Query("SELECT app_id,start_utc,end_utc FROM open_interval WHERE end_utc>$0 AND start_utc<$1",
                     r => (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2)), fromMs, toMs))
        {
            if (!byApp.TryGetValue(appId, out var g) || g.Hidden) continue;
            g.Sessions++;
            AddOverlap(s, e, (i, sec) =>
            {
                g.Open += sec;
                buckets[i].Open[g.Id] = buckets[i].Open.GetValueOrDefault(g.Id) + sec;
            });
            g.LastMs = Math.Max(g.LastMs, Math.Min(e, toMs));
        }

        // device
        var heat = new double[7][];
        for (int i = 0; i < 7; i++) heat[i] = new double[24];
        var live = new List<(long s, long e)>();       // active + idle spans, for "longest session"
        var devSegs = new List<(string state, long s, long e)>();
        foreach (var (state, s, e) in _db.Query("SELECT state,start_utc,end_utc FROM device_interval WHERE end_utc>$0 AND start_utc<$1 ORDER BY start_utc",
                     r => (r.GetString(0), r.GetInt64(1), r.GetInt64(2)), fromMs, toMs))
        {
            long cs = Math.Max(s, fromMs), ce = Math.Min(e, toMs);
            if (ce <= cs) continue;
            devSegs.Add((state, cs, ce));
            if (state == "active") AddOverlap(s, e, (i, sec) => buckets[i].Active += sec);
            else if (state == "idle") AddOverlap(s, e, (i, sec) => buckets[i].Idle += sec);
            if (state is "active" or "idle") live.Add((cs, ce));
            if (state == "active") AddHeat(heat, cs, ce);
        }

        // normalise heat to "average active minutes per hour"
        var today = Today();
        var occ = new int[7];
        var lastDay = toExcl.AddDays(-1) < today ? toExcl.AddDays(-1) : today;
        for (var d = from; d <= lastDay; d = d.AddDays(1)) occ[((int)d.DayOfWeek + 6) % 7]++;
        for (int w = 0; w < 7; w++)
            for (int h = 0; h < 24; h++)
                heat[w][h] = occ[w] == 0 ? 0 : Math.Min(60, heat[w][h] / occ[w] / 60.0);

        // groups -> dto, colour slots
        var groups = byName.Values.Where(g => !g.Hidden && (g.Fg > 0 || g.Open > 0)).OrderByDescending(g => g.Fg).ThenByDescending(g => g.Open).ToList();
        var slots = AssignSlots(groups.Where(g => g.Fg > 0).Take(5).ToList());
        var dto = new StatsDto
        {
            Device = DeviceName,
            Period = kind.ToString().ToLowerInvariant(),
            From = from.ToString("yyyy-MM-dd"),
            To = toExcl.AddDays(-1).ToString("yyyy-MM-dd"),
            Granularity = monthly ? "month" : "day",
            GeneratedMs = NowMs(),
            Buckets = buckets,
            Heat = heat,
            Groups = groups.Select(g => new GroupDto
            {
                Id = g.Id, Name = g.Name, Exe = g.Exe, Path = g.Path, DetectedName = g.Detected, Custom = g.Custom, Unnamed = g.Unnamed,
                Hidden = g.Hidden, AliasId = g.AliasId, Slot = slots.GetValueOrDefault(g.Id), Fg = g.Fg, Open = g.Open,
                Sessions = g.Sessions, LastMs = g.LastMs, AppIds = g.AppIds,
            }).ToList(),
        };
        dto.HasData = devSegs.Count > 0 || groups.Count > 0;

        // tiles
        var t = dto.Tiles;
        t.ActiveSec = buckets.Sum(b => b.Active);
        t.IdleSec = buckets.Sum(b => b.Idle);
        var counted = (int)Math.Clamp(Math.Min(toExcl.DayNumber, today.DayNumber + 1) - from.DayNumber, 1, 100000);
        t.DaysCounted = counted;
        t.AvgDaySec = t.ActiveSec / counted;
        var (pf, pt) = Previous(kind, from, toExcl);
        t.PrevActiveSec = ActiveSeconds(DayStartMs(pf), DayStartMs(pt));
        t.TopGroupId = groups.FirstOrDefault(g => g.Fg > 0)?.Id;
        FillLongest(t, live, fgList.Select(x => (x.g.Id, x.s, x.e)).ToList());

        if (kind == PeriodKind.Day)
        {
            dto.Timeline = new TimelineDto { DayStartMs = fromMs, DaySeconds = (toMs - fromMs) / 1000.0 };
            foreach (var (state, s, e) in devSegs)
                dto.Timeline.Device.Add(new SegDto { S = (s - fromMs) / 1000.0, E = (e - fromMs) / 1000.0, State = state });
            foreach (var (g, s, e) in fgList.OrderBy(x => x.s))
            {
                double ss = (Math.Max(s, fromMs) - fromMs) / 1000.0, ee = (Math.Min(e, toMs) - fromMs) / 1000.0;
                var last = dto.Timeline.Fg.Count > 0 ? dto.Timeline.Fg[^1] : null;
                if (last != null && last.G == g.Id && ss - last.E < 2) last.E = ee;      // merge touching blips
                else dto.Timeline.Fg.Add(new SegDto { S = ss, E = ee, G = g.Id });
            }
        }
        return dto;
    }

    private void AddHeat(double[][] heat, long s, long e)
    {
        long cur = s;
        while (cur < e)
        {
            var local = ToLocal(cur);
            var hourStart = new DateTime(local.Year, local.Month, local.Day, local.Hour, 0, 0);
            long next = LocalToMs(hourStart.AddHours(1));
            if (next <= cur) next = cur + 3_600_000;
            long end = Math.Min(next, e);
            heat[((int)local.DayOfWeek + 6) % 7][local.Hour] += (end - cur) / 1000.0;
            cur = end;
        }
    }

    private static void FillLongest(TilesDto t, List<(long s, long e)> live, List<(string g, long s, long e)> fg)
    {
        if (live.Count == 0) return;
        var spans = new List<(long s, long e)>();
        var cur = live[0];
        foreach (var x in live.Skip(1))
        {
            if (x.s - cur.e <= 60_000) cur = (cur.s, Math.Max(cur.e, x.e));
            else { spans.Add(cur); cur = x; }
        }
        spans.Add(cur);
        var best = spans.OrderByDescending(x => x.e - x.s).First();
        t.LongestSec = (best.e - best.s) / 1000.0;
        t.LongestStartMs = best.s;
        t.LongestGroupId = fg.Where(x => x.e > best.s && x.s < best.e)
            .GroupBy(x => x.g)
            .OrderByDescending(gr => gr.Sum(x => Math.Min(x.e, best.e) - Math.Max(x.s, best.s)))
            .Select(gr => gr.Key).FirstOrDefault();
    }

    private Dictionary<string, int> AssignSlots(List<Grp> top)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = _db.GetMeta("color_slots");
            if (json != null) foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, int>>(json)!) map[kv.Key] = kv.Value;
        }
        catch { /* corrupted value: start over */ }
        var result = new Dictionary<string, int>();
        var used = new HashSet<int>();
        bool dirty = false;
        foreach (var g in top)
            if (map.TryGetValue(g.Name, out var s) && s is >= 1 and <= 5 && used.Add(s)) result[g.Id] = s;
        foreach (var g in top.Where(g => !result.ContainsKey(g.Id)))
        {
            int s = Enumerable.Range(1, 5).First(i => !used.Contains(i));
            used.Add(s); result[g.Id] = s; map[g.Name] = s; dirty = true;
        }
        if (dirty) _db.SetMeta("color_slots", JsonSerializer.Serialize(map));
        return result;
    }

    public double ActiveSeconds(long fromMs, long toMs) =>
        (_db.Scalar<long?>("SELECT SUM(MIN(end_utc,$1)-MAX(start_utc,$0)) FROM device_interval WHERE state='active' AND end_utc>$0 AND start_utc<$1", fromMs, toMs) ?? 0) / 1000.0;

    // ---------- flyout, unnamed apps, sessions ----------

    public FlyoutDto Flyout()
    {
        var today = Today();
        var stats = Get(PeriodKind.Day, today);
        var f = new FlyoutDto { Stats = stats, NowMs = NowMs(), Unnamed = UnnamedApps() };
        var first = stats.Timeline?.Device.FirstOrDefault(s => s.State == "active");
        if (first != null && stats.Timeline != null) f.FirstActiveMs = stats.Timeline.DayStartMs + (long)(first.S * 1000);
        return f;
    }

    public List<UnnamedDto> UnnamedApps()
    {
        var alias = AliasIndex.Load(_db);
        long since = NowMs() - 30L * 86_400_000;
        var seen = _db.Query("SELECT DISTINCT app_id FROM foreground_interval WHERE end_utc>$0 UNION SELECT DISTINCT app_id FROM open_interval WHERE end_utc>$0",
            r => r.GetInt64(0), since).ToHashSet();
        var dismissed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { var j = _db.GetMeta("dismissed_unnamed"); if (j != null) foreach (var p in JsonSerializer.Deserialize<List<string>>(j)!) dismissed.Add(p); } catch { }
        var list = new Dictionary<string, UnnamedDto>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in _db.Query("SELECT id,exe_path,exe_name,detected_name,source,title_rule_id,first_seen_utc FROM app",
                     r => new AppRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6))))
        {
            if (!seen.Contains(a.Id)) continue;
            var v = alias.View(a);
            if (!v.Unnamed || v.Hidden || dismissed.Contains(a.ExePath)) continue;
            if (!list.TryGetValue(a.ExePath, out var u)) list[a.ExePath] = u = new UnnamedDto { Exe = a.ExeName, Path = a.ExePath };
            u.AppIds.Add(a.Id);
        }
        return list.Values.ToList();
    }

    /// <summary>Description of one app (all rows with these ids) for the rename dialog.</summary>
    public GroupDto? AppInfo(IEnumerable<long> appIds)
    {
        var ids = appIds.Distinct().ToList();
        if (ids.Count == 0) return null;
        var alias = AliasIndex.Load(_db);
        var rows = _db.Query($"SELECT id,exe_path,exe_name,detected_name,source,title_rule_id,first_seen_utc FROM app WHERE id IN ({string.Join(",", ids)})",
            r => new AppRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
        if (rows.Count == 0) return null;
        var first = rows[0]; var v = alias.View(first);
        return new GroupDto
        {
            Id = "g" + first.Id, Name = v.Name, Exe = first.ExeName, Path = first.ExePath, DetectedName = first.DetectedName,
            Custom = v.Custom, Unnamed = v.Unnamed, Hidden = v.Hidden, AliasId = v.AliasId, AppIds = rows.Select(r => r.Id).ToList(),
        };
    }

    public List<SessionDto> Sessions(IEnumerable<long> appIds, PeriodKind kind, DateOnly anchor, DateOnly? rangeEnd = null)
    {
        var ids = appIds.ToList();
        if (ids.Count == 0) return new();
        var (from, toExcl) = RangeOf(kind, anchor, rangeEnd);
        return _db.Query(
            $"SELECT start_utc,end_utc,title FROM foreground_interval WHERE app_id IN ({string.Join(",", ids)}) AND end_utc>$0 AND start_utc<$1 AND end_utc-start_utc>=1000 ORDER BY start_utc DESC LIMIT 300",
            r => new SessionDto { StartMs = r.GetInt64(0), EndMs = r.GetInt64(1), Title = r.IsDBNull(2) ? null : r.GetString(2) },
            DayStartMs(from), DayStartMs(toExcl));
    }
}

/// <summary>Create, change and remove custom names (DESIGN §3.4).</summary>
public sealed class AliasService
{
    private readonly Db _db;
    public AliasService(Db db) { _db = db; }

    public sealed class SaveRequest
    {
        public List<long> AppIds { get; set; } = new();
        public string Name { get; set; } = "";
        public string MatchKind { get; set; } = "exe_path";
        public string? TitleContains { get; set; }
        public string? MergeInto { get; set; }
        public bool Hidden { get; set; }
    }

    public List<Alias> List() => AliasIndex.Load(_db).All.ToList();

    public void Save(SaveRequest r)
    {
        string name = (string.IsNullOrWhiteSpace(r.MergeInto) ? r.Name : r.MergeInto!).Trim();
        if (name.Length == 0) throw new ArgumentException("name");
        if (name.Length > 80) name = name[..80];
        string? title = string.IsNullOrWhiteSpace(r.TitleContains) ? null : r.TitleContains.Trim();
        var apps = AppsByIds(r.AppIds);
        _db.Transaction(() =>
        {
            foreach (var a in apps.GroupBy(a => a.ExePath, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
            {
                if (a.TitleRuleId > 0)
                {
                    // Editing an existing title-scoped name.
                    _db.Exec("UPDATE app_alias SET display_name=$0, hidden=$1, title_contains=COALESCE($2,title_contains) WHERE id=$3",
                        name, r.Hidden ? 1 : 0, title, a.TitleRuleId);
                    continue;
                }
                string kind = r.MatchKind == "exe_name" ? "exe_name" : "exe_path";
                string value = kind == "exe_name" ? a.ExeName : a.ExePath;
                var existing = _db.Scalar<long?>(
                    "SELECT id FROM app_alias WHERE match_kind=$0 AND LOWER(match_value)=LOWER($1) AND COALESCE(title_contains,'')=COALESCE($2,'')",
                    kind, value, title);
                if (existing != null)
                    _db.Exec("UPDATE app_alias SET display_name=$0, hidden=$1 WHERE id=$2", name, r.Hidden ? 1 : 0, existing);
                else
                    _db.Exec("INSERT INTO app_alias(match_kind,match_value,title_contains,display_name,hidden) VALUES($0,$1,$2,$3,$4)",
                        kind, value, title, name, r.Hidden ? 1 : 0);
            }
        });
    }

    /// <summary>Removes every custom name / hide rule that applies to these apps.</summary>
    public void Reset(IEnumerable<long> appIds)
    {
        var apps = AppsByIds(appIds);
        _db.Transaction(() =>
        {
            foreach (var a in apps)
            {
                if (a.TitleRuleId > 0) _db.Exec("DELETE FROM app_alias WHERE id=$0", a.TitleRuleId);
                _db.Exec("DELETE FROM app_alias WHERE COALESCE(title_contains,'')='' AND ((match_kind='exe_path' AND LOWER(match_value)=LOWER($0)) OR (match_kind='exe_name' AND LOWER(match_value)=LOWER($1)))",
                    a.ExePath, a.ExeName);
            }
        });
    }

    public void SetHidden(long aliasId, bool hidden) => _db.Exec("UPDATE app_alias SET hidden=$0 WHERE id=$1", hidden ? 1 : 0, aliasId);

    public void Delete(long aliasId) => _db.Exec("DELETE FROM app_alias WHERE id=$0", aliasId);

    public void DismissUnnamed(string exePath)
    {
        var list = new List<string>();
        try { var j = _db.GetMeta("dismissed_unnamed"); if (j != null) list = JsonSerializer.Deserialize<List<string>>(j)!; } catch { }
        if (!list.Contains(exePath, StringComparer.OrdinalIgnoreCase)) list.Add(exePath);
        _db.SetMeta("dismissed_unnamed", JsonSerializer.Serialize(list));
    }

    private List<AppRow> AppsByIds(IEnumerable<long> ids)
    {
        var list = ids.Distinct().ToList();
        if (list.Count == 0) return new();
        return _db.Query($"SELECT id,exe_path,exe_name,detected_name,source,title_rule_id,first_seen_utc FROM app WHERE id IN ({string.Join(",", list)})",
            r => new AppRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
    }
}
