using System.Globalization;
using System.Reflection;
using System.Text;

namespace TimeTracker.Core;

public static class Res
{
    private static readonly Assembly Asm = typeof(Res).Assembly;

    public static Stream? Open(string name) => Asm.GetManifestResourceStream(name);

    public static string ReadText(string name)
    {
        using var s = Open(name) ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s, Encoding.UTF8);
        return r.ReadToEnd();
    }

    public static (byte[] Data, string Mime)? Web(string path)
    {
        using var s = Open("Web/" + path);
        if (s == null) return null;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        string mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            _ => "application/octet-stream",
        };
        return (ms.ToArray(), mime);
    }
}

/// <summary>Self-contained HTML reports (same renderer as the dashboard) and CSV exports.</summary>
public static class ReportBuilder
{
    public static string BuildHtml(StatsDto stats, string lang)
    {
        var template = Res.ReadText("Web/report.html");
        var css = Res.ReadText("Web/app.css");
        var js = Res.ReadText("Web/common.js") + "\n" + Res.ReadText("Web/charts.js");
        var data = Json.Serialize(new { stats, lang, strings = I18n.Strings(lang) }).Replace("</", "<\\/");
        string title = I18n.T(lang, "report.title", stats.Device);
        return template
            .Replace("{{LANG}}", lang)
            .Replace("{{TITLE}}", System.Net.WebUtility.HtmlEncode(title))
            .Replace("{{CSS}}", css)
            .Replace("{{JS}}", js.Replace("</script", "<\\/script"))
            .Replace("{{DATA}}", data);
    }

    /// <summary>One row per app per day: date, app, executable, minutes in front, minutes open.</summary>
    public static string BuildCsv(StatsDto daily, string lang)
    {
        bool de = lang == "de";
        char sep = de ? ';' : ',';
        var ci = de ? CultureInfo.GetCultureInfo("de-DE") : CultureInfo.InvariantCulture;
        string Q(string s) => s.Contains(sep) || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        var sb = new StringBuilder();
        sb.Append('﻿');
        sb.AppendLine(string.Join(sep, new[]
        {
            I18n.T(lang, "csv.date"), I18n.T(lang, "csv.app"), I18n.T(lang, "csv.exe"),
            I18n.T(lang, "csv.fg"), I18n.T(lang, "csv.open"),
        }));
        foreach (var b in daily.Buckets)
        {
            foreach (var g in daily.Groups)
            {
                double fg = b.Fg.GetValueOrDefault(g.Id), op = b.Open.GetValueOrDefault(g.Id);
                if (fg < 1 && op < 1) continue;
                sb.AppendLine(string.Join(sep, new[]
                {
                    b.Key, Q(g.Name), Q(g.Exe), (fg / 60).ToString("0.0", ci), (op / 60).ToString("0.0", ci),
                }));
            }
        }
        return sb.ToString();
    }

    public static string SafeName(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    public static string PeriodTag(StatsDto s) => s.Period switch
    {
        "day" => s.From,
        "week" => $"{ISOWeek.GetYear(DateTime.Parse(s.From, CultureInfo.InvariantCulture))}-W{ISOWeek.GetWeekOfYear(DateTime.Parse(s.From, CultureInfo.InvariantCulture)):00}",
        "month" => s.From[..7],
        "year" => s.From[..4],
        _ => $"{s.From}_{s.To}",
    };

    /// <summary>Writes the HTML report and a CSV next to it. Returns the HTML path.</summary>
    public static string Generate(StatsService stats, PeriodKind kind, DateOnly anchor, DateOnly? rangeEnd, string lang, string dir)
    {
        Directory.CreateDirectory(dir);
        var s = stats.Get(kind, anchor, rangeEnd);
        var daily = kind == PeriodKind.Year ? stats.Get(kind, anchor, rangeEnd, dailyBuckets: true) : s;
        string name = $"TimeTracker_{SafeName(stats.DeviceName)}_{PeriodTag(s)}_{lang}";
        string html = Path.Combine(dir, name + ".html");
        File.WriteAllText(html, BuildHtml(s, lang), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, name + ".csv"), BuildCsv(daily, lang), new UTF8Encoding(false));
        return html;
    }

    /// <summary>Creates last week's / last month's report once, after the period has ended. Returns the files written.</summary>
    public static List<string> RunScheduled(StatsService stats, Db db, Settings settings, string lang)
    {
        var made = new List<string>();
        if (!settings.AutoReports) return made;
        var today = stats.Today();

        var lastWeek = StatsService.WeekStart(today).AddDays(-7);
        string wk = lastWeek.ToString("yyyy-MM-dd");
        if (db.GetMeta("auto_week_done") != wk)
        {
            if (stats.Get(PeriodKind.Week, lastWeek).HasData) made.Add(Generate(stats, PeriodKind.Week, lastWeek, null, lang, settings.ReportDir));
            db.SetMeta("auto_week_done", wk);
        }
        var lastMonth = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
        string mo = lastMonth.ToString("yyyy-MM");
        if (db.GetMeta("auto_month_done") != mo)
        {
            if (stats.Get(PeriodKind.Month, lastMonth).HasData) made.Add(Generate(stats, PeriodKind.Month, lastMonth, null, lang, settings.ReportDir));
            db.SetMeta("auto_month_done", mo);
        }
        return made;
    }
}
