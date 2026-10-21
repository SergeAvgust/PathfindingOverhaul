using System.Text.Json;

namespace PathfindingFix;

// The settings JSON the launcher hands to Entry.InitWithOptions (see settings.schema.json). Missing or malformed values keep their defaults.
internal sealed class Settings
{
    public const string ModeReplace = "replace", ModeCompare = "compare", ModeOff = "off";

    public float HopPenaltyPercent = 10f;
    public bool RoutePointOrders = true;
    public bool RouteFromShipPosition = true;
    public string Mode = ModeReplace;
    public bool LogEnabled;
    public bool LogCacheChanges;
    public bool LogPathTrace;
    public int CacheBudgetMb = 64;
    public int CompareReportSeconds;

    // The cost added to every jump, as a fraction of the jump range of the search.
    public float HopPenaltyFraction => HopPenaltyPercent / 100f;

    public static Settings Current { get; private set; } = new();

    public static Settings Parse(string json)
    {
        var s = new Settings();
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (TryNumber(root, "HopPenaltyPercent", out double penalty))
                {
                    s.HopPenaltyPercent = (float)Math.Clamp(penalty, 0, 50);
                }
                if (root.TryGetProperty("Mode", out JsonElement mode) && mode.ValueKind == JsonValueKind.String)
                {
                    string m = (mode.GetString() ?? "").Trim().ToLowerInvariant();
                    if (m is ModeReplace or ModeCompare or ModeOff)
                    {
                        s.Mode = m;
                    }
                }
                s.RoutePointOrders = Flag(root, "RoutePointOrders", true);
                s.RouteFromShipPosition = Flag(root, "RouteFromShipPosition", true);
                s.LogEnabled = Flag(root, "LogEnabled", false);
                s.LogCacheChanges = Flag(root, "LogCacheChanges", false);
                s.LogPathTrace = Flag(root, "LogPathTrace", false);
                if (TryNumber(root, "CacheBudgetMb", out double budget))
                {
                    s.CacheBudgetMb = (int)Math.Clamp(budget, 4, 1024);
                }
                if (TryNumber(root, "CompareReportSeconds", out double seconds))
                {
                    s.CompareReportSeconds = (int)Math.Clamp(seconds, 0, 3600);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Info("Settings could not be read, using defaults: " + ex.Message);
        }
        Current = s;
        return s;
    }

    private static bool TryNumber(JsonElement root, string key, out double value)
    {
        value = 0;
        return root.TryGetProperty(key, out JsonElement e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out value);
    }

    private static bool Flag(JsonElement root, string key, bool fallback)
        => root.TryGetProperty(key, out JsonElement e) && e.ValueKind is JsonValueKind.True or JsonValueKind.False ? e.GetBoolean() : fallback;

    public override string ToString()
        => $"mode {Mode}, hop penalty {HopPenaltyPercent:0.##}%, point orders {(RoutePointOrders ? "routed" : "left to the game")}, cache {CacheBudgetMb} MB, log {(LogEnabled ? "on" : "off")}, cache-change log {(LogCacheChanges ? "on" : "off")}, path trace {(LogPathTrace ? "on" : "off")}, compare report every {CompareReportSeconds} s";
}
