using System.Runtime.InteropServices;
using TaskMgrLite.Interop;

namespace TaskMgrLite.Core;

/// <summary>一次 DXGI 显存采样。Name 只在初始化时分配一次，之后复用同一引用。</summary>
internal readonly record struct GpuMemorySample(
    bool Valid,
    string? Name,
    ulong DedicatedBytes,
    ulong BudgetBytes,
    ulong UsageBytes)
{
    public static readonly GpuMemorySample Empty = new(false, null, 0, 0, 0);
}

/// <summary>
/// DXGI 显存探针：适配器名、专用显存总量、当前进程预算、当前进程使用量。
///
/// 重要口径：QueryVideoMemoryInfo 的 Budget / CurrentUsage 是“当前进程”的预算和用量，
/// 不是全系统显存占用。当前进程没有 D3D 资源时 CurrentUsage 就是 0；任务管理器那种
/// 全系统 “2.8 / 8.0 GB” 需要 PDH 或 D3DKMT，不在 v1 的 DXGI 范围内。
///
/// GPU 引擎占用率同样不在 DXGI 的能力范围内；本类不提供、也不伪造 0%。
/// 初始化只做一次，采样期只调 QueryVideoMemoryInfo，不新建 COM 对象。
/// </summary>
internal sealed class GpuMemoryReader : IDisposable
{
    private DxgiNative.IDXGIFactory1? _factory;
    private DxgiNative.IDXGIAdapter3? _adapter;
    private string? _name;
    private ulong _dedicatedBytes;
    private bool _initialized;
    private bool _disposed;

    public void Initialize()
    {
        if (_initialized || _disposed)
            return;

        _initialized = true;

        try
        {
            Guid iid = DxgiNative.IidFactory1;
            int hr = DxgiNative.CreateDXGIFactory1(ref iid, out IntPtr pFactory);
            if (hr < 0 || pFactory == IntPtr.Zero)
            {
                if (pFactory != IntPtr.Zero)
                    Marshal.Release(pFactory);

                return;
            }

            DxgiNative.IDXGIFactory1 factory;
            try
            {
                factory = (DxgiNative.IDXGIFactory1)Marshal.GetObjectForIUnknown(pFactory);
            }
            finally
            {
                Marshal.Release(pFactory);
            }

            _factory = factory;

            for (uint i = 0; ; i++)
            {
                int enumHr = factory.EnumAdapters1(i, out IntPtr pAdapter1);
                if (enumHr != 0 || pAdapter1 == IntPtr.Zero)
                    break;

                IntPtr pAdapter3 = IntPtr.Zero;
                try
                {
                    Guid iid3 = DxgiNative.IidAdapter3;
                    if (Marshal.QueryInterface(pAdapter1, in iid3, out pAdapter3) != 0)
                        continue;

                    DxgiNative.IDXGIAdapter3? adapter = null;
                    try
                    {
                        adapter = (DxgiNative.IDXGIAdapter3)Marshal.GetObjectForIUnknown(pAdapter3);
                    }
                    catch (InvalidCastException)
                    {
                        continue;
                    }

                    if (adapter is null)
                        continue;

                    if (adapter.GetDesc1(out DxgiNative.AdapterDesc1 desc) != 0
                        || (desc.Flags & DxgiNative.AdapterFlagSoftware) != 0)
                    {
                        Marshal.ReleaseComObject(adapter);
                        continue;
                    }

                    _adapter = adapter;
                    _name = GetName(desc);
                    _dedicatedBytes = (ulong)desc.DedicatedVideoMemory;
                    break;
                }
                finally
                {
                    if (pAdapter3 != IntPtr.Zero)
                        Marshal.Release(pAdapter3);

                    Marshal.Release(pAdapter1);
                }
            }
        }
        catch (DllNotFoundException)
        {
            // 极简系统 / 服务器核心可能没有 dxgi.dll。
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (COMException)
        {
        }
        catch (InvalidCastException)
        {
        }
    }

    public bool TrySample(out GpuMemorySample sample)
    {
        sample = GpuMemorySample.Empty;

        if (_disposed || _adapter is null)
            return false;

        int hr = _adapter.QueryVideoMemoryInfo(
            0,
            DxgiNative.MemorySegmentLocal,
            out DxgiNative.QueryVideoMemoryInfo info);

        if (hr != 0)
            return false;

        sample = new GpuMemorySample(
            true,
            _name,
            _dedicatedBytes,
            info.Budget,
            info.CurrentUsage);

        return true;
    }

    private static string? GetName(DxgiNative.AdapterDesc1 desc)
        => string.IsNullOrWhiteSpace(desc.Description) ? null : desc.Description;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

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
