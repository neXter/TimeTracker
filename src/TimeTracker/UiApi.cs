using System.Diagnostics;
using System.Text.Json;
using TimeTracker.Core;

namespace TimeTracker;

/// <summary>Commands the web UI can send to the app (see web/common.js: api(cmd, args)).</summary>
internal sealed partial class TrayApp
{
    private static string Str(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString()) : "";

    private static long[] Ids(JsonElement a, string name) =>
        a.ValueKind == JsonValueKind.Object && a.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetInt64()).ToArray() : Array.Empty<long>();

    private static (PeriodKind Kind, DateOnly Anchor, DateOnly? End) ParsePeriod(JsonElement a)
    {
        var kind = Enum.Parse<PeriodKind>(Str(a, "period"), ignoreCase: true);
        var anchor = DateOnly.Parse(Str(a, "anchor"), System.Globalization.CultureInfo.InvariantCulture);
        DateOnly? end = Str(a, "rangeEnd") is { Length: > 0 } e ? DateOnly.Parse(e, System.Globalization.CultureInfo.InvariantCulture) : null;
        return (kind, anchor, end);
    }

    public object? Handle(WebWindow w, string cmd, JsonElement a)
    {
        switch (cmd)
        {
            case "init":
                return new { lang = Lang, strings = I18n.Strings(Lang), device = Installer.DeviceName, version = Installer.VersionText };

            case "stats":
                { var (k, an, end) = ParsePeriod(a); return Stats.Get(k, an, end); }

            case "flyout":
                {
                    var f = Stats.Flyout();
                    return new { stats = f.Stats, unnamed = f.Unnamed, firstActiveMs = f.FirstActiveMs, nowMs = f.NowMs, paused = Collector.IsPaused, pausedUntilMs = Collector.PausedUntilMs };
                }

            case "sessions":
                { var (k, an, end) = ParsePeriod(a); return Stats.Sessions(Ids(a, "appIds"), k, an, end); }

            case "appInfo":
                return Stats.AppInfo(Ids(a, "appIds"));

            // ---- custom names ----
            case "saveAlias":
                {
                    var req = JsonSerializer.Deserialize<AliasService.SaveRequest>(a.GetRawText(), Json.Options)!;
                    Aliases.Save(req);
                    AliasesChanged(w);
                    return true;
                }
            case "resetApps": Aliases.Reset(Ids(a, "appIds")); AliasesChanged(w); return true;
            case "deleteAlias": Aliases.Delete(long.Parse(Str(a, "id"))); AliasesChanged(w); return true;
            case "showAlias": Aliases.SetHidden(long.Parse(Str(a, "id")), false); AliasesChanged(w); return true;
            case "dismissUnnamed": Aliases.DismissUnnamed(Str(a, "path")); BroadcastRefresh(w); return true;

            // ---- settings ----
            case "getSettings":
                return new
                {
                    language = Settings.Language,
                    resolvedLanguage = Lang,
                    autostart = new { state = Autostart.State(), command = Autostart.CurrentCommand() },
                    idleMinutes = Settings.IdleMinutes,
                    fullscreenActive = Settings.FullscreenActive,
                    recordTitles = Settings.RecordTitles,
                    reportDir = Settings.ReportDir,
                    autoReports = Settings.AutoReports,
                    pinSet = Settings.PinSet,
                    dbPath = Db.Path,
                    dbSize = DbSize(),
                    version = Installer.VersionText,
                    device = Installer.DeviceName,
                    installed = Installer.IsInstalledCopy,
                    aliases = Aliases.List(),
                };

            case "setSetting":
                {
                    string key = Str(a, "key");
                    if (!Settings.Set(key, Str(a, "value"))) throw new ArgumentException("unknown setting " + key);
                    if (key == "language") { UpdateTooltip(); BroadcastRefresh(w); }
                    return true;
                }

            case "setAutostart":
                {
                    if (!RequirePin()) return Autostart.State();
                    bool enable = Str(a, "enabled").Equals("true", StringComparison.OrdinalIgnoreCase);
                    if (enable) Autostart.Enable(Installer.IsInstalledCopy || !File.Exists(Installer.InstalledExe) ? Installer.CurrentExe : Installer.InstalledExe);
                    else Autostart.Disable();
                    return Autostart.State();
                }

            case "openWindowsStartup":
                OpenWithShell("ms-settings:startupapps"); return true;

            case "chooseReportDir":
                {
                    using var dlg = new FolderBrowserDialog { SelectedPath = Settings.ReportDir, ShowNewFolderButton = true, UseDescriptionForTitle = true, Description = T("set.reportDir") };
                    if (dlg.ShowDialog(w) != DialogResult.OK) return null;
                    Settings.Set("reportDir", dlg.SelectedPath);
                    return dlg.SelectedPath;
                }

            case "setPin":
                {
                    string pin = Str(a, "pin");
                    if (pin.Length < 4) throw new ArgumentException("pin too short");
                    // Changing an existing PIN needs the current one, which the settings dialog collects itself.
                    if (Settings.PinSet && !Settings.VerifyPin(Str(a, "current"))) return false;
                    Settings.SetPin(pin); return true;
                }
            case "clearPin":
                if (!RequirePin()) return false;
                Settings.ClearPin(); return true;

            case "openFolder":
                {
                    string dir = Str(a, "which") == "data" ? Installer.DataDir : Settings.ReportDir;
                    Directory.CreateDirectory(dir);
                    OpenWithShell("explorer.exe", $"\"{dir}\"");
                    return true;
                }

            case "deleteAllData":
                {
                    if (!RequirePin()) return false;
                    if (MessageBox.Show(w, T("delete.confirm"), "TimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
                    Db.DeleteTrackingData();
                    Collector.ForgetState();
                    UpdateTooltip(); BroadcastRefresh(w);
                    MessageBox.Show(w, T("delete.done"), "TimeTracker", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
                }

            case "uninstall":
                {
                    if (!Installer.IsInstalledCopy) return false;
                    if (!RequirePin()) return false;
                    if (MessageBox.Show(w, T("uninstall.confirm"), "TimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
                    bool deleteData = MessageBox.Show(w, T("uninstall.keepData", Installer.DataDir), "TimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    ExitApp();
                    Installer.Uninstall(deleteData);
                    Environment.Exit(0);
                    return true;
                }

            // ---- reports ----
            case "generateReport":
                { var (k, an, end) = ParsePeriod(a); return new { path = GenerateReport(k, an, end, open: true) }; }
            case "report":
                GenerateReport(Str(a, "kind")); return true;

            // ---- windows & tracking ----
            case "openDashboard":
                {
                    var ids = Ids(a, "rename");
                    BeginInvoke(() => ShowDashboard(ids.Length > 0 ? ids : null));
                    return true;
                }
            case "openSettings": BeginInvoke(ShowSettings); return true;
            case "resize": w.ResizeFlyout((int)double.Parse(Str(a, "height"), System.Globalization.CultureInfo.InvariantCulture)); return true;
            case "close": BeginInvoke(() => { if (w.Page == "flyout") w.HideFlyout(); else w.Close(); }); return true;
            case "resume": ResumeTracking(); return true;

            default:
                throw new ArgumentException("unknown command " + cmd);
        }
    }

    private void BeginInvoke(Action a) => _ui.BeginInvoke(a);

    private void AliasesChanged(WebWindow from)
    {
        Collector.InvalidateAliases();
        BroadcastRefresh(from);
    }

    public void BroadcastRefresh(WebWindow? except)
    {
        foreach (var win in _windows.ToList()) if (win != except) win.Post(new { @event = "refresh" });
    }

    private long DbSize()
    {
        long n = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { var f = new FileInfo(Db.Path + suffix); if (f.Exists) n += f.Length; } catch { }
        }
        return n;
    }
}
