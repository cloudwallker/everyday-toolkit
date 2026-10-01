namespace EverydayToolkit.Core;

// Access is serialized by ContentStore. Weights use the existing encrypted payload quota;
// UTF-16 text takes at most twice its UTF-8 payload, plus entry-count-bounded object overhead.
internal sealed class ResidentCache<T>(Func<int> maximumCount, Func<long> maximumBytes)
{
    private sealed record Item(T Value, long Bytes, LinkedListNode<Guid> Node);
    private readonly Dictionary<Guid, Item> entries = [];
    private readonly LinkedList<Guid> order = [];
    private long bytes;
    internal T GetOrAdd(Guid id, long weight, Func<T> loader, Func<T, bool>? valid = null)
    {
        Trim();
        if (entries.TryGetValue(id, out var item))
        {
            if (valid is null || valid(item.Value))
            { order.Remove(item.Node); order.AddLast(item.Node); return item.Value; }
            Remove(id);
        }
        var value = loader();
        var countLimit = Math.Max(0, maximumCount()); var byteLimit = Math.Max(0, maximumBytes());
        if (countLimit == 0 || weight > byteLimit) return value;
        while (entries.Count >= countLimit || bytes > byteLimit - weight) Remove(order.First!.Value);
        entries.Add(id, new(value, weight, order.AddLast(id))); bytes += weight;
        return value;
    }
    internal void Remove(Guid id)
    {
        if (!entries.Remove(id, out var item)) return;
        bytes -= item.Bytes; order.Remove(item.Node);
    }
    internal void Retain(HashSet<Guid> existing)
    {
        foreach (var id in entries.Keys.Where(id => !existing.Contains(id)).ToArray()) Remove(id);
        Trim();
    }
    internal bool IsEmpty => entries.Count == 0;
    internal void Clear() { entries.Clear(); order.Clear(); bytes = 0; }
    private void Trim()
    {
        var countLimit = Math.Max(0, maximumCount()); var byteLimit = Math.Max(0, maximumBytes());
        while (entries.Count > countLimit || bytes > byteLimit) Remove(order.First!.Value);
    }
}
