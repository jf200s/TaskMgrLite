namespace TaskMgrLite.Core;

/// <summary>
/// 固定容量 LRU。命令行缓存用：最多 512 条，命中时把节点移到表头，超容量淘汰表尾。
/// 只在 UI 线程访问，不加锁。
/// </summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly record struct Entry(TKey Key, TValue Value);

    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map;
    private readonly LinkedList<Entry> _order = new();

    public LruCache(int capacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        _capacity = capacity;
        _map = new Dictionary<TKey, LinkedListNode<Entry>>(capacity);
    }

    public int Count => _map.Count;
    public int Capacity => _capacity;

    public bool TryGet(TKey key, out TValue value)
    {
        if (!_map.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            value = default!;
            return false;
        }

        if (node.Previous is not null)
        {
            _order.Remove(node);
            _order.AddFirst(node);
        }

        value = node.Value.Value;
        return true;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
        {
            node.Value = new Entry(key, value);
            if (node.Previous is not null)
            {
                _order.Remove(node);
                _order.AddFirst(node);
            }
            return;
        }

        LinkedListNode<Entry> added = _order.AddFirst(new Entry(key, value));
        _map[key] = added;

        if (_map.Count <= _capacity) return;

        LinkedListNode<Entry>? last = _order.Last;
        if (last is null) return;

        _order.RemoveLast();
        _map.Remove(last.Value.Key);
    }

    public bool Remove(TKey key)
    {
        if (!_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            return false;

        _map.Remove(key);
        _order.Remove(node);
        return true;
    }

    public void Clear()
    {
        _map.Clear();
        _order.Clear();
    }
}
