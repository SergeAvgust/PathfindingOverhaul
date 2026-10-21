using System.Runtime.CompilerServices;
using DistantWorlds.Core.NonBlocking;
using DistantWorlds.Types;

namespace PathfindingFix;

// What the solver needs to know about one galaxy instance, indexed by system id (the same convention as the game's own pathfinding arrays):
// the star system objects (their SystemDistances lists are the graph), positions for the A* estimate, and a token that tells galaxies apart in the tree cache
// without keeping the Galaxy alive. A view is rebuilt when the number of systems changes (the editor can add a star).
internal sealed class GalaxyView
{
    private static readonly ConditionalWeakTable<Galaxy, GalaxyView> Views = new();
    private static readonly object Gate = new();
    private static int _tokens;

    public readonly int Token;
    public readonly int Count;
    public readonly int Size;
    public readonly StarSystem?[] ById;
    public readonly int[] Ids;
    public readonly float[] X, Y;

    private GalaxyView(int token, ConcurrentDictionary<int, StarSystem> systems)
    {
        Token = token;
        var list = new List<StarSystem>(systems.Count);
        int maxId = 0;
        foreach (StarSystem? s in systems.Values)
        {
            if (s != null)
            {
                list.Add(s);
                maxId = Math.Max(maxId, s.SystemId);
            }
        }
        Count = list.Count;
        Size = maxId + 1;
        ById = new StarSystem?[Size];
        X = new float[Size];
        Y = new float[Size];
        Ids = new int[Count];
        for (int i = 0; i < Count; i++)
        {
            StarSystem s = list[i];
            ById[s.SystemId] = s;
            X[s.SystemId] = s.GalaxyX;
            Y[s.SystemId] = s.GalaxyY;
            Ids[i] = s.SystemId;
        }
    }

    public static GalaxyView For(Galaxy galaxy)
    {
        int count = galaxy.Systems.Count;
        if (Views.TryGetValue(galaxy, out GalaxyView? view) && view.Count == count)
        {
            return view;
        }
        lock (Gate)
        {
            if (Views.TryGetValue(galaxy, out view))
            {
                if (view.Count == galaxy.Systems.Count)
                {
                    return view;
                }
                Views.Remove(galaxy);
                view = new GalaxyView(view.Token, galaxy.Systems);
            }
            else
            {
                view = new GalaxyView(++_tokens, galaxy.Systems);
            }
            Views.Add(galaxy, view);
            return view;
        }
    }

    // The token of a galaxy that already has a view (used by the list-change hook, which must not build one).
    public static bool TryToken(Galaxy galaxy, out int token)
    {
        if (Views.TryGetValue(galaxy, out GalaxyView? view))
        {
            token = view.Token;
            return true;
        }
        token = 0;
        return false;
    }
}
