using System.Text.RegularExpressions;
using TimeTracker.Core;
using Xunit;

namespace TimeTracker.Tests;

public class StatsTests
{
    /// <summary>Inserts an app and returns its id.</summary>
    private static long App(Db db, string path, string name, string source = "version-info", long rule = 0) =>
        db.Insert("INSERT INTO app(exe_path,exe_name,detected_name,source,title_rule_id,first_seen_utc) VALUES($0,$1,$2,$3,$4,0)",
            path, Path.GetFileName(path), name, source, rule);

    private static void Dev(Db db, string state, string a, string b) =>
        db.Exec("INSERT INTO device_interval(state,start_utc,end_utc) VALUES($0,$1,$2)", state, T.Ms(a), T.Ms(b));
    private static void Fg(Db db, long app, string a, string b) =>
        db.Exec("INSERT INTO foreground_interval(app_id,start_utc,end_utc) VALUES($0,$1,$2)", app, T.Ms(a), T.Ms(b));
    private static void Open(Db db, long app, string a, string b) =>
        db.Exec("INSERT INTO open_interval(app_id,start_utc,end_utc) VALUES($0,$1,$2)", app, T.Ms(a), T.Ms(b));

    private static StatsService Svc(Db db, string now, TimeZoneInfo? tz = null) =>
        new(db, "TEST-PC", tz ?? TimeZoneInfo.Utc) { NowMs = () => T.Ms(now) };

    [Fact]
    public void Week_view_sums_per_day_and_per_app()
    {
        var db = Db.InMemory();
        long chrome = App(db, @"C:\chrome.exe", "Google Chrome"), word = App(db, @"C:\WINWORD.EXE", "Microsoft Word");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T11:00:00Z");     // Mon, 60 min
        Dev(db, "active", "2026-11-04T18:00:00Z", "2026-11-04T19:30:00Z");     // Wed, 90 min
        Dev(db, "idle", "2026-11-04T19:30:00Z", "2026-11-04T19:40:00Z");
        Fg(db, chrome, "2026-11-02T10:00:00Z", "2026-11-02T10:40:00Z");
        Fg(db, word, "2026-11-02T10:40:00Z", "2026-11-02T11:00:00Z");
        Fg(db, chrome, "2026-11-04T18:00:00Z", "2026-11-04T19:30:00Z");
        Open(db, chrome, "2026-11-02T09:55:00Z", "2026-11-02T12:00:00Z");

        var s = Svc(db, "2026-11-06T12:00:00Z").Get(PeriodKind.Week, new DateOnly(2026, 11, 4));
        Assert.Equal("2026-11-02", s.From); Assert.Equal("2026-11-08", s.To);
        Assert.Equal(7, s.Buckets.Count);
        Assert.Equal(3600, s.Buckets[0].Active, 1);
        Assert.Equal(5400, s.Buckets[2].Active, 1);
        Assert.Equal(600, s.Buckets[2].Idle, 1);
        Assert.Equal(9000, s.Tiles.ActiveSec, 1);

        var gc = s.Groups.Single(g => g.Name == "Google Chrome");
        Assert.Equal(2400 + 5400, gc.Fg, 1);
        Assert.Equal(7500, gc.Open, 1);       // open 09:55-12:00 clipped to the week, all on Monday
        Assert.Equal(1, gc.Sessions);
        Assert.Equal("Google Chrome", s.Groups[0].Name);   // sorted by foreground time
        Assert.True(gc.Slot is >= 1 and <= 5);
        Assert.NotEqual(gc.Slot, s.Groups.Single(g => g.Name == "Microsoft Word").Slot);
        Assert.Equal(6000, s.Tiles.LongestSec, 1);        // the Wednesday run: 90 min active + 10 min idle are merged
    }

    [Fact]
    public void Longest_session_merges_active_and_idle_and_names_top_app()
    {
        var db = Db.InMemory();
        long chrome = App(db, @"C:\chrome.exe", "Google Chrome");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T10:30:00Z");
        Dev(db, "idle", "2026-11-02T10:30:00Z", "2026-11-02T10:40:00Z");
        Dev(db, "active", "2026-11-02T10:40:00Z", "2026-11-02T11:00:00Z");
        Dev(db, "locked", "2026-11-02T11:00:00Z", "2026-11-02T12:00:00Z");
        Fg(db, chrome, "2026-11-02T10:00:00Z", "2026-11-02T10:30:00Z");
        var s = Svc(db, "2026-11-02T13:00:00Z").Get(PeriodKind.Day, new DateOnly(2026, 11, 2));
        Assert.Equal(3600, s.Tiles.LongestSec, 1);
        Assert.Equal(s.Groups[0].Id, s.Tiles.LongestGroupId);
        Assert.NotNull(s.Timeline);
        Assert.Contains(s.Timeline!.Device, d => d.State == "locked");
    }

    [Fact]
    public void Sessions_past_local_midnight_are_split_between_days()
    {
        var db = Db.InMemory();
        long game = App(db, @"C:\game.exe", "Game");
        // 23:00 -> 01:30 local (UTC+1 in winter => 22:00Z -> 00:30Z)
        var tz = TimeZoneInfo.CreateCustomTimeZone("UTC+1", TimeSpan.FromHours(1), "UTC+1", "UTC+1");
        Dev(db, "active", "2026-11-02T22:00:00Z", "2026-11-03T00:30:00Z");
        Fg(db, game, "2026-11-02T22:00:00Z", "2026-11-03T00:30:00Z");
        var s = Svc(db, "2026-11-10T00:00:00Z", tz).Get(PeriodKind.Week, new DateOnly(2026, 11, 2));
        Assert.Equal(3600, s.Buckets[0].Active, 1);            // Mon 23:00-24:00 local
        Assert.Equal(5400, s.Buckets[1].Active, 1);            // Tue 00:00-01:30 local
        Assert.Equal(9000, s.Groups[0].Fg, 1);
    }

    [Fact]
    public void Dst_change_day_has_correct_buckets()
    {
        TimeZoneInfo tz;
        try { tz = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time"); }
        catch { tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"); }
        var db = Db.InMemory();
        long a = App(db, @"C:\a.exe", "A");
        // 2026-10-25 is the end of DST in Germany: that local day has 25 hours.
        var svc = Svc(db, "2026-11-10T00:00:00Z", tz);
        Dev(db, "active", "2026-10-24T22:00:00Z", "2026-10-25T23:00:00Z");   // exactly one local day (25h): 00:00 CEST -> 00:00 CET
        var s = svc.Get(PeriodKind.Week, new DateOnly(2026, 10, 25));
        Assert.Equal(25 * 3600, s.Buckets[6].Active, 1);       // Sunday
        Assert.Equal(0, s.Buckets[0].Active, 1);               // Monday of that week (Oct 19)
        var day = svc.Get(PeriodKind.Day, new DateOnly(2026, 10, 25));
        Assert.Equal(25 * 3600, day.Timeline!.DaySeconds, 1);
    }

    [Fact]
    public void Heatmap_counts_active_minutes_by_weekday_and_hour()
    {
        var db = Db.InMemory();
        Dev(db, "active", "2026-11-04T18:30:00Z", "2026-11-04T19:30:00Z");   // Wednesday 18:30-19:30
        var s = Svc(db, "2026-11-08T12:00:00Z").Get(PeriodKind.Week, new DateOnly(2026, 11, 4));
        Assert.Equal(30, s.Heat[2][18], 0.5);
        Assert.Equal(30, s.Heat[2][19], 0.5);
        Assert.Equal(0, s.Heat[2][17]);
        Assert.Equal(0, s.Heat[0][18]);
    }

    [Fact]
    public void Hidden_and_default_hidden_apps_are_left_out()
    {
        var db = Db.InMemory();
        long chrome = App(db, @"C:\chrome.exe", "Google Chrome"), lockapp = App(db, @"C:\Windows\LockApp.exe", "LockApp");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T11:00:00Z");
        Fg(db, chrome, "2026-11-02T10:00:00Z", "2026-11-02T10:30:00Z");
        Fg(db, lockapp, "2026-11-02T10:30:00Z", "2026-11-02T11:00:00Z");
        var s = Svc(db, "2026-11-03T00:00:00Z").Get(PeriodKind.Day, new DateOnly(2026, 11, 2));
        Assert.Single(s.Groups);
        Assert.Equal("Google Chrome", s.Groups[0].Name);
        Assert.Equal(3600, s.Tiles.ActiveSec, 1);    // device time is unaffected by hiding
    }

    [Fact]
    public void Blips_shorter_than_a_second_are_ignored()
    {
        var db = Db.InMemory();
        long a = App(db, @"C:\a.exe", "A");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T10:01:00Z");
        db.Exec("INSERT INTO foreground_interval(app_id,start_utc,end_utc) VALUES($0,1000,1400)", a);
        db.Exec("INSERT INTO foreground_interval(app_id,start_utc,end_utc) VALUES($0,$1,$2)", a, T.Ms("2026-11-02T10:00:00Z"), T.Ms("2026-11-02T10:00:00Z") + 800);
        var s = Svc(db, "2026-11-03T00:00:00Z").Get(PeriodKind.Day, new DateOnly(2026, 11, 2));
        Assert.Empty(s.Groups);
    }

    [Fact]
    public void Colour_slots_stay_stable_for_the_same_app()
    {
        var db = Db.InMemory();
        long a = App(db, @"C:\a.exe", "A"), b = App(db, @"C:\b.exe", "B");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T12:00:00Z");
        Fg(db, a, "2026-11-02T10:00:00Z", "2026-11-02T10:10:00Z");
        Fg(db, b, "2026-11-02T10:10:00Z", "2026-11-02T11:50:00Z");
        var svc = Svc(db, "2026-11-03T00:00:00Z");
        var first = svc.Get(PeriodKind.Day, new DateOnly(2026, 11, 2));
        int slotA = first.Groups.Single(g => g.Name == "A").Slot, slotB = first.Groups.Single(g => g.Name == "B").Slot;
        // a different period in which the order is reversed
        Fg(db, a, "2026-11-03T10:00:00Z", "2026-11-03T11:50:00Z");
        Fg(db, b, "2026-11-03T11:50:00Z", "2026-11-03T12:00:00Z");
        Dev(db, "active", "2026-11-03T10:00:00Z", "2026-11-03T12:00:00Z");
        var second = svc.Get(PeriodKind.Day, new DateOnly(2026, 11, 3));
        Assert.Equal(slotA, second.Groups.Single(g => g.Name == "A").Slot);
        Assert.Equal(slotB, second.Groups.Single(g => g.Name == "B").Slot);
        Assert.NotEqual(slotA, slotB);
    }

    [Fact]
    public void Previous_period_delta_uses_the_period_before()
    {
        var db = Db.InMemory();
        Dev(db, "active", "2026-10-27T10:00:00Z", "2026-10-27T11:00:00Z");   // previous week: 60 min
        Dev(db, "active", "2026-11-03T10:00:00Z", "2026-11-03T12:00:00Z");   // this week: 120 min
        var s = Svc(db, "2026-11-08T00:00:00Z").Get(PeriodKind.Week, new DateOnly(2026, 11, 3));
        Assert.Equal(7200, s.Tiles.ActiveSec, 1);
        Assert.Equal(3600, s.Tiles.PrevActiveSec, 1);
    }

    [Fact]
    public void Year_view_uses_monthly_buckets()
    {
        var db = Db.InMemory();
        Dev(db, "active", "2026-03-10T10:00:00Z", "2026-03-10T11:00:00Z");
        var s = Svc(db, "2026-11-08T00:00:00Z").Get(PeriodKind.Year, new DateOnly(2026, 6, 1));
        Assert.Equal("month", s.Granularity);
        Assert.Equal(12, s.Buckets.Count);
        Assert.Equal(3600, s.Buckets[2].Active, 1);
    }

    // ---------- custom names ----------

    [Fact]
    public void Custom_name_applies_to_all_past_data()
    {
        var db = Db.InMemory();
        long g = App(db, @"D:\Games\Hollow\game_x64.exe", "game_x64", "fallback");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T11:00:00Z");
        Fg(db, g, "2026-11-02T10:00:00Z", "2026-11-02T11:00:00Z");
        var svc = Svc(db, "2026-11-03T00:00:00Z");
        var before = svc.Get(PeriodKind.Day, new DateOnly(2026, 11, 2));
        Assert.True(before.Groups[0].Unnamed);
        Assert.Single(svc.UnnamedApps());

        new AliasService(db).Save(new AliasService.SaveRequest { AppIds = new() { g }, Name = "Hollow Quest", MatchKind = "exe_path" });
        var after = svc.Get(PeriodKind.Day, new DateOnly(2026, 11, 2));
        Assert.Equal("Hollow Quest", after.Groups[0].Name);
        Assert.True(after.Groups[0].Custom);
        Assert.False(after.Groups[0].Unnamed);
        Assert.Empty(svc.UnnamedApps());

        new AliasService(db).Reset(new[] { g });
        Assert.True(svc.Get(PeriodKind.Day, new DateOnly(2026, 11, 2)).Groups[0].Unnamed);
    }

    [Fact]
    public void Same_name_merges_two_exes_into_one_app()
    {
        var db = Db.InMemory();
        long launcher = App(db, @"C:\Game\launcher.exe", "launcher", "fallback"), game = App(db, @"C:\Game\game.exe", "game", "fallback");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T11:00:00Z");
        Fg(db, launcher, "2026-11-02T10:00:00Z", "2026-11-02T10:10:00Z");
        Fg(db, game, "2026-11-02T10:10:00Z", "2026-11-02T11:00:00Z");
        var al = new AliasService(db);
        al.Save(new() { AppIds = new() { game }, Name = "Big Game", MatchKind = "exe_name" });
        al.Save(new() { AppIds = new() { launcher }, Name = "ignored", MergeInto = "Big Game", MatchKind = "exe_name" });
        var s = Svc(db, "2026-11-03T00:00:00Z").Get(PeriodKind.Day, new DateOnly(2026, 11, 2));
        Assert.Single(s.Groups);
        Assert.Equal("Big Game", s.Groups[0].Name);
        Assert.Equal(3600, s.Groups[0].Fg, 1);
        Assert.Equal(2, s.Groups[0].AppIds.Count);
    }

    [Fact]
    public void Exe_name_rule_matches_any_folder_and_path_rule_wins()
    {
        var idx = new AliasIndex(new[]
        {
            new Alias(1, "exe_name", "game.exe", null, "By Name", false),
            new Alias(2, "exe_path", @"C:\Special\game.exe", null, "By Path", false),
        });
        Assert.Equal("By Name", idx.View(new AppRow(1, @"D:\Other\game.exe", "game.exe", "game", "fallback", 0, 0)).Name);
        Assert.Equal("By Path", idx.View(new AppRow(2, @"c:\special\GAME.EXE", "GAME.EXE", "game", "fallback", 0, 0)).Name);
    }

    [Fact]
    public void Hiding_and_showing_an_app_through_aliases()
    {
        var db = Db.InMemory();
        long a = App(db, @"C:\tool.exe", "Tool");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T11:00:00Z");
        Fg(db, a, "2026-11-02T10:00:00Z", "2026-11-02T11:00:00Z");
        var al = new AliasService(db);
        al.Save(new() { AppIds = new() { a }, Name = "Tool", MatchKind = "exe_path", Hidden = true });
        var svc = Svc(db, "2026-11-03T00:00:00Z");
        Assert.Empty(svc.Get(PeriodKind.Day, new DateOnly(2026, 11, 2)).Groups);
        var id = al.List().Single(x => x.MatchValue == @"C:\tool.exe").Id;
        al.SetHidden(id, false);
        Assert.Single(svc.Get(PeriodKind.Day, new DateOnly(2026, 11, 2)).Groups);
    }

    // ---------- reports, settings, i18n ----------

    [Fact]
    public void Report_html_is_self_contained_and_csv_follows_the_language()
    {
        var db = Db.InMemory();
        long chrome = App(db, @"C:\chrome.exe", "Google Chrome");
        Dev(db, "active", "2026-11-02T10:00:00Z", "2026-11-02T11:00:00Z");
        Fg(db, chrome, "2026-11-02T10:00:00Z", "2026-11-02T10:45:00Z");
        var svc = Svc(db, "2026-11-08T00:00:00Z");
        var s = svc.Get(PeriodKind.Week, new DateOnly(2026, 11, 2));
        var html = ReportBuilder.BuildHtml(s, "de");
        Assert.Contains("Google Chrome", html);
        Assert.Contains("TEST-PC", html);
        Assert.Contains("TimeTracker-Bericht", html);
        Assert.DoesNotContain("{{", html);
        Assert.DoesNotContain("src=\"common.js\"", html);
        var csvDe = ReportBuilder.BuildCsv(s, "de");
        Assert.Contains("2026-11-02;Google Chrome;chrome.exe;45,0;", csvDe);
        var csvEn = ReportBuilder.BuildCsv(s, "en");
        Assert.Contains("2026-11-02,Google Chrome,chrome.exe,45.0,", csvEn);
        Assert.Equal("2026-W45", ReportBuilder.PeriodTag(s));
    }

    [Fact]
    public void Scheduled_reports_are_written_once_per_period()
    {
        var db = Db.InMemory();
        long a = App(db, @"C:\a.exe", "A");
        Dev(db, "active", "2026-10-27T10:00:00Z", "2026-10-27T11:00:00Z");
        Fg(db, a, "2026-10-27T10:00:00Z", "2026-10-27T11:00:00Z");
        var dir = Path.Combine(Path.GetTempPath(), "tt-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var set = new Settings(db); set.Set("reportDir", dir);
            var svc = Svc(db, "2026-11-03T09:00:00Z");   // Tuesday: last week (Oct 26-Nov 1) is complete
            var made = ReportBuilder.RunScheduled(svc, db, set, "en");
            Assert.NotEmpty(made);
            Assert.All(made, f => Assert.True(File.Exists(f)));
            Assert.Empty(ReportBuilder.RunScheduled(svc, db, set, "en"));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Pin_is_hashed_and_verified()
    {
        var db = Db.InMemory(); var set = new Settings(db);
        Assert.False(set.PinSet);
        set.SetPin("4711");
        Assert.True(set.PinSet);
        Assert.True(set.VerifyPin("4711")); Assert.False(set.VerifyPin("0000"));
        Assert.DoesNotContain("4711", db.GetMeta("pin_hash"));
        set.ClearPin(); Assert.False(set.PinSet);
    }

    [Fact]
    public void Settings_persist_in_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), "tt-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new Db(path)) { var s = new Settings(db); s.Set("idleMinutes", 10); s.Set("language", "de"); s.Set("recordTitles", true); }
            using (var db = new Db(path)) { var s = new Settings(db); Assert.Equal(10, s.IdleMinutes); Assert.Equal("de", s.Language); Assert.True(s.RecordTitles); }
        }
        finally { foreach (var f in Directory.GetFiles(Path.GetTempPath(), Path.GetFileName(path) + "*")) try { File.Delete(f); } catch { } }
    }

    [Fact]
    public void Language_resolves_from_windows_culture()
    {
        Assert.Equal("de", I18n.Resolve("auto", new System.Globalization.CultureInfo("de-AT")));
        Assert.Equal("en", I18n.Resolve("auto", new System.Globalization.CultureInfo("nl-NL")));
        Assert.Equal("de", I18n.Resolve("de", new System.Globalization.CultureInfo("en-US")));
        Assert.Equal("3h 05m", I18n.FormatDuration("en", 3 * 3600 + 5 * 60));
        Assert.Equal("3 Std. 05 Min.", I18n.FormatDuration("de", 3 * 3600 + 5 * 60));
    }

    [Fact]
    public void Every_translation_key_exists_in_both_languages_and_is_used_consistently()
    {
        var en = I18n.Strings("en"); var de = I18n.Strings("de");
        using var s = typeof(I18n).Assembly.GetManifestResourceStream("i18n.json")!;
        var raw = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(s)!;
        Assert.Empty(raw["en"].Keys.Except(raw["de"].Keys));
        Assert.Empty(raw["de"].Keys.Except(raw["en"].Keys));
        foreach (var kv in raw["en"])
        {
            var ph = Regex.Matches(kv.Value, @"\{(\d)\}").Select(m => m.Value).Distinct().OrderBy(x => x);
            var phDe = Regex.Matches(raw["de"][kv.Key], @"\{(\d)\}").Select(m => m.Value).Distinct().OrderBy(x => x);
            Assert.True(ph.SequenceEqual(phDe), $"placeholder mismatch in {kv.Key}");
        }
    }

    [Theory]
    [InlineData("Web/charts.js")] [InlineData("Web/dashboard.js")] [InlineData("Web/flyout.js")] [InlineData("Web/settings.js")] [InlineData("Web/common.js")]
    public void Every_translation_key_used_in_the_web_ui_is_defined(string file)
    {
        var keys = I18n.Strings("en").Keys.ToHashSet();
        var src = Res.ReadText(file);
        var prefixes = new[] { "fmt", "btn", "period", "nav", "dash", "export", "tile", "chart", "tip", "table", "heat", "tl", "state", "apps", "rn", "sess", "fl", "rep", "report", "set", "pin", "about" };
        var used = Regex.Matches(src, @"'([a-z]+(?:\.[A-Za-z0-9]+)+)'").Select(m => m.Groups[1].Value)
            .Where(k => prefixes.Contains(k.Split('.')[0])).Distinct();
        foreach (var k in used) Assert.True(keys.Contains(k), $"{file}: missing translation key '{k}'");
        foreach (var k in new[] { "empty" }) Assert.Contains(k, keys);
        // dynamic keys
        foreach (var k in new[] { "period.day", "period.week", "period.month", "period.year", "state.active", "state.idle", "state.locked", "state.untracked" }) Assert.Contains(k, keys);
    }
}
