using System.Runtime.InteropServices;

namespace TaskMgrLite.Interop;

/// <summary>
/// dxcore.dll 的最小 COM 封装。GUID / 枚举值取自 Windows SDK 10.0.26100 的
/// um\dxcore_interface.h。
///
/// 三个容易踩的点：
///   1. 按 LUID 取适配器是 GetAdapterByLuid，没有 EnumAdapterByLuid；
///   2. AdapterEngineCount 必须走 IDXCoreAdapter1::GetPropertyWithInput，输入是物理适配器索引；
///   3. DriverDescription 是 UTF-8 char*，不是 wchar_t*。
/// </summary>
internal static class DxCoreNative
{
    private const string Dll = "dxcore.dll";

    internal static readonly Guid IidAdapterFactory = new("78ee5945-c36e-4b13-a669-005dd11c0f06");
    internal static readonly Guid IidAdapterList = new("526c7776-40e9-459b-b711-f32ad76dfc28");
    internal static readonly Guid IidAdapter = new("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e");
    internal static readonly Guid IidAdapter1 = new("a0783366-cfa3-43be-9d79-55b2da97c63c");
    internal static readonly Guid AttributeD3D11Graphics = new("8c47866b-7583-450d-f0f0-6bada895af4b");

    // enum class DXCoreAdapterProperty : uint32_t
    internal const int PropertyInstanceLuid = 0;
    internal const int PropertyDriverDescription = 2;
    internal const int PropertyDedicatedAdapterMemory = 7;
    internal const int PropertyIsHardware = 11;
    internal const int PropertyPhysicalAdapterCount = 15;
    internal const int PropertyAdapterEngineCount = 16;

    // enum class DXCoreAdapterState : uint32_t
    internal const int StateAdapterMemoryUsageBytes = 2;
    internal const int StateAdapterEngineRunningTimeMicroseconds = 4;

    // enum class DXCoreMemoryType : uint32_t
    internal const int MemoryTypeDedicated = 0;
    internal const int MemoryTypeShared = 1;

    // ExactSpelling 下入口名默认取方法名，必须显式写 EntryPoint，否则抛 EntryPointNotFoundException。
    [DllImport(Dll, ExactSpelling = true, EntryPoint = "DXCoreCreateAdapterFactory")]
    internal static extern int CreateAdapterFactory(in Guid riid, out IntPtr ppvFactory);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    /// <summary>DXCoreAdapterEngineIndex(8B) + processId(4B) = 12B。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EngineQueryInput
    {
        public uint PhysicalAdapterIndex;
        public uint EngineIndex;
        public uint ProcessId; // 全系统口径填 0
    }

    /// <summary>原生 { uint64_t runningTime; bool processQuerySucceeded; }，按 8 对齐 => 16B。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct EngineQueryOutput
    {
        public ulong RunningTime;
        public byte ProcessQuerySucceeded;
    }

    /// <summary>原生 DXCoreMemoryQueryInput：uint32 physicalAdapterIndex + uint32 memoryType = 8B。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryQueryInput
    {
        public uint PhysicalAdapterIndex;
        public uint MemoryType;
    }

    /// <summary>原生 DXCoreMemoryUsage：committed + resident = 16B。</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryUsage
    {
        public ulong Committed;
        public ulong Resident;
    }

    [ComImport]
    [Guid("78ee5945-c36e-4b13-a669-005dd11c0f06")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXCoreAdapterFactory
    {
        [PreserveSig]
        int CreateAdapterList(uint numAttributes, IntPtr filterAttributes, in Guid riid, out IntPtr ppvAdapterList);

        [PreserveSig]
        int GetAdapterByLuid(in Luid adapterLuid, in Guid riid, out IntPtr ppvAdapter);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsNotificationTypeSupported(int notificationType);

        [PreserveSig]
        int RegisterEventNotification(IntPtr dxCoreObject, int notificationType, IntPtr callbackFunction, IntPtr callbackContext, out uint eventCookie);

        [PreserveSig]
        int UnregisterEventNotification(uint eventCookie);
    }

    [ComImport]
    [Guid("526c7776-40e9-459b-b711-f32ad76dfc28")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXCoreAdapterList
    {
        [PreserveSig]
        int GetAdapter(uint index, in Guid riid, out IntPtr ppvAdapter);

        [PreserveSig]
        uint GetAdapterCount();

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsStale();

        [PreserveSig]
        int GetFactory(in Guid riid, out IntPtr ppvFactory);

        [PreserveSig]
        int Sort(uint numPreferences, IntPtr preferences);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsAdapterPreferenceSupported(int preference);
    }

    /// <summary>
    /// vtable 顺序必须和头文件一致：IsValid, IsAttributeSupported, IsPropertySupported,
    /// GetProperty, GetPropertySize, IsQueryStateSupported, QueryState, IsSetStateSupported,
    /// SetState, GetFactory。原生 bool 是 1 字节，必须用 U1 返回。
    /// </summary>
    [ComImport]
    [Guid("f0db4c7f-fe5a-42a2-bd62-f2a6cf6fc83e")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXCoreAdapter
    {
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsValid();

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsAttributeSupported(in Guid attributeGuid);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsPropertySupported(int property);

        [PreserveSig]
        int GetProperty(int property, nuint bufferSize, IntPtr propertyData);

        [PreserveSig]
        int GetPropertySize(int property, out nuint bufferSize);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsQueryStateSupported(int state);

        [PreserveSig]
        int QueryState(int state, nuint inputStateDetailsSize, IntPtr inputStateDetails, nuint outputBufferSize, IntPtr outputBuffer);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsSetStateSupported(int state);

        [PreserveSig]
        int SetState(int state, nuint inputStateDetailsSize, IntPtr inputStateDetails, nuint inputDataSize, IntPtr inputData);

        [PreserveSig]
        int GetFactory(in Guid riid, out IntPtr ppvFactory);
    }

    /// <summary>
    /// IDXCoreAdapter1 = 基接口 10 个槽 + GetPropertyWithInput。
    /// ComImport 接口不要写 : IDXCoreAdapter 继承，CLR 不会展平基接口 vtable，
    /// 所以基方法必须原样重抄，保证槽位一致。
    /// </summary>
    [ComImport]
    [Guid("a0783366-cfa3-43be-9d79-55b2da97c63c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IDXCoreAdapter1
    {
        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsValid();

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsAttributeSupported(in Guid attributeGuid);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsPropertySupported(int property);

        [PreserveSig]
        int GetProperty(int property, nuint bufferSize, IntPtr propertyData);

        [PreserveSig]
        int GetPropertySize(int property, out nuint bufferSize);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsQueryStateSupported(int state);

        [PreserveSig]
        int QueryState(int state, nuint inputStateDetailsSize, IntPtr inputStateDetails, nuint outputBufferSize, IntPtr outputBuffer);

        [PreserveSig]
        [return: MarshalAs(UnmanagedType.U1)]
        bool IsSetStateSupported(int state);

        [PreserveSig]
        int SetState(int state, nuint inputStateDetailsSize, IntPtr inputStateDetails, nuint inputDataSize, IntPtr inputData);

        [PreserveSig]
        int GetFactory(in Guid riid, out IntPtr ppvFactory);

        [PreserveSig]
        int GetPropertyWithInput(int property, nuint inputPropertyDetailsSize, IntPtr inputPropertyDetails, nuint outputBufferSize, IntPtr outputBuffer);
    }

    internal static unsafe Luid ReadLuid(IDXCoreAdapter1 adapter)
    {
        Luid luid = default;
        return adapter.GetProperty(PropertyInstanceLuid, (nuint)sizeof(Luid), (IntPtr)(&luid)) < 0
            ? default
            : luid;
    }

    internal static unsafe uint ReadUInt32(IDXCoreAdapter1 adapter, int property)
    {
        uint value = 0;
        return adapter.GetProperty(property, sizeof(uint), (IntPtr)(&value)) < 0 ? 0u : value;
    }

    internal static unsafe ulong ReadUInt64(IDXCoreAdapter1 adapter, int property)
    {
        ulong value = 0;
        return adapter.GetProperty(property, sizeof(ulong), (IntPtr)(&value)) < 0 ? 0ul : value;
    }

    internal static unsafe bool ReadBool(IDXCoreAdapter1 adapter, int property)
    {
        byte value = 0;
        return adapter.GetProperty(property, sizeof(byte), (IntPtr)(&value)) >= 0 && value != 0;
    }

    /// <summary>AdapterEngineCount 带输入：uint32 physicalAdapterIndex -> uint32 count。</summary>
    internal static unsafe uint ReadEngineCount(IDXCoreAdapter1 adapter, uint physicalIndex)
    {
        uint count = 0;
        int hr = adapter.GetPropertyWithInput(
            PropertyAdapterEngineCount,
            (nuint)sizeof(uint), (IntPtr)(&physicalIndex),
            (nuint)sizeof(uint), (IntPtr)(&count));

        return hr < 0 ? 0u : count;
    }

    internal static unsafe string? ReadUtf8String(IDXCoreAdapter1 adapter, int property)
    {
        if (adapter.GetPropertySize(property, out nuint bytes) < 0
            || bytes < 2
            || bytes > 1024)
        {
            return null;
        }

        IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
        try
        {
            if (adapter.GetProperty(property, bytes, buffer) < 0)
                return null;

            return Marshal.PtrToStringUTF8(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
