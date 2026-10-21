using System.Collections.Concurrent;
using DistantWorlds.Types;

namespace PathfindingFix;

// The exact search, directly on the game's own neighbour lists (StarSystem.SystemDistances: per system the links to every system within its maximum range, sorted by distance,
// with the distance and the travel time of each link). A search for a jump range simply stops reading a list at the first link longer than the range, so every range works with no setup.
//   - cost of a link = its travel time + a hop penalty (a fraction of the jump range): the cheapest route by the game's own time, and of nearly equal routes the one with fewer jumps;
//   - Tree: Dijkstra from one source to every system (answers CalculateSystemTimes, and any FindPath from that source once cached);
//   - FindPath without a cached tree: A* with the straight-line distance as the estimate. That is exact when no link is faster than its own length (checked per change sequence);
//     otherwise a plain Dijkstra stops at the destination;
//   - a search between two systems that no chain of links connects fails at once (connected groups per jump range, recomputed when the lists change).
// Scratch arrays are per thread and marked with a stamp, so a search allocates nothing but its result.
internal static class PathEngine
{
    private sealed class Scratch
    {
        public float[] Cost = Array.Empty<float>();
        public float[] Pure = Array.Empty<float>();
        public int[] Prev = Array.Empty<int>();
        public int[] Seen = Array.Empty<int>();
        public int[] Closed = Array.Empty<int>();
        public int Stamp;
        public int[] HeapNode = new int[1024];
        public float[] HeapKey = new float[1024];
        public int HeapCount;
    }

    [ThreadStatic] private static Scratch? _scratch;

    private static Scratch Begin(int size)
    {
        Scratch s = _scratch ??= new Scratch();
        if (s.Cost.Length < size)
        {
            s.Cost = new float[size];
            s.Pure = new float[size];
            s.Prev = new int[size];
            s.Seen = new int[size];
            s.Closed = new int[size];
            s.Stamp = 0;
        }
        if (++s.Stamp == int.MaxValue)
        {
            Array.Clear(s.Seen);
            Array.Clear(s.Closed);
            s.Stamp = 1;
        }
        s.HeapCount = 0;
        return s;
    }

    private static void Push(Scratch s, int node, float key)
    {
        if (s.HeapCount == s.HeapNode.Length)
        {
            Array.Resize(ref s.HeapNode, s.HeapCount * 2);
            Array.Resize(ref s.HeapKey, s.HeapCount * 2);
        }
        int i = s.HeapCount++;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (s.HeapKey[parent] <= key)
            {
                break;
            }
            s.HeapNode[i] = s.HeapNode[parent];
            s.HeapKey[i] = s.HeapKey[parent];
            i = parent;
        }
        s.HeapNode[i] = node;
        s.HeapKey[i] = key;
    }

    private static int Pop(Scratch s)
    {
        int top = s.HeapNode[0];
        int count = --s.HeapCount;
        if (count > 0)
        {
            int node = s.HeapNode[count];
            float key = s.HeapKey[count];
            int i = 0;
            while (true)
            {
                int child = 2 * i + 1;
                if (child >= count)
                {
                    break;
                }
                if (child + 1 < count && s.HeapKey[child + 1] < s.HeapKey[child])
                {
                    child++;
                }
                if (s.HeapKey[child] >= key)
                {
                    break;
                }
                s.HeapNode[i] = s.HeapNode[child];
                s.HeapKey[i] = s.HeapKey[child];
                i = child;
            }
            s.HeapNode[i] = node;
            s.HeapKey[i] = key;
        }
        return top;
    }

    // Search from `from`; with `to` >= 0 it stops when `to` is settled (A* if `astar`, else Dijkstra), with -1 it settles everything reachable.
    private static void Run(GalaxyView view, Scratch s, int from, int to, float range, float penalty, bool astar)
    {
        float tx = to >= 0 ? view.X[to] : 0, ty = to >= 0 ? view.Y[to] : 0;
        float Estimate(int v)
        {
            if (!astar)
            {
                return 0;
            }
            float dx = view.X[v] - tx, dy = view.Y[v] - ty;
            return MathF.Sqrt(dx * dx + dy * dy);
        }

        s.Seen[from] = s.Stamp;
        s.Cost[from] = 0;
        s.Pure[from] = 0;
        s.Prev[from] = -1;
        Push(s, from, to >= 0 ? Estimate(from) : 0);
        while (s.HeapCount > 0)
        {
            int u = Pop(s);
            if (s.Closed[u] == s.Stamp)
            {
                continue;
            }
            s.Closed[u] = s.Stamp;
            if (u == to)
            {
                return;
            }
            SystemNodeValueList? links = view.ById[u]?.SystemDistances;
            if (links == null)
            {
                continue;
            }
            float c = s.Cost[u];
            int n = links.Count;
            for (int i = 0; i < n; i++)
            {
                SystemNodeValue link = links.GetByIndexDirectly(i);
                if (link.Distance > range)
                {
                    break;
                }
                int v = link.Index;
                if ((uint)v >= (uint)view.Size || view.ById[v] == null || s.Closed[v] == s.Stamp)
                {
                    continue;
                }
                float next = c + link.TimeCost + penalty;
                if (s.Seen[v] != s.Stamp)
                {
                    s.Seen[v] = s.Stamp;
                    s.Cost[v] = next;
                    s.Pure[v] = s.Pure[u] + link.TimeCost;
                    s.Prev[v] = u;
                    Push(s, v, to >= 0 ? next + Estimate(v) : next);
                }
                else if (next < s.Cost[v])
                {
                    s.Cost[v] = next;
                    s.Pure[v] = s.Pure[u] + link.TimeCost;
                    s.Prev[v] = u;
                    Push(s, v, to >= 0 ? next + Estimate(v) : next);
                }
            }
        }
    }

    // The tree from `source` for the jump range: costs and predecessors of every system; infinity and -1 for the ones no chain of links reaches.
    public static Tree BuildTree(GalaxyView view, int source, float range, float penaltyFraction)
    {
        long seq = ChangeLog.Seq;
        Scratch s = Begin(view.Size);
        Run(view, s, source, -1, range, penaltyFraction * range, astar: false);
        var tree = new Tree
        {
            Token = view.Token,
            RangeBits = BitConverter.SingleToInt32Bits(range),
            Source = source,
            Seq = seq,
            Cost = new float[view.Size],
            Pure = new float[view.Size],
            Prev = new int[view.Size],
        };
        for (int v = 0; v < view.Size; v++)
        {
            bool seen = s.Seen[v] == s.Stamp;
            tree.Cost[v] = seen ? s.Cost[v] : float.PositiveInfinity;
            tree.Pure[v] = seen ? s.Pure[v] : float.PositiveInfinity;
            tree.Prev[v] = seen ? s.Prev[v] : -1;
        }
        return tree;
    }

    // System ids from the tree's source to `to`, or an empty array when unreachable.
    public static int[] PathFromTree(Tree tree, int to)
    {
        if ((uint)to >= (uint)tree.Cost.Length || float.IsInfinity(tree.Cost[to]))
        {
            return Array.Empty<int>();
        }
        var steps = new List<int>(16);
        for (int v = to; v != -1; v = tree.Prev[v])
        {
            steps.Add(v);
            if (steps.Count > tree.Prev.Length)
            {
                return Array.Empty<int>(); // a cycle can only mean a corrupt tree
            }
        }
        steps.Reverse();
        return steps.ToArray();
    }

    // System ids from `from` to `to` without a cached tree, or an empty array when unreachable.
    public static int[] FindPath(GalaxyView view, int from, int to, float range, float penaltyFraction)
    {
        if (from == to)
        {
            return new[] { from };
        }
        GraphFacts facts = Facts(view, range);
        if (facts.Component[from] != facts.Component[to])
        {
            return Array.Empty<int>();
        }
        Stats.Add(ref Stats.AStarSearches);
        Scratch s = Begin(view.Size);
        Run(view, s, from, to, range, penaltyFraction * range, facts.TimeAtLeastDistance);
        if (s.Seen[to] != s.Stamp)
        {
            return Array.Empty<int>();
        }
        var steps = new List<int>(16);
        for (int v = to; v != -1; v = s.Prev[v])
        {
            steps.Add(v);
        }
        steps.Reverse();
        return steps.ToArray();
    }

    // ---- facts about the lists that hold until some list really changes ----

    private sealed class GraphFacts
    {
        public long Seq;
        public int[] Component = Array.Empty<int>();
        public bool TimeAtLeastDistance;
    }

    private static readonly ConcurrentDictionary<(int Token, int RangeBits), GraphFacts> FactCache = new();

    private static GraphFacts Facts(GalaxyView view, float range)
    {
        var key = (view.Token, BitConverter.SingleToInt32Bits(range));
        long seq = ChangeLog.Seq;
        if (FactCache.TryGetValue(key, out GraphFacts? facts) && facts.Seq == seq && facts.Component.Length >= view.Size)
        {
            return facts;
        }
        var parent = new int[view.Size];
        for (int i = 0; i < parent.Length; i++)
        {
            parent[i] = i;
        }
        int Find(int v)
        {
            while (parent[v] != v)
            {
                parent[v] = parent[parent[v]];
                v = parent[v];
            }
            return v;
        }
        bool atLeast = true;
        foreach (int u in view.Ids)
        {
            SystemNodeValueList? links = view.ById[u]?.SystemDistances;
            if (links == null)
            {
                continue;
            }
            for (int i = 0; i < links.Count; i++)
            {
                SystemNodeValue link = links.GetByIndexDirectly(i);
                if (link.Distance > range)
                {
                    break;
                }
                if (link.TimeCost < link.Distance * 0.9999f)
                {
                    atLeast = false;
                }
                if ((uint)link.Index < (uint)view.Size && view.ById[link.Index] != null)
                {
                    parent[Find(u)] = Find(link.Index);
                }
            }
        }
        var component = new int[view.Size];
        for (int v = 0; v < component.Length; v++)
        {
            component[v] = Find(v);
        }
        facts = new GraphFacts { Seq = seq, Component = component, TimeAtLeastDistance = atLeast };
        FactCache[key] = facts;
        return facts;
    }
}
