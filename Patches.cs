using System.Diagnostics;
using System.Reflection;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace PathfindingFix;

// The Harmony hooks. Parameter names must be those of the game's methods.
//   replace mode: prefixes that answer FindPath and CalculateSystemTimes themselves (return false) or leave the call to the game (return true) for the cases Replacement does not handle;
//   compare mode: the game answers, a finalizer runs the replacement beside it and records both (Compare);
//   both: a prefix and postfix on StarSystem.CalculateSystemDistances that turn real changes of a system's neighbour list into change-log entries (ChangeLog).
internal static class Patches
{
    private const BindingFlags Declared = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    [ThreadStatic] private static int _depth;
    [ThreadStatic] private static bool _inTimes;
    private static long _errorsLogged, _changesLogged, _identicalLogged;

    public static bool Install(Harmony harmony, Settings settings)
    {
        MethodInfo? find = typeof(PathFindingSystem).GetMethods(Declared).FirstOrDefault(m => m.Name == "FindPath" && m.GetParameters().Any(p => p.Name == "allowReverse"));
        MethodInfo? times = typeof(StarSystemListExtensions).GetMethods(Declared).FirstOrDefault(m => m.Name == "CalculateSystemTimes" && m.GetParameters().Any(p => p.Name == "systemsToExclude"));
        MethodInfo? distances = typeof(StarSystem).GetMethod("CalculateSystemDistances", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        if (find == null || times == null || distances == null)
        {
            Log.Info($"ERROR: a game method was not found (FindPath {find != null}, CalculateSystemTimes {times != null}, CalculateSystemDistances {distances != null}); nothing is patched.");
            return false;
        }
        harmony.Patch(distances, prefix: Own(nameof(DistancesPrefix)), postfix: Own(nameof(DistancesPostfix)));
        if (settings.RoutePointOrders && settings.Mode == Settings.ModeReplace)
        {
            try
            {
                PointOrders.Install(harmony);
            }
            catch (Exception ex)
            {
                Log.Info("Point orders could not be installed: " + ex);
            }
        }
        if (settings.LogPathTrace)
        {
            try
            {
                OrderDiagnostics.Install(harmony);
            }
            catch (Exception ex)
            {
                Log.Info("Order diagnostics could not be installed: " + ex.Message);
            }
        }
        if (settings.LogPathTrace || settings.RoutePointOrders)
        {
            // The method that turns two locations into a list of jump locations for a ship order; the postfix routes point orders and logs empty routes.
            MethodInfo? order = typeof(Empire).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                .FirstOrDefault(m => m.Name == "FindBestJumpPathToDestination" && m.GetParameters().Any(p => p.Name == "startPoint") && m.GetParameters().Any(p => p.Name == "destinationLocation")
                                     && m.GetParameters().Any(p => p.Name == "jumpRangeOverride"));
            if (order != null)
            {
                harmony.Patch(order, postfix: Own(nameof(OrderPostfix)));
            }
            else
            {
                Log.Info("The order route method was not found; orders are not traced.");
            }
        }
        if (settings.Mode == Settings.ModeCompare)
        {
            harmony.Patch(find, prefix: Own(nameof(CompareFindPrefix)), finalizer: Own(nameof(CompareFindFinalizer)));
            harmony.Patch(times, prefix: Own(nameof(CompareTimesPrefix)), finalizer: Own(nameof(CompareTimesFinalizer)));
        }
        else
        {
            harmony.Patch(find, prefix: Own(nameof(FindPathPrefix)));
            harmony.Patch(times, prefix: Own(nameof(TimesPrefix)));
        }
        return true;
    }

    private static HarmonyMethod Own(string name) => new(typeof(Patches).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic));

    // ---- path trace (LogPathTrace + a file named trace.now): every FindPath call of a short window, to see what an order does ----

    // The last TraceRing calls, in memory (cheap: one string per call); trace.now writes the recent ones to the log, so the order can be given first and the file created after it.
    private const int TraceRing = 1000;
    private static readonly (long Ticks, string Line)[] Ring = new (long, string)[TraceRing];
    private static long _ringPos;

    private static bool Tracing => Settings.Current.LogPathTrace;

    public static void DumpTrace(int seconds, int maxLines)
    {
        long end = Interlocked.Read(ref _ringPos), from = Math.Max(0, end - TraceRing);
        long limit = Stopwatch.GetTimestamp() - seconds * Stopwatch.Frequency;
        var lines = new List<string>();
        for (long i = from; i < end; i++)
        {
            (long ticks, string line) = Ring[i % TraceRing];
            if (line != null && ticks >= limit)
            {
                lines.Add(line);
            }
        }
        if (lines.Count > maxLines)
        {
            lines.RemoveRange(0, lines.Count - maxLines);
        }
        Log.Info($"Path trace: the last {lines.Count} FindPath calls of the last {seconds} s, oldest first.");
        foreach (string line in lines)
        {
            Log.Info(line);
        }
    }

    private static string Ids(StarSystemList? list)
    {
        if (list == null)
        {
            return "null";
        }
        if (list.Count == 0)
        {
            return "no path";
        }
        var ids = new List<string>();
        for (int i = 0; i < Math.Min(list.Count, 40); i++)
        {
            ids.Add(list[i]?.SystemId.ToString() ?? "?");
        }
        return string.Join(">", ids) + (list.Count > 40 ? ">..." : "");
    }

    private static string Place(StarSystem? s) => s == null ? "null" : $"{s.SystemId}({s.GalaxyX / 1000000},{s.GalaxyY / 1000000})";

    private static void Trace(Galaxy? galaxy, StarSystem? source, StarSystem? destination, float jumpRange, Point sourceDeep, Point destinationDeep, string ours, string game)
    {
        string straight = source != null && destination != null
            ? $"{Math.Sqrt(Math.Pow(source.GalaxyX - destination.GalaxyX, 2) + Math.Pow(source.GalaxyY - destination.GalaxyY, 2)) / 1e6:0.#}M apart"
            : "";
        string line = $"Trace FindPath {Place(source)} -> {Place(destination)} ({straight}), jump range {jumpRange / 1e6:0.#}M, deep points {sourceDeep.X},{sourceDeep.Y} / {destinationDeep.X},{destinationDeep.Y}; "
            + $"replacement: {ours}; game: {game}; called by {Cause()}";
        long slot = Interlocked.Increment(ref _ringPos) - 1;
        Ring[slot % TraceRing] = (Stopwatch.GetTimestamp(), line);
    }

    private static void Error(string where, Exception ex)
    {
        Stats.Add(ref Stats.Errors);
        if (Interlocked.Increment(ref _errorsLogged) <= 5)
        {
            Log.Info($"ERROR in {where}, the game's own method runs instead: {ex}");
        }
    }

    // ---- orders (observation only) ----

    private static long _orderFailuresLogged;

    // Empire.FindBestJumpPathToDestination(startPoint, jumpRange, jumpRangeOverride, removeStartSystem, startLocation, destinationLocation, out totalDistance, out totalTime, startPointOutsideSystem, destinationPointOutsideSystem):
    // the route of a ship order as a list of location ids. When it comes back empty or null the ship goes straight at the target, so such calls are logged with what a search says about them.
    private static readonly FieldInfo? EmpireGalaxy = typeof(Empire).GetField("_Galaxy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    [ThreadStatic] private static bool _routingPointOrder;
    private static long _pointOrdersRouted;

    // An order whose destination has no Location (a point in open space) gets an empty route from the game and the ship flies a straight line, however far away the point is.
    // The game's own coordinate overload (ship, galaxyX, galaxyY) routes such orders to FindNearestLocation(x, y); this does the same for the object overload.
    // Only trips between systems are routed: inside one system the empty route (fly directly) stays.
    private static bool RoutePointOrder(Empire empire, StellarObject startPoint, float jumpRange, float jumpRangeOverride, bool removeStartSystem, Location startLocation, Location? destinationLocation,
        ref List<int>? result, ref float totalDistance, ref float totalTime, Point startDeep, Point destinationDeep)
    {
        if (_routingPointOrder || destinationLocation != null || destinationDeep == Point.Zero || startLocation == null || startPoint == null || !(jumpRange > 0f))
        {
            return false;
        }
        if (EmpireGalaxy?.GetValue(empire) is not Galaxy galaxy)
        {
            return false;
        }
        if (startPoint is Ship ship)
        {
            double dx = startPoint.GalaxyX - destinationDeep.X, dy = startPoint.GalaxyY - destinationDeep.Y;
            if (dx * dx + dy * dy < ship.CalculateHyperjumpThresholdSquared())
            {
                return false; // too close to jump: the game flies it directly
            }
        }
        Location? nearest = galaxy.Locations.FindNearestLocation(destinationDeep.X, destinationDeep.Y);
        if (nearest == null || nearest.IsTemporary || nearest.SystemId == startLocation.SystemId)
        {
            return false;
        }
        _routingPointOrder = true;
        try
        {
            List<int>? routed = empire.FindBestJumpPathToDestination(startPoint, jumpRange, jumpRangeOverride, removeStartSystem, startLocation, nearest, out float distance, out float time, startDeep, Point.Zero);
            if (routed == null || routed.Count == 0)
            {
                return false;
            }
            result = routed;
            totalDistance = distance;
            totalTime = time;
            long n = Interlocked.Increment(ref _pointOrdersRouted);
            if (n <= 30)
            {
                Log.Info($"Point order routed through the jump network: {startPoint.GetType().Name} at {startPoint.GalaxyX / 1000000},{startPoint.GalaxyY / 1000000} -> point {destinationDeep.X / 1000000},{destinationDeep.Y / 1000000}, "
                    + $"nearest location {nearest.LocationId} in system {nearest.SystemId}; route of {routed.Count} locations (order {n}).");
            }
            return true;
        }
        finally
        {
            _routingPointOrder = false;
        }
    }

    private static long _temporaryOrdersSeen, _temporaryOrdersRouted;

    // A destination that is a temporary Location (the game makes one for a click that is not exactly on a star or planet) gets a route of just that location and no search, however far away it is:
    // the ship makes one straight jump toward it, up to its jump range. When the point is out of jump range, route to the nearest permanent location first (as for a click on the star itself)
    // and keep the temporary location at the end of the route, so that the ship still goes to the point.
    private static bool RouteTemporaryDestination(Empire empire, StellarObject startPoint, float jumpRange, float jumpRangeOverride, bool removeStartSystem, Location startLocation, Location destinationLocation,
        ref List<int>? result, ref float totalDistance, ref float totalTime, Point startDeep)
    {
        if (_routingPointOrder || !destinationLocation.IsTemporary || result == null || result.Count != 1 || result[0] != destinationLocation.LocationId || startLocation == null || startPoint == null || !(jumpRange > 0f))
        {
            return false;
        }
        long seen = Interlocked.Increment(ref _temporaryOrdersSeen);
        double dx = startPoint.GalaxyX - destinationLocation.GalaxyX, dy = startPoint.GalaxyY - destinationLocation.GalaxyY;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        string who = $"{startPoint.GetType().Name} at {startPoint.GalaxyX / 1000000},{startPoint.GalaxyY / 1000000} -> temporary location {destinationLocation.LocationId} at {destinationLocation.GalaxyX / 1000000},{destinationLocation.GalaxyY / 1000000} ({distance / 1e6:0.#}M away, jump range {jumpRange / 1e6f:0.#}M)";
        if (!Settings.Current.RoutePointOrders || distance <= jumpRange || EmpireGalaxy?.GetValue(empire) is not Galaxy galaxy)
        {
            if (seen <= 40)
            {
                Log.Info($"Order to a temporary location: {who}; one direct jump, left as the game gives it.");
            }
            return false;
        }
        Location? nearest = galaxy.Locations.FindNearestLocation(destinationLocation.GalaxyX, destinationLocation.GalaxyY);
        if (nearest == null || nearest.IsTemporary || nearest.LocationId == startLocation.LocationId || nearest.SystemId == startLocation.SystemId)
        {
            if (seen <= 40)
            {
                Log.Info($"Order to a temporary location: {who}; no usable permanent location nearby ({(nearest == null ? "none" : $"location {nearest.LocationId}, system {nearest.SystemId}, temporary {nearest.IsTemporary}")}), left as the game gives it.");
            }
            return false;
        }
        _routingPointOrder = true;
        try
        {
            List<int>? routed = empire.FindBestJumpPathToDestination(startPoint, jumpRange, jumpRangeOverride, removeStartSystem, startLocation, nearest, out float routeDistance, out float routeTime, startDeep, Point.Zero);
            if (routed == null || routed.Count == 0)
            {
                if (seen <= 40)
                {
                    Log.Info($"Order to a temporary location: {who}; nearest location {nearest.LocationId} in system {nearest.SystemId} has no route either, left as the game gives it.");
                }
                return false;
            }
            double lx = nearest.GalaxyX - destinationLocation.GalaxyX, ly = nearest.GalaxyY - destinationLocation.GalaxyY;
            float leg = (float)Math.Sqrt(lx * lx + ly * ly);
            routed.Add(destinationLocation.LocationId);
            result = routed;
            totalDistance = routeDistance + leg;
            totalTime = routeTime + leg;
            long n = Interlocked.Increment(ref _temporaryOrdersRouted);
            if (n <= 40)
            {
                Log.Info($"Order to a temporary location routed through the jump network: {who}; via location {nearest.LocationId} in system {nearest.SystemId}, {routed.Count} locations (order {n}).");
            }
            return true;
        }
        finally
        {
            _routingPointOrder = false;
        }
    }

    private static void OrderPostfix(Empire __instance, StellarObject startPoint, float jumpRange, float jumpRangeOverride, bool removeStartSystem, Location startLocation, Location destinationLocation,
        ref List<int>? __result, ref float totalDistance, ref float totalTime, Point startPointOutsideSystem, Point destinationPointOutsideSystem)
    {
        try
        {
            if (destinationLocation != null && Settings.Current.RoutePointOrders
                && RouteTemporaryDestination(__instance, startPoint, jumpRange, jumpRangeOverride, removeStartSystem, startLocation, destinationLocation, ref __result, ref totalDistance, ref totalTime, startPointOutsideSystem))
            {
                return;
            }
            if (__result != null && __result.Count > 0)
            {
                return;
            }
            if (Settings.Current.RoutePointOrders && RoutePointOrder(__instance, startPoint, jumpRange, jumpRangeOverride, removeStartSystem, startLocation, destinationLocation, ref __result, ref totalDistance, ref totalTime,
                    startPointOutsideSystem, destinationPointOutsideSystem))
            {
                return;
            }
            if (!Settings.Current.LogPathTrace)
            {
                return;
            }
            if (Interlocked.Increment(ref _orderFailuresLogged) > 150)
            {
                return;
            }
            string Loc(Location? l) => l == null ? "null" : $"location {l.LocationId} in system {l.SystemId}{(l.IsTemporary ? " (temporary)" : "")} at {l.GalaxyX / 1000000},{l.GalaxyY / 1000000}";
            string text = $"Order route empty ({(__result == null ? "null" : "0 locations")}): {startPoint?.GetType().Name} at {startPoint?.GalaxyX / 1000000},{startPoint?.GalaxyY / 1000000} -> {Loc(destinationLocation)}; start {Loc(startLocation)}; "
                + $"jump range {jumpRange / 1e6f:0.#}M, override {jumpRangeOverride / 1e6f:0.#}M, deep start {startPointOutsideSystem.X},{startPointOutsideSystem.Y}, deep end {destinationPointOutsideSystem.X},{destinationPointOutsideSystem.Y}";
            Galaxy? galaxy = Replacement.LastGalaxy;
            if (galaxy != null && startLocation != null && destinationLocation != null && jumpRange > 0)
            {
                GalaxyView view = GalaxyView.For(galaxy);
                int from = startLocation.SystemId, to = destinationLocation.SystemId;
                if ((uint)from < (uint)view.Size && (uint)to < (uint)view.Size && view.ById[from] != null && view.ById[to] != null)
                {
                    float penalty = Settings.Current.HopPenaltyFraction;
                    string needed = "not reachable even with 3x the range";
                    foreach (float factor in new[] { 1f, 1.05f, 1.1f, 1.25f, 1.5f, 2f, 3f })
                    {
                        int[] path = PathEngine.FindPath(view, from, to, jumpRange * factor, penalty);
                        if (path.Length > 0)
                        {
                            needed = factor == 1f ? $"our search finds a route of {path.Length} systems at this range" : $"needs about {factor:0.##}x the range ({jumpRange * factor / 1e6f:0.#}M), then {path.Length} systems";
                            break;
                        }
                    }
                    double straight = Math.Sqrt(Math.Pow(view.X[from] - view.X[to], 2) + Math.Pow(view.Y[from] - view.Y[to], 2)) / 1e6;
                    text += $"; systems {from} -> {to} are {straight:0.#}M apart: {needed}";
                }
            }
            Log.Info(text);
            long slot = Interlocked.Increment(ref _ringPos) - 1;
            Ring[slot % TraceRing] = (Stopwatch.GetTimestamp(), text);
        }
        catch (Exception ex)
        {
            Error("order route observer", ex);
        }
    }

    // ---- replace mode ----

    private static bool FindPathPrefix(Galaxy galaxy, bool[] excludedSystems, StarSystem source, StarSystem destination, float jumpRange, Point sourceDeepSpacePoint, Point destinationDeepSpacePoint,
        ref StarSystemList __result, out float totalDistance, out float totalTime)
    {
        totalDistance = 0;
        totalTime = 0;
        try
        {
            long t0 = Log.Verbose ? Stopwatch.GetTimestamp() : 0;
            if (!Replacement.TryFindPath(galaxy, excludedSystems, source, destination, jumpRange, sourceDeepSpacePoint, destinationDeepSpacePoint, out StarSystemList list, out float distance, out float time, out Outcome outcome))
            {
                Stats.Add(ref Stats.Fallbacks);
                if (Tracing)
                {
                    Trace(galaxy, source, destination, jumpRange, sourceDeepSpacePoint, destinationDeepSpacePoint, "not handled, the game's search runs", "answers");
                }
                return true;
            }
            if (Tracing)
            {
                Trace(galaxy, source, destination, jumpRange, sourceDeepSpacePoint, destinationDeepSpacePoint, $"{Ids(list)} (distance {distance / 1e6:0.#}M, time {time / 1e6:0.#}M, {outcome})", "not run");
            }
            __result = list;
            totalDistance = distance;
            totalTime = time;
            Stats.Add(ref Stats.FindPathCalls);
            if (outcome == Outcome.Same)
            {
                Stats.Add(ref Stats.FindPathSameSystem);
            }
            else if (list.Count == 0)
            {
                Stats.Add(ref Stats.FindPathFailed);
            }
            if (t0 != 0)
            {
                Stats.Add(ref Stats.FindPathTicks, Stopwatch.GetTimestamp() - t0);
            }
            return false;
        }
        catch (Exception ex)
        {
            Error("FindPath", ex);
            return true;
        }
    }

    private static bool TimesPrefix(Galaxy galaxy, bool[] systemsToExclude, StarSystem startSystem, float jumpRange, ref Dictionary<int, float> __result)
    {
        try
        {
            long t0 = Log.Verbose ? Stopwatch.GetTimestamp() : 0;
            if (!Replacement.TryTimes(galaxy, systemsToExclude, startSystem, jumpRange, out Dictionary<int, float> times, out _))
            {
                Stats.Add(ref Stats.Fallbacks);
                return true;
            }
            __result = times;
            Stats.Add(ref Stats.TimesCalls);
            if (t0 != 0)
            {
                Stats.Add(ref Stats.TimesTicks, Stopwatch.GetTimestamp() - t0);
            }
            return false;
        }
        catch (Exception ex)
        {
            Error("CalculateSystemTimes", ex);
            return true;
        }
    }

    // ---- compare mode: the game answers, the replacement is run and timed beside it ----

    private static void CompareFindPrefix(out long __state)
    {
        __state = 0;
        if (_depth++ == 0 && !_inTimes)
        {
            __state = Stopwatch.GetTimestamp();
        }
    }

    private static Exception? CompareFindFinalizer(long __state, StarSystemList? __result, Galaxy galaxy, bool[] excludedSystems, StarSystem source, StarSystem destination, float jumpRange,
        float totalTime, Point sourceDeepSpacePoint, Point destinationDeepSpacePoint, Exception? __exception)
    {
        _depth--;
        if (__state == 0 || __exception != null)
        {
            return __exception;
        }
        long vanillaTicks = Stopwatch.GetTimestamp() - __state;
        try
        {
            Compare.RecordPath(vanillaTicks, __result, totalTime, galaxy, excludedSystems, source, destination, jumpRange, sourceDeepSpacePoint, destinationDeepSpacePoint);
            if (Tracing)
            {
                string ours = Replacement.TryFindPath(galaxy, excludedSystems, source, destination, jumpRange, sourceDeepSpacePoint, destinationDeepSpacePoint, out StarSystemList list, out float distance, out float time, out Outcome outcome)
                    ? $"{Ids(list)} (distance {distance / 1e6:0.#}M, time {time / 1e6:0.#}M, {outcome})"
                    : "not handled";
                Trace(galaxy, source, destination, jumpRange, sourceDeepSpacePoint, destinationDeepSpacePoint, ours, $"{Ids(__result)} (time {totalTime / 1e6:0.#}M)");
            }
        }
        catch (Exception ex)
        {
            Error("compare FindPath", ex);
        }
        return __exception;
    }

    private static void CompareTimesPrefix(out long __state)
    {
        __state = 0;
        if (!_inTimes && _depth == 0)
        {
            _inTimes = true;
            __state = Stopwatch.GetTimestamp();
        }
    }

    private static Exception? CompareTimesFinalizer(long __state, Dictionary<int, float>? __result, Galaxy galaxy, bool[] systemsToExclude, StarSystem startSystem, float jumpRange, Exception? __exception)
    {
        if (__state == 0)
        {
            return __exception;
        }
        _inTimes = false;
        long vanillaTicks = Stopwatch.GetTimestamp() - __state;
        if (__exception != null)
        {
            return __exception;
        }
        try
        {
            Compare.RecordTimes(vanillaTicks, __result, galaxy, systemsToExclude, startSystem, jumpRange);
        }
        catch (Exception ex)
        {
            Error("compare CalculateSystemTimes", ex);
        }
        return __exception;
    }

    // ---- changes of the game's neighbour lists ----

    private static void DistancesPrefix(StarSystem __instance, out SystemNodeValueList? __state)
    {
        __state = __instance.SystemDistances;
    }

    private static void DistancesPostfix(StarSystem __instance, SystemNodeValueList? __state, Galaxy galaxy)
    {
        try
        {
            Stats.Add(ref Stats.ListRewrites);
            if (galaxy == null || !GalaxyView.TryToken(galaxy, out int token))
            {
                return; // no search has used this galaxy yet, so no cached tree can be affected
            }
            SystemNodeValueList? now = __instance.SystemDistances;
            int id = __instance.SystemId, added = 0, removed = 0, altered = 0;
            if (__state != null && now != null && !ReferenceEquals(__state, now))
            {
                var old = new Dictionary<int, (float Distance, float Time)>(__state.Count);
                for (int i = 0; i < __state.Count; i++)
                {
                    SystemNodeValue n = __state.GetByIndexDirectly(i);
                    old[n.Index] = (n.Distance, n.TimeCost);
                }
                for (int i = 0; i < now.Count; i++)
                {
                    SystemNodeValue n = now.GetByIndexDirectly(i);
                    if (old.Remove(n.Index, out var o))
                    {
                        if (o.Distance != n.Distance || o.Time != n.TimeCost)
                        {
                            ChangeLog.Append(token, id, n.Index, o.Distance, o.Time, n.Distance, n.TimeCost);
                            altered++;
                        }
                    }
                    else
                    {
                        ChangeLog.Append(token, id, n.Index, float.NaN, float.NaN, n.Distance, n.TimeCost);
                        added++;
                    }
                }
                foreach (var gone in old)
                {
                    ChangeLog.Append(token, id, gone.Key, gone.Value.Distance, gone.Value.Time, float.NaN, float.NaN);
                    removed++;
                }
            }
            else if (now != null && !ReferenceEquals(__state, now))
            {
                for (int i = 0; i < now.Count; i++)
                {
                    SystemNodeValue n = now.GetByIndexDirectly(i);
                    ChangeLog.Append(token, id, n.Index, float.NaN, float.NaN, n.Distance, n.TimeCost);
                    added++;
                }
            }
            if (added + removed + altered == 0)
            {
                Stats.Add(ref Stats.ListIdentical);
                if (Settings.Current.LogCacheChanges && Interlocked.Increment(ref _identicalLogged) <= 20)
                {
                    Log.Info($"Neighbour list of system {id} rewritten with identical content; nothing invalidated. Caused by: {Cause()}");
                }
                return;
            }
            Stats.Add(ref Stats.ListChanged);
            Stats.Add(ref Stats.LinkChanges, added + removed + altered);
            if (Settings.Current.LogCacheChanges && Interlocked.Increment(ref _changesLogged) <= 200)
            {
                Log.Info($"Neighbour list of system {id} changed: {added} links added, {removed} removed, {altered} with another distance or time. Caused by: {Cause()}");
            }
        }
        catch (Exception ex)
        {
            Error("CalculateSystemDistances", ex);
        }
    }

    // The innermost game methods above the call, without this mod, Harmony and dynamic methods.
    private static string Cause()
    {
        var names = new List<string>();
        foreach (StackFrame f in new StackTrace(2, false).GetFrames())
        {
            MethodBase? m = f.GetMethod();
            Type? t = m?.DeclaringType;
            if (t == null || t.Namespace == "PathfindingFix" || t.Namespace?.StartsWith("HarmonyLib") == true || (t.Name == "StarSystem" && m!.Name.StartsWith("CalculateSystemDistances")))
            {
                continue;
            }
            names.Add($"{t.Name}.{m!.Name}");
            if (names.Count == 4)
            {
                break;
            }
        }
        return names.Count == 0 ? "(unknown)" : string.Join(" < ", names);
    }
}
