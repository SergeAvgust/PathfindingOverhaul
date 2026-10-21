using System.Reflection;
using System.Reflection.Emit;
using DistantWorlds.Types;
using HarmonyLib;

namespace PathfindingFix;

// Move orders given to a point in space (a click that does not hit a star, planet or ship; the hit area of a star is tiny, so near misses are common).
//   - For a single ship the game builds the order in ShipMission.ResolveValidMissions and replaces a point beyond the ship's jump range by the point exactly one jump range away
//     in that direction (the blind "jump toward it" arrow). The clicked point is lost and no route is ever searched.
//   - Fleets and several ships get no such clamp, and a fleet's point order is routed to the nearest location through the jump network (Fleet.AssignMissionToShip).
// This makes the single ship behave like a fleet: the clamp is removed (the one comparison that skips it when the point is within range now always skips it), and when a manual Move to a point
// beyond one jump reaches Ship.AssignMissionCore without a jump path, the path is computed as Ship.AssignMissionMove(galaxyX, galaxyY, ...) does: the route to the location nearest to the point.
// The ship follows the lanes to that location and covers the rest to the point. Orders within one jump are untouched. Setting: RoutePointOrders.
internal static class PointOrders
{
    private static readonly FieldInfo? ShipGalaxy = typeof(Ship).GetField("_Galaxy", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    [ThreadStatic] private static bool _routing;
    private static long _routed;

    public static void Install(Harmony harmony)
    {
        MethodInfo? resolve = typeof(ShipMission).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .FirstOrDefault(m => m.Name == "ResolveValidMissions" && m.GetParameters().Any(p => p.Name == "playerEmpire") && m.GetParameters().Any(p => p.Name == "ship"));
        if (resolve != null)
        {
            harmony.Patch(resolve, transpiler: new HarmonyMethod(typeof(PointOrders).GetMethod(nameof(RemoveRangeClamp), BindingFlags.Static | BindingFlags.NonPublic)));
        }
        else
        {
            Log.Info("Point orders: ShipMission.ResolveValidMissions (single ship) not found; the jump-range clamp stays.");
        }
        int core = 0;
        foreach (MethodInfo m in typeof(Ship).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                     .Where(m => m.Name == "AssignMissionCore" && m.GetParameters().Any(p => p.Name == "jumpPathLocationIds") && m.GetParameters().Any(p => p.Name == "primaryGalaxyX")))
        {
            harmony.Patch(m, prefix: new HarmonyMethod(typeof(PointOrders).GetMethod(nameof(RoutePrefix), BindingFlags.Static | BindingFlags.NonPublic)));
            core++;
        }
        Log.Info($"Point orders: single-ship clamp {(resolve != null ? "patched" : "NOT patched")}, route computed in {core} mission methods.");
    }

    // The comparison "distance to the point <= jump range" (ble.un right after the load of HyperDriveJumpRange, with the angle computation that does the clamping within a few instructions)
    // becomes "pop, pop, br": the clamp is always skipped.
    private static IEnumerable<CodeInstruction> RemoveRangeClamp(IEnumerable<CodeInstruction> instructions)
    {
        List<CodeInstruction> original = instructions.ToList();
        List<CodeInstruction> codes = original.ToList();
        FieldInfo? jumpRange = typeof(ShipSummary).GetField("HyperDriveJumpRange");
        MethodInfo? angle = typeof(Calculations).GetMethod("DetermineAngle", new[] { typeof(int), typeof(int), typeof(int), typeof(int) });
        if (jumpRange == null || angle == null)
        {
            Log.Info("Point orders: HyperDriveJumpRange or Calculations.DetermineAngle not found; the clamp stays.");
            return original;
        }
        int patched = 0;
        for (int i = 1; i < codes.Count; i++)
        {
            if (codes[i].opcode != OpCodes.Ble_Un || !codes[i - 1].LoadsField(jumpRange))
            {
                continue;
            }
            bool clamp = false;
            for (int j = i + 1; j < Math.Min(codes.Count, i + 16); j++)
            {
                if (codes[j].Calls(angle))
                {
                    clamp = true;
                    break;
                }
            }
            if (!clamp)
            {
                continue;
            }
            object label = codes[i].operand;
            var pop = new CodeInstruction(OpCodes.Pop);
            pop.labels.AddRange(codes[i].labels);
            pop.blocks.AddRange(codes[i].blocks);
            codes[i] = pop;
            codes.Insert(i + 1, new CodeInstruction(OpCodes.Pop));
            codes.Insert(i + 2, new CodeInstruction(OpCodes.Br, label));
            patched++;
            i += 2;
        }
        if (patched != 1)
        {
            Log.Info($"Point orders: expected to find the jump-range clamp once, found {patched}; the method is left as the game has it.");
            return original;
        }
        Log.Info("Point orders: the jump-range clamp of single-ship move orders is removed.");
        return codes;
    }

    private static void RoutePrefix(Ship __instance, ShipMissionType missionType, StellarObject primaryTarget, int primaryGalaxyX, int primaryGalaxyY, ref List<int>? jumpPathLocationIds, bool manuallyAssigned)
    {
        try
        {
            if (_routing || !Settings.Current.RoutePointOrders || !manuallyAssigned || missionType != ShipMissionType.Move || primaryTarget != null || primaryGalaxyX < 0 || primaryGalaxyY < 0
                || (jumpPathLocationIds != null && jumpPathLocationIds.Count > 0))
            {
                return;
            }
            float range = __instance.Summary?.HyperDriveJumpRange ?? 0f;
            if (!(range > 0f))
            {
                return;
            }
            double dx = __instance.GalaxyX - primaryGalaxyX, dy = __instance.GalaxyY - primaryGalaxyY;
            double distance = Math.Sqrt(dx * dx + dy * dy);
            if (distance <= range || __instance.GetEmpire() is not Empire empire || ShipGalaxy?.GetValue(__instance) is not Galaxy galaxy)
            {
                return; // one jump reaches it: the game's own direct jump
            }
            Location? nearest = galaxy.Locations.FindNearestLocation(primaryGalaxyX, primaryGalaxyY);
            if (nearest == null || nearest.IsTemporary)
            {
                return;
            }
            _routing = true;
            try
            {
                List<int>? route = empire.FindBestJumpPathToDestination(__instance, nearest.LocationId, out _, out _);
                if (route == null || route.Count == 0)
                {
                    return;
                }
                jumpPathLocationIds = route;
                long n = Interlocked.Increment(ref _routed);
                if (n <= 40)
                {
                    Log.Info($"Move order to a point routed through the jump network: ship at {__instance.GalaxyX / 1000000},{__instance.GalaxyY / 1000000} -> point {primaryGalaxyX / 1000000},{primaryGalaxyY / 1000000} "
                        + $"({distance / 1e6:0.#}M, jump range {range / 1e6f:0.#}M), nearest location {nearest.LocationId} in system {nearest.SystemId}; route of {route.Count} locations (order {n}).");
                }
            }
            finally
            {
                _routing = false;
            }
        }
        catch (Exception ex)
        {
            Log.Info("Point orders: routing a move order failed, the game's own handling applies: " + ex.Message);
        }
    }
}
