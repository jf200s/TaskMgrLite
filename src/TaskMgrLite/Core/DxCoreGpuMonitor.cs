using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>
/// 全系统 GPU 引擎占用率（DXCore 官方接口）。
///
/// 口径：
///   AdapterEngineRunningTimeMicroseconds 是内核累计的引擎忙时（微秒，全系统、所有进程）。
///   两次采样的差值 / 墙钟微秒 = 单引擎利用率；整卡取所有引擎的最大值，绝不求和，
///   与任务管理器一致。因此 NVENC 忙时整卡占用率会高于 3D 引擎，这是预期行为。
///
/// 选卡：
///   遍历所有 IsHardware 适配器，取 DedicatedAdapterMemory 最大的一张（独显）。
///   主卡休眠时所有引擎的累计值不增长，结果为 0%，不是失败。
///
/// 约束：
///   1. 稳态采样 0 B 托管分配：查询缓冲与基线数组在 Initialize 时一次性分配；
///   2. 不用 PDH / WMI / D3DKMT，只走 dxcore.dll；
///   3. 初始化失败（低版本系统、无 dxcore.dll、无硬件适配器）只降级为不可用，不抛异常。
///
/// 线程模型：Initialize / TrySample / Dispose 都必须在同一个采集线程上调用（Dispose 例外，
/// 跨公寓释放 RCW 是安全的，只是会顺带做一次封送）。
/// </summary>
internal sealed class DxCoreGpuMonitor : IDisposable
{
    private const int MaxPhysicalAdapters = 8;
    private const int MaxEnginesPerAdapter = 64;
    private const int MaxEngines = 256;

    private static readonly double TicksToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;

    private DxCoreNative.IDXCoreAdapterFactory? _factory;
    private DxCoreNative.IDXCoreAdapter1? _adapter;
    private string? _adapterName;

    private IntPtr _input;
    private IntPtr _output;

    private ulong[] _prevRunning = Array.Empty<ulong>();
    private bool[] _hasPrev = Array.Empty<bool>();
    private long[] _lastQueryTimestamp = Array.Empty<long>();
    private int[] _engineCounts = Array.Empty<int>();
    private int[] _offsets = Array.Empty<int>();

    private int _physicalCount;
    private int _slotCount;
    private long _lastTimestamp;
    private bool _hasBaseline;
    private bool _initialized;
    private bool _disposed;

    /// <summary>主卡的驱动描述（UTF-8），初始化失败为 null。只在初始化时分配一次。</summary>
    public string? AdapterName => _adapterName;

    public bool HasAdapter => _adapter is not null;

    /// <summary>主卡引擎总数（所有物理适配器之和），用于诊断。</summary>
    public int EngineCount => _slotCount;

    /// <summary>主卡 LUID，诊断用。</summary>
    public uint LuidLow { get; private set; }

    public int LuidHigh { get; private set; }

    /// <summary>专用显存总量（Initialize 时读 DedicatedAdapterMemory）。</summary>
    public ulong DedicatedTotalBytes { get; private set; }

    /// <summary>
    /// 全系统专用显存已用（最近一次成功采样）。
    /// 取 Resident 而不是 Committed：实测 Resident 与 PDH
    /// \GPU Adapter Memory(*)\Dedicated Usage 同一时刻逐字节相等，Committed 只有一半。
    /// </summary>
    public ulong DedicatedUsedBytes { get; private set; }

    /// <summary>最近一次显存查询是否成功；false 时 UI 应显示“—”，不要把旧值当新数据。</summary>
    public bool MemoryValid { get; private set; }

    public unsafe void Initialize()
    {
        if (_initialized || _disposed)
            return;

        _initialized = true;

        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            {
                Log.WriteLine("[dxcore] os gate");
                return;
            }

            if (!TryCreateFactory(out DxCoreNative.IDXCoreAdapterFactory? factory) || factory is null)
            {
                Log.WriteLine("[dxcore] factory failed");
                return;
            }

            _factory = factory;

            if (!TryFindBestAdapter(factory, out DxCoreNative.Luid luid))
            {
                Log.WriteLine("[dxcore] no hardware adapter");
                return;
            }

            Guid iid = DxCoreNative.IidAdapter1;
            int adapterHr = factory.GetAdapterByLuid(in luid, in iid, out IntPtr pAdapter);
            if (adapterHr < 0 || pAdapter == IntPtr.Zero)
            {
                Log.WriteLine($"[dxcore] GetAdapterByLuid hr=0x{adapterHr:X8}");
                return;
            }

            DxCoreNative.IDXCoreAdapter1 adapter;
            try
            {
                adapter = (DxCoreNative.IDXCoreAdapter1)Marshal.GetObjectForIUnknown(pAdapter);
            }
            finally
            {
                Marshal.Release(pAdapter);
            }

            if (!adapter.IsValid())
            {
                Log.WriteLine("[dxcore] adapter invalid");
                Marshal.ReleaseComObject(adapter);
                return;
            }

            if (!BuildEngineLayout(adapter))
            {
                Log.WriteLine($"[dxcore] layout failed physical={DxCoreNative.ReadUInt32(adapter, DxCoreNative.PropertyPhysicalAdapterCount)}");
                Marshal.ReleaseComObject(adapter);
                return;
            }

            _adapter = adapter;
            _adapterName = DxCoreNative.ReadUtf8String(adapter, DxCoreNative.PropertyDriverDescription);
            DedicatedTotalBytes = DxCoreNative.ReadUInt64(adapter, DxCoreNative.PropertyDedicatedAdapterMemory);
            LuidLow = luid.LowPart;
            LuidHigh = luid.HighPart;

            _input = Marshal.AllocHGlobal(sizeof(DxCoreNative.EngineQueryInput));
            _output = Marshal.AllocHGlobal(sizeof(DxCoreNative.EngineQueryOutput));

            // 显存查询复用同一对缓冲：入参 8B <= 12B，出参 16B == 16B。
            Debug.Assert(sizeof(DxCoreNative.MemoryQueryInput) <= sizeof(DxCoreNative.EngineQueryInput));
            Debug.Assert(sizeof(DxCoreNative.MemoryUsage) <= sizeof(DxCoreNative.EngineQueryOutput));
            _lastTimestamp = Stopwatch.GetTimestamp();
            _hasBaseline = false;

            Log.WriteLine($"[dxcore] ready name={_adapterName} engines={_slotCount} luid=0x{luid.HighPart:X8}{luid.LowPart:X8}");
        }
        catch (DllNotFoundException)
        {
            // 服务器核心 / 极简镜像没有 dxcore.dll。
            Log.WriteLine("[dxcore] dxcore.dll not found");
        }
        catch (EntryPointNotFoundException)
        {
            Log.WriteLine("[dxcore] entry point missing");
        }
        catch (COMException ex)
        {
            Log.WriteLine($"[dxcore] COM 0x{ex.HResult:X8} {ex.Message}");
        }
        catch (InvalidCastException)
        {
            Log.WriteLine("[dxcore] invalid cast (interface not exposed)");
        }
        catch (Exception ex)
        {
            Log.WriteLine($"[dxcore] {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>便捷重载：内部取一次 QPC。</summary>
    public bool TrySample(out double utilization)
        => TrySample(Stopwatch.GetTimestamp(), out utilization);

    /// <summary>
    /// 采样一次。返回 false 表示本帧没有可用数据（未初始化 / 首次建立基线 / 所有引擎失败），
    /// UI 应显示 N/A，而不是 0%。
    /// </summary>
    public unsafe bool TrySample(long timestamp, out double utilization)
    {
        utilization = 0;

        if (_disposed || _adapter is null || _input == IntPtr.Zero)
            return false;

        double wallMicroseconds = (timestamp - _lastTimestamp) * TicksToMicroseconds;
        if (wallMicroseconds <= 0)
            return false;

        _lastTimestamp = timestamp;

        // 显存和引擎占用率同一帧刷新；即使后面引擎全部失败，显存数据仍然有效。
        SampleMemory();

        double max = 0;
        int ok = 0;

        for (int p = 0; p < _physicalCount; p++)
        {
            int start = _offsets[p];
            int engines = _engineCounts[p];

            for (int e = 0; e < engines; e++)
            {
                int slot = start + e;

                // 入参直接写进复用的非托管缓冲：blittable 赋值，0 分配。
                *(DxCoreNative.EngineQueryInput*)_input = new DxCoreNative.EngineQueryInput
                {
                    PhysicalAdapterIndex = (uint)p,
                    EngineIndex = (uint)e,
                    ProcessId = 0,
                };

                int hr = _adapter.QueryState(
                    DxCoreNative.StateAdapterEngineRunningTimeMicroseconds,
                    (nuint)sizeof(DxCoreNative.EngineQueryInput), _input,
                    (nuint)sizeof(DxCoreNative.EngineQueryOutput), _output);

                if (hr < 0)
                {
                    // 单个引擎失败只跳过它；整卡取其余引擎的最大值。
                    continue;
                }

                ulong running = ((DxCoreNative.EngineQueryOutput*)_output)->RunningTime;
                ok++;

                long lastEngineTimestamp = _lastQueryTimestamp[slot];

                if (!_hasPrev[slot])
                {
                    _hasPrev[slot] = true;
                    _prevRunning[slot] = running;
                    _lastQueryTimestamp[slot] = timestamp;
                    continue;
                }

                ulong prev = _prevRunning[slot];
                _prevRunning[slot] = running;
                _lastQueryTimestamp[slot] = timestamp;

                // 计数器倒退：驱动重置 / 引擎休眠唤醒，本帧按 0 处理，不产生负值。
                if (running < prev)
                    continue;

                // 分母用该引擎自己的上次成功时刻：某个引擎中间失败一帧时，
                // 恢复后仍按真实经过时间算，不会把 2 秒的增量摊到 1 秒上。
                double engineWallMicroseconds = (timestamp - lastEngineTimestamp) * TicksToMicroseconds;
                if (engineWallMicroseconds <= 0)
                    continue;

                double percent = (running - prev) * 100.0 / engineWallMicroseconds;
                if (percent > 100.0)
                    percent = 100.0;
                if (percent > max)
                    max = percent;
            }
        }

        if (ok == 0)
            return false;

        if (!_hasBaseline)
        {
            // 第一帧只建立基线，没有差值可言。
            _hasBaseline = true;
            return false;
        }

        utilization = max;
        return true;
    }

    /// <summary>
    /// 全系统专用显存占用：QueryState(AdapterMemoryUsageBytes) 的入参是
    /// { physicalAdapterIndex, memoryType }，8 字节；出参 { committed, resident }，16 字节。
    /// 取 Resident，和任务管理器的“专用 GPU 内存”同口径（已用 PDH 逐字节对过）。
    /// 复用 _input / _output，不新增任何分配；失败时保留上一次成功值并清 MemoryValid。
    /// </summary>
    private unsafe void SampleMemory()
    {
        MemoryValid = false;

        if (_adapter is null || _input == IntPtr.Zero || _physicalCount <= 0)
            return;

        ulong resident = 0;

        for (int p = 0; p < _physicalCount; p++)
        {
            *(DxCoreNative.MemoryQueryInput*)_input = new DxCoreNative.MemoryQueryInput
            {
                PhysicalAdapterIndex = (uint)p,
                MemoryType = DxCoreNative.MemoryTypeDedicated,
            };

            int hr = _adapter.QueryState(
                DxCoreNative.StateAdapterMemoryUsageBytes,
                (nuint)sizeof(DxCoreNative.MemoryQueryInput), _input,
                (nuint)sizeof(DxCoreNative.MemoryUsage), _output);

            if (hr < 0)
                return;

            resident += ((DxCoreNative.MemoryUsage*)_output)->Resident;
        }

        DedicatedUsedBytes = resident;
        MemoryValid = true;
    }

    private static bool TryCreateFactory(out DxCoreNative.IDXCoreAdapterFactory? factory)
    {
        factory = null;
        IntPtr pFactory = IntPtr.Zero;

        int hr;
        try
        {
            hr = DxCoreNative.CreateAdapterFactory(in DxCoreNative.IidAdapterFactory, out pFactory);
        }
        catch (DllNotFoundException)
        {
            Log.WriteLine("[dxcore] dxcore.dll not found");
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            Log.WriteLine("[dxcore] DXCoreCreateAdapterFactory missing");
            return false;
        }

        if (hr < 0 || pFactory == IntPtr.Zero)
        {
            Log.WriteLine($"[dxcore] CreateAdapterFactory hr=0x{hr:X8}");
            return false;
        }

        try
        {
            factory = (DxCoreNative.IDXCoreAdapterFactory)Marshal.GetObjectForIUnknown(pFactory);
        }
        finally
        {
            Marshal.Release(pFactory);
        }

        return factory is not null;
    }

    /// <summary>选 DedicatedAdapterMemory 最大的硬件适配器；软件适配器（WARP）一律排除。</summary>
    private static unsafe bool TryFindBestAdapter(DxCoreNative.IDXCoreAdapterFactory factory, out DxCoreNative.Luid bestLuid)
    {
        bestLuid = default;

        Guid iidList = DxCoreNative.IidAdapterList;
        Guid filter = DxCoreNative.AttributeD3D11Graphics;

        if (factory.CreateAdapterList(1, (IntPtr)(&filter), in iidList, out IntPtr pList) < 0
            || pList == IntPtr.Zero)
        {
            return false;
        }

        DxCoreNative.IDXCoreAdapterList list;
        try
        {
            list = (DxCoreNative.IDXCoreAdapterList)Marshal.GetObjectForIUnknown(pList);
        }
        finally
        {
            Marshal.Release(pList);
        }

        try
        {
            uint count = list.GetAdapterCount();
            bool found = false;
            ulong bestMemory = 0;

            for (uint i = 0; i < count; i++)
            {
                Guid iidAdapter = DxCoreNative.IidAdapter1;
                if (list.GetAdapter(i, in iidAdapter, out IntPtr pAdapter) < 0
                    || pAdapter == IntPtr.Zero)
                {
                    continue;
                }

                var adapter = (DxCoreNative.IDXCoreAdapter1)Marshal.GetObjectForIUnknown(pAdapter);
                Marshal.Release(pAdapter);

                try
                {
                    if (!adapter.IsValid()
                        || !DxCoreNative.ReadBool(adapter, DxCoreNative.PropertyIsHardware))
                    {
                        continue;
                    }

                    ulong dedicated = DxCoreNative.ReadUInt64(adapter, DxCoreNative.PropertyDedicatedAdapterMemory);
                    if (!found || dedicated > bestMemory)
                    {
                        bestLuid = DxCoreNative.ReadLuid(adapter);
                        bestMemory = dedicated;
                        found = true;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            return found;
        }
        finally
        {
            Marshal.ReleaseComObject(list);
        }
    }

    private bool BuildEngineLayout(DxCoreNative.IDXCoreAdapter1 adapter)
    {
        uint physical = DxCoreNative.ReadUInt32(adapter, DxCoreNative.PropertyPhysicalAdapterCount);
        if (physical == 0)
            physical = 1;
        if (physical > MaxPhysicalAdapters)
            return false;

        var counts = new int[physical];
        var offsets = new int[physical + 1];
        int slots = 0;

        for (uint p = 0; p < physical; p++)
        {
            uint engines = DxCoreNative.ReadEngineCount(adapter, p);
            if (engines > MaxEnginesPerAdapter)
                return false;

            counts[p] = (int)engines;
            offsets[p + 1] = offsets[p] + (int)engines;
            slots += (int)engines;
        }

        if (slots <= 0 || slots > MaxEngines)
            return false;

        _physicalCount = (int)physical;
        _engineCounts = counts;
        _offsets = offsets;
        _slotCount = slots;

        // 基线数组同样只在初始化时分配，采样期只读写元素。
        _prevRunning = new ulong[slots];
        _hasPrev = new bool[slots];
        _lastQueryTimestamp = new long[slots];
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_input != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_input);
            _input = IntPtr.Zero;
        }

        if (_output != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_output);
            _output = IntPtr.Zero;
        }

        if (_adapter is not null)
        {
            try { Marshal.ReleaseComObject(_adapter); }
            catch (ArgumentException) { }
            _adapter = null;
        }

        if (_factory is not null)
        {
            try { Marshal.ReleaseComObject(_factory); }
            catch (ArgumentException) { }
            _factory = null;
        }
    }

}
