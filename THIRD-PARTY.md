# Third-party packages

NNA Wallpaper is MIT-licensed. It depends only on packages under permissive licenses
(MIT / BSD / MS-PL / Apache-2.0). No GPL code is used or copied; desktop embedding is
implemented from Microsoft documentation and our own code.

| Package | Version | License | Used for |
|---|---|---|---|
| Microsoft.Web.WebView2 | 1.0.4191.47 | BSD-3-Clause | Rendering wallpaper pages and the settings window |
| Velopack | 1.2.0 | MIT | Installer, portable build, auto-update from GitHub Releases |
| vpk (dotnet tool) | 1.2.0 | MIT | Packaging releases |
| NAudio | 2.2.1 | MIT | WASAPI loopback capture for the equalizer spectrum |
| System.Drawing.Common | 8.0.10 | MIT | Rendering extracted PNG icons for launcher items |
| Hardcodet.NotifyIcon.Wpf | 2.0.1 | MIT | Tray icon |
| Microsoft.Windows.CsWin32 | 0.3.333 | MIT | Generated Win32 P/Invoke |
| System.Diagnostics.PerformanceCounter | 10.0.11 | MIT | CPU frequency counter |
| xunit / xunit.runner.visualstudio / Microsoft.NET.Test.Sdk | 2.9.3 / 3.1.5 / 17.14.1 | Apache-2.0 / MIT | Tests |

Runtime (not bundled): Microsoft Edge WebView2 Runtime (Evergreen), .NET 8 (self-contained in releases).
