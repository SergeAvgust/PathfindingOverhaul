namespace PathfindingFix;

// What really changed in the game's neighbour lists, in order. A cached tree remembers the sequence number it was built at and is checked lazily against the entries added since
// (never dropped just because some list was rewritten): it stays valid unless a changed link was one of its own edges, or a new or cheaper link now gives some system a shorter route.
// Entries are directed like the lists themselves: (U, V) is the link in U's list.
internal static class ChangeLog
{
    private struct Entry
    {
        public int Token, U, V;
        public float OldDistance, OldTime, NewDistance, NewTime; // time NaN: no such link
    }

    private const int Capacity = 400_000;
    private const int MaxCheck = 20_000;
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static long _firstSeq = 1;
    private static long _seq;

    public static long Seq => Volatile.Read(ref _seq);

    public static void Append(int token, int u, int v, float oldDistance, float oldTime, float newDistance, float newTime)
    {
        lock (Gate)
        {
            if (Entries.Count >= Capacity)
            {
                int drop = Capacity / 2;
                Entries.RemoveRange(0, drop);
                _firstSeq += drop;
            }
            Entries.Add(new Entry { Token = token, U = u, V = v, OldDistance = oldDistance, OldTime = oldTime, NewDistance = newDistance, NewTime = newTime });
            Volatile.Write(ref _seq, _seq + 1);
        }
    }

    // True when the tree is still exact. A valid tree is moved forward to the current sequence number so that the next lookup is free.
    public static bool Validate(Tree tree, float range, float penalty, out string reason)
    {
        reason = "";
        long now = Seq;
        if (tree.Seq >= now)
        {
            return true;
        }
        lock (Gate)
        {
            now = _seq;
            long index = tree.Seq + 1 - _firstSeq;
            if (index < 0)
            {
                reason = "change log trimmed past the tree";
                return false;
            }
            if (Entries.Count - index > MaxCheck)
            {
                reason = $"{Entries.Count - index} changes since the tree was built";
                return false;
            }
            for (long i = index; i < Entries.Count; i++)
            {
                Entry e = Entries[(int)i];
                if (e.Token != tree.Token)
                {
                    continue;
                }
                if (Affects(e, tree, range, penalty, out reason))
                {
                    return false;
                }
            }
        }
        tree.Seq = now;
        return true;
    }

    private static bool Affects(Entry e, Tree tree, float range, float penalty, out string reason)
    {
        reason = "";
        if ((uint)e.U >= (uint)tree.Prev.Length || (uint)e.V >= (uint)tree.Prev.Length)
        {
            reason = $"link {e.U}->{e.V} outside the tree";
            return true;
        }
        bool wasThere = !float.IsNaN(e.OldTime) && e.OldDistance <= range;
        bool isThere = !float.IsNaN(e.NewTime) && e.NewDistance <= range;
        if (!wasThere && !isThere)
        {
            return false;
        }
        if (wasThere && isThere && e.OldTime == e.NewTime)
        {
            return false;
        }
        if (wasThere && tree.Prev[e.V] == e.U)
        {
            reason = $"tree link {e.U}->{e.V} {(isThere ? $"changed {e.OldTime:0.#} -> {e.NewTime:0.#}" : "removed")}";
            return true;
        }
        if (isThere)
        {
            float atU = tree.Cost[e.U];
            if (!float.IsInfinity(atU) && atU + e.NewTime + penalty < tree.Cost[e.V] * (1f - 1e-6f))
            {
                reason = $"link {e.U}->{e.V} ({(wasThere ? "cheaper" : "new")}: {e.NewTime:0.#}) now beats the tree";
                return true;
            }
        }
        return false;
    }
}
