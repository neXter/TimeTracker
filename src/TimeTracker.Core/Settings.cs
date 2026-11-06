using System.Security.Cryptography;

namespace TimeTracker.Core;

/// <summary>User settings, stored in the meta table and cached in memory (the collector reads them every second).</summary>
public sealed class Settings
{
    private readonly Db _db;
    public Settings(Db db)
    {
        _db = db;
        IdleMinutes = ParseInt(db.GetMeta("idle_minutes"), 5, 1, 120);
        FullscreenActive = db.GetMeta("fullscreen_active") != "0";
        RecordTitles = db.GetMeta("record_titles") == "1";
        Language = db.GetMeta("language") ?? "auto";
        ReportDir = db.GetMeta("report_dir") ?? DefaultReportDir;
        AutoReports = db.GetMeta("auto_reports") != "0";
    }

    public static string DefaultReportDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TimeTracker");

    public volatile int IdleMinutes;
    public volatile bool FullscreenActive;
    public volatile bool RecordTitles;
    public string Language;      // "auto" | "en" | "de"
    public string ReportDir;
    public volatile bool AutoReports;

    private static int ParseInt(string? s, int def, int min, int max) =>
        int.TryParse(s, out var v) ? Math.Clamp(v, min, max) : def;

    /// <summary>Applies one setting from the UI. Returns false for an unknown key.</summary>
    public bool Set(string key, object? value)
    {
        string s = value?.ToString() ?? "";
        switch (key)
        {
            case "idleMinutes": IdleMinutes = ParseInt(s, 5, 1, 120); _db.SetMeta("idle_minutes", IdleMinutes.ToString()); break;
            case "fullscreenActive": FullscreenActive = IsTrue(s); _db.SetMeta("fullscreen_active", FullscreenActive ? "1" : "0"); break;
            case "recordTitles": RecordTitles = IsTrue(s); _db.SetMeta("record_titles", RecordTitles ? "1" : "0"); break;
            case "language": Language = s is "en" or "de" ? s : "auto"; _db.SetMeta("language", Language); break;
            case "reportDir": if (string.IsNullOrWhiteSpace(s)) return false; ReportDir = s; _db.SetMeta("report_dir", s); break;
            case "autoReports": AutoReports = IsTrue(s); _db.SetMeta("auto_reports", AutoReports ? "1" : "0"); break;
            default: return false;
        }
        return true;
    }

    private static bool IsTrue(string s) => s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";

    // ---- PIN (PBKDF2, never stored in clear) ----
    public bool PinSet => _db.GetMeta("pin_hash") != null;

    public void SetPin(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, 100_000, HashAlgorithmName.SHA256, 32);
        _db.SetMeta("pin_salt", Convert.ToBase64String(salt));
        _db.SetMeta("pin_hash", Convert.ToBase64String(hash));
    }

    public void ClearPin() { _db.SetMeta("pin_salt", null); _db.SetMeta("pin_hash", null); }

    public bool VerifyPin(string pin)
    {
        var s = _db.GetMeta("pin_salt"); var h = _db.GetMeta("pin_hash");
        if (s == null || h == null) return true;
        var calc = Rfc2898DeriveBytes.Pbkdf2(pin, Convert.FromBase64String(s), 100_000, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(calc, Convert.FromBase64String(h));
    }
}
