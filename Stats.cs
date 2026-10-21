using System.Diagnostics;

namespace PathfindingFix;

// Cheap counters (a few interlocked additions per call) and the periodic summary line of the LogEnabled setting.
internal static class Stats
{
    public static long FindPathCalls, FindPathFailed, FindPathSameSystem, FindPathTicks;
    public static long TimesCalls, TimesTicks;
    public static long TreeHits, TreeBuilds, TreeInvalidated, TreeEvicted, AStarSearches;
    public static long Fallbacks, Errors;
    public static long ListRewrites, ListIdentical, ListChanged, LinkChanges;

    private static long[] _last = new long[16];

    public static void Add(ref long counter, long n = 1) => Interlocked.Add(ref counter, n);

    private static long[] Snapshot() => new[]
    {
        FindPathCalls, FindPathFailed, FindPathSameSystem, FindPathTicks, TimesCalls, TimesTicks, TreeHits, TreeBuilds, TreeInvalidated, TreeEvicted, AStarSearches, Fallbacks, Errors,
        ListRewrites, ListIdentical, ListChanged,
    };


    // One line with what happened since the previous call.
    public static string Summary()
    {
        long[] now = Snapshot();
        long[] d = new long[now.Length];
        for (int i = 0; i < now.Length; i++)
        {
            d[i] = now[i] - _last[i];
        }
        _last = now;
        double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
        return $"FindPath {d[0]} calls ({d[1]} found no path, {d[2]} same system, {Ms(d[3]):0.0} ms, {(d[0] == 0 ? 0 : Ms(d[3]) * 1000 / d[0]):0.0} us each); "
            + $"CalculateSystemTimes {d[4]} runs ({Ms(d[5]):0.0} ms, {(d[4] == 0 ? 0 : Ms(d[5]) / d[4]):0.00} ms each); "
            + $"trees: {d[6]} hits, {d[7]} built, {d[8]} rebuilt after a change, {d[9]} evicted, {d[10]} A* searches, cache {TreeCache.Count} trees {TreeCache.Bytes / 1048576.0:0.0} MB; "
            + $"fallbacks to the game's search {d[11]} (in total: {Replacement.FallbackReasons[0]} null system, {Replacement.FallbackReasons[1]} jump range not positive, {Replacement.FallbackReasons[2]} excluded systems, {Replacement.FallbackReasons[3]} no neighbour list, {Replacement.FallbackReasons[4]} unknown system), errors {d[12]}; neighbour lists rewritten {d[13]} ({d[14]} identical, {d[15]} changed)";
    }
}
