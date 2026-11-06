using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace TimeTracker.Core;

/// <summary>English / German text. One embedded JSON file is the single source for the native UI and the web UI.</summary>
public static class I18n
{
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = Load();

    private static Dictionary<string, Dictionary<string, string>> Load()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("i18n.json")
                      ?? throw new InvalidOperationException("i18n.json missing");
        return JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(s)!;
    }

    public static readonly string[] Languages = { "en", "de" };

    /// <summary>Maps the setting ("auto", "en", "de") to a concrete language. "auto" follows the Windows display language.</summary>
    public static string Resolve(string setting, CultureInfo? ui = null)
    {
        if (setting is "en" or "de") return setting;
        var two = (ui ?? CultureInfo.CurrentUICulture).TwoLetterISOLanguageName;
        return two == "de" ? "de" : "en";
    }

    public static IReadOnlyDictionary<string, string> Strings(string lang)
    {
        var d = new Dictionary<string, string>(Tables["en"]);
        if (lang != "en" && Tables.TryGetValue(lang, out var t)) foreach (var kv in t) d[kv.Key] = kv.Value;
        return d;
    }

    public static string T(string lang, string key, params object?[] args)
    {
        string s = Tables.TryGetValue(lang, out var t) && t.TryGetValue(key, out var v) ? v
                 : Tables["en"].TryGetValue(key, out var e) ? e : key;
        return args.Length == 0 ? s : string.Format(CultureInfo.CurrentCulture, s, args);
    }

    public static CultureInfo Culture(string lang) => CultureInfo.GetCultureInfo(lang == "de" ? "de-DE" : "en-GB");

    public static string FormatDuration(string lang, double seconds)
    {
        int m = (int)Math.Round(seconds / 60.0);
        if (seconds > 0 && m == 0) return T(lang, "fmt.lt1m");
        return m < 60 ? T(lang, "fmt.m", m) : T(lang, "fmt.hm", m / 60, (m % 60).ToString("00"));
    }
}
