using TimeTracker.Core;

namespace TimeTracker;

internal static class Prompts
{
    /// <summary>Asks for the PIN when one is set. Returns true when no PIN is set or the right PIN was entered.</summary>
    public static bool RequirePin(Settings settings, string lang, IWin32Window? owner = null)
    {
        if (!settings.PinSet) return true;
        using var f = new Form
        {
            Text = I18n.T(lang, "pin.prompt.title"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = false, TopMost = true, ClientSize = new Size(340, 130),
            Icon = AppIcons.Base,
        };
        var label = new Label { Text = I18n.T(lang, "pin.prompt.text"), Left = 16, Top = 16, Width = 308, Height = 22 };
        var box = new TextBox { Left = 16, Top = 44, Width = 308, UseSystemPasswordChar = true, MaxLength = 12 };
        var ok = new Button { Text = I18n.T(lang, "btn.ok"), Left = 168, Top = 86, Width = 74, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = I18n.T(lang, "btn.cancel"), Left = 250, Top = 86, Width = 74, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { label, box, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        f.Shown += (_, _) => { f.Activate(); box.Focus(); };
        for (int attempt = 0; attempt < 5; attempt++)
        {
            box.Clear();
            if (f.ShowDialog(owner) != DialogResult.OK) return false;
            if (settings.VerifyPin(box.Text)) return true;
            MessageBox.Show(I18n.T(lang, "pin.wrong"), I18n.T(lang, "pin.prompt.title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        return false;
    }

    public static (DateOnly From, DateOnly To)? AskRange(string lang)
    {
        using var f = new Form
        {
            Text = I18n.T(lang, "range.title"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen, ShowInTaskbar = false, TopMost = true, ClientSize = new Size(340, 150),
            Icon = AppIcons.Base,
        };
        var today = DateTime.Today;
        var l1 = new Label { Text = I18n.T(lang, "range.from"), Left = 16, Top = 20, Width = 60 };
        var d1 = new DateTimePicker { Left = 90, Top = 16, Width = 234, Format = DateTimePickerFormat.Long, Value = today.AddDays(-6), MaxDate = today };
        var l2 = new Label { Text = I18n.T(lang, "range.to"), Left = 16, Top = 58, Width = 60 };
        var d2 = new DateTimePicker { Left = 90, Top = 54, Width = 234, Format = DateTimePickerFormat.Long, Value = today, MaxDate = today };
        var ok = new Button { Text = I18n.T(lang, "range.create"), Left = 150, Top = 104, Width = 100, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = I18n.T(lang, "btn.cancel"), Left = 258, Top = 104, Width = 66, DialogResult = DialogResult.Cancel };
        f.Controls.AddRange(new Control[] { l1, d1, l2, d2, ok, cancel });
        f.AcceptButton = ok; f.CancelButton = cancel;
        if (f.ShowDialog() != DialogResult.OK) return null;
        var a = DateOnly.FromDateTime(d1.Value); var b = DateOnly.FromDateTime(d2.Value);
        return a <= b ? (a, b) : (b, a);
    }

    public static void Info(string text, string lang, MessageBoxIcon icon = MessageBoxIcon.Information) =>
        MessageBox.Show(text, "TimeTracker", MessageBoxButtons.OK, icon);
}

internal static class AppIcons
{
    private static Icon? _base;
    public static Icon Base => _base ??= Load(SystemInformation.SmallIconSize);

    public static Icon Load(Size size)
    {
        using var s = typeof(AppIcons).Assembly.GetManifestResourceStream("app.ico")!;
        return new Icon(s, size);
    }

    /// <summary>The tray icon with a small orange dot while tracking is paused.</summary>
    public static Icon WithPausedBadge(Size size)
    {
        using var baseIcon = Load(size);
        using var bmp = new Bitmap(baseIcon.ToBitmap());
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            int d = Math.Max(7, bmp.Width / 2);
            var r = new Rectangle(bmp.Width - d, bmp.Height - d, d - 1, d - 1);
            using var white = new SolidBrush(Color.White); using var orange = new SolidBrush(Color.FromArgb(0xf2, 0x8c, 0x1b));
            g.FillEllipse(white, r.X - 1, r.Y - 1, r.Width + 2, r.Height + 2);
            g.FillEllipse(orange, r);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
