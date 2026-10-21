using HarmonyLib;

namespace PathfindingFix;

// Entry point behind Entry.InitWithOptions: reads the settings, installs the Harmony patches (nothing at all in off mode) and starts the small background thread that
// writes the statistics summary (LogEnabled) and the compare report. The mod is enabled and disabled by the launcher; nothing here needs a switch for that.
internal static class Bootstrap
{
    private static bool _started;

    public static void Init(string json)
    {
        if (_started)
        {
            return;
        }
        _started = true;
        try
        {
            Log.Start();
            Settings settings = Settings.Parse(json);
            Log.Verbose = settings.LogEnabled;
            Log.Info($"Pathfinding Fix 0.1.0 starting: {settings}");
            if (settings.Mode == Settings.ModeOff)
            {
                Log.Info("Mode is off: nothing is patched.");
                return;
            }
            var harmony = new Harmony("pathfindingfix.dw2mod");
            if (!Patches.Install(harmony, settings))
            {
                return;
            }
            Log.Info($"Patches installed ({settings.Mode} mode).");
            if (settings.LogEnabled || settings.LogPathTrace || settings.Mode == Settings.ModeCompare)
            {
                StartBackgroundThread(settings);
            }
        }
        catch (Exception ex)
        {
            Log.Info("Init failed: " + ex);
        }
    }

    private static void StartBackgroundThread(Settings settings)
    {
        string folder = Path.GetDirectoryName(typeof(Bootstrap).Assembly.Location) ?? ".";
        string trigger = Path.Combine(folder, "report.now"), traceTrigger = Path.Combine(folder, "trace.now");
        Log.Info($"Watching for trace.now / report.now in {folder}");
        var thread = new Thread(() =>
        {
            int sinceSummary = 0, sinceReport = 0;
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                    sinceSummary++;
                    sinceReport++;
                    if (settings.LogPathTrace && File.Exists(traceTrigger))
                    {
                        File.Delete(traceTrigger);
                        Patches.DumpTrace(120, 600);
                    }
                    if (settings.Mode == Settings.ModeCompare && (File.Exists(trigger) || (settings.CompareReportSeconds > 0 && sinceReport >= settings.CompareReportSeconds)))
                    {
                        sinceReport = 0;
                        if (File.Exists(trigger))
                        {
                            File.Delete(trigger);
                        }
                        Compare.Report();
                    }
                    else if (settings.LogEnabled && sinceSummary >= 60)
                    {
                        sinceSummary = 0;
                        Log.Info("Last minute: " + Stats.Summary());
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("Background thread: " + ex.Message);
                }
            }
        })
        {
            IsBackground = true,
            Name = "PathfindingFix log",
        };
        thread.Start();
    }
}
