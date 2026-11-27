using System.Diagnostics;
using System.Runtime.InteropServices;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>
/// --diag-perf：不创建窗口，验证性能采集层的硬契约。
///
/// 1. 原生结构体大小 / 关键偏移；
/// 2. MetricsCollector 的首帧有效标志、第二帧速率与物理内存；
/// 3. 预热后连续采样的托管分配必须为 0；
/// 4. SMBIOS / DXGI 在无硬件环境下允许不可用，但不能抛异常。
/// </summary>
public static class PerfDiagnostics
{
    private static int _passed;
    private static int _failed;

    public static int Run()
    {
        _passed = 0;
        _failed = 0;

        Check("native layout", NativeLayout);
        Check("collector first/second frame", CollectorContract);
        Check("steady zero allocation", SteadyAllocation);
        Check("dxcore gpu utilization", DxCoreSmoke);
        Check("optional hardware smoke", HardwareSmoke);

        Log.WriteLine($"[diag-perf] {_passed} passed, {_failed} failed");
        return _failed;
    }

    private static void NativeLayout()
    {
        ExpectSize<MetricsNative.FileTime>(8);
        ExpectSize<MetricsNative.Guid16>(16);
        ExpectSize<MetricsNative.MemoryStatusEx>(64);
        ExpectSize<MetricsNative.PerformanceInformation>(104);
        ExpectSize<MetricsNative.MibIfRow2>(1352);
        ExpectSize<MetricsNative.MibIfTable2>(1360);

        ExpectOffset<MetricsNative.MibIfRow2>("InterfaceLuid", 0);
        ExpectOffset<MetricsNative.MibIfRow2>("InterfaceAndOperStatusFlags", 1152);
        ExpectOffset<MetricsNative.MibIfRow2>("InOctets", 1208);
        ExpectOffset<MetricsNative.MibIfRow2>("OutOctets", 1280);
    }

    private static void CollectorContract()
    {
        using var collector = new MetricsCollector();

        MetricsSnapshot first = collector.Sample();

        if (first.CpuValid)
            throw new Exception("first CPU sample should not be valid");
        if (!first.MemoryValid)
            throw new Exception("first physical-memory sample should be valid");
        if (first.DiskValid)
            throw new Exception("first disk sample should not be valid");
        if (first.NetworkValid)
            throw new Exception("first network sample should not be valid");

        Thread.Sleep(120);
        MetricsSnapshot second = collector.Sample();

        if (!second.CpuValid)
            throw new Exception("second CPU sample should be valid");
        if (!second.MemoryValid)
            throw new Exception("second physical-memory sample should be valid");
        if (second.TotalPhysicalBytes == 0)
            throw new Exception("physical memory total should be non-zero");
        if (!second.DiskValid)
            throw new Exception("second disk sample should be valid");
        if (!second.NetworkValid)
            throw new Exception("second network sample should be valid");

        // 端到端：DXCore 选卡 → LUID 回传给 DXGI → 显存与占用率必须指向同一张卡。
        Log.WriteLine(
            $"[diag-perf] gpu name={second.GpuName ?? "<none>"} memValid={second.GpuMemoryValid} " +
            $"utilValid={second.GpuUtilizationValid} util={second.GpuUtilizationPercent:0.0}%");
    }

    /// <summary>DXCore 初始化 + 两帧采样的冒烟。无硬件适配器（服务器 / 老系统）允许不可用。</summary>
    private static void DxCoreSmoke()
    {
        using var monitor = new DxCoreGpuMonitor();
        monitor.Initialize();

        if (!monitor.HasAdapter)
        {
            Log.WriteLine("[diag-perf] dxcore unavailable (allowed)");
            return;
        }

        if (monitor.EngineCount <= 0)
            throw new Exception("adapter present but engine count is zero");

        monitor.TrySample(Stopwatch.GetTimestamp(), out _);
        Thread.Sleep(1100);

        bool valid = monitor.TrySample(Stopwatch.GetTimestamp(), out double utilization);
        Log.WriteLine(
            $"[diag-perf] dxcore adapter={monitor.AdapterName} engines={monitor.EngineCount} " +
            $"valid={valid} utilization={utilization:0.0}% " +
            $"vram={monitor.DedicatedUsedBytes}/{monitor.DedicatedTotalBytes} valid={monitor.MemoryValid}");

        if (!valid)
            throw new Exception("engine running-time query never became valid");

        if (monitor.DedicatedTotalBytes > 0 && !monitor.MemoryValid)
            throw new Exception("dedicated memory query failed on adapter with dedicated memory");
    }

    private static void SteadyAllocation()
    {
        using var collector = new MetricsCollector();

        // 首帧初始化 SMBIOS / DXGI / 网卡表，不计入稳态。
        for (int i = 0; i < 4; i++)
        {
            collector.Sample();
            Thread.Sleep(25);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10; i++)
        {
            collector.Sample();
            Thread.Sleep(25);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0)
            throw new Exception($"steady state allocated {allocated} bytes");
    }

    private static void HardwareSmoke()
    {
        MemorySpeedInfo speed = MemorySpeedReader.Read();
        Log.WriteLine($"[diag-perf] smbios valid={speed.Valid} speed={speed.MtS} modules={speed.ModuleCount}");
    }

    private static void ExpectSize<T>(int expected) where T : struct
    {
        int actual = Marshal.SizeOf<T>();
        if (actual != expected)
            throw new Exception($"{typeof(T).Name} size={actual}, expected {expected}");
    }

    private static void ExpectOffset<T>(string field, int expected) where T : struct
    {
        int actual = Marshal.OffsetOf<T>(field).ToInt32();
        if (actual != expected)
            throw new Exception($"{typeof(T).Name}.{field} offset={actual}, expected {expected}");
    }

    private static void Check(string name, Action test)
    {
        try
        {
            test();
            _passed++;
            Log.WriteLine($"[diag-perf] PASS  {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Log.WriteLine($"[diag-perf] FAIL  {name}: {ex.Message}");
        }
    }
}
