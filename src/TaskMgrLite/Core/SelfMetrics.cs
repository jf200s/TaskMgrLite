using System.Runtime.InteropServices;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

public readonly record struct SelfMetrics(
    double PrivateWorkingSetMb,
    double WorkingSetMb,
    double PrivateCommitMb,
    int Gen0,
    int Gen1,
    int Gen2,
    uint HandleCount,
    double ManagedHeapMb,
    long TotalAllocatedBytes);

public static class SelfMetricsReader
{
    private const double BytesToMb = 1024.0 * 1024.0;

    /// <param name="selfSample">
    /// 本次快照中自己的那条记录。专用工作集从快照取，保证和「内存」列同源同口径；
    /// 取不到时为 0，由调用方标注。
    /// </param>
    public static SelfMetrics Capture(ProcessSample? selfSample)
    {
        var counters = new NativeMethods.PROCESS_MEMORY_COUNTERS_EX
        {
            cb = (uint)Marshal.SizeOf<NativeMethods.PROCESS_MEMORY_COUNTERS_EX>()
        };

        bool ok = NativeMethods.GetProcessMemoryInfo(
            NativeMethods.GetCurrentProcess(),
            ref counters,
            (int)counters.cb);

        double privateWsMb = selfSample is { } s ? s.WorkingSetPrivate / BytesToMb : 0;
        double wsMb = ok ? (double)counters.WorkingSetSize / BytesToMb : 0;
        double commitMb = ok ? (double)counters.PrivateUsage / BytesToMb : 0;

        uint handles = 0;
        NativeMethods.GetProcessHandleCount(NativeMethods.GetCurrentProcess(), out handles);

        return new SelfMetrics(
            PrivateWorkingSetMb: privateWsMb,
            WorkingSetMb: wsMb,
            PrivateCommitMb: commitMb,
            Gen0: GC.CollectionCount(0),
            Gen1: GC.CollectionCount(1),
            Gen2: GC.CollectionCount(2),
            HandleCount: handles,
            ManagedHeapMb: GC.GetTotalMemory(forceFullCollection: false) / BytesToMb,
            TotalAllocatedBytes: GC.GetTotalAllocatedBytes(precise: false));
    }
}
