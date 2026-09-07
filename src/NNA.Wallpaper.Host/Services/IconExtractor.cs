using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Extracts a 64x64 PNG icon (with alpha) for a launch item: a file, a folder, or a packaged app
/// referenced by AUMID. Primary path is the shell's IShellItemImageFactory, parsed either from a
/// plain path (works for files and folders alike) or "shell:AppsFolder\&lt;aumid&gt;" for packaged
/// apps. SHDefExtractIcon is the fallback for plain files when the shell item path fails.
/// </summary>
internal static class IconExtractor
{
    private const int Size = 64;

    /// <summary>
    /// Tries to produce a 64x64 PNG at <paramref name="outFile"/> for the given launch item.
    /// <paramref name="path"/> is the item's open/cmd/icon_from target (file, folder, or exe);
    /// <paramref name="aumid"/>, when set, takes precedence and is resolved through
    /// "shell:AppsFolder\&lt;aumid&gt;". Runs the shell/COM calls on a dedicated STA thread —
    /// several shell extensions (cloud-storage overlay providers, icon handlers) are apartment
    /// affine and can misbehave when called from a thread-pool (MTA) thread.
    /// </summary>
    public static bool TryExtract(string? path, string? aumid, string outFile)
    {
        string? parseName = !string.IsNullOrEmpty(aumid) ? "shell:AppsFolder\\" + aumid : path;

        bool ok = false;
        if (!string.IsNullOrEmpty(parseName))
        {
            ok = RunOnSta(() => TryViaShellItem(parseName!, outFile));
        }
        if (!ok && string.IsNullOrEmpty(aumid) && !string.IsNullOrEmpty(path) && File.Exists(path))
        {
            ok = RunOnSta(() => TryViaDefExtractIcon(path!, outFile));
        }
        return ok;
    }

    private static bool RunOnSta(Func<bool> body)
    {
        bool result = false;
        var thread = new Thread(() =>
        {
            try { result = body(); }
            catch { result = false; }
        })
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        return result;
    }

    private static bool TryViaShellItem(string parseName, string outFile)
    {
        try
        {
            var hr = PInvoke.SHCreateItemFromParsingName(parseName, null, out IShellItem item);
            if (hr.Failed || item is null) return false;

            var factory = (IShellItemImageFactory)item;
            factory.GetImage(
                new Windows.Win32.Foundation.SIZE(Size, Size),
                SIIGBF.SIIGBF_ICONONLY | SIIGBF.SIIGBF_BIGGERSIZEOK,
                out DeleteObjectSafeHandle hbm);
            using (hbm)
            {
                if (hbm.IsInvalid) return false;
                return SaveHbitmapAsPng(hbm, outFile);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Copies the HBITMAP's raw 32bpp pixels (with alpha) into a managed Bitmap and saves it as
    /// PNG. Bitmap.FromHbitmap would go through a 24bpp GDI conversion and drop the alpha
    /// channel, so instead: GetObject to read width/height/stride/bits pointer, then LockBits on
    /// a Format32bppArgb Bitmap and copy row by row.
    /// </summary>
    private static unsafe bool SaveHbitmapAsPng(DeleteObjectSafeHandle hbm, string outFile)
    {
        Span<byte> buf = stackalloc byte[Marshal.SizeOf<BITMAP>()];
        int written = PInvoke.GetObject(hbm, buf);
        if (written <= 0) return false;

        var bmp = MemoryMarshal.Read<BITMAP>(buf);
        if (bmp.bmWidth <= 0 || bmp.bmHeight <= 0 || bmp.bmBits == null) return false;

        using var managed = new Bitmap(bmp.bmWidth, bmp.bmHeight, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, bmp.bmWidth, bmp.bmHeight);
        var data = managed.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int rowBytes = bmp.bmWidth * 4;
            byte* src = (byte*)bmp.bmBits;
            byte* dst = (byte*)data.Scan0;
            for (int y = 0; y < bmp.bmHeight; y++)
            {
                Buffer.MemoryCopy(src + (long)y * bmp.bmWidthBytes, dst + (long)y * data.Stride, rowBytes, rowBytes);
            }
        }
        finally
        {
            managed.UnlockBits(data);
        }

        return SaveAtomic(managed, outFile);
    }

    private static bool TryViaDefExtractIcon(string exePath, string outFile)
    {
        try
        {
            uint sizeParam = (uint)Size | ((uint)Size << 16); // LOWORD = large icon size, HIWORD = small
            var hr = PInvoke.SHDefExtractIcon(exePath, 0, 0, out DestroyIconSafeHandle large, out DestroyIconSafeHandle small, sizeParam);
            using (large)
            using (small)
            {
                if (hr.Failed || large.IsInvalid) return false;
                using var icon = Icon.FromHandle(large.DangerousGetHandle());
                using var bmp = icon.ToBitmap();
                return SaveAtomic(bmp, outFile);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Saves <paramref name="bitmap"/> as a PNG atomically. Shared with WindowsService's icon route.</summary>
    internal static bool SaveAtomic(Bitmap bitmap, string outFile)
    {
        var dir = Path.GetDirectoryName(outFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = outFile + ".tmp";
        bitmap.Save(tmp, ImageFormat.Png);
        File.Move(tmp, outFile, overwrite: true);
        return true;
    }
}
