using System.Net;
using System.Runtime.InteropServices;

namespace TaskMgrLite.Core;

/// <summary>
/// 读取 TCP/UDP 的 IPv4 / IPv6 四张 owner-PID 表。
///
/// 只有 ReadAll()：全系统端口，供独立的网络端口视图按需轮询。
/// 这里不会在任何 1Hz 进程采集路径上被调用。
/// </summary>
public static class PortReader
{
    private const int AfInet = 2;
    private const int AfInet6 = 23;

    private const uint NoError = 0;
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInsufficientBuffer = 122;

    private const int TcpTableOwnerPidAll = 5;
    private const int UdpTableOwnerPid = 1;

    // MIB_TCPROW_OWNER_PID / MIB_TCP6ROW_OWNER_PID
    private const int Tcp4RowSize = 24;
    private const int Tcp6RowSize = 56;
    // MIB_UDPROW_OWNER_PID / MIB_UDP6ROW_OWNER_PID
    private const int Udp4RowSize = 12;
    private const int Udp6RowSize = 28;

    private const int MaxTableBytes = 64 * 1024 * 1024;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        IntPtr pTcpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(
        IntPtr pUdpTable,
        ref int pdwSize,
        [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf,
        int tableClass,
        uint reserved);

    /// <summary>
    /// 读取全系统所有 TCP/UDP IPv4/IPv6 端口。
    /// 结果按 协议 → 本地端点 → 远程端点 → PID 排序，保证同一组端口在
    /// 多次采样间顺序稳定，让 UI Diff 只处理真正的增删。
    /// </summary>
    public static PortQueryResult ReadAll() => ReadCore();

    private static PortQueryResult ReadCore()
    {
        var entries = new List<PortEntry>(2048);
        bool denied = false;
        bool error = false;

        ReadOne(tcp: true, AfInet, entries, ref denied, ref error);
        ReadOne(tcp: true, AfInet6, entries, ref denied, ref error);
        ReadOne(tcp: false, AfInet, entries, ref denied, ref error);
        ReadOne(tcp: false, AfInet6, entries, ref denied, ref error);

        // 稳定顺序：Diff 只关心协议/端点/PID，排序键也保持一致。
        entries.Sort(static (a, b) =>
        {
            int byProtocol = string.CompareOrdinal(a.Protocol, b.Protocol);
            if (byProtocol != 0) return byProtocol;

            int byLocal = string.CompareOrdinal(a.LocalEndpoint, b.LocalEndpoint);
            if (byLocal != 0) return byLocal;

            int byRemote = string.CompareOrdinal(a.RemoteEndpoint, b.RemoteEndpoint);
            if (byRemote != 0) return byRemote;

            return a.Pid.CompareTo(b.Pid);
        });

        PortEntry[] result = entries.ToArray();
        PortQueryStatus status = result.Length > 0
            ? (denied || error ? PortQueryStatus.Partial : PortQueryStatus.Ok)
            : denied
                ? PortQueryStatus.AccessDenied
                : error
                    ? PortQueryStatus.Error
                    : PortQueryStatus.NoData;

        return new PortQueryResult(result, status, status.ToString());
    }

    private static void ReadOne(
        bool tcp,
        int family,
        List<PortEntry> output,
        ref bool denied,
        ref bool error)
    {
        try
        {
            TableResult table = ReadTable(tcp, family);
            if (table.Entries.Length > 0)
                output.AddRange(table.Entries);

            if (table.Status == PortQueryStatus.AccessDenied)
                denied = true;
            else if (table.Status == PortQueryStatus.Error)
                error = true;
        }
        catch
        {
            // 网络表属于不可信非托管数据；单张表失败不影响其它表。
            error = true;
        }
    }

    private static TableResult ReadTable(bool tcp, int family)
    {
        int size = 0;
        uint code = Call(tcp, IntPtr.Zero, ref size, family);

        if (code != NoError && code != ErrorInsufficientBuffer)
            return FromError(code);

        if (size <= 0)
            return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.NoData);

        if (size > MaxTableBytes)
            return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                int actual = size;
                code = Call(tcp, buffer, ref actual, family);

                if (code == ErrorInsufficientBuffer)
                {
                    // 表在两次调用之间增长：释放后按新大小重试。
                    size = actual;
                    if (size <= 0 || size > MaxTableBytes)
                        return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);
                    continue;
                }

                if (code != NoError)
                    return FromError(code);

                return Parse(buffer, actual, tcp, family);
            }
            finally
            {
                // 所有路径都必须释放，包括解析抛异常。
                Marshal.FreeHGlobal(buffer);
            }
        }

        return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);
    }

    private static uint Call(bool tcp, IntPtr table, ref int size, int family)
    {
        return tcp
            ? GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidAll, 0)
            : GetExtendedUdpTable(table, ref size, false, family, UdpTableOwnerPid, 0);
    }

    private static TableResult FromError(uint code)
    {
        PortQueryStatus status = code == ErrorAccessDenied
            ? PortQueryStatus.AccessDenied
            : PortQueryStatus.Error;
        return new TableResult(Array.Empty<PortEntry>(), status);
    }

    private static TableResult Parse(
        IntPtr buffer,
        int size,
        bool tcp,
        int family)
    {
        if (size < 4)
            return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);

        uint count = (uint)Marshal.ReadInt32(buffer, 0);
        bool ipv6 = family == AfInet6;
        int rowSize = tcp
            ? (ipv6 ? Tcp6RowSize : Tcp4RowSize)
            : (ipv6 ? Udp6RowSize : Udp4RowSize);

        int maxRows = (size - 4) / rowSize;
        if (count > (uint)maxRows)
            return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);

        // 全表模式下条目可能上千，先按表大小估个容量，减少 List 扩容。
        int capacity = (int)Math.Min((long)count, 8192L);
        var entries = new List<PortEntry>(capacity);

        for (uint i = 0; i < count; i++)
        {
            int offset = 4 + (int)i * rowSize;
            if (offset < 4 || offset + rowSize > size)
                return new TableResult(Array.Empty<PortEntry>(), PortQueryStatus.Error);

            uint ownerPid = ReadOwnerPid(buffer, offset, tcp, ipv6);

            entries.Add(tcp
                ? ReadTcpRow(buffer, offset, ipv6, ownerPid)
                : ReadUdpRow(buffer, offset, ipv6, ownerPid));
        }

        return new TableResult(
            entries.ToArray(),
            entries.Count > 0 ? PortQueryStatus.Ok : PortQueryStatus.NoData);
    }

    private static uint ReadOwnerPid(IntPtr buffer, int offset, bool tcp, bool ipv6)
    {
        return tcp
            ? (uint)Marshal.ReadInt32(buffer, offset + (ipv6 ? 52 : 20))
            : (uint)Marshal.ReadInt32(buffer, offset + (ipv6 ? 24 : 8));
    }

    private static PortEntry ReadTcpRow(IntPtr buffer, int offset, bool ipv6, uint pid)
    {
        if (!ipv6)
        {
            uint state = (uint)Marshal.ReadInt32(buffer, offset);
            string local = FormatV4(buffer, offset + 4);
            ushort localPort = ReadPort(buffer, offset + 8);
            string remote = FormatV4(buffer, offset + 12);
            ushort remotePort = ReadPort(buffer, offset + 16);

            return new PortEntry(
                "TCP4",
                FormatEndpoint(local, localPort, ipv6: false),
                FormatEndpoint(remote, remotePort, ipv6: false),
                TcpState(state),
                pid,
                localPort,
                remotePort);
        }

        uint localScope = (uint)Marshal.ReadInt32(buffer, offset + 16);
        string local6 = FormatV6(buffer, offset, localScope);
        ushort localPort6 = ReadPort(buffer, offset + 20);
        uint remoteScope = (uint)Marshal.ReadInt32(buffer, offset + 40);
        string remote6 = FormatV6(buffer, offset + 24, remoteScope);
        ushort remotePort6 = ReadPort(buffer, offset + 44);
        uint state6 = (uint)Marshal.ReadInt32(buffer, offset + 48);

        return new PortEntry(
            "TCP6",
            FormatEndpoint(local6, localPort6, ipv6: true),
            FormatEndpoint(remote6, remotePort6, ipv6: true),
            TcpState(state6),
            pid,
            localPort6,
            remotePort6);
    }

    private static PortEntry ReadUdpRow(IntPtr buffer, int offset, bool ipv6, uint pid)
    {
        if (!ipv6)
        {
            string local = FormatV4(buffer, offset);
            ushort localPort = ReadPort(buffer, offset + 4);
            return new PortEntry(
                "UDP4",
                FormatEndpoint(local, localPort, ipv6: false),
                "—",
                "—",
                pid,
                localPort,
                0);
        }

        uint scope = (uint)Marshal.ReadInt32(buffer, offset + 16);
        string local6 = FormatV6(buffer, offset, scope);
        ushort localPort6 = ReadPort(buffer, offset + 20);
        return new PortEntry(
            "UDP6",
            FormatEndpoint(local6, localPort6, ipv6: true),
            "—",
            "—",
            pid,
            localPort6,
            0);
    }

    private static ushort ReadPort(IntPtr buffer, int offset)
    {
        // dwLocalPort / dwRemotePort 的低 16 位是网络字节序。
        uint raw = (uint)Marshal.ReadInt32(buffer, offset);
        return (ushort)(((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF));
    }

    private static string FormatV4(IntPtr buffer, int offset)
    {
        byte[] bytes = new byte[4];
        Marshal.Copy(IntPtr.Add(buffer, offset), bytes, 0, 4);
        return new IPAddress(bytes).ToString();
    }

    private static string FormatV6(IntPtr buffer, int offset, uint scopeId)
    {
        byte[] bytes = new byte[16];
        Marshal.Copy(IntPtr.Add(buffer, offset), bytes, 0, 16);
        return new IPAddress(bytes, scopeId).ToString();
    }

    private static string FormatEndpoint(string address, ushort port, bool ipv6) =>
        ipv6 ? $"[{address}]:{port}" : $"{address}:{port}";

    private static string TcpState(uint state) => state switch
    {
        1 => "CLOSED",
        2 => "LISTEN",
        3 => "SYN_SENT",
        4 => "SYN_RCVD",
        5 => "ESTABLISHED",
        6 => "FIN_WAIT1",
        7 => "FIN_WAIT2",
        8 => "CLOSE_WAIT",
        9 => "CLOSING",
        10 => "LAST_ACK",
        11 => "TIME_WAIT",
        12 => "DELETE_TCB",
        _ => state.ToString()
    };

    private readonly record struct TableResult(PortEntry[] Entries, PortQueryStatus Status);
}
