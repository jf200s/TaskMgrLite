using TaskMgrLite.Core;

namespace TaskMgrLite.ViewModels;

/// <summary>
/// 一行。Depth / HasChildren / IsExpanded 由树的扁平化结果驱动，
/// 它们和采集数据完全解耦：采集只改 CPU/内存/句柄，树状态只改这三个。
/// </summary>
public sealed class ProcessRowViewModel : ObservableObject
{
    private double _cpuPercent = -1;   // -1 = 还没有基准，显示「—」
    private double _memoryMb;
    private uint _handleCount;
    private string _commandLine = string.Empty;
    private string _imagePath = string.Empty;
    private string _userName = string.Empty;

    private int _depth;
    private bool _hasChildren;
    private bool _isExpanded = true;

    public ProcessRowViewModel(
        uint pid,
        uint parentPid,
        string name,
        int depth,
        long createTime,
        DateTime? startTime = null)
    {
        Pid = pid;
        ParentPid = parentPid;
        Name = name;
        CreateTime = createTime;
        StartTime = startTime;
        _depth = Math.Clamp(depth, 0, 64);
    }

    public uint Pid { get; }
    public uint ParentPid { get; }
    public string Name { get; }
    public long CreateTime { get; }
    public DateTime? StartTime { get; }

    public string StartTimeDisplay => StartTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "—";

    /// <summary>稳定 identity，PID 复用也不会串行。</summary>
    public ProcKey KeyStruct => new(Pid, CreateTime);

    public int Depth
    {
        get => _depth;
        set
        {
            int clamped = Math.Clamp(value, 0, 64);
            if (Set(ref _depth, clamped))
                OnPropertyChanged(nameof(IndentWidth));
        }
    }

    /// <summary>树模式的缩进，靠一个空 Border 撑开，不嵌套控件。</summary>
    public double IndentWidth => _depth * 16.0;

    public bool HasChildren
    {
        get => _hasChildren;
        set
        {
            if (Set(ref _hasChildren, value))
                OnPropertyChanged(nameof(ToggleGlyph));
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value))
                OnPropertyChanged(nameof(ToggleGlyph));
        }
    }

    public string ToggleGlyph => !_hasChildren ? string.Empty : (_isExpanded ? "▾" : "▸");

    public double CpuPercent
    {
        get => _cpuPercent;
        set
        {
            if (Set(ref _cpuPercent, value))
                OnPropertyChanged(nameof(CpuDisplay));
        }
    }

    public string CpuDisplay => _cpuPercent < 0 ? "—" : _cpuPercent.ToString("F1");

    public double MemoryMb
    {
        get => _memoryMb;
        set
        {
            if (Set(ref _memoryMb, value))
                OnPropertyChanged(nameof(MemoryDisplay));
        }
    }

    public string MemoryDisplay => $"{_memoryMb:F1} MB";

    public uint HandleCount
    {
        get => _handleCount;
        set => Set(ref _handleCount, value);
    }

    public string CommandLine
    {
        get => _commandLine;
        set => Set(ref _commandLine, value);
    }

    public string ImagePath
    {
        get => _imagePath;
        set => Set(ref _imagePath, value);
    }

    public string UserName
    {
        get => _userName;
        set => Set(ref _userName, value);
    }
}
