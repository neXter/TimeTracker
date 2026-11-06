using System.Diagnostics;
using System.Text.RegularExpressions;
using TimeTracker.Core;

namespace TimeTracker;

/// <summary>
/// Friendly names for executables (DESIGN §3.2): Steam game manifests, then the file's version info,
/// then the raw exe file name (which the UI flags as "unnamed" so the user can give it a name).
/// </summary>
internal sealed class AppNamer : INameDetector
{
    private readonly Dictionary<string, (long at, List<(string installDir, string name)> games)> _steam = new(StringComparer.OrdinalIgnoreCase);

    public (string Name, string Source) Detect(string exePath)
    {
        if (TrySteam(exePath) is { } game) return (game, "steam");

        try
        {
            var vi = FileVersionInfo.GetVersionInfo(exePath);
            string baseName = Path.GetFileNameWithoutExtension(exePath);
            string desc = Clean(vi.FileDescription), product = Clean(vi.ProductName);
            string pick = "";
            if (desc.Length > 0 && !desc.Equals(baseName, StringComparison.OrdinalIgnoreCase)) pick = desc;
            else if (product.Length > 0 && !product.Equals(baseName, StringComparison.OrdinalIgnoreCase)) pick = product;
            else if (desc.Length > 0) pick = desc;
            if (pick.Length > 0 && !IsJunk(pick))
                return (pick, exePath.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ? "package" : "version-info");
        }
        catch { /* inaccessible file: fall through */ }

        return (Path.GetFileName(exePath), "fallback");
    }

    private static string Clean(string? s) => (s ?? "").Trim().Trim('\0');

    private static bool IsJunk(string s) =>
        s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || s.Equals("Application", StringComparison.OrdinalIgnoreCase) ||
        s.Equals("Unknown", StringComparison.OrdinalIgnoreCase) || s.Length > 80;

    // ---------- Steam ----------

    private string? TrySteam(string exePath)
    {
        int i = exePath.IndexOf(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        string steamapps = exePath[..(i + @"\steamapps".Length)];
        string rest = exePath[(i + @"\steamapps\common\".Length)..];
        int slash = rest.IndexOf('\\');
        if (slash <= 0) return null;
        string installDir = rest[..slash];

        long now = Environment.TickCount64;
        if (!_steam.TryGetValue(steamapps, out var entry) || now - entry.at > 300_000 ||
            !entry.games.Any(g => g.installDir.Equals(installDir, StringComparison.OrdinalIgnoreCase)) && now - entry.at > 20_000)
        {
            entry = (now, LoadManifests(steamapps));
            _steam[steamapps] = entry;
        }
        foreach (var g in entry.games)
            if (g.installDir.Equals(installDir, StringComparison.OrdinalIgnoreCase)) return g.name;
        return null;
    }

    private static List<(string, string)> LoadManifests(string steamapps)
    {
        var list = new List<(string, string)>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
            {
                try
                {
                    string text = File.ReadAllText(f);
                    var dir = Regex.Match(text, "\"installdir\"\\s+\"([^\"]+)\"");
                    var name = Regex.Match(text, "\"name\"\\s+\"([^\"]+)\"");
                    if (dir.Success && name.Success) list.Add((dir.Groups[1].Value, name.Groups[1].Value));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }
}
