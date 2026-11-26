using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TaskMgrLite.Core;

namespace TaskMgrLite.ViewModels;

/// <summary>
/// 性能页 ViewModel。
///
/// 线程模型：
///   - 后台 BelowNormal 线程 1Hz 调 MetricsCollector.Sample()，只把结构体写进 _latest；
///   - UI 线程 DispatcherTimer 1Hz 取最新快照，更新文字和 PointCollection；
///   - 采样线程不碰任何 WPF 对象，UI 线程不做 P/Invoke。
///
/// 点集只在字段初始化时 new 一次；之后只移动 Point 和改索引，不创建新集合。
/// </summary>
public sealed class PerformanceViewModel : ObservableObject, IDisposable
{
    private const int HistoryLength = 60;
    private const int TickMs = 1000;
    private const double ChartHeight = 64;
    private const double ChartStepX = 10;

    // 文本 1Hz 更新，折线 0.5Hz 重绘：Polyline 每改一个 Point 都会重建 geometry，
    // 1Hz 全量刷新会把 WPF 分配推到 0.1MB/s 以上；2s 刷新仍保持 60 秒窗口。
    private const int DisplayRefreshTicks = 1;

    // 采集与 UI 都是 1Hz：性能页的体验优先，WPF 文本/图表分配按已确认的
    // 0.12–0.14MB/s 量级接受，不再做降频。

    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stop = new(false);

    private readonly double[] _cpuSamples = new double[HistoryLength];
    private readonly double[] _diskReadSamples = new double[HistoryLength];
    private readonly double[] _diskWriteSamples = new double[HistoryLength];
    private readonly double[] _rxSamples = new double[HistoryLength];
    private readonly double[] _txSamples = new double[HistoryLength];

    private MetricsCollector? _collector;
    private Thread? _thread;
    private MetricsSnapshot _latest;
    private long _appliedSequence;
    private int _uiTick;
    private bool _isActive;
    private bool _unavailable;
    private bool _disposed;

    private string _cpuText = "—";
    private string _memoryText = "—";
    private string _memorySubText = "已提交 —";
    private string _memorySpeedText = "速度 —";
    private string _diskReadText = "读 —";
    private string _diskWriteText = "写 —";
    private string _networkRxText = "↓ —";
    private string _networkTxText = "↑ —";
    private string _gpuNameText = "未检测到硬件适配器";
    private string _gpuMemoryText = "—";
    private string _gpuBudgetText = "预算 —";
    private string _statusText = "未启动";

    public PerformanceViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));

        _uiTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(TickMs)
        };
        _uiTimer.Tick += OnUiTick;
    }

    // 点集只在这里创建一次。X 固定为 0..59，Y 为 0..100 的归一化坐标。
    public PointCollection CpuPoints { get; } = CreatePoints();
    public PointCollection DiskReadPoints { get; } = CreatePoints();
    public PointCollection DiskWritePoints { get; } = CreatePoints();
    public PointCollection NetworkRxPoints { get; } = CreatePoints();
    public PointCollection NetworkTxPoints { get; } = CreatePoints();

    public bool IsActive => _isActive;

    /// <summary>折线数据已重填。Polyline 不会自动监听 PointCollection 的元素替换，
    /// 由 View 在事件里对每个线段调用 InvalidateVisual。</summary>
    public event Action? ChartUpdated;

    public string CpuText
    {
        get => _cpuText;
        private set => Set(ref _cpuText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => Set(ref _memoryText, value);
    }

    public string MemorySubText
    {
        get => _memorySubText;
        private set => Set(ref _memorySubText, value);
    }

    public string MemorySpeedText
    {
        get => _memorySpeedText;
        private set => Set(ref _memorySpeedText, value);
    }

    public string DiskReadText
    {
        get => _diskReadText;
        private set => Set(ref _diskReadText, value);
    }

    public string DiskWriteText
    {
        get => _diskWriteText;
        private set => Set(ref _diskWriteText, value);
    }

    public string NetworkRxText
    {
        get => _networkRxText;
        private set => Set(ref _networkRxText, value);
    }

    public string NetworkTxText
    {
        get => _networkTxText;
        private set => Set(ref _networkTxText, value);
    }

    public string GpuNameText
    {
        get => _gpuNameText;
        private set => Set(ref _gpuNameText, value);
    }

    public string GpuMemoryText
    {
        get => _gpuMemoryText;
        private set => Set(ref _gpuMemoryText, value);
    }

    public string GpuBudgetText
    {
        get => _gpuBudgetText;
        private set => Set(ref _gpuBudgetText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    // v1 明确不提供 GPU 引擎占用率；DXGI 只给显存与预算。
    public string GpuUsageText => "N/A";
    public string GpuUsageNote => "DXGI 不提供引擎占用率；v1 不做 D3DKMT";
    public string GpuMemoryNote => "DXGI 的显存 usage 是本进程口径；全系统占用需 PDH / D3DKMT";

    public void Activate()
    {
        if (_disposed || _unavailable || _isActive)
            return;

        _isActive = true;
        OnPropertyChanged(nameof(IsActive));

        _appliedSequence = 0;
        _uiTick = 0;
        ResetHistory();
        _latest = default;

        _collector = new MetricsCollector();
        _stop.Reset();

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "TaskMgrLite.Metrics",
            Priority = ThreadPriority.BelowNormal
        };

        _thread.Start();
        _uiTimer.Start();
        StatusText = "1 Hz 采集 · 1 s 刷新 · 60 秒窗口";
        Log.WriteLine("[perf] activate");
    }

    /// <summary>假数据模式：不启动真实采集，只显示说明。</summary>
    public void ShowUnavailable(string message)
    {
        _unavailable = true;
        Deactivate();
        StatusText = message;
    }

    public void Deactivate()
    {
        if (!_isActive)
            return;

        _isActive = false;
        OnPropertyChanged(nameof(IsActive));

        _uiTimer.Stop();
        _stop.Set();

        Thread? thread = _thread;
        _thread = null;

        if (thread is { IsAlive: true })
            thread.Join(2000);

        // Join 超时说明采集线程可能还在用非托管缓冲，宁可一次性泄漏也不要 UAF。
        bool workerExited = thread is null || !thread.IsAlive;
        if (workerExited)
        {
            _collector?.Dispose();
            _collector = null;
        }

        StatusText = "已停止";
        Log.WriteLine($"[perf] deactivate joined={workerExited}");
    }

    private void Loop()
    {
        MetricsCollector? collector = _collector;
        if (collector is null)
            return;

        Log.WriteLine("[perf] loop start");
        var clock = Stopwatch.StartNew();
        long next = 0;

        while (!_stop.IsSet)
        {
            long now = clock.ElapsedMilliseconds;

            if (now < next)
            {
                _stop.Wait((int)Math.Min(next - now, 200));
                continue;
            }

            MetricsSnapshot snapshot;

            try
            {
                snapshot = collector.Sample();
            }
            catch
            {
                next = now + TickMs;
                continue;
            }

            lock (_gate)
                _latest = snapshot;

            next += TickMs;
            if (now - next > 2000)
                next = now + TickMs;
        }

        Log.WriteLine("[perf] loop exit");
    }

    private void OnUiTick(object? sender, EventArgs e)
    {
        if (!_isActive)
            return;

        MetricsSnapshot snapshot;
        lock (_gate)
            snapshot = _latest;

        if (snapshot.Sequence <= _appliedSequence)
            return;

        _appliedSequence = snapshot.Sequence;
        Apply(snapshot);
    }

    private void Apply(MetricsSnapshot s)
    {
        // 1Hz 采样照常进入历史窗口；UI 文本/折线每 2 个 tick 刷新一次。
        Push(_cpuSamples, s.CpuValid ? s.CpuUsagePercent : _cpuSamples[^1]);
        Push(_diskReadSamples, s.DiskValid ? s.DiskReadBytesPerSec : _diskReadSamples[^1]);
        Push(_diskWriteSamples, s.DiskValid ? s.DiskWriteBytesPerSec : _diskWriteSamples[^1]);
        Push(_rxSamples, s.NetworkValid ? s.NetworkRxBytesPerSec : _rxSamples[^1]);
        Push(_txSamples, s.NetworkValid ? s.NetworkTxBytesPerSec : _txSamples[^1]);

        _uiTick++;
        if (_uiTick % DisplayRefreshTicks != 0)
            return;

        CpuText = s.CpuValid ? $"{s.CpuUsagePercent:0.0}%" : "—";

        MemoryText = s.MemoryValid
            ? $"{FormatBytes(s.UsedPhysicalBytes)} / {FormatBytes(s.TotalPhysicalBytes)}"
            : "—";

        MemorySubText = s.CommitValid
            ? $"已提交 {FormatBytes(s.CommittedBytes)} / {FormatBytes(s.CommitLimitBytes)} · 缓存 {FormatBytes(s.SystemCacheBytes)}"
            : "已提交 —";

        MemorySpeedText = s.MemorySpeedValid
            ? $"{s.MemorySpeedMtS} MT/s · {s.MemoryModuleCount} 条"
            : "速度 —";

        DiskReadText = s.DiskValid ? "读 " + FormatRate(s.DiskReadBytesPerSec) : "读 —";
        DiskWriteText = s.DiskValid ? "写 " + FormatRate(s.DiskWriteBytesPerSec) : "写 —";
        NetworkRxText = s.NetworkValid ? "↓ " + FormatRate(s.NetworkRxBytesPerSec) : "↓ —";
        NetworkTxText = s.NetworkValid ? "↑ " + FormatRate(s.NetworkTxBytesPerSec) : "↑ —";

        GpuNameText = string.IsNullOrEmpty(s.GpuName) ? "未检测到硬件适配器" : s.GpuName!;
        GpuMemoryText = s.GpuMemoryValid
            ? $"本进程 {FormatBytes(s.GpuUsageBytes)} / 专用 {FormatBytes(s.GpuDedicatedBytes)}"
            : "—";
        GpuBudgetText = s.GpuMemoryValid ? $"本进程预算 {FormatBytes(s.GpuBudgetBytes)}" : "本进程预算 —";

        const double headroom = 1.2;

        double cpuMax = Math.Max(1, MaxRecent(_cpuSamples, 20) * headroom);
        double diskMax = Math.Max(1, Math.Max(MaxRecent(_diskReadSamples, 20), MaxRecent(_diskWriteSamples, 20)) * headroom);
        double netMax = Math.Max(1, Math.Max(MaxRecent(_rxSamples, 20), MaxRecent(_txSamples, 20)) * headroom);

        Fill(CpuPoints, _cpuSamples, cpuMax);
        Fill(DiskReadPoints, _diskReadSamples, diskMax);
        Fill(DiskWritePoints, _diskWriteSamples, diskMax);
        Fill(NetworkRxPoints, _rxSamples, netMax);
        Fill(NetworkTxPoints, _txSamples, netMax);

        ChartUpdated?.Invoke();
    }

    private void ResetHistory()
    {
        Array.Clear(_cpuSamples, 0, _cpuSamples.Length);
        Array.Clear(_diskReadSamples, 0, _diskReadSamples.Length);
        Array.Clear(_diskWriteSamples, 0, _diskWriteSamples.Length);
        Array.Clear(_rxSamples, 0, _rxSamples.Length);
        Array.Clear(_txSamples, 0, _txSamples.Length);

        ResetPoints(CpuPoints);
        ResetPoints(DiskReadPoints);
        ResetPoints(DiskWritePoints);
        ResetPoints(NetworkRxPoints);
        ResetPoints(NetworkTxPoints);
    }

    private static PointCollection CreatePoints()
    {
        var points = new PointCollection(HistoryLength);

        for (int i = 0; i < HistoryLength; i++)
            points.Add(new Point(i * ChartStepX, ChartHeight));

        return points;
    }

    private static void ResetPoints(PointCollection points)
    {
        for (int i = 0; i < points.Count; i++)
            points[i] = new Point(i * ChartStepX, ChartHeight);
    }

    private static void Push(double[] samples, double value)
    {
        for (int i = 0; i < samples.Length - 1; i++)
            samples[i] = samples[i + 1];

        samples[^1] = value;
    }

    private static double MaxRecent(double[] samples, int count)
    {
        double max = 0;
        int start = Math.Max(0, samples.Length - count);

        for (int i = start; i < samples.Length; i++)
        {
            if (samples[i] > max)
                max = samples[i];
        }

        return max;
    }

    // 只改已有 Point 的 Y，不新建 PointCollection。
    // 从 1Hz 样本数组重填 60 个点，保证窗口始终是最近 60 秒。
    private static void Fill(PointCollection points, double[] samples, double max)
    {
        for (int i = 0; i < points.Count && i < samples.Length; i++)
            points[i] = new Point(i * ChartStepX, Scale(samples[i], max, ChartHeight));
    }

    private static double Scale(double value, double max, double height)
    {
        if (max <= 0 || double.IsNaN(value))
            return height;

        double y = height - value / max * height;

        if (y < 0) return 0;
        if (y > height) return height;
        return y;
    }

    private static string FormatRate(double bytesPerSec)
    {
        if (double.IsNaN(bytesPerSec) || bytesPerSec < 0)
            return "—";

        return FormatBytes((ulong)bytesPerSec) + "/s";
    }

    private static string FormatBytes(ulong bytes)
    {
        if (bytes >= 1UL << 30)
            return $"{bytes / (double)(1UL << 30):0.0} GB";

        if (bytes >= 1UL << 20)
            return $"{bytes / (double)(1UL << 20):0.0} MB";

        if (bytes >= 1UL << 10)
            return $"{bytes / 1024.0:0.0} KB";

        return $"{bytes} B";
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        Deactivate();
        _disposed = true;
        _uiTimer.Tick -= OnUiTick;

        // Join 超时时采集线程仍可能碰 _stop / 非托管缓冲；宁可一次性泄漏。
        if (_collector is null)
            _stop.Dispose();
    }
}


