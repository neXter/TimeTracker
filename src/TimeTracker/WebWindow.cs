using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;
using TimeTracker.Core;

namespace TimeTracker;

/// <summary>A window that hosts one of the embedded HTML pages (flyout, dashboard, settings) in WebView2.</summary>
internal sealed class WebWindow : Form
{
    private static Task<CoreWebView2Environment>? _env;
    private static Task<CoreWebView2Environment> Env() =>
        _env ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(Installer.DataDir, "WebView2"));

    public string Page { get; }
    private readonly TrayApp _app;
    private readonly WebView2 _wv = new() { Dock = DockStyle.Fill };
    private readonly bool _flyout;

    // Flyout life cycle: it is created once at startup (hidden, "warm") and then only shown/hidden, because
    // creating a WebView2 and loading the page cold takes 1-2 s.
    private bool _wantVisible;                 // the user asked for it to be on screen
    private bool _ready;                       // the page has rendered at least once
    private int _lastHeight = 520;
    private DateTime _hideOnDeactivateAfter = DateTime.MaxValue;
    private Stopwatch? _showTimer;
    public bool IsFlyoutVisible => _flyout && _wantVisible;

    public static bool SystemUsesDarkMode()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int v && v == 0; }
        catch { return false; }
    }

    public WebWindow(TrayApp app, string page, string? query = null)
    {
        _app = app; Page = page; _flyout = page == "flyout";
        _query = query;
        Icon = AppIcons.Load(new Size(32, 32));
        Text = "TimeTracker";
        bool dark = SystemUsesDarkMode();
        var bg = _flyout ? (dark ? Color.FromArgb(0x24, 0x24, 0x23) : Color.FromArgb(0xfb, 0xfb, 0xfa)) : (dark ? Color.FromArgb(0x0d, 0x0d, 0x0d) : Color.FromArgb(0xf9, 0xf9, 0xf7));
        BackColor = bg; _wv.DefaultBackgroundColor = bg;
        Controls.Add(_wv);

        if (_flyout)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true; StartPosition = FormStartPosition.Manual;
            ClientSize = LogicalToDeviceUnits(new Size(372, 520));
            Opacity = 0;
            Location = new Point(-32000, -32000);   // warm-up happens off-screen and fully transparent
        }
        else
        {
            ShowInTaskbar = true; StartPosition = FormStartPosition.CenterScreen;
            ClientSize = LogicalToDeviceUnits(page == "dashboard" ? new Size(1180, 840) : new Size(720, 860));
            MinimumSize = LogicalToDeviceUnits(page == "dashboard" ? new Size(820, 560) : new Size(560, 480));
            if (page == "settings") { MaximizeBox = false; }
        }
        Load += async (_, _) => await InitWebAsync();
        Deactivate += (_, _) => { if (_flyout && _wantVisible && DateTime.UtcNow > _hideOnDeactivateAfter) HideFlyout(); };
        FormClosed += (_, _) => { try { _wv.Dispose(); } catch { } _app.WindowClosed(this); };
    }

    private readonly string? _query;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_flyout) { int pref = 2; Native.DwmSetWindowAttribute(Handle, Native.DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)); }
        try { int dark = SystemUsesDarkMode() ? 1 : 0; Native.DwmSetWindowAttribute(Handle, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref dark, sizeof(int)); } catch { }
    }

    private async Task InitWebAsync()
    {
        try
        {
            var env = await Env();
            await _wv.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // The window was closed while WebView2 was still starting (e.g. the flyout was replaced by the dashboard).
            if (IsDisposed || Disposing || !IsHandleCreated || ex is System.Runtime.InteropServices.COMException { HResult: unchecked((int)0x80004004) })
            {
                Log.Write("WebView2 init cancelled (window closed): " + ex.Message);
                return;
            }
            Log.Write("WebView2 init failed: " + ex);
            string lang = _app.Lang;
            MessageBox.Show(lang == "de"
                    ? "Für die Anzeige wird die „Microsoft Edge WebView2“-Laufzeit benötigt. Sie ist bei Windows 11 enthalten. Download: https://go.microsoft.com/fwlink/p/?LinkId=2124703"
                    : "The \"Microsoft Edge WebView2\" runtime is required to show this window. It is included with Windows 11. Download: https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "TimeTracker", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            Close(); return;
        }
        var core = _wv.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Environment.GetEnvironmentVariable("TIMETRACKER_DEVTOOLS") == "1";
        core.Settings.AreDefaultContextMenusEnabled = core.Settings.AreDevToolsEnabled;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = core.Settings.AreDevToolsEnabled;
        core.AddWebResourceRequestedFilter("https://app.local/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnResourceRequested;
        core.WebMessageReceived += OnMessage;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith("https://app.local/", StringComparison.Ordinal)) e.Cancel = true; };
        core.DocumentTitleChanged += (_, _) => { if (!_flyout) Text = core.DocumentTitle; };
        core.Navigate($"https://app.local/{Page}.html{(_query != null ? "?" + _query : "")}");

        if (_flyout)
        {
            // Safety net: if the page never reports its size, still let the window appear when it was requested.
            _ = Task.Delay(4000).ContinueWith(_ => BeginInvoke(() => { if (!_ready) ResizeFlyout(_lastHeight); }), TaskScheduler.Default);
        }
    }

    // ---------- flyout show / hide ----------

    /// <summary>Shows the (already loaded) flyout. It becomes visible once the page has re-rendered with fresh numbers.</summary>
    public void RequestShowFlyout()
    {
        if (!_flyout) return;
        _showTimer = Stopwatch.StartNew();
        _wantVisible = true;
        _hideOnDeactivateAfter = DateTime.UtcNow.AddMilliseconds(700);
        Wake();
        if (!_ready) return;                      // first render still pending: ResizeFlyout shows it when it arrives
        Opacity = 0;                               // invisible until the refreshed content is in place
        if (!Visible) Show();
        Post(new { @event = "refresh" });
        _ = Task.Delay(1500).ContinueWith(_ => BeginInvoke(() => { if (_wantVisible && Opacity < 1) ResizeFlyout(_lastHeight); }), TaskScheduler.Default);
    }

    public void HideFlyout()
    {
        if (!_flyout) return;
        bool wasVisible = _wantVisible;
        _wantVisible = false;
        if (Visible) Hide();
        if (wasVisible) _app.FlyoutHidden();
        _ = TrimWhenHiddenAsync();
    }

    /// <summary>
    /// While hidden, tell WebView2 to give memory back (it is only a warm cache): resident memory drops from about
    /// 280 MB to about 40 MB within a minute. Waking it up again takes a few milliseconds.
    /// </summary>
    private async Task TrimWhenHiddenAsync()
    {
        try
        {
            await Task.Delay(2500);
            if (_wantVisible || IsDisposed) return;      // the user opened it again in the meantime
            var core = _wv.CoreWebView2;
            if (core == null) return;
            try { core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low; }
            catch (Exception ex) { Log.Write("memory level failed: " + ex.Message); }
        }
        catch (Exception ex) { Log.Write("trim failed: " + ex.Message); }
        await Task.CompletedTask;
    }

    private void Wake()
    {
        try
        {
            var core = _wv.CoreWebView2;
            if (core == null) return;
            if (core.IsSuspended) core.Resume();
            core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch (Exception ex) { Log.Write("wake failed: " + ex.Message); }
    }

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        var env = _wv.CoreWebView2.Environment;
        var path = new Uri(e.Request.Uri).AbsolutePath.TrimStart('/');
        if (path.Length == 0) path = Page + ".html";
        var res = Res.Web(path);
        if (res == null)
        {
            e.Response = env.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }
        e.Response = env.CreateWebResourceResponse(new MemoryStream(res.Value.Data), 200, "OK",
            $"Content-Type: {res.Value.Mime}\r\nCache-Control: no-store");
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        long id = 0;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt64();
            string cmd = root.GetProperty("cmd").GetString() ?? "";
            var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
            var result = _app.Handle(this, cmd, args);
            Reply(new { id, ok = true, result });
        }
        catch (Exception ex)
        {
            Log.Write("UI command failed: " + ex);
            if (id != 0) Reply(new { id, ok = false, error = ex.Message });
        }
    }

    private void Reply(object msg)
    {
        try { _wv.CoreWebView2?.PostWebMessageAsJson(Json.Serialize(msg)); } catch { }
    }

    public void Post(object msg) => Reply(msg);

    /// <summary>Sizes the flyout to its content and anchors it above the taskbar, next to the tray area.</summary>
    public void ResizeFlyout(int cssHeight)
    {
        if (!_flyout) return;
        _lastHeight = cssHeight; _ready = true;
        if (!_wantVisible)                         // warm-up (or a refresh while hidden) finished: stay out of sight
        {
            if (Visible) { Hide(); _ = TrimWhenHiddenAsync(); }
            return;
        }
        var screen = Screen.FromPoint(Cursor.Position);
        var wa = screen.WorkingArea;
        int margin = LogicalToDeviceUnits(12);
        int w = LogicalToDeviceUnits(372);
        int h = Math.Min(LogicalToDeviceUnits(Math.Max(cssHeight, 200)), wa.Height - 2 * margin);
        ClientSize = new Size(w, h);
        // Anchor at the bottom-right of the work area (the tray sits there on a default Windows 11 taskbar).
        int x = wa.Right - Width - margin, y = wa.Bottom - Height - margin;
        Location = new Point(Math.Max(wa.Left, x), Math.Max(wa.Top, y));
        if (!Visible) Show();
        if (Opacity < 1)
        {
            Opacity = 1; Activate();
            if (_showTimer != null) Log.Write($"flyout visible {_showTimer.ElapsedMilliseconds} ms after the request");
        }
    }

    // While warming up (hidden) the flyout must never take the focus away from what the user is doing.
    protected override bool ShowWithoutActivation => _flyout && !_wantVisible;
}
