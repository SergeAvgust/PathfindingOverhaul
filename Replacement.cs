using DistantWorlds.Types;
using Stride.Core.Mathematics;

namespace PathfindingFix;

internal enum Outcome : byte
{
    Hit,      // answered from a cached tree
    Built,    // a tree was built (and cached) for the answer
    AStar,    // no tree for this source: single A* search
    Same,     // source and destination are the same system
    Fallback, // not handled: the game's own search runs
}

// The replacement of PathFindingSystem.FindPath and StarSystemListExtensions.CalculateSystemTimes, with the game's conventions kept:
// failure = an empty list with 1E+11 as distance and time; the same system = a one-element list; results are marked shared; the path goes through the game's own
// CheckCanSkipSystems (waypoint trimming near deep-space ends, and the totals); a direct link that is faster than the found route replaces it.
// The nebula allowance and allowReverse arguments are ignored: the search is exact over all links, the travel time already includes what nebulae cost.
// Cases that are not handled here (a null system, a non-positive jump range, systems excluded from the search, a source without a neighbour list) return false,
// and the game's own method runs.
internal static class Replacement
{
    public const float NoPath = 1E+11f;

    // Why a call was left to the game's own search: 0 null system, 1 jump range not positive, 2 excluded systems, 3 no neighbour list, 4 unknown system.
    public static readonly long[] FallbackReasons = new long[5];

    // The galaxy of the latest real search (jump range over 1M), for the diagnostics that run outside a search.
    public static Galaxy? LastGalaxy;

    private static bool Decline(int reason)
    {
        Interlocked.Increment(ref FallbackReasons[reason]);
        return false;
    }

    private static bool HasExcluded(bool[]? excluded)
    {
        if (excluded == null)
        {
            return false;
        }
        foreach (bool b in excluded)
        {
            if (b)
            {
                return true;
            }
        }
        return false;
    }

    private static StarSystemList NewList()
    {
        var list = new StarSystemList(false);
        list.Unshared();
        return list;
    }

    public static bool TryFindPath(Galaxy? galaxy, bool[]? excluded, StarSystem? source, StarSystem? destination, float jumpRange, Point sourceDeepSpacePoint, Point destinationDeepSpacePoint,
        out StarSystemList result, out float totalDistance, out float totalTime, out Outcome outcome)
    {
        result = null!;
        totalDistance = 0;
        totalTime = 0;
        outcome = Outcome.Fallback;
        if (galaxy == null || source == null || destination == null)
        {
            return Decline(0);
        }
        if (!(jumpRange > 0f))
        {
            return Decline(1);
        }
        if (HasExcluded(excluded))
        {
            return Decline(2);
        }
        int from = source.SystemId, to = destination.SystemId;
        if (from == to)
        {
            result = NewList();
            result.Add(source);
            result.Shared();
            outcome = Outcome.Same;
            return true;
        }
        GalaxyView view = GalaxyView.For(galaxy);
        if (jumpRange > 1e6f)
        {
            LastGalaxy = galaxy;
        }
        SystemNodeValueList? links = source.SystemDistances;
        if (links == null || links.Count == 0)
        {
            return Decline(3);
        }
        if ((uint)from >= (uint)view.Size || (uint)to >= (uint)view.Size || view.ById[from] == null || view.ById[to] == null)
        {
            return Decline(4);
        }

        float directTime = 0, directDistance = 0;
        SystemNodeValue direct = links.GetById(to);
        if (!direct.IsEmpty && direct.Distance <= jumpRange)
        {
            directTime = direct.TimeCost;
            directDistance = direct.Distance;
        }

        Settings settings = Settings.Current;
        float penalty = settings.HopPenaltyFraction;
        int[] path;
        Tree? tree = TreeCache.TryGet(view.Token, BitConverter.SingleToInt32Bits(jumpRange), from, penalty);
        if (tree != null)
        {
            Stats.Add(ref Stats.TreeHits);
            outcome = Outcome.Hit;
            path = PathEngine.PathFromTree(tree, to);
        }
        else
        {
            outcome = Outcome.AStar;
            path = PathEngine.FindPath(view, from, to, jumpRange, penalty);
        }

        // A ship in open space is not at its source system: that is only the nearest location's system, often behind the ship. Search from the ship's own position instead, so that its first jump
        // can go straight to any system within range (the game's trimming of the start only reaches systems within 62.5M of the ship, hence the backtracking of vanilla routes).
        // The direct-link rule below compares routes that start at the source system, which is not fair to a route from the ship, so it is dropped then.
        if (sourceDeepSpacePoint != Point.Zero && settings.RouteFromShipPosition)
        {
            int[] fromShip = PathEngine.FindPathFromPoint(view, galaxy, sourceDeepSpacePoint.X, sourceDeepSpacePoint.Y, to, jumpRange, penalty);
            if (fromShip.Length > 0)
            {
                path = fromShip;
                directTime = 0;
            }
        }

        if (path.Length == 0)
        {
            result = NewList();
            result.Shared();
            totalDistance = NoPath;
            totalTime = NoPath;
            return true;
        }

        StarSystemList list = NewList();
        foreach (int id in path)
        {
            list.Add(view.ById[id]!);
        }
        StarSystemList? skipped = PathFindingSystem.CheckCanSkipSystems(galaxy, list, jumpRange, ref totalDistance, ref totalTime, sourceDeepSpacePoint, destinationDeepSpacePoint);
        if (skipped != null)
        {
            list = skipped;
        }
        if (directTime > 0 && directTime < totalTime)
        {
            list = NewList();
            list.Add(source);
            list.Add(destination);
            totalTime = directTime;
            totalDistance = directDistance;
        }
        if (!list.IsShared)
        {
            list.Shared();
        }
        result = list;
        return true;
    }

    // The travel time to every system the start system can reach with the jump range, the start itself with 0 (what the game's method returns).
    public static bool TryTimes(Galaxy? galaxy, bool[]? excluded, StarSystem? start, float jumpRange, out Dictionary<int, float> result, out Outcome outcome)
    {
        result = null!;
        outcome = Outcome.Fallback;
        if (galaxy == null || start == null)
        {
            return Decline(0);
        }
        if (!(jumpRange > 0f))
        {
            return Decline(1);
        }
        if (HasExcluded(excluded))
        {
            return Decline(2);
        }
        GalaxyView view = GalaxyView.For(galaxy);
        int from = start.SystemId;
        SystemNodeValueList? links = start.SystemDistances;
        if (links == null || links.Count == 0)
        {
            return Decline(3);
        }
        if ((uint)from >= (uint)view.Size || view.ById[from] == null)
        {
            return Decline(4);
        }
        float penalty = Settings.Current.HopPenaltyFraction;
        int bits = BitConverter.SingleToInt32Bits(jumpRange);
        Tree? tree = TreeCache.TryGet(view.Token, bits, from, penalty);
        if (tree != null)
        {
            Stats.Add(ref Stats.TreeHits);
            outcome = Outcome.Hit;
        }
        else
        {
            tree = PathEngine.BuildTree(view, from, jumpRange, penalty);
            TreeCache.Add(tree);
            Stats.Add(ref Stats.TreeBuilds);
            outcome = Outcome.Built;
        }
        result = new Dictionary<int, float>(view.Count);
        foreach (int id in view.Ids)
        {
            if (!float.IsInfinity(tree.Cost[id]))
            {
                result[id] = tree.Pure[id];
            }
        }
        return true;
    }
}
