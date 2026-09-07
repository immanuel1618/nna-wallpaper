using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NNA.Wallpaper;
using NNA.Wallpaper.Host;
using NNA.Wallpaper.Host.Config;

namespace NNA.Wallpaper.WindowPreview;

/// <summary>
/// Stage 3A dev helper (not part of NNA.Wallpaper.sln): opens the real <see cref="SettingsWindow"/>
/// against an already-running instance (default port 1618, the owner's) purely to see and screenshot
/// the custom "BrandWindow" title bar chrome without a full app run. Read-only: /settings/ is a page
/// load, nothing is submitted or clicked besides the title bar buttons the smoke-test itself drives.
///
/// Usage: WindowPreview.exe [port] [outPngPath]
/// </summary>
internal static class Program
{
    private static class Native
    {
        [DllImport("user32.dll")]
        public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        var port = args.Length > 0 && int.TryParse(args[0], out var p) ? p : 1618;
        var outPath = args.Length > 1 ? args[1] : Path.Combine("H:\\night-runs\\nna-wallpaper-2\\shots", "stage3-window.png");

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        // Chrome.xaml is compiled into NNA.Wallpaper.dll; merge it into this process's own
        // Application.Resources so SettingsWindow's StaticResource="{StaticResource BrandWindow}"
        // (declared in SettingsWindow.xaml, which ships in that same assembly) resolves here too.
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/NNA.Wallpaper;component/Themes/Chrome.xaml"),
        });

        var paths = Paths.Resolve(null); // default data dir == the owner's, read-only use here
        var log = new Log(paths);
        var config = new ConfigStore(paths, log);
        config.Load();
        var ctx = new HostContext(paths, config, log, port, new NullHostApp());

        var window = new SettingsWindow(ctx, null);
        window.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Screenshot(window, outPath);
                Console.WriteLine("saved: " + outPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("screenshot failed: " + ex.Message);
            }
            window.Close();
            app.Shutdown();
        };
        timer.Start();

        app.Run();
    }

    private static void Screenshot(Window window, string outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var hwnd = new WindowInteropHelper(window).Handle;
        var w = (int)window.ActualWidth;
        var h = (int)window.ActualHeight;
        using var bmp = new Bitmap(Math.Max(w, 1), Math.Max(h, 1));
        using var g = Graphics.FromImage(bmp);
        var hdc = g.GetHdc();
        try
        {
            Native.PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
        }
        finally
        {
            g.ReleaseHdc(hdc);
        }
        bmp.Save(outPath, ImageFormat.Png);
    }
}
