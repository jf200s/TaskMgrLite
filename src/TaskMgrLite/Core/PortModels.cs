namespace TaskMgrLite.Core;

public enum PortQueryStatus
{
    Ok,
    NoData,
    Partial,
    AccessDenied,
    Error
}

public readonly record struct PortEntry(
    string Protocol,
    string LocalEndpoint,
    string RemoteEndpoint,
    string State);

public sealed record PortQueryResult(
    PortEntry[] Entries,
    PortQueryStatus Status,
    string Message)
{
    public string DisplayText => Status switch
    {
        PortQueryStatus.Ok => $"已加载 {Entries.Length} 个端口",
        PortQueryStatus.NoData => "未发现端口",
        PortQueryStatus.Partial => $"部分网络表读取失败，已显示 {Entries.Length} 个端口",
        PortQueryStatus.AccessDenied => "无权限读取",
        _ => "读取失败"
    };

    public static PortQueryResult Error(string message) =>
        new(Array.Empty<PortEntry>(), PortQueryStatus.Error, message);
}
