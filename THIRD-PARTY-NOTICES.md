# Third-party notices

TimeTracker itself is proprietary (see [LICENSE](LICENSE)). The compiled `TimeTracker.exe` contains or uses the
following third-party components, which keep their own licenses. The full license texts ship with the respective
packages (NuGet) and are available at the links below.

| Component | Used for | License | Source |
|---|---|---|---|
| .NET runtime and base class libraries (self-contained in the exe) | Runtime | MIT | https://github.com/dotnet/runtime |
| .NET Windows Forms | Tray icon, native dialogs, window frames | MIT | https://github.com/dotnet/winforms |
| Microsoft.Data.Sqlite.Core | Database access | MIT | https://github.com/dotnet/efcore |
| SQLitePCLRaw (core, provider, `bundle_winsqlite3`) | Binds to the SQLite that ships with Windows | Apache-2.0 | https://github.com/ericsink/SQLitePCL.raw |
| Microsoft.Web.WebView2 SDK (managed wrapper and `WebView2Loader.dll`) | Hosts the HTML screens | Microsoft WebView2 SDK license (BSD-style) | https://www.nuget.org/packages/Microsoft.Web.WebView2 |

Not shipped, but used at run time through Windows:

- **SQLite** (`winsqlite3.dll`, part of Windows). SQLite is in the public domain.
- **Microsoft Edge WebView2 Runtime**, part of Windows 11 (an optional install on Windows 10).

Development and test only (not part of the shipped exe): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT).

Please check the exact license text of each package version before redistributing a binary; this list is a summary,
not legal advice.
