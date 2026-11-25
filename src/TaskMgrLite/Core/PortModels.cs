namespace TaskMgrLite.Core;

public enum PortQueryStatus
{
    Ok,
    NoData,
    Partial,
    AccessDenied,
    Error
}

/// <summary>
/// 一条端口行。Pid 是四张网络表里的 owner PID；
/// LocalPort / RemotePort 是解析后的数值端口，便于搜索和数值排序。
/// </summary>
public readonly record struct PortEntry(
    string Protocol,
    string LocalEndpoint,
    string RemoteEndpoint,
    string State,
    uint Pid,
    ushort LocalPort,
    ushort RemotePort)
{
    /// <summary>端口视图的稳定身份：协议 + 本地端点 + 远程端点 + 所属 PID。</summary>
    public PortKey Key => new(Protocol, LocalEndpoint, RemoteEndpoint, Pid);
}

/// <summary>
/// 端口 Diff 的 key。State 不在 key 里：状态变化只更新行属性，
/// 不删除/插入行，避免 LISTEN → ESTABLISHED 时引发行闪烁。
/// </summary>
public readonly record struct PortKey(
    string Protocol,
    string LocalEndpoint,
    string RemoteEndpoint,
    uint Pid);

public sealed record PortQueryResult(
    PortEntry[] Entries,
    PortQueryStatus Status,
    string Message)
{
    public string DisplayText => Describe(Status, Entries.Length);

    public static string Describe(PortQueryStatus status, int count) => status switch
    {
        PortQueryStatus.Ok => $"已加载 {count} 个端口",
        PortQueryStatus.NoData => "未发现端口",
        PortQueryStatus.Partial => $"部分网络表读取失败，已显示 {count} 个端口",
        PortQueryStatus.AccessDenied => "无权限读取",
        _ => "读取失败"
    };

    public static PortQueryResult Error(string message) =>
        new(Array.Empty<PortEntry>(), PortQueryStatus.Error, message);
}
