using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Power;
using Windows.Win32.System.SystemInformation;

namespace NNA.Wallpaper.Host.Services;

/// <summary>
/// Low-level Win32/NT readings behind /stats: per-core CPU busy time, overall CPU busy time,
/// physical memory, and processor clock speed. Kept separate from <see cref="StatsService"/> so
/// the sampling orchestration (locking, timers, JSON shape) stays readable on its own.
/// </summary>
internal static class NativeStats
{
    // NtQuerySystemInformation is an undocumented native API with no CsWin32/win32metadata
    // coverage, so it is the one manual DllImport in this file (per project convention, manual
    // P/Invoke is only allowed for ntdll). Layout and class value come from the public
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION documentation used by tools such as psutil.
    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int systemInformationClass, IntPtr systemInformation, int systemInformationLength, out int returnLength);

    private const int SystemProcessorPerformanceInformation = 8;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public uint InterruptCount;
    }

    /// <summary>Cumulative idle/kernel/user time (100ns units) for one logical processor.</summary>
    public readonly record struct CoreTimes(long Idle, long Kernel, long User);

    /// <summary>Reads per-core cumulative times via NtQuerySystemInformation. Null on failure.</summary>
    public static CoreTimes[]? ReadCoreTimes(int coreCount)
    {
        if (coreCount <= 0) return Array.Empty<CoreTimes>();
        int size = Marshal.SizeOf<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>();
        IntPtr buf = Marshal.AllocHGlobal(size * coreCount);
        try
        {
            int status = NtQuerySystemInformation(SystemProcessorPerformanceInformation, buf, size * coreCount, out _);
            if (status != 0) return null;
            var result = new CoreTimes[coreCount];
            for (int i = 0; i < coreCount; i++)
            {
                var info = Marshal.PtrToStructure<SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION>(buf + i * size);
                result[i] = new CoreTimes(info.IdleTime, info.KernelTime, info.UserTime);
            }
            return result;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>System-wide idle/kernel/user time (100ns units) via GetSystemTimes (CsWin32).</summary>
    public static bool TryGetSystemTimes(out long idle, out long kernel, out long user)
    {
        idle = kernel = user = 0;
        if (!PInvoke.GetSystemTimes(out var i, out var k, out var u)) return false;
        idle = FileTimeToLong(i);
        kernel = FileTimeToLong(k);
        user = FileTimeToLong(u);
        return true;
    }

    private static long FileTimeToLong(System.Runtime.InteropServices.ComTypes.FILETIME ft) =>
        ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    /// <summary>Physical memory usage via GlobalMemoryStatusEx (CsWin32).</summary>
    public static bool TryGetMemory(out ulong usedBytes, out ulong totalBytes, out double percent)
    {
        usedBytes = totalBytes = 0;
        percent = 0;
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!PInvoke.GlobalMemoryStatusEx(ref mem)) return false;
        totalBytes = mem.ullTotalPhys;
        usedBytes = mem.ullTotalPhys - mem.ullAvailPhys;
        percent = mem.dwMemoryLoad;
        return true;
    }

    /// <summary>Per-processor clock info via CallNtPowerInformation(ProcessorInformation). Null on failure.</summary>
    public static unsafe PROCESSOR_POWER_INFORMATION[]? QueryProcessorPowerInformation(int coreCount)
    {
        if (coreCount <= 0) return null;
        var arr = new PROCESSOR_POWER_INFORMATION[coreCount];
        fixed (PROCESSOR_POWER_INFORMATION* p = arr)
        {
            var status = PInvoke.CallNtPowerInformation(
                POWER_INFORMATION_LEVEL.ProcessorInformation, null, 0, p, (uint)(sizeof(PROCESSOR_POWER_INFORMATION) * coreCount));
            if (status != 0) return null;
        }
        return arr;
    }
}
