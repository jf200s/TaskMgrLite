using System.Collections.Concurrent;
using System.Windows.Threading;

namespace TaskMgrLite.Core;

/// <summary>
/// 详情面板的路径/用户名懒加载服务。
/// 和 CommandLineService 同构：UI 请求、单后台 worker、有界队列、去重、LRU、负结果缓存。
/// 只在选中行时触发，不进入 1Hz 采集/渲染热路径。
/// </summary>
public sealed class ProcessDetailsService : IDisposable
{
    private const int CacheCapacity = 256;
    private const int QueueCapacity = 64;

    private readonly Dispatcher _dispatcher;
    private readonly Func<ProcKey, bool> _isAlive;
    private readonly Action<ProcKey, ProcessDetailsResult> _onResolved;

    private readonly LruCache<ProcKey, ProcessDetailsResult> _cache = new(CacheCapacity);
    private readonly HashSet<ProcKey> _pending = new();
    private readonly BlockingCollection<ProcKey> _queue =
        new(new ConcurrentQueue<ProcKey>(), QueueCapacity);

    private readonly Thread _worker;
    private volatile bool _disposed;

    public ProcessDetailsService(
        Dispatcher dispatcher,
        Func<ProcKey, bool> isAlive,
        Action<ProcKey, ProcessDetailsResult> onResolved)
    {
        _dispatcher = dispatcher;
        _isAlive = isAlive;
        _onResolved = onResolved;

        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "TaskMgrLite.Details",
            Priority = ThreadPriority.BelowNormal
        };
        _worker.Start();
    }

    public void Request(ProcKey key)
    {
        if (_disposed || !_isAlive(key)) return;

        if (_cache.TryGet(key, out ProcessDetailsResult cached))
        {
            _onResolved(key, cached);
            return;
        }

        if (!_pending.Add(key)) return;

        if (!_queue.TryAdd(key))
            _pending.Remove(key);
    }

    /// <summary>用户动作直接读到结果后回填缓存，避免紧接着的选中请求重复读。</summary>
    public void Remember(ProcKey key, ProcessDetailsResult result)
    {
        if (_disposed || !_isAlive(key)) return;

        _cache.Set(key, result);
        _onResolved(key, result);
    }

    public bool TryGetCached(ProcKey key, out ProcessDetailsResult result) =>
        _cache.TryGet(key, out result);

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

            ProcessDetailsResult result;
            try
            {
                result = ProcessDetailsReader.Read(key);
            }
            catch
            {
                result = new ProcessDetailsResult(
                    null, ProcessInfoStatus.Error,
                    null, ProcessInfoStatus.Error);
            }

            try
            {
                _dispatcher.BeginInvoke(
                    DispatcherPriority.Background,
                    new Action(() => Complete(key, result)));
            }
            catch
            {
                break;
            }
        }
    }

    private void Complete(ProcKey key, ProcessDetailsResult result)
    {
        _pending.Remove(key);

        if (_disposed || !_isAlive(key)) return;

        _cache.Set(key, result);
        _onResolved(key, result);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _queue.CompleteAdding(); } catch { }

        if (_worker.IsAlive)
            _worker.Join(500);

        if (!_worker.IsAlive)
        {
            try { _queue.Dispose(); } catch { }
        }
    }
}
