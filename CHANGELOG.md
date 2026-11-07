# Changelog

Versions follow [semantic versioning](https://semver.org): new features raise the MINOR number, fixes the PATCH
number. The version is set in one place, [Directory.Build.props](Directory.Build.props). Upgrading never touches
recorded data: see "Updating" in the [README](README.md).

## 1.1.2

- Fix: in the tray flyout, durations in the "Most used today" list (e.g. "2 Std. 02 Min.") no longer wrap onto two
  lines when the flyout is narrow. The time column now fits its widest value and the bars take the remaining space.

## 1.1.1

- Fix: the chart legend said "Other (1 apps)"; it now says "Other (1 app)" (English and German).
- Safer updates and tests: the installer only stops the installed copy it is about to replace. Instances started with
  a separate `TIMETRACKER_DATA` folder (development, demo screenshots) have their own lock and can run next to an
  installed copy.
- Developer switches for demo screenshots: `TIMETRACKER_DEVICE_NAME`, `TIMETRACKER_NO_COLLECT=1`.
- Screenshots in the README.

## 1.1.0

- **Faster flyout:** the tray flyout is loaded in the background after start and only shown on click, instead of
  being built from scratch each time (previously about 2 s, now about 15 ms). While hidden, its WebView2 trims itself
  to roughly 40 to 75 MB resident memory (about 280 MB untrimmed).
- **About popover:** an (i) button in the dashboard shows the app icon, version, author and credits.
- **Versioning and safe updates:** one version number for the whole app. Running a newer `TimeTracker.exe` offers
  *Update* (and warns about older versions). The database now has numbered migrations, makes an automatic backup
  before changing its layout, and refuses (without touching it) data written by a newer version.
- License (all rights reserved), third-party notices, README for a public repository.

## 1.0.0

- First version: background tracking of active time, open apps and apps in front; tray flyout; dashboard
  (day / week / month / year); custom app names; HTML and CSV reports; settings (autostart, idle threshold, PIN,
  language); English and German; self-installing single-file exe.
