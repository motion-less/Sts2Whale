using System.Runtime.InteropServices;

namespace CombatSolver;

internal readonly record struct PhysicalMemoryUsage(
    long UsedBytes,
    long TotalBytes)
{
    // The GC fallback is a collection-time pressure estimate, not an OS physical sample.
    public bool IsPhysicalSample { get; init; }
    public static PhysicalMemoryUsage Capture(GCMemoryInfo fallback)
    {
        if (OperatingSystem.IsWindows())
            return CaptureWindows(fallback);

        if (OperatingSystem.IsMacOS())
        {
            try
            {
                PhysicalMemoryUsage mac = CaptureMacOS();
                if (mac.IsPhysicalSample) return mac;
            }
            catch
            {
                // 拿不到再用下面的兜底
            }
        }

        // 其他非 Windows 平台：Godot 的 OS.GetMemoryInfo 是跨平台的；
        // available 在个别平台是 -1，退回 free；还不行才用 GC 的兜底值（不标物理采样）。
        try
        {
            Godot.Collections.Dictionary info = Godot.OS.GetMemoryInfo();
            long total = info.TryGetValue("physical", out var physical) ? physical.AsInt64() : 0;
            long available = info.TryGetValue("available", out var avail) ? avail.AsInt64() : -1;
            if (available < 0 && info.TryGetValue("free", out var free)) available = free.AsInt64();
            if (total > 0 && available >= 0)
            {
                available = Math.Min(available, total);
                return new PhysicalMemoryUsage(Math.Max(0, total - available), total)
                {
                    IsPhysicalSample = true,
                };
            }
        }
        catch
        {
            // 拿不到就用下面的 GC 兜底
        }

        return new PhysicalMemoryUsage(
            Math.Max(0, fallback.MemoryLoadBytes),
            Math.Max(0, fallback.TotalAvailableMemoryBytes));
    }

    /// <summary>
    /// macOS 的 Godot <c>OS.GetMemoryInfo().available</c> 只给 free（不含 inactive/speculative），
    /// 看起来会像「系统快没内存了」。这里直接读 <c>host_statistics64</c>：
    /// 可用 ≈ free + inactive + speculative，和活动监视器的「可用」口径接近。
    /// </summary>
    private static PhysicalMemoryUsage CaptureMacOS()
    {
        long total = 0;
        try
        {
            Godot.Collections.Dictionary info = Godot.OS.GetMemoryInfo();
            if (info.TryGetValue("physical", out var physical)) total = physical.AsInt64();
        }
        catch { }
        if (total <= 0) return default;

        uint pageSize;
        if (host_page_size(HostPort, out pageSize) != 0 || pageSize == 0) return default;

        int size = Marshal.SizeOf<VmStatistics64>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            uint count = (uint)(size / sizeof(uint));
            if (host_statistics64(HostPort, HostVmInfo64, buffer, ref count) != 0) return default;
            VmStatistics64 vm = Marshal.PtrToStructure<VmStatistics64>(buffer);
            long pages = (long)vm.FreeCount + vm.InactiveCount + vm.SpeculativeCount;
            long available = Math.Clamp(pages * pageSize, 0, total);
            return new PhysicalMemoryUsage(total - available, total) { IsPhysicalSample = true };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static PhysicalMemoryUsage CaptureWindows(GCMemoryInfo fallback)
    {
        MemoryStatusEx status = new()
        {
            Length = (uint)Marshal.SizeOf<MemoryStatusEx>(),
        };
        if (!GlobalMemoryStatusEx(ref status))
        {
            return new PhysicalMemoryUsage(
                Math.Max(0, fallback.MemoryLoadBytes),
                Math.Max(0, fallback.TotalAvailableMemoryBytes));
        }

        long total = status.TotalPhysicalMemory > (ulong)long.MaxValue
            ? long.MaxValue
            : (long)status.TotalPhysicalMemory;
        long available = status.AvailablePhysicalMemory > (ulong)long.MaxValue
            ? long.MaxValue
            : (long)status.AvailablePhysicalMemory;
        return new PhysicalMemoryUsage(
            Math.Max(0, total - Math.Min(total, available)),
            total) { IsPhysicalSample = true };
    }

    private const int HostVmInfo64 = 4;
    private static readonly uint HostPort = mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern uint mach_host_self();

    [DllImport("libSystem.dylib")]
    private static extern int host_page_size(uint host, out uint pageSize);

    [DllImport("libSystem.dylib")]
    private static extern int host_statistics64(uint host, int flavor, IntPtr info, ref uint count);

    [StructLayout(LayoutKind.Sequential)]
    private struct VmStatistics64
    {
        public uint FreeCount;
        public uint ActiveCount;
        public uint InactiveCount;
        public uint WireCount;
        public ulong ZeroFillCount;
        public ulong Reactivations;
        public ulong PageIns;
        public ulong PageOuts;
        public ulong Faults;
        public ulong CowFaults;
        public ulong Lookups;
        public ulong Hits;
        public ulong Purges;
        public uint PurgeableCount;
        public uint SpeculativeCount;
        public ulong Decompressions;
        public ulong Compressions;
        public ulong SwapIns;
        public ulong SwapOuts;
        public uint CompressorPageCount;
        public uint ThrottledCount;
        public uint ExternalPageCount;
        public uint InternalPageCount;
        public ulong TotalUncompressedPagesInCompressor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoadPercent;
        public ulong TotalPhysicalMemory;
        public ulong AvailablePhysicalMemory;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtualMemory;
        public ulong AvailableVirtualMemory;
        public ulong AvailableExtendedVirtualMemory;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
