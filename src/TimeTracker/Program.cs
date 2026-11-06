using System.Diagnostics;
using TimeTracker.Core;

namespace TimeTracker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.System);
        Application.ThreadException += (_, e) => Log.Write("UI exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Unhandled: " + e.ExceptionObject);

        bool Has(string a) => args.Contains(a, StringComparer.OrdinalIgnoreCase);
        string lang = I18n.Resolve(ReadLanguageSetting());

        try
        {
            if (Has("--uninstall")) return Uninstall(lang);
            if (Has("--install")) { Installer.Install(); if (!Has("--no-launch")) Installer.Launch("--background --first-run"); return 0; }

            // A downloaded copy (not the installed one) offers to install itself instead of silently running.
            if (!Installer.IsInstalledCopy && !Has("--portable") && !Has("--dev"))
                return OfferInstall(lang);

            // One instance per Windows user. A second launch just opens the dashboard of the first.
            using var mutex = new Mutex(true, Installer.MutexName, out bool first);
            if (!first)
            {
                try { using var ev = EventWaitHandle.OpenExisting(Has("--settings") ? Installer.SettingsEventName : Has("--flyout") ? Installer.FlyoutEventName : Installer.ShowEventName); ev.Set(); } catch { }
                return 0;
            }

            using var app = new TrayApp(showFlyoutOnStart: Has("--first-run"));
            Application.Run(app);
            return 0;
        }
        catch (NewerDatabaseException nd)
        {
            Log.Write("refused newer database: " + nd.Message);
            MessageBox.Show(I18n.T(lang, "data.newer", nd.FoundVersion, nd.SupportedVersion), "TimeTracker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return 2;
        }
        catch (Exception ex)
        {
            Log.Write("fatal: " + ex);
            MessageBox.Show(ex.Message, "TimeTracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>The language setting lives in the database; before the app is running we read it directly.</summary>
    private static string ReadLanguageSetting()
    {
        try
        {
            var path = Path.Combine(Installer.DataDir, "tracker.db");
            if (!File.Exists(path)) return "auto";
            using var db = new Db(path);
            return db.GetMeta("language") ?? "auto";
        }
        catch { return "auto"; }
    }

    private static int OfferInstall(string lang)
    {
        var installedVersion = Installer.IsInstalled ? Installer.InstalledVersion : null;
        var mine = Installer.CurrentVersion;
        bool update = installedVersion != null;
        string heading = I18n.T(lang, update ? "install.updateTitle" : "install.title");
        string text = installedVersion == null ? I18n.T(lang, "install.text")
            : installedVersion < mine ? I18n.T(lang, "install.updateText", installedVersion, mine)
            : installedVersion == mine ? I18n.T(lang, "install.sameText", mine)
            : I18n.T(lang, "install.olderText", installedVersion, mine);
        var install = new TaskDialogButton(I18n.T(lang, update ? "install.updateBtn" : "install.btn"));
        var runOnce = new TaskDialogButton(I18n.T(lang, "install.runOnce"));
        var page = new TaskDialogPage
        {
            Caption = "TimeTracker",
            Heading = heading,
            Text = text,
            Icon = installedVersion > mine ? TaskDialogIcon.Warning : TaskDialogIcon.Information,
            Buttons = { install, runOnce, TaskDialogButton.Cancel },
            DefaultButton = installedVersion > mine ? TaskDialogButton.Cancel : install,
            AllowCancel = true,
            Footnote = new TaskDialogFootnote { Text = I18n.T(lang, "install.footer", mine) },
        };
        var result = TaskDialog.ShowDialog(page);
        if (result == install)
        {
            try
            {
                Installer.Install();
                // A fresh install shows the flyout once so the user sees where the icon is; an update just restarts quietly.
                Installer.Launch(update ? "--background" : "--background --first-run");
                MessageBox.Show(update ? I18n.T(lang, "install.updated", mine) : I18n.T(lang, "install.done"), "TimeTracker", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log.Write("install failed: " + ex);
                MessageBox.Show(I18n.T(lang, "install.failed", ex.Message), "TimeTracker", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
            return 0;
        }
        if (result == runOnce)
        {
            using var mutex = new Mutex(true, Installer.MutexName, out bool first);
            if (!first) { MessageBox.Show(I18n.T(lang, "run.already"), "TimeTracker", MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; }
            using var app = new TrayApp(showFlyoutOnStart: true);
            Application.Run(app);
        }
        return 0;
    }

    private static int Uninstall(string lang)
    {
        // Started from "Apps & features". Ask once, stop the running tracker, then remove everything.
        if (MessageBox.Show(I18n.T(lang, "uninstall.confirm"), "TimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 0;
        bool deleteData = MessageBox.Show(I18n.T(lang, "uninstall.keepData", Installer.DataDir), "TimeTracker", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        Installer.StopOthers();
        Installer.Uninstall(deleteData);
        MessageBox.Show(I18n.T(lang, "uninstall.done"), "TimeTracker", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }
}
