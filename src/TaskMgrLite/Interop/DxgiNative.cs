using System.Runtime.InteropServices;

namespace TaskMgrLite.Interop;

/// <summary>
/// 只覆盖“显存总量 + 预算 + 使用量”所需的 DXGI 1.4 子集。
///
/// 不引入 Vortice / SharpDX 等包装库；接口按 vtable 顺序手写，
/// 未调用的方法只保留槽位。GPU 引擎占用率 DXGI 不提供，本文件刻意不声明。
/// </summary>
internal static class DxgiNative
{
    internal static readonly Guid IidFactory1 =
        new("770AAE78-F26F-4DBA-A829-253C83D1B387");

    internal static readonly Guid IidAdapter3 =
        new("645967A4-1392-4310-A798-8053CE3E93FD");

    internal const int MemorySegmentLocal = 0;
    internal const uint AdapterFlagSoftware = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    // x64 大小 312 字节：Description 256 + 4 个 UINT + 3 个 SIZE_T + LUID + Flags。
    // Description 用 ByValTStr 而不是 fixed char：实测 fixed char 在 COM out 封送时
    // 只复制了第一个 WCHAR，其余变成 0xCDCD；ByValTStr 才会完整转换 128 个字符。
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct AdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public Luid AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryVideoMemoryInfo
    {
        public ulong Budget;
        public ulong CurrentUsage;
        public ulong AvailableForReservation;
        public ulong CurrentReservation;
    }

    [ComImport]
    [Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIFactory1
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr pUnk);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr pWindowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);
        [PreserveSig] int EnumAdapters1(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int IsCurrent();
    }

    // vtable：IDXGIObject 4 + IDXGIAdapter 3 + Adapter1 1 + Adapter2 1 + Adapter3 6。
    [ComImport]
    [Guid("645967A4-1392-4310-A798-8053CE3E93FD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXGIAdapter3
    {
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr pUnk);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint pDataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr pDesc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long pUMDVersion);
        [PreserveSig] int GetDesc1(out AdapterDesc1 pDesc);
        [PreserveSig] int GetDesc2(IntPtr pDesc);
        [PreserveSig] int RegisterHardwareContentProtectionTeardownStatusEvent(IntPtr hEvent, out uint pCookie);
        [PreserveSig] int UnregisterHardwareContentProtectionTeardownStatus(uint cookie);
        [PreserveSig] int QueryVideoMemoryInfo(uint nodeIndex, int memorySegmentGroup, out QueryVideoMemoryInfo pVideoMemoryInfo);
        [PreserveSig] int SetVideoMemoryReservation(uint nodeIndex, int memorySegmentGroup, ulong reservation);
        [PreserveSig] int RegisterVideoMemoryBudgetChangeNotificationEvent(IntPtr hEvent, out uint pCookie);
        [PreserveSig] int UnregisterVideoMemoryBudgetChangeNotification(uint cookie);
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    internal static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
}
