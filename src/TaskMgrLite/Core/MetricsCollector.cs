using System.Diagnostics;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>
/// 一次性能采样结果。只包含值类型；GPU 名称是初始化时创建一次的字符串引用，
/// 后续采样只复制引用，不产生新字符串。
///
/// 失效语义：
///   - CpuValid：首次采样 / 计数器倒退 / 调用失败时为 false；
///   - MemoryValid：GlobalMemoryStatusEx 本轮成功；
///   - CommitValid：GetPerformanceInfo 本轮成功；
///   - MemorySpeedValid：SMBIOS 初始化解析成功；
///   - DiskValid / NetworkValid：首次采样或速率计算失败时为 false；
///   - GpuMemoryValid：DXGI 本轮查询成功；
///   - GpuUtilizationValid：v1 永远为 false，占用率显示 N/A。
///
/// 任一 Valid 为 false 时，对应数值保留上一份有效值；UI 必须结合标志显示“—”，
/// 不能把保留值当成新数据。
/// </summary>
public readonly record struct MetricsSnapshot(
    long Sequence,
    long Timestamp,
    double CpuUsagePercent,
    bool CpuValid,
    ulong TotalPhysicalBytes,
    ulong AvailablePhysicalBytes,
    ulong UsedPhysicalBytes,
    bool MemoryValid,
    ulong CommittedBytes,
    ulong CommitLimitBytes,
    ulong SystemCacheBytes,
    bool CommitValid,
    uint MemoryLoadPercent,
    uint MemorySpeedMtS,
    int MemoryModuleCount,
    bool MemorySpeedValid,
    double DiskReadBytesPerSec,
    double DiskWriteBytesPerSec,
    bool DiskValid,
    double NetworkRxBytesPerSec,
    double NetworkTxBytesPerSec,
    bool NetworkValid,
    string? GpuName,
    ulong GpuDedicatedBytes,
    ulong GpuBudgetBytes,
    // DXGI QueryVideoMemoryInfo 的 usage/budget 是“当前进程”口径，不是全系统。
    ulong GpuUsageBytes,
    bool GpuMemoryValid,
    double GpuUtilizationPercent,
    bool GpuUtilizationValid);

/// <summary>
/// CPU 总体 + 物理内存 + 系统磁盘吞吐 + 系统网络吞吐 + GPU 显存 的 1Hz 采集器。
///
/// 设计约束：
///   1. 不依赖 PDH / WMI / PerformanceCounter；
///   2. 稳态采样路径零托管分配：原生缓冲跨 tick 复用，返回值是结构体；
///   3. 只允许单个采集线程调用 <see cref="Sample"/>；
///   4. 不抛异常：单项失败时保留上一份有效值并清空对应 Valid；
///   5. 初始化（SMBIOS / DXGI）挂在第一次 Sample 上，不阻塞 UI 启动。
///
/// CPU 口径是内核真值：kernel 时间包含 idle，所以 busy = kernel + user - idle。
/// GPU 占用率不在 DXGI 能力范围内，v1 固定 N/A。
/// </summary>
public sealed class MetricsCollector : IDisposable
{
    private readonly NetworkThroughputReader _network = new();
    private readonly SystemDiskReader _disk = new();
    private readonly GpuMemoryReader _gpu = new();

    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasCpuBaseline;

    private MemorySpeedInfo _memorySpeed;
    private GpuMemorySample _lastGpu = GpuMemorySample.Empty;
    private MetricsSnapshot _last;
    private long _sequence;
    private bool _initialized;
    private bool _disposed;

    /// <summary>
    /// 采样一次。首次调用建立 CPU / 磁盘 / 网络基线，对应 Valid 为 false；
    /// 第二次开始返回速率。SMBIOS 与 DXGI 只在第一次调用时初始化。
    /// </summary>
    public MetricsSnapshot Sample()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(MetricsCollector));

        InitializeOnce();

        long timestamp = Stopwatch.GetTimestamp();
        MetricsSnapshot last = _last;

        double cpu = last.CpuUsagePercent;
        bool cpuValid = ReadCpu(out double currentCpu);
        if (cpuValid)
            cpu = currentCpu;

        ulong totalPhys = last.TotalPhysicalBytes;
        ulong availPhys = last.AvailablePhysicalBytes;
        ulong usedPhys = last.UsedPhysicalBytes;
        uint memoryLoad = last.MemoryLoadPercent;
        bool memoryValid = ReadPhysicalMemory(
            out ulong currentTotal,
            out ulong currentAvail,
            out uint currentLoad);

        if (memoryValid)
        {
            totalPhys = currentTotal;
            availPhys = currentAvail;
            usedPhys = currentTotal >= currentAvail ? currentTotal - currentAvail : 0;
            memoryLoad = currentLoad;
        }

        ulong committed = last.CommittedBytes;
        ulong commitLimit = last.CommitLimitBytes;
        ulong systemCache = last.SystemCacheBytes;
        bool commitValid = ReadCommit(out ulong currentCommitted, out ulong currentLimit, out ulong currentCache);

        if (commitValid)
        {
            committed = currentCommitted;
            commitLimit = currentLimit;
            systemCache = currentCache;
        }

        double diskRead = last.DiskReadBytesPerSec;
        double diskWrite = last.DiskWriteBytesPerSec;
        bool diskValid = _disk.TrySample(timestamp, out double currentRead, out double currentWrite);
        if (diskValid)
        {
            diskRead = currentRead;
            diskWrite = currentWrite;
        }

        double netRx = last.NetworkRxBytesPerSec;
        double netTx = last.NetworkTxBytesPerSec;
        bool netValid = _network.TrySample(timestamp, out double currentRx, out double currentTx);
        if (netValid)
        {
            netRx = currentRx;
            netTx = currentTx;
        }

        GpuMemorySample gpu = _lastGpu;
        bool gpuValid = _gpu.TrySample(out GpuMemorySample currentGpu);
        if (gpuValid)
        {
            gpu = currentGpu;
            _lastGpu = currentGpu;
        }

        var snapshot = new MetricsSnapshot(
            Sequence: ++_sequence,
            Timestamp: timestamp,
            CpuUsagePercent: cpu,
            CpuValid: cpuValid,
            TotalPhysicalBytes: totalPhys,
            AvailablePhysicalBytes: availPhys,
            UsedPhysicalBytes: usedPhys,
            MemoryValid: memoryValid,
            CommittedBytes: committed,
            CommitLimitBytes: commitLimit,
            SystemCacheBytes: systemCache,
            CommitValid: commitValid,
            MemoryLoadPercent: memoryLoad,
            MemorySpeedMtS: _memorySpeed.MtS,
            MemoryModuleCount: _memorySpeed.ModuleCount,
            MemorySpeedValid: _memorySpeed.Valid,
            DiskReadBytesPerSec: diskRead,
            DiskWriteBytesPerSec: diskWrite,
            DiskValid: diskValid,
            NetworkRxBytesPerSec: netRx,
            NetworkTxBytesPerSec: netTx,
            NetworkValid: netValid,
            GpuName: gpu.Name,
            GpuDedicatedBytes: gpu.DedicatedBytes,
            GpuBudgetBytes: gpu.BudgetBytes,
            GpuUsageBytes: gpu.UsageBytes,
            GpuMemoryValid: gpuValid,
            GpuUtilizationPercent: double.NaN,
            GpuUtilizationValid: false);

        _last = snapshot;
        return snapshot;
    }

    private void InitializeOnce()
    {
        if (_initialized)
            return;

        _initialized = true;

        try
        {
            _memorySpeed = MemorySpeedReader.Read();
        }
        catch (Exception)
        {
            // SMBIOS 数据异常不应该拖垮整条采集链；速度显示“—”，其它指标继续。
            _memorySpeed = default;
        }

        _gpu.Initialize();
    }

    private bool ReadCpu(out double percent)
    {
        percent = 0;

        if (!MetricsNative.GetSystemTimes(
                out MetricsNative.FileTime idleTime,
                out MetricsNative.FileTime kernelTime,
                out MetricsNative.FileTime userTime))
        {
            return false;
        }

        ulong idle = idleTime.ToUInt64();
        ulong kernel = kernelTime.ToUInt64();
        ulong user = userTime.ToUInt64();

        bool valid = false;

        if (_hasCpuBaseline
            && idle >= _prevIdle
            && kernel >= _prevKernel
            && user >= _prevUser)
        {
            ulong idleDelta = idle - _prevIdle;
            ulong kernelDelta = kernel - _prevKernel;
            ulong userDelta = user - _prevUser;
            ulong total = kernelDelta + userDelta;

            if (total > 0)
            {
                // kernel 已包含 idle，不能再把 idle 当成额外一份时间。
                ulong busy = total > idleDelta ? total - idleDelta : 0;
                percent = busy * 100.0 / total;
                if (percent > 100.0)
                    percent = 100.0;
                valid = true;
            }
        }

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        _hasCpuBaseline = true;
        return valid;
    }

    private static bool ReadPhysicalMemory(out ulong total, out ulong available, out uint load)
    {
        total = 0;
        available = 0;
        load = 0;

        var mem = default(MetricsNative.MemoryStatusEx);
        mem.Length = SizeOf<MetricsNative.MemoryStatusEx>();

        if (!MetricsNative.GlobalMemoryStatusEx(ref mem))
            return false;

        total = mem.TotalPhys;
        available = mem.AvailPhys;
        load = mem.MemoryLoad;
        return true;
    }

    private static bool ReadCommit(out ulong committed, out ulong limit, out ulong systemCache)
    {
        committed = 0;
        limit = 0;
        systemCache = 0;

        if (!MetricsNative.GetPerformanceInfo(
                out MetricsNative.PerformanceInformation perf,
                SizeOf<MetricsNative.PerformanceInformation>())
            || (ulong)perf.PageSize == 0)
        {
            return false;
        }

        ulong pageSize = (ulong)perf.PageSize;
        committed = (ulong)perf.CommitTotal * pageSize;
        limit = (ulong)perf.CommitLimit * pageSize;
        systemCache = (ulong)perf.SystemCache * pageSize;
        return true;
    }

    private static unsafe uint SizeOf<T>() where T : unmanaged => (uint)sizeof(T);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _network.Dispose();
        _disk.Dispose();
        _gpu.Dispose();
    }
}
