namespace TimeTracker.Core;

/// <summary>What the UI shows for an app after custom names / hiding rules are applied.</summary>
public record AppView(string Name, bool Custom, bool Unnamed, bool Hidden, long? AliasId);

/// <summary>
/// Custom names (see DESIGN §3.4). The name is looked up when data is viewed, so naming an app
/// changes all past data. Title-scoped rules are the exception: they split apps at recording time.
/// </summary>
public sealed class AliasIndex
{
    /// <summary>System helper processes that are never useful in usage statistics.</summary>
    public static readonly HashSet<string> DefaultHidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "LockApp.exe", "SearchHost.exe", "StartMenuExperienceHost.exe", "ShellExperienceHost.exe",
        "TextInputHost.exe", "LogonUI.exe", "dwm.exe", "ApplicationFrameHost.exe", "TimeTracker.exe",
        "SecHealthUI.exe", "CredentialUIBroker.exe", "consent.exe",
    };

    private readonly List<Alias> _all;
    public IReadOnlyList<Alias> All => _all;

    public AliasIndex(IEnumerable<Alias> all) { _all = all.ToList(); }

    public static AliasIndex Load(Db db) => new(db.Query(
        "SELECT id,match_kind,match_value,title_contains,display_name,hidden FROM app_alias ORDER BY id",
        r => new Alias(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.GetInt64(5) != 0)));

    private static bool Matches(Alias a, string exePath)
    {
        return a.MatchKind == "exe_path"
            ? string.Equals(a.MatchValue, exePath, StringComparison.OrdinalIgnoreCase)
            : string.Equals(a.MatchValue, Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Title-scoped rule for a window, if any (longest keyword wins, path rules before name rules).</summary>
    public Alias? TitleRule(string exePath, string title)
    {
        if (string.IsNullOrEmpty(title)) return null;
        return _all
            .Where(a => !string.IsNullOrEmpty(a.TitleContains) && Matches(a, exePath)
                        && title.Contains(a.TitleContains!, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.MatchKind == "exe_path")
            .ThenByDescending(a => a.TitleContains!.Length)
            .FirstOrDefault();
    }

    public Alias? ById(long id) => _all.FirstOrDefault(a => a.Id == id);

    /// <summary>The whole-app rule (no title condition) for an app: exact path first, then file name.</summary>
    public Alias? WholeAppRule(string exePath)
    {
        return _all.Where(a => string.IsNullOrEmpty(a.TitleContains) && Matches(a, exePath))
                   .OrderByDescending(a => a.MatchKind == "exe_path")
                   .FirstOrDefault();
    }

    public AppView View(AppRow app)
    {
        if (app.TitleRuleId > 0)
        {
            var rule = ById(app.TitleRuleId);
            if (rule != null) return new AppView(rule.DisplayName, true, false, rule.Hidden, rule.Id);
        }
        var a = WholeAppRule(app.ExePath);
        if (a != null) return new AppView(a.DisplayName, true, false, a.Hidden, a.Id);
        bool hidden = DefaultHidden.Contains(app.ExeName);
        return new AppView(app.DetectedName, false, app.Source == "fallback", hidden, null);
    }
}
