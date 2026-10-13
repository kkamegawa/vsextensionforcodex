namespace Codex.VisualStudio.Extension;

/// <summary>
/// An insertion-ordered map that evicts its oldest entries once it exceeds a fixed capacity.
/// Removal is O(1), so entries retired out of order never need compaction.
/// </summary>
internal sealed class BoundedOrderedMap<TKey, TValue>
    where TKey : notnull
{
    private readonly int capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> nodes;
    private readonly LinkedList<KeyValuePair<TKey, TValue>> order = new();

    public BoundedOrderedMap(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        nodes = new Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>>(comparer);
    }

    public int Count => nodes.Count;

    /// <summary>Entries from oldest to newest.</summary>
    public IEnumerable<KeyValuePair<TKey, TValue>> Entries => order;

    public bool ContainsKey(TKey key) => nodes.ContainsKey(key);

    public bool TryGetValue(TKey key, out TValue? value)
    {
        if (nodes.TryGetValue(key, out LinkedListNode<KeyValuePair<TKey, TValue>>? node))
        {
            value = node.Value.Value;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>Adds a new entry as the newest one; an existing key is left unchanged.</summary>
    public bool TryAdd(TKey key, TValue value)
    {
        if (nodes.ContainsKey(key))
        {
            return false;
        }

        nodes.Add(key, order.AddLast(new KeyValuePair<TKey, TValue>(key, value)));
        while (nodes.Count > capacity && order.First is { } oldest)
        {
            order.RemoveFirst();
            nodes.Remove(oldest.Value.Key);
        }

        return true;
    }

    public bool Remove(TKey key) => Remove(key, out _);

    public bool Remove(TKey key, out TValue? value)
    {
        if (!nodes.Remove(key, out LinkedListNode<KeyValuePair<TKey, TValue>>? node))
        {
            value = default;
            return false;
        }

        order.Remove(node);
        value = node.Value.Value;
        return true;
    }

    public void Clear()
    {
        nodes.Clear();
        order.Clear();
    }
}

/// <summary>An insertion-ordered set that evicts its oldest members beyond a fixed capacity.</summary>
internal sealed class BoundedOrderedSet<T>
    where T : notnull
{
    private readonly BoundedOrderedMap<T, bool> map;

    public BoundedOrderedSet(int capacity, IEqualityComparer<T>? comparer = null)
        => map = new BoundedOrderedMap<T, bool>(capacity, comparer);

    public int Count => map.Count;

    public bool Add(T item) => map.TryAdd(item, true);

    public bool Contains(T item) => map.ContainsKey(item);

    public bool Remove(T item) => map.Remove(item);

    public void Clear() => map.Clear();
}
