using System.Collections.Concurrent;

namespace PathfindingFix;

// A full shortest-path tree from one source system for one jump range: cost with the hop penalty (what the search minimised), the pure travel time along that route
// (what the game's CalculateSystemTimes returns) and the predecessor of every system, all indexed by system id. Immutable once built, except for Seq and LastUse.
internal sealed class Tree
{
    public int Token, RangeBits, Source;
    public long Seq;
    public long LastUse;
    public float[] Cost = Array.Empty<float>();
    public float[] Pure = Array.Empty<float>();
    public int[] Prev = Array.Empty<int>();

    public long Bytes => 16 + (long)Cost.Length * 4 + (long)Pure.Length * 4 + (long)Prev.Length * 4;
    public float Range => BitConverter.Int32BitsToSingle(RangeBits);
}

// Trees by (galaxy token, jump range, source). Bounded by a byte budget with least-recently-used replacement; nothing is ever dropped wholesale: a new galaxy simply has a new token
// and its trees gradually push the old galaxy's out, and a tree made stale by a neighbour-list change is rebuilt when it is next asked for (ChangeLog).
internal static class TreeCache
{
    private static readonly ConcurrentDictionary<(int Token, int RangeBits, int Source), Tree> Trees = new();
    private static readonly object EvictGate = new();
    private static long _bytes, _tick;

    public static int Count => Trees.Count;
    public static long Bytes => Interlocked.Read(ref _bytes);

    // The tree if one is cached and still exact; a stale one is removed.
    public static Tree? TryGet(int token, int rangeBits, int source, float penaltyFraction)
    {
        if (!Trees.TryGetValue((token, rangeBits, source), out Tree? tree))
        {
            return null;
        }
        float range = tree.Range;
        if (!ChangeLog.Validate(tree, range, penaltyFraction * range, out string reason))
        {
            if (Trees.TryRemove(new KeyValuePair<(int, int, int), Tree>((token, rangeBits, source), tree)))
            {
                Interlocked.Add(ref _bytes, -tree.Bytes);
            }
            Stats.Add(ref Stats.TreeInvalidated);
            if (Settings.Current.LogCacheChanges)
            {
                Log.Info($"Tree of system {source} (range {range / 1e6f:0.#}M) rebuilt: {reason}");
            }
            return null;
        }
        tree.LastUse = Interlocked.Increment(ref _tick);
        return tree;
    }

    public static void Add(Tree tree)
    {
        tree.LastUse = Interlocked.Increment(ref _tick);
        var key = (tree.Token, tree.RangeBits, tree.Source);
        if (Trees.TryGetValue(key, out Tree? old))
        {
            Interlocked.Add(ref _bytes, -old.Bytes);
        }
        Trees[key] = tree;
        long bytes = Interlocked.Add(ref _bytes, tree.Bytes);
        long budget = (long)Settings.Current.CacheBudgetMb * 1048576;
        if (bytes > budget)
        {
            Evict(budget);
        }
    }

    private static void Evict(long budget)
    {
        lock (EvictGate)
        {
            while (Interlocked.Read(ref _bytes) > budget && !Trees.IsEmpty)
            {
                (int, int, int) oldestKey = default;
                long min = long.MaxValue;
                foreach (var kv in Trees)
                {
                    if (kv.Value.LastUse < min)
                    {
                        min = kv.Value.LastUse;
                        oldestKey = kv.Key;
                    }
                }
                if (Trees.TryRemove(oldestKey, out Tree? gone))
                {
                    Interlocked.Add(ref _bytes, -gone.Bytes);
                    Stats.Add(ref Stats.TreeEvicted);
                }
                else
                {
                    break;
                }
            }
        }
    }
}
