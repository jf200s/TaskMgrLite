using System.Runtime.InteropServices;

namespace TaskMgrLite.Interop;

/// <summary>
/// 性能采集用到的原生声明。集中放一处，便于审计布局和句柄生命周期。
///
/// 所有结构体按 x64 布局定义；项目只发布 x64。若以后支持 x86，
/// 必须先重新核对 SYSTEM_PERFORMANCE_INFORMATION / MIB_IF_ROW2 的偏移。
/// </summary>
internal static class MetricsNative
{
    internal const int SystemPerformanceInformation = 2;
    internal const uint StatusSuccess = 0;
    internal const uint StatusInfoLengthMismatch = 0xC000_0004;

    internal const uint IfTypeSoftwareLoopback = 24;
    internal const uint IfTypeTunnel = 131;
    internal const byte FilterInterfaceFlag = 0x02;

    internal const uint SmbiosProvider = 0x5253_4D42; // 'RSMB'

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint Low;
        public uint High;

        public readonly ulong ToUInt64() => ((ulong)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    // x64 为 104 字节（含 CommitPeak），x86 为 56 字节。
    [StructLayout(LayoutKind.Sequential)]
    internal struct PerformanceInformation
    {
        public uint Cb;
        public nuint CommitTotal;
        public nuint CommitLimit;
        public nuint CommitPeak;
        public nuint PhysicalTotal;
        public nuint PhysicalAvailable;
        public nuint SystemCache;
        public nuint KernelTotal;
        public nuint KernelPaged;
        public nuint KernelNonpaged;
        public nuint PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    // 16 字节 GUID 占位。用 fixed byte 而不是 System.Guid，确保结构体严格 blittable，
    // ref 传参时不会触发封送复制。
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct Guid16
    {
        public fixed byte Data[16];
    }

    // MIB_IF_ROW2，x64 为 1352 字节。
    // 字段顺序必须与 netioapi.h 完全一致；fixed 数组用来避免字符串封送分配。
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MibIfRow2
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid16 InterfaceGuid;
        public fixed char Alias[257];
        public fixed char Description[257];
        public uint PhysicalAddressLength;
        public fixed byte PhysicalAddress[32];
        public fixed byte PermanentPhysicalAddress[32];
        public uint Mtu;
        public uint Type;
        public uint TunnelType;
        public uint MediaType;
        public uint PhysicalMediumType;
        public uint AccessType;
        public uint DirectionType;
        public byte InterfaceAndOperStatusFlags;
        public uint OperStatus;
        public uint AdminStatus;
        public uint MediaConnectState;
        public Guid16 NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed;
        public ulong ReceiveLinkSpeed;

        public ulong InOctets;
        public ulong InUcastPkts;
        public ulong InNUcastPkts;
        public ulong InDiscards;
        public ulong InErrors;
        public ulong InUnknownProtos;
        public ulong InUcastOctets;
        public ulong InMulticastOctets;
        public ulong InBroadcastOctets;

        public ulong OutOctets;
        public ulong OutUcastPkts;
        public ulong OutNUcastPkts;
        public ulong OutDiscards;
        public ulong OutErrors;
        public ulong OutUcastOctets;
        public ulong OutMulticastOctets;
        public ulong OutBroadcastOctets;
        public ulong OutQLen;
    }

    // 表头 NumEntries 之后是 MIB_IF_ROW2 数组；x64 下数组起始偏移为 8。
    // 文档提醒可能存在对齐 padding，按 sizeof(MIB_IF_ROW2) 推进，不做逐字节假设。
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct MibIfTable2
    {
        public uint NumEntries;
        public MibIfRow2 Table;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void IpInterfaceChangeCallback(
        IntPtr callerContext,
        IntPtr row,
        uint notificationType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(
        out FileTime idleTime,
        out FileTime kernelTime,
        out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetPerformanceInfo(
        out PerformanceInformation pPerformanceInformation,
        uint cb);

    [DllImport("ntdll.dll")]
    internal static extern uint NtQuerySystemInformation(
        int systemInformationClass,
        IntPtr systemInformation,
        int systemInformationLength,
        out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetSystemFirmwareTable(
        uint firmwareTableProviderSignature,
        uint firmwareTableId,
        IntPtr firmwareTableBuffer,
        uint bufferSize);

    [DllImport("iphlpapi.dll")]
    internal static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    internal static extern uint GetIfEntry2(IntPtr row);

    [DllImport("iphlpapi.dll")]
    internal static extern void FreeMibTable(IntPtr memory);

    [DllImport("iphlpapi.dll")]
    internal static extern uint NotifyIpInterfaceChange(
        ushort family,
        IpInterfaceChangeCallback callback,
        IntPtr callerContext,
        [MarshalAs(UnmanagedType.U1)] bool initialNotification,
        out IntPtr notificationHandle);

    [DllImport("iphlpapi.dll")]
    internal static extern uint CancelMibChangeNotify2(IntPtr notificationHandle);
}
