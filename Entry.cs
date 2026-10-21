namespace PathfindingFix;

// Found by the mod launcher's injection scan (a public static class named Entry); the loader prefers InitWithOptions(string) and hands it the settings JSON.
// See DW2-Mod-Launcher/docs/DLL Injection.md.
public static class Entry
{
    public static void InitWithOptions(string json) => Bootstrap.Init(json);

    public static void Init() => Bootstrap.Init("{}");
}
