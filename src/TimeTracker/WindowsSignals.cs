using TimeTracker.Core;

namespace TimeTracker;

/// <summary>Reads the real foreground window, open windows, idle time, fullscreen state and gamepad input.</summary>
internal sealed class WindowsSignals : ISignals
{
    private readonly int _ownPid = Environment.ProcessId;
    private readonly Dictionary<uint, (string? path, long at)> _pathCache = new();

    public volatile bool Locked;
    public long NowMs => Time.NowMs();
    public bool IsLocked => Locked;

    // ---------- idle (keyboard / mouse / touch / gamepad) ----------

    private readonly uint[] _lastPacket = new uint[4];
    private readonly bool[] _padSeen = new bool[4];
    private long _lastPadInputTick = long.MinValue / 2;
    private long _lastPadScan;

    public double IdleSeconds
    {
        get
        {
            var li = new Native.LASTINPUTINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.LASTINPUTINFO>() };
            double idle = double.MaxValue;
            if (Native.GetLastInputInfo(ref li))
                idle = unchecked((uint)Environment.TickCount - li.dwTime) / 1000.0;
            PollGamepads();
            double pad = (Environment.TickCount64 - _lastPadInputTick) / 1000.0;
            return Math.Min(idle, pad);
        }
    }

    private void PollGamepads()
    {
        long now = Environment.TickCount64;
        bool scanDisconnected = now - _lastPadScan > 5000;   // probing absent pads is comparatively slow
        if (scanDisconnected) _lastPadScan = now;
        for (int i = 0; i < 4; i++)
        {
            if (!_padSeen[i] && !scanDisconnected) continue;
            int r = Native.XInput(i, out var st);
            if (r == -1) return;                // XInput not available
            if (r != 0) { _padSeen[i] = false; continue; }
            if (_padSeen[i] && st.dwPacketNumber != _lastPacket[i]) _lastPadInputTick = now;
            _padSeen[i] = true;
            _lastPacket[i] = st.dwPacketNumber;
        }
    }

    // ---------- foreground ----------

    public WindowInfo? Foreground()
    {
        try
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            return Describe(h);
        }
        catch { return null; }
    }

    public bool ForegroundIsFullscreen
    {
        get
        {
            try
            {
                IntPtr h = Native.GetForegroundWindow();
                if (h == IntPtr.Zero || h == Native.GetShellWindow()) return false;
                var cls = Native.ClassName(h);
                if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
                if (!Native.GetWindowRect(h, out var r)) return false;
                var mon = Native.MonitorFromWindow(h, 2 /* MONITOR_DEFAULTTONEAREST */);
                var mi = new Native.MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
                if (!Native.GetMonitorInfo(mon, ref mi)) return false;
                var m = mi.rcMonitor;
                return r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
            }
            catch { return false; }
        }
    }

    // ---------- open windows ----------

    public IReadOnlyList<WindowInfo> OpenWindows()
    {
        var list = new List<WindowInfo>();
        Native.EnumWindows((h, _) =>
        {
            try
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                if (((long)Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0) return true;
                if (Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                var title = Native.WindowText(h);
                if (title.Length == 0) return true;
                var w = Describe(h, title);
                if (w != null && w.Pid != _ownPid) list.Add(w);
            }
            catch { /* skip windows that vanish while enumerating */ }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // ---------- helpers ----------

    private WindowInfo? Describe(IntPtr h, string? title = null)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        if (pid == 0) return null;
        string? path = PathOf(pid);
        if (path == null) return null;

        // Store apps are hosted in ApplicationFrameHost.exe; the real app is a child window of another process.
        if (path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
        {
            uint real = 0;
            Native.EnumChildWindows(h, (c, _) =>
            {
                Native.GetWindowThreadProcessId(c, out uint cp);
                if (cp != 0 && cp != pid) { real = cp; return false; }
                return true;
            }, IntPtr.Zero);
            if (real != 0) { var rp = PathOf(real); if (rp != null) { path = rp; pid = real; } }
        }
        return new WindowInfo((int)pid, path, title ?? Native.WindowText(h));
    }

    private string? PathOf(uint pid)
    {
        long now = Environment.TickCount64;
        if (_pathCache.TryGetValue(pid, out var c) && now - c.at < 10_000) return c.path;
        var p = Native.ProcessPath(pid);
        if (_pathCache.Count > 512) _pathCache.Clear();
        _pathCache[pid] = (p, now);
        return p;
    }
}
