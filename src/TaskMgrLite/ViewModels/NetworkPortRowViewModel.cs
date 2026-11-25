using TaskMgrLite.Core;

namespace TaskMgrLite.ViewModels;

/// <summary>
/// 端口列表的一行。
///
/// Key 字段（协议/本地/远程/PID）不可变，Diff 用它们认行；
/// State 和 ProcessName 可变，同一 key 的状态变化只更新属性，不换行。
/// </summary>
public sealed class NetworkPortRowViewModel : ObservableObject
{
    private string _state;
    private string _processName;

    public NetworkPortRowViewModel(in PortEntry entry, string processName)
    {
        Key = entry.Key;
        Protocol = entry.Protocol;
        LocalEndpoint = entry.LocalEndpoint;
        RemoteEndpoint = entry.RemoteEndpoint;
        Pid = entry.Pid;
        LocalPort = entry.LocalPort;
        RemotePort = entry.RemotePort;

        _state = entry.State;
        _processName = NormalizeName(processName, entry.Pid);
    }

    public PortKey Key { get; }
    public string Protocol { get; }
    public string LocalEndpoint { get; }
    public string RemoteEndpoint { get; }
    public uint Pid { get; }
    public ushort LocalPort { get; }
    public ushort RemotePort { get; }

    public string PidDisplay => Pid.ToString();
    public string LocalPortDisplay => LocalPort.ToString();
    public string RemotePortDisplay => RemoteEndpoint == "—" ? "—" : RemotePort.ToString();

    public string State
    {
        get => _state;
        private set => Set(ref _state, value);
    }

    public string ProcessName
    {
        get => _processName;
        private set => Set(ref _processName, value);
    }

    public void Update(in PortEntry entry, string processName)
    {
        State = entry.State;
        ProcessName = NormalizeName(processName, entry.Pid);
    }

    public static string NormalizeName(string? processName, uint pid)
    {
        if (!string.IsNullOrEmpty(processName))
            return processName;

        return pid is 0 or 4 ? "System/Unknown" : "Unknown";
    }
}
