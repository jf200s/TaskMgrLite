namespace TaskMgrLite.Core;

/// <summary>
/// 命令行缓存：成功结果永久，AccessDenied / Error 30 秒后惰性过期。
/// 只在 UI 线程访问；没有定时器，也没有后台清扫。
/// </summary>
public sealed class CommandLineCache
{
    public const int NegativeTtlMs = 30_000;

    private readonly record struct Entry(CommandLineResult Result, long StoredAtMs);

    private readonly LruCache<ProcKey, Entry> _cache;

    public CommandLineCache(int capacity = 512)
    {
        _cache = new LruCache<ProcKey, Entry>(capacity);
    }

    public int Count => _cache.Count;

    public bool TryGet(ProcKey key, long nowMs, out CommandLineResult result)
    {
        if (!_cache.TryGet(key, out Entry entry))
        {
            result = default;
            return false;
        }

        long age = nowMs - entry.StoredAtMs;
        if (age < 0) age = 0;

        if (IsExpired(entry.Result, age))
        {
            _cache.Remove(key);
            result = default;
            return false;
        }

        result = entry.Result;
        return true;
    }

    public void Set(ProcKey key, CommandLineResult result, long nowMs) =>
        _cache.Set(key, new Entry(result, nowMs));

    public void Remove(ProcKey key) => _cache.Remove(key);

    public static bool IsExpired(CommandLineResult result, long ageMs) =>
        ageMs >= NegativeTtlMs &&
        result.Status is CommandLineStatus.AccessDenied or CommandLineStatus.Error;
}
