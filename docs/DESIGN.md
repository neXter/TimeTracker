# TimeTracker: proposal & design

*Status: implemented (v1.0) · 2026-11-06. See §13 for what differs from the original proposal.*
*Mocks: [mocks/ui-mocks.html](mocks/ui-mocks.html). Open it in Edge or Chrome. It has four screens: tray flyout and menu, dashboard, naming an app, and settings.*

## 1. What it is

A small Windows 11 app that runs in the background and starts when you log in. It has no window, no Start-menu entry, and no taskbar button. It lives only as an icon in the notification area (the tray, next to the clock). It records:

1. **Device active time**: when someone is actually using the PC.
2. **App open time**: how long each app had a visible window, focused or not.
3. **App foreground time**: how long each app was the focused window while the device was active.

From the tray icon you can open a **flyout** with today's numbers, a **dashboard** with day, week and month views, and **reports** for a week, a month or a custom range. Reports are a self-contained HTML file (print to PDF from the browser) plus CSV.

Apps get a name, not a category: "Google Chrome", "Discord", "Rocket League", "Microsoft Word". Names are detected automatically where possible. **Where all that's available is an `.exe` file name, you can give the app your own name** (§3.4, §6.2), and that name is then used everywhere, including old data and reports.

**Purpose.** This is a self-monitoring tool: people can see how they use their own computer. That drives the design: the icon is always visible, the data stays on the device, and the numbers are shown to the person they belong to.

### Non-goals (v1)
- Categorising apps, blocking apps, or time limits. (Microsoft Family Safety covers limits for child accounts. This tool is about *seeing* usage.)
- Tracking websites or document names. Window titles are an opt-in setting, off by default (see §9).
- Any cloud backend, sync, or cross-device viewing. All data stays on the device it was recorded on.

---

## 2. Definitions: what exactly gets measured

Most of the design depends on these definitions, so they come first.

| Term | Definition |
|---|---|
| **Active** | Session unlocked **and** user input in the last *N* minutes (default 5), **or** an idle-exempt condition holds (below). |
| **Idle** | Session unlocked but no input for more than *N* minutes and no exemption. Shown separately; not counted as "active". |
| **Locked / off** | Workstation locked, signed out, asleep, hibernated or shut down. Nothing is counted. |
| **Idle exemptions** | (a) The foreground window is fullscreen (games, videos). (b) Gamepad input, detected by polling XInput, because `GetLastInputInfo` doesn't see controllers. (c) Optional: an app holds a "display required" power request (video playback). |
| **App open** | The process has at least one *visible, top-level, non-tool window*. This counts Word with three documents as open, but not a background updater or Steam minimised to the tray. Counted whether the device is active or not. |
| **App foreground** | The app owns the foreground window **and** the device is *Active*. If you walk away from Chrome, foreground time stops after the idle threshold, and the idle minutes are not counted retroactively. |

So the following always holds: *sum of foreground time for a day = active time for that day*. Open time per app can be more than either.

---

## 3. Architecture

One process, `TimeTracker.exe`, per Windows user session.

```
┌──────────────────────────── TimeTracker.exe (user session) ───────────────────────────┐
│                                                                                       │
│  Signals                         Collector                       Storage              │
│  ───────                         ─────────                       ───────              │
│  WinEvent EVENT_SYSTEM_FOREGROUND─┐                                                   │
│  1 s poll: GetForegroundWindow ───┤  ┌──────────────────┐   ┌──────────────────────┐   │
│  GetLastInputInfo + XInput ───────┼─▶│  State machine    │──▶│ SQLite (WAL)         │   │
│  WTS session lock/unlock ─────────┤  │  Active/Idle/Off  │   │ %LOCALAPPDATA%\      │   │
│  Power suspend/resume ────────────┤  │  + current app    │   │   TimeTracker\       │   │
│  5 s poll: EnumWindows (open apps)┘  └────────┬─────────┘   │   tracker.db         │   │
│                                               │             └──────────┬───────────┘   │
│                                       ┌───────▼────────┐               │               │
│                                       │  App resolver  │               │               │
│                                       │ exe → friendly │      ┌────────▼──────────┐    │
│                                       │ name + icon    │      │ Query/aggregation │    │
│                                       └────────────────┘      └───┬──────────┬────┘    │
│                                                                   │          │         │
│   Tray icon + flyout (WPF) ◀──────────────────────────────────────┘          │         │
│   Dashboard window (WPF + WebView2, HTML charts) ◀───────────────────────────┤         │
│   Report generator (same HTML templates → .html / .csv) ◀────────────────────┘         │
└───────────────────────────────────────────────────────────────────────────────────────┘
```

### 3.1 Collector
- **Events first, polling as a safety net.** `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` catches app switches immediately. A 1-second timer re-checks the foreground window and input idle time, which is cheap. A 5-second timer walks top-level windows to update the set of open apps.
- `SystemEvents.SessionSwitch` (lock/unlock) and `SystemEvents.PowerModeChanged` (suspend/resume) close or open intervals right away.
- **Heartbeat.** The current interval's `end_utc` is written every 15 s. After a crash or power loss you lose at most 15 s, and on the next start any interval left open is closed at its last heartbeat.

### 3.2 App resolver (exe to a friendly name)
In order of preference:
1. **Custom names** set by you (see §3.4), e.g. `game_x64.exe` → **Hollow Quest**, or `javaw.exe` + window title contains "Minecraft" → **Minecraft**. These always win over automatic detection.
2. **Steam games.** If the exe path is under `…\steamapps\common\<dir>\`, look up `appmanifest_*.acf` with `installdir = <dir>` and use its `name` ("Rocket League"). Game exes often have useless version info such as "UnrealGame", so this step matters. Epic, GOG and Xbox games get similar handling (Epic `.item` manifests, packaged-app display names).
3. **Packaged/UWP apps.** The foreground window often belongs to `ApplicationFrameHost.exe`, so resolve the real child process, then use the package display name.
4. **Version info.** `FileDescription`, then `ProductName` ("Google Chrome", "Microsoft Word").
5. **Fallback.** The raw exe file name (e.g. `game_x64.exe`). The UI marks these as **unnamed** and offers "Name this app" (flyout hint, Apps table, Settings → Apps).

The exe icon is extracted once and cached in the DB for the UI.
Protected or anti-cheat processes sometimes refuse `PROCESS_QUERY_LIMITED_INFORMATION`. In that case fall back to `GetWindowModuleFileName` or the window class, and finally to "Unknown (title)".

### 3.3 Storage: SQLite, one file per device and user

Data is stored as intervals, not samples. That keeps the DB small (roughly 1–3 MB a year) and makes queries simple.

```sql
CREATE TABLE app (
  id            INTEGER PRIMARY KEY,
  exe_path      TEXT NOT NULL UNIQUE,
  exe_name      TEXT NOT NULL,
  detected_name TEXT NOT NULL,        -- from the resolver (steps 2-5); never edited by the user
  source        TEXT,                 -- 'version-info' | 'steam' | 'package' | 'fallback'
  title_rule_id INTEGER REFERENCES app_alias(id),  -- set only for title-scoped names, see §3.4
  icon_png      BLOB,
  first_seen_utc INTEGER NOT NULL
);

CREATE TABLE app_alias (             -- your custom names (see §3.4)
  id             INTEGER PRIMARY KEY,
  match_kind     TEXT NOT NULL CHECK (match_kind IN ('exe_path','exe_name')),
  match_value    TEXT NOT NULL,       -- full path, or file name in any folder
  title_contains TEXT,                -- optional; only for names that depend on the window title
  display_name   TEXT NOT NULL,
  hidden         INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE device_interval (       -- non-overlapping, covers the timeline
  id        INTEGER PRIMARY KEY,
  state     TEXT NOT NULL CHECK (state IN ('active','idle','locked')),
  start_utc INTEGER NOT NULL,        -- unix ms
  end_utc   INTEGER NOT NULL
);

CREATE TABLE foreground_interval (   -- only while state = active
  id        INTEGER PRIMARY KEY,
  app_id    INTEGER NOT NULL REFERENCES app(id),
  start_utc INTEGER NOT NULL,
  end_utc   INTEGER NOT NULL,
  title     TEXT                     -- NULL unless "record window titles" is on
);

CREATE TABLE open_interval (         -- may overlap across apps
  id        INTEGER PRIMARY KEY,
  app_id    INTEGER NOT NULL REFERENCES app(id),
  start_utc INTEGER NOT NULL,
  end_utc   INTEGER NOT NULL
);

CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);   -- device name, user, schema version, tz
CREATE INDEX ix_fg_start   ON foreground_interval(start_utc);
CREATE INDEX ix_open_start ON open_interval(start_utc);
CREATE INDEX ix_dev_start  ON device_interval(start_utc);
```

- Times are stored as UTC. Each query converts to the current local time zone and splits intervals at local midnight, so daylight-saving changes and late-night sessions land on the correct day.
- Raw foreground switches are kept. Very short blips under 1 s, such as alt-tabbing past windows, are filtered out when querying, not when storing.

### 3.4 Custom names
- **Name is looked up when you view data, not when it is recorded.** The effective name of an app is `app_alias.display_name`, if a rule matches, otherwise `app.detected_name`. So naming an app fixes **all past data** immediately, and changing or removing a name is always safe.
- **Matching.** A rule matches either the exact exe path ("this exact file only") or the exe file name in any folder (survives game updates and reinstalls to another drive). The path rule wins when both match.
- **Merging.** Queries group by effective name. Two exes with the same name, e.g. a game's launcher and the game itself, appear as one app with their time added up. The "Merge into another app" dropdown in the UI just copies the target's name.
- **Window-title rules** (e.g. `javaw.exe` + "Minecraft") are the one exception, since titles are not stored by default. The collector evaluates them while recording and tags the interval with a separate `app` row (`title_rule_id`). They therefore apply **from the moment you create them**, and the UI says so.
- **Hide** removes an app from charts, tables and reports (its time is still part of device active time). Hidden apps can be listed and restored in Settings → Apps.
- The raw exe name and path are always shown as the second line in the UI, so a custom name can never make it unclear what an entry really is.

### 3.5 Query layer
A small set of aggregate queries feeds every view and report:
`ActiveByDay`, `ActiveByHourOfWeek` (heatmap), `ForegroundByApp(range)`, `ForegroundByAppByDay(range)`, `OpenByApp(range)`, `Sessions(app, range)`, `Timeline(day)`.

---

## 4. Technology

| Choice | Why |
|---|---|
| **C# / .NET 10 (LTS)**, WPF | Native Win32 interop (WinEvent hooks, WTS, XInput) is easy, the footprint is small, and it can ship as a single exe. |
| **H.NotifyIcon.Wpf** | Tray icon and a Windows 11-style flyout. Also handles Explorer restarts. |
| **Microsoft.Data.Sqlite** | Embedded, zero setup, one file per device that is easy to copy. |
| **WebView2** (in the box on Win 11) | The dashboard is an HTML/SVG page. **The same templates render the dashboard and the exported reports**, so a report looks like the dashboard. WebView2 is created only while the dashboard is open and disposed afterwards. |
| Charts | Hand-written SVG, or a small bundled library (no CDN). It works offline. |
| **.resx resource files (en, de)** | All UI and report text is translatable; German is a first-class language rather than an afterthought. |

**Footprint budget (background):** under 30 MB RAM, about 0% CPU (1 Hz wake-ups), under 1 KB of disk writes per 15 s.

**Alternatives considered:** Rust + Tauri gives a smaller binary, but Win32 hooks and tray handling are more work. A Python script with PyInstaller is quick to prototype, but it is heavier at runtime and easy to break. Electron is too heavy to run in the background all day.

---

## 5. Install, autostart, and staying out of the way

- **Install:** `TimeTracker.exe --install` copies itself to `%LOCALAPPDATA%\Programs\TimeTracker\` and registers autostart. It creates **no Start-menu shortcut or desktop icon**. `--uninstall` reverses both and optionally deletes the data.
- **Autostart, switchable in Settings (Mock 4):** an `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\TimeTracker` value pointing at the exe with `--background`. It is simple, needs no admin rights, and starts at sign-in. **On after install by default.**
  - *Off:* the Run value is removed. The app keeps running until the next sign-in or Exit. The switch shows a warning that time while the app isn't running is not tracked (it shows as "Untracked" on the timeline, §8).
  - *On:* the value is written again.
  - **Blocked by Windows:** the user can also disable the app in *Windows Settings → Apps → Startup*, which sets a flag under `…\Explorer\StartupApproved\Run`. The settings screen reads that flag and shows "Blocked by Windows" with a button that opens `ms-settings:startupapps`. The app can't re-enable it itself.
  - With a PIN set (§8), the switch asks for the PIN.
- **Single instance:** a named mutex. A second launch just opens the dashboard of the running instance.
- **Tray visibility:** Windows 11 puts new tray icons in the overflow (^) by default. Pin it once via *Settings → Personalization → Taskbar → Other system tray icons*. Apps can't do this through a documented API, so the installer shows a one-time hint.
- **Dashboard window:** a normal window while open (so Alt-Tab works). When closed, the app goes back to tray-only.

---

## 6. UI

See **[mocks/ui-mocks.html](mocks/ui-mocks.html)**: Mock 1 flyout and menu, Mock 2 dashboard, Mock 3 naming an app, Mock 4 settings.

### 6.1 Tray icon
- **Hover tooltip:** `TimeTracker · Active today 3h 12m`.
- **Left-click → flyout (Mock 1):** active time today, a 24-hour timeline strip (active / idle / locked), the top 5 apps today by foreground time, and buttons for *Dashboard* and *Report*.
- **Right-click → menu:** Open dashboard · Generate report ▸ (This week, Last week, This month, Last month, Custom…) · Pause tracking ▸ (15 min, 1 h, until tomorrow) · Settings · Exit.
- **Settings…** opens the settings window (§6.3, Mock 4).
- The icon shows a small "paused" badge while tracking is paused, so a pause can't go unnoticed.
- If any app is still unnamed (only its raw `.exe` name is known), the flyout shows one line: "1 app has no name yet: `game_x64.exe` · Name it". It disappears when everything is named, and can be dismissed per app ("Keep as is").

### 6.2 Dashboard (Mock 2)
- **Filter row:** the device name (plain label, the real Windows computer name), period (Day / Week / Month / Year), date navigation, and *Export report*.
- **Stat tiles:** active time (with change vs. the previous period), daily average, most-used app, and the longest continuous session.
- **Foreground time per day:** stacked columns, top 5 apps plus "Other".
- **When the device is used:** a weekday × hour heatmap. This shows patterns such as late-night gaming at a glance.
- **Apps table:** foreground vs. open time on the same bar (foreground drawn inside open), sessions, and last used. Clicking a row opens that app's sessions for the period.
  - Each row shows the **exe file name under the app name** and has a **pencil** to rename it.
  - Apps with no detected name appear in italics as their exe name with a **Name this app** button.
  - A custom name carries a small "✎ custom" tag.
- **Name this app dialog (Mock 3):** exe name, full path and this period's usage (so you can recognise what it is), then *Display name*, *Apply to* (this exact file, or any file with this name in any folder), an optional *window title contains* rule, an optional *Merge into another app*, *Hide this app*, and *Reset to detected name*. A live preview line shows how the name will appear.
- **Day view** (not mocked) replaces the column chart with the full timeline: device states on top, foreground app segments below.

### 6.3 Settings
A small window (Mock 4) with five groups:

| Group | Settings |
|---|---|
| **General** | **Start TimeTracker when I sign in to Windows** (on/off, see §5), with a status line showing the current state (registered / removed / blocked by Windows). **Language**: *Same as Windows* (default), English or Deutsch. Takes effect immediately. |
| **Tracking** | Idle threshold (2 / 5 / 10 / 15 min) · count fullscreen apps and gamepad input as active · record window titles (off). |
| **Apps** | Custom names and hidden apps: count, plus **Manage…** to list, edit, reset and un-hide them. Default-hidden system helpers such as `explorer.exe` and `LockApp.exe`. |
| **Reports** | Report folder · automatic weekly and monthly reports. |
| **Protection** | PIN on/off and Set PIN. The PIN guards Pause, Exit, Settings and the autostart switch. |

The footer shows the DB location and size, with *Open folder* and *Delete all data…* (asks for confirmation).

### 6.4 Language and device name
- **Languages: English and German.** All text (UI, tooltips, tray menu, reports) comes from `.resx` resource files, so nothing is hard-coded. The default is the Windows display language (German if Windows is German, otherwise English). The Settings language selector overrides this. Weekday and month names, date formats, `3h 12m` vs. `3 Std. 12 Min.` and decimal separators follow the chosen language.
- **Device name.** The real Windows computer name (`Environment.MachineName`) is read at every start and shown in the flyout header, the dashboard filter row, the settings window and report file names. It is a label only and is not stored per row, so if the PC is renamed, the new name appears everywhere, including on old data.

---

## 7. Reports

**On demand.** *Generate report → period* writes one self-contained `TimeTracker_<DEVICE>_<period>.html` (charts inline, works offline) plus `…csv` (one row per app per day). `<DEVICE>` is the Windows computer name. The browser's *Print → Save as PDF* turns the HTML into a PDF.

**Scheduled.** Optionally, every Monday (and on the 1st of each month) the previous week or month is written automatically to the **report folder** (default: `Documents\TimeTracker`).

**Language.** Reports are written in the language selected in Settings (English or German), with matching weekday and month names, date formats and number formats.

**Everything stays on the device.** Each device keeps its own data and has its own dashboard and reports. There is no sync and no cross-device view. If the data should ever be shared, the report files are the way to do it, and it is a deliberate action by the person who owns the device.

---

## 8. Transparency and the PIN

**Transparency.** The tray icon is always visible and the owner of the device can open the dashboard at any time. There is no hidden mode. The tool shows people their own usage; it is not designed to watch someone without their knowledge.

**What the PIN does.** If a PIN is set (Settings → Protection), *Pause*, *Exit*, *Settings* (including the autostart switch) and *Delete all data* ask for it. It stops casual changes, for example by someone else using the laptop. It does **not** stop a determined user with Task Manager, who can end the process, remove the startup entry or delete the data file. That level of protection is deliberately **not** part of the design: it would require an administrator-installed service and would make the tool feel like surveillance.

**Gaps are visible.** The app also reads Windows' own logon, lock and unlock events when it starts, so periods when **the device was in use but the tracker wasn't running** (killed, autostart off, crash) appear as "Untracked" on the timeline and are excluded from totals. Nothing is silently missing.

---

## 9. Privacy and security
- No network traffic at all. Reports are only written to a folder on the device.
- **Window titles are off by default.** They can contain website titles, chat names and document names. When enabled they're stored only locally and are shown only in the per-app session drill-down.
- No keystroke content and no screenshots, ever. Only "input happened" timestamps.
- The DB is a plain SQLite file in the user's profile folder. Anyone who can sign in as that user (or an administrator) can read it. That is acceptable for a self-monitoring tool.

---

## 10. Edge cases (and how they're handled)

| Case | Handling |
|---|---|
| Watching a video or movie with no input | Fullscreen foreground app → idle-exempt. A non-fullscreen video becomes "idle" after *N* min unless the power-request exemption is on. |
| Gaming with a controller | XInput polling (packet-number change) counts as input. |
| Game launched via Steam | The resolver uses Steam's manifest name. Steam itself is tracked separately, only when its window is open or focused. |
| Java Minecraft (`javaw.exe`) | Built-in default custom-name rule: window title contains "Minecraft" → *Minecraft*. You can edit or remove it like any other. |
| Game or tool whose exe has no product name (`game_x64.exe`) | Shown as the raw exe name, flagged as unnamed, and offered in the flyout and Apps table. A custom name applies to all past data (§3.4). |
| Same program with several exes (launcher + game) | Give them the same name, or use "Merge into". Time is added up in charts and reports. |
| Game updates and moves to a new folder | Use "any file with this name, in any folder", so the custom name keeps matching. |
| Autostart switched off, or blocked in Windows Startup settings | Status line in Settings; time while the app isn't running shows as a gap (§5, §8). |
| Store/UWP apps | Resolve through `ApplicationFrameHost` to the real package. |
| Sleep, hibernate, lid closed | Power events close intervals. If the event was missed, the heartbeat caps the loss at 15 s. |
| Crash or hard power-off | The open interval is closed at its last heartbeat on the next start. |
| Session past midnight | Split at local midnight when querying. |
| DST or time-zone change | UTC storage plus conversion to local time when querying. |
| Fast user switching / multiple Windows accounts | One instance per user session, each with its own DB. Each person only sees their own data. |
| Elevated or anti-cheat processes | Limited-access query; window/module fallbacks; "Unknown (title)" as a last resort. |
| Remote Desktop session | Treated like a local session (input comes from the remote client). |

---

## 11. Decisions made, and what is still open

**Decided**

| Topic | Decision |
|---|---|
| Purpose | A tool for **self-monitoring**: each person sees their own device. |
| Data location | Stored **locally on each device only**. No shared folder, no snapshots, no cross-device view. |
| Tamper resistance | **A PIN is enough**. No watchdog service, no admin install. Gaps in tracking stay visible on the timeline. |
| Window titles | **Off by default.** |
| Idle threshold | **5 minutes** (changeable in Settings). |
| Tray icon | **Always visible**, on both devices. |
| Languages | **English and German** (§6.4). |
| Device name | The real **Windows computer name** is used everywhere (§6.4). |

**Still open**
1. **Who sets the PIN?** If it is meant as the user's own protection against casual "Exit" (for example a friend using the laptop), the user sets it. If it is meant for someone else to keep control, that person sets it and the user does not know it. The tool works either way; it is a matter of agreement between the people involved.

---

## 12. Milestones

| # | Deliverable | Notes |
|---|---|---|
| **M1** | Collector + SQLite + autostart + tray icon with the "today" tooltip + a minimal Settings window (autostart switch, language) | Run it for about a week to check the data (resolver names, idle behaviour). All UI text goes through English/German resource files from the start. |
| **M2** | Flyout + dashboard (Day / Week / Month) + **custom names** (pencil, Name this app dialog, flyout hint) | Custom names come early because unnamed exes will show up right in the first week of data. |
| **M3** | Reports (HTML/CSV, English and German), scheduled report folder, remaining Settings (tracking, reports) | |
| **M4** | PIN, hidden-app management, polish | |

---

## 13. Implementation notes (v1.0)

What was built, and where it differs from the proposal above.

| Topic | As built |
|---|---|
| **Stack** | .NET 10, WinForms for the tray and window frames, **WebView2** for the flyout, dashboard and settings (HTML/CSS/JS embedded in the exe). The same rendering code produces the exported reports. |
| **Layout** | `src/TimeTracker.Core` (database, collector state machine, statistics, reports, translations, web assets), `src/TimeTracker` (Windows integration and UI host), `tests/` (xUnit), `tools/` (icon and demo-data scripts). |
| **SQLite** | Uses the SQLite that ships with Windows (`winsqlite3.dll`), so Windows Update keeps it patched and no native database library is bundled. |
| **Translations** | One embedded `i18n.json` (English, German) shared by the native tray/dialogs and the web UI, instead of `.resx` files. A test checks that both languages have the same keys and placeholders and that every key used by the web code exists. |
| **Foreground detection** | Polled once per second (`GetForegroundWindow`) instead of a WinEvent hook. Intervals are therefore accurate to about a second. |
| **Open windows** | `EnumWindows` every 5 s: visible, un-owned, non-tool windows with a title that are not cloaked (Store apps are resolved through `ApplicationFrameHost`). |
| **Idle** | `GetLastInputInfo`, plus XInput gamepad packets, plus the fullscreen exemption. Time before the idle threshold is still counted as active (not retroactive). |
| **Untracked time** | The collector records *why* a gap exists: sleep, shutdown and logoff are stored as "locked / off"; a crash, kill, Exit or pause is stored as "untracked". The Windows event log is not needed. |
| **Install** | `TimeTracker.exe` installs itself when double-clicked (dialog: Install / Run once / Cancel), to `%LOCALAPPDATA%\Programs\TimeTracker`, with autostart and an *Apps & features* entry. `--uninstall` (or Settings → Uninstall) removes everything and asks whether to keep the data. |
| **Command line** | `--background` (autostart), `--install`, `--uninstall`, `--portable` (run without installing), `--settings` / `--flyout` (ask the running instance to open that window), `--dev`. Environment: `TIMETRACKER_DATA` (data folder), `TIMETRACKER_NO_OPEN=1` (tests: never open files or folders), `TIMETRACKER_DEVTOOLS=1`. |
| **Not built** | Multi-device viewing and the watchdog service (dropped by decision, §11). The per-app session drill-down shows window titles only when "Record window titles" is on. |
| **Flyout speed** | Loading WebView2 and the page cold took about 2 s. The flyout window is therefore created hidden a few seconds after start and only shown or hidden afterwards (about 15 ms). Hidden, WebView2 is told to use a low memory level, which brings its resident memory down from about 280 MB to roughly 40 to 75 MB after about a minute. |
| **Versions and updates** | One version number in `Directory.Build.props`. Running a newer exe offers *Update* (and warns on older versions). Data lives outside the program folder and is never touched by the updater. The database has numbered migrations, a consistent backup is made before a layout change, a failed migration rolls back, and a database written by a newer version is refused untouched. |
