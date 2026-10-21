using System.Reflection;
using System.Text;
using DistantWorlds.Types;
using HarmonyLib;
using Stride.Core.Mathematics;

namespace PathfindingFix;

// LogPathTrace only: what happens to a move order of the player, step by step, so that an order that ends as a straight jump can be explained.
//   - every overload of Empire.FindBestJumpPathToDestination (the route of an order as a list of location ids), with its arguments and the route it returned;
//   - Ship.AssignMissionCore when the mission is assigned by hand: the mission type, the target, and the jump path the ship is finally given (empty or missing: it jumps straight at the target).
// Only ships of the player's empire are logged, at most MaxLines lines.
internal static class OrderDiagnostics
{
    private const int MaxLines = 400;
    private static long _lines;

    public static void Install(Harmony harmony)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        int routes = 0, missions = 0;
        foreach (MethodInfo m in typeof(Empire).GetMethods(flags).Where(m => m.Name == "FindBestJumpPathToDestination"))
        {
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(OrderDiagnostics).GetMethod(nameof(RoutePostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            routes++;
        }
        foreach (MethodInfo m in typeof(Ship).GetMethods(flags).Where(m => m.Name == "AssignMissionCore" && m.GetParameters().Any(p => p.Name == "jumpPathLocationIds")))
        {
            harmony.Patch(m, postfix: new HarmonyMethod(typeof(OrderDiagnostics).GetMethod(nameof(MissionPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            missions++;
        }
        Log.Info($"Order diagnostics: {routes} route methods and {missions} mission methods observed (player ships only, up to {MaxLines} lines).");
    }

    private static bool IsPlayerShip(Ship? ship)
    {
        try
        {
            return ship != null && ship.GetEmpire()?.IsPlayer == true;
        }
        catch
        {
            return false;
        }
    }

    private static string Fmt(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case Location l:
                return $"location {l.LocationId} (system {l.SystemId}{(l.IsTemporary ? ", temporary" : "")}) at {l.GalaxyX / 1000000},{l.GalaxyY / 1000000}";
            case Ship s:
                return $"ship at {s.GalaxyX / 1000000},{s.GalaxyY / 1000000}";
            case StellarObject o:
                return $"{o.GetType().Name} at {o.GalaxyX / 1000000},{o.GalaxyY / 1000000}";
            case Point p:
                return $"({p.X / 1000000},{p.Y / 1000000})";
            case float f:
                return Math.Abs(f) >= 100000 ? $"{f / 1e6f:0.#}M" : f.ToString("0.##");
            case int i:
                return Math.Abs(i) >= 100000 ? $"{i / 1000000}M" : i.ToString();
            case List<int> ids:
                return ids.Count == 0 ? "empty list" : $"{ids.Count} locations [{string.Join(">", ids.Take(30))}{(ids.Count > 30 ? ">..." : "")}]";
            default:
                return value.ToString() ?? "";
        }
    }

    // The game methods that led here, innermost first (this mod, Harmony and dynamic methods left out).
    private static string Stack()
    {
        var names = new List<string>();
        foreach (System.Diagnostics.StackFrame f in new System.Diagnostics.StackTrace(2, false).GetFrames())
        {
            MethodBase? m = f.GetMethod();
            Type? t = m?.DeclaringType;
            if (t == null || t.Namespace == "PathfindingFix" || t.Namespace?.StartsWith("HarmonyLib") == true)
            {
                continue;
            }
            names.Add($"{t.Name}.{m!.Name}");
            if (names.Count == 12)
            {
                break;
            }
        }
        return names.Count == 0 ? "(unknown)" : string.Join(" < ", names);
    }

    private static void RoutePostfix(MethodBase __originalMethod, object[] __args, List<int>? __result)
    {
        try
        {
            Ship? ship = __args.OfType<Ship>().FirstOrDefault();
            if (!IsPlayerShip(ship) || Interlocked.Increment(ref _lines) > MaxLines)
            {
                return;
            }
            ParameterInfo[] ps = __originalMethod.GetParameters();
            var sb = new StringBuilder("Order route ").Append(__originalMethod.Name).Append('(');
            for (int i = 0; i < ps.Length && i < __args.Length; i++)
            {
                if (ps[i].ParameterType.IsByRef)
                {
                    continue;
                }
                sb.Append(i == 0 ? "" : ", ").Append(ps[i].Name).Append('=').Append(Fmt(__args[i]));
            }
            sb.Append(") => ").Append(Fmt(__result));
            Log.Info(sb.ToString());
        }
        catch (Exception ex)
        {
            Log.Info("Order diagnostics (route) failed: " + ex.Message);
        }
    }

    private static void MissionPostfix(Ship __instance, MethodBase __originalMethod, object[] __args)
    {
        try
        {
            ParameterInfo[] ps = __originalMethod.GetParameters();
            object? Arg(string name)
            {
                for (int i = 0; i < ps.Length; i++)
                {
                    if (ps[i].Name == name)
                    {
                        return __args[i];
                    }
                }
                return null;
            }
            if (Arg("manuallyAssigned") is not true || !IsPlayerShip(__instance) || Interlocked.Increment(ref _lines) > MaxLines)
            {
                return;
            }
            bool emptyPath = Arg("jumpPathLocationIds") is not List<int> { Count: > 0 };
            Log.Info($"Order mission: {Fmt(__instance)} gets mission {Arg("missionType")}, target {Fmt(Arg("primaryTarget"))}, point {Fmt(Arg("primaryGalaxyX"))},{Fmt(Arg("primaryGalaxyY"))}, jump path {Fmt(Arg("jumpPathLocationIds"))}, jump range {Fmt(__instance.Summary?.HyperDriveJumpRange ?? 0f)}"
                + (emptyPath ? $"; called by {Stack()}" : ""));
        }
        catch (Exception ex)
        {
            Log.Info("Order diagnostics (mission) failed: " + ex.Message);
        }
    }
}
