using System.Collections.Concurrent;
using System.Windows.Threading;

namespace TaskMgrLite.Core;

/// <summary>
/// 命令行懒加载服务。
///
/// 线程模型：
///   - UI 线程只做 Request / 缓存命中 / 结果应用；
///   - 单个后台 worker 串行读命令行，避免 OpenProcess 卡住 UI；
///   - 结果通过 Dispatcher 回到 UI 线程。
///
/// 有界队列 + pending 集合保证滚动时不会排出一万条重复请求；
/// LRU 512 保证缓存不会随进程 churn 无限增长。
/// </summary>
public sealed class CommandLineService : IDisposable
{
    private const int CacheCapacity = 512;
    private const int QueueCapacity = 256;

    private readonly Dispatcher _dispatcher;
    private readonly Func<ProcKey, bool> _isAlive;
    private readonly Action<ProcKey, CommandLineResult> _onResolved;

    private readonly CommandLineCache _cache = new(CacheCapacity);
    private readonly HashSet<ProcKey> _pending = new();
    private readonly BlockingCollection<ProcKey> _queue =
        new(new ConcurrentQueue<ProcKey>(), QueueCapacity);

    private readonly CommandLineReader _reader = new();
    private readonly Thread _worker;

    private volatile bool _disposed;

    public CommandLineService(
        Dispatcher dispatcher,
        Func<ProcKey, bool> isAlive,
        Action<ProcKey, CommandLineResult> onResolved)
    {
        _dispatcher = dispatcher;
        _isAlive = isAlive;
        _onResolved = onResolved;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "TaskMgrLite.CommandLine",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    public int CacheCount => _cache.Count;
    public int PendingCount => _pending.Count;

    /// <summary>UI 线程调用。缓存命中时同步回调，未命中时排队。</summary>
    public void Request(ProcKey key)
    {
        if (_disposed || !_isAlive(key)) return;

        if (_cache.TryGet(key, Environment.TickCount64, out CommandLineResult cached))
        {
            _onResolved(key, cached);
            return;
        }

        if (!_pending.Add(key)) return;

        if (!_queue.TryAdd(key))
            _pending.Remove(key);
    }

    /// <summary>进程退出 / PID 复用后清缓存。worker 里尚未返回的请求会被 isAlive 丢掉。</summary>
    public void Forget(ProcKey key)
    {
        _cache.Remove(key);
        _pending.Remove(key);
    }

    private void WorkerLoop()
    {
        foreach (ProcKey key in _queue.GetConsumingEnumerable())
        {
            if (_disposed) break;

            CommandLineResult result;
            try
            {
                result = _reader.Read(key);
            }
            catch
            {
                result = new CommandLineResult(null, CommandLineStatus.Error);
            }

            try
            {
                _dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => Complete(key, result)));
            }
            catch
            {
                // Dispatcher 已关闭：直接退出
                break;
            }
        }
    }

    private void Complete(ProcKey key, CommandLineResult result)
    {
        _pending.Remove(key);

        // 进程可能在读取期间退出；不要给已删除的 key 写缓存或回调
        if (_disposed || !_isAlive(key)) return;

        _cache.Set(key, result, Environment.TickCount64);
        _onResolved(key, result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _queue.CompleteAdding(); } catch { }

        if (_worker.IsAlive)
            _worker.Join(500);

        // worker 可能卡在 OpenProcess/NtQueryInformationProcess；它是后台线程，
        // 不在退出路径上强杀。只有确认已退出才释放非托管缓冲。
        if (!_worker.IsAlive)
        {
            try { _queue.Dispose(); } catch { }
            _reader.Dispose();
        }
    }
}
