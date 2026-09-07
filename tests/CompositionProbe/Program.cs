using System.Drawing.Imaging;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.DirectComposition;

namespace CompositionProbe;

/// <summary>
/// Standalone probe for stage 2A of the NNA Wallpaper composition-hosting change (see
/// src/NNA.Wallpaper/Engine/CompositionHost.cs for the production code and the reasoning). Proves,
/// away from the owner's live desktop (this opens a normal top-level window, not a wallpaper window
/// behind the icon layer), that:
///  1. DCompositionCreateDevice2(null) succeeds and a WebView2 CoreWebView2CompositionController can
///     render into that DirectComposition visual tree via RootVisualTarget;
///  2. hover (":hover" CSS) is stable when input arrives ONLY through
///     ICoreWebView2CompositionController.SendMouseInput — the same mechanism
///     src/NNA.Wallpaper/Engine/InputBridge.cs uses for composition-hosted wallpaper windows — with
///     no real OS mouse movement involved at all.
/// Run with: dotnet run --project tests/CompositionProbe/CompositionProbe.csproj
/// </summary>
internal static class Program
{
    // A 200x200 square positioned at a known spot in a 600x400 window; #sq:hover turns it white.
    private const string HoverPage =
        "<!doctype html><html><head><meta charset='utf-8'><style>" +
        "html,body{margin:0;height:100%;background:#111}" +
        "#sq{position:absolute;left:200px;top:100px;width:200px;height:200px;background:#333}" +
        "#sq:hover{background:#fff}" +
        "</style></head><body><div id='sq'></div></body></html>";

    private const string UserDataDir =
        @"C:\Users\imman\AppData\Local\Temp\claude\h--\177acbd5-5f45-4de3-8283-018df2861388\scratchpad\comp-probe";

    private const string ShotPath = @"H:\night-runs\nna-wallpaper-2\shots\stage2-comp-probe.png";

    // Square bounds in client coordinates, matching the CSS above.
    private const int SqLeft = 200, SqTop = 100, SqRight = 400, SqBottom = 300;

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();

        var form = new Form
        {
            Text = "CompositionProbe",
            StartPosition = FormStartPosition.Manual,
            Location = new Point(80, 80),
            ClientSize = new Size(600, 400),
            FormBorderStyle = FormBorderStyle.FixedSingle,
            MaximizeBox = false,
        };

        var exitCode = 1;
        form.Shown += async (_, _) =>
        {
            try
            {
                exitCode = await RunProbeAsync(form) ? 0 : 1;
            }
            catch (Exception ex)
            {
                Console.WriteLine("PROBE FAILED: " + ex);
                exitCode = 1;
            }
            finally
            {
                Application.Exit();
            }
        };
        Application.Run(form);
        Environment.ExitCode = exitCode;
    }

    private static async Task<bool> RunProbeAsync(Form form)
    {
        Directory.CreateDirectory(UserDataDir);
        var env = await CoreWebView2Environment.CreateAsync(null, UserDataDir);
        Console.WriteLine("webview2 runtime: " + env.BrowserVersionString);

        // --- DirectComposition device/target/visual: variant A from the task, DCompositionCreateDevice2(null) ---
        var hr = PInvoke.DCompositionCreateDevice2(null, out IDCompositionDevice device);
        hr.ThrowOnFailure();
        Console.WriteLine("DCompositionCreateDevice2(null): OK (variant A)");

        var hwnd = (HWND)form.Handle;
        device.CreateTargetForHwnd(hwnd, true, out var target);
        device.CreateVisual(out var visual);
        target.SetRoot(visual);
        device.Commit();
        Console.WriteLine("DComp target + root visual created and committed");

        var comp = await env.CreateCoreWebView2CompositionControllerAsync(form.Handle);
        comp.RootVisualTarget = visual;
        // Per WebView2.idl on RootVisualTarget: "WebView will connect its visual tree to the
        // provided visual before returning from the property setter. The app needs to commit on its
        // device [after] setting the RootVisualTarget property." Without this second Commit, nothing
        // ever appears on screen (confirmed empirically: first run rendered a blank grey window).
        device.Commit();
        comp.Bounds = new Rectangle(Point.Empty, form.ClientSize);
        comp.IsVisible = true;
        Console.WriteLine($"CoreWebView2CompositionController created, RootVisualTarget set, Bounds={comp.Bounds}");

        var navDone = new TaskCompletionSource();
        comp.CoreWebView2.NavigationCompleted += (_, _) => navDone.TrySetResult();
        comp.CoreWebView2.NavigateToString(HoverPage);
        await navDone.Task;
        await Task.Delay(400); // let the first composed frame actually reach the screen

        var before = CaptureSquareMean(form, save: false);
        Console.WriteLine($"brightness BEFORE hover: {before:F2}");

        // Wait 2s (per spec), then drive hover purely through SendMouseInput — no OS cursor movement —
        // 20 Move events at the square's centre, 100ms apart, exactly like InputBridge.OnMouseComposition.
        await Task.Delay(2000);
        var center = new Point(form.ClientSize.Width / 2, form.ClientSize.Height / 2);
        for (var i = 0; i < 20; i++)
        {
            comp.SendMouseInput(CoreWebView2MouseEventKind.Move, CoreWebView2MouseEventVirtualKeys.None, 0, center);
            await Task.Delay(100);
        }

        var after = CaptureSquareMean(form, save: true);
        Console.WriteLine($"brightness AFTER hover (20x SendMouseInput Move, 100ms step): {after:F2}");
        var pass = after > before + 10;
        Console.WriteLine(pass ? "PASS composition hosting renders and SendMouseInput hover holds" : "FAIL hover did not hold");
        return pass;
    }

    /// <summary>Screenshots the form's client area from the real screen (CopyFromScreen — this is
    /// genuine composited pixels, not a WebView2-internal capture) and returns the mean brightness
    /// (0..255) of the square region. Saves the frame to <see cref="ShotPath"/> when requested.</summary>
    private static double CaptureSquareMean(Form form, bool save)
    {
        var topLeft = form.PointToScreen(Point.Empty);
        using var bmp = new Bitmap(form.ClientSize.Width, form.ClientSize.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(topLeft, Point.Empty, form.ClientSize);
        }
        if (save)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ShotPath)!);
            bmp.Save(ShotPath, ImageFormat.Png);
            Console.WriteLine("saved screenshot: " + ShotPath);
        }
        long total = 0;
        var count = 0;
        for (var y = SqTop + 20; y < SqBottom - 20; y += 4)
        {
            for (var x = SqLeft + 20; x < SqRight - 20; x += 4)
            {
                var c = bmp.GetPixel(x, y);
                total += c.R + c.G + c.B;
                count++;
            }
        }
        return count == 0 ? 0 : total / (3.0 * count);
    }
}
