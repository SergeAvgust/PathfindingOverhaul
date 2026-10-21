using System.Diagnostics;
using System.Text;
using DistantWorlds.Types;
using Stride.Core.Mathematics;

namespace PathfindingFix;

// Compare mode (Mode = compare, a developer setting): the game's own search answers, and the replacement runs right after it on the same thread and the same inputs,
// timed, with its answer thrown away. The cache fills only from the game's real traffic. The report (log, every CompareReportSeconds or when a file named report.now appears
// next to the DLL) gives per kind of call and phase of play (seconds since the first call: 0-20, 20-60, 60-120, 120+) the time of both sides as percentiles,
// how the replacement was answered (cached tree, built tree, A*), and how the answers differ.
internal static class Compare
{
    private const int Buckets = 256, PerOctave = 8;
    private const double MinValue = 0.01; // microseconds, or a ratio
    private static readonly double[] PhaseBounds = { 20, 60, 120 };
    private const int Phases = 4;

    private sealed class Hist
    {
        public readonly long[] Counts = new long[Buckets];
        public long N, SumMilli;
        private long _maxBits;

        public void Add(double value)
        {
            int index = value <= MinValue ? 0 : Math.Min(Buckets - 1, (int)(Math.Log2(value / MinValue) * PerOctave));
            Interlocked.Increment(ref Counts[index]);
            Interlocked.Increment(ref N);
            Interlocked.Add(ref SumMilli, (long)(value * 1000));
            long bits = BitConverter.DoubleToInt64Bits(value);
            long seen;
            while (BitConverter.Int64BitsToDouble(seen = Interlocked.Read(ref _maxBits)) < value)
            {
                if (Interlocked.CompareExchange(ref _maxBits, bits, seen) == seen)
                {
                    break;
                }
            }
        }

        public double Max => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _maxBits));
        public double Total => SumMilli / 1000.0;

        public double Percentile(double p)
        {
            if (N == 0)
            {
                return 0;
            }
            long target = (long)Math.Ceiling(p * N), seen = 0;
            for (int i = 0; i < Buckets; i++)
            {
                seen += Counts[i];
                if (seen >= target)
                {
                    return Math.Min(Max, MinValue * Math.Pow(2, (i + 1) / (double)PerOctave));
                }
            }
            return Max;
        }
    }

    private sealed class Cell
    {
        public readonly Hist Vanilla = new(), Candidate = new();
        public readonly long[] Outcomes = new long[5];
    }

    // kind 0: CalculateSystemTimes, kind 1: FindPath
    private static readonly Cell[,] Cells = CreateCells();
    private static readonly Hist Ratio = new();
    private static long _first;
    private static long _vanillaOnlyFailed, _candidateOnlyFailed, _bothFailed, _bothFound, _longer, _shorter;
    private static long _timesVanillaReached, _timesCandidateReached, _timesFewer, _timesMore;
    private static long _skipped, _vanillaTruncated;

    private static Cell[,] CreateCells()
    {
        var cells = new Cell[2, Phases];
        for (int k = 0; k < 2; k++)
        {
            for (int p = 0; p < Phases; p++)
            {
                cells[k, p] = new Cell();
            }
        }
        return cells;
    }

    private static int Phase()
    {
        long now = Stopwatch.GetTimestamp();
        Interlocked.CompareExchange(ref _first, now, 0);
        double s = (now - Interlocked.Read(ref _first)) / (double)Stopwatch.Frequency;
        int p = 0;
        while (p < PhaseBounds.Length && s >= PhaseBounds[p])
        {
            p++;
        }
        return p;
    }

    private static double Us(long ticks) => ticks * 1e6 / Stopwatch.Frequency;

    public static void RecordPath(long vanillaTicks, StarSystemList? vanillaResult, float vanillaTime, Galaxy? galaxy, bool[]? excluded, StarSystem? source, StarSystem? destination,
        float jumpRange, Point sourceDeep, Point destinationDeep)
    {
        int phase = Phase();
        long t0 = Stopwatch.GetTimestamp();
        bool handled = Replacement.TryFindPath(galaxy, excluded, source, destination, jumpRange, sourceDeep, destinationDeep, out StarSystemList list, out _, out float time, out Outcome outcome);
        long candidateTicks = Stopwatch.GetTimestamp() - t0;
        Cell cell = Cells[1, phase];
        Interlocked.Increment(ref cell.Outcomes[(int)outcome]);
        if (!handled)
        {
            Interlocked.Increment(ref _skipped);
            return;
        }
        cell.Vanilla.Add(Us(vanillaTicks));
        cell.Candidate.Add(Us(candidateTicks));
        if (outcome == Outcome.Same)
        {
            return;
        }
        bool vanillaFailed = vanillaResult == null || vanillaResult.Count == 0, candidateFailed = list.Count == 0;
        // The game's search swallows every exception and returns whatever it had built: a route that stops short of the destination (with no deep-space end to explain it).
        if (!vanillaFailed && destination != null && destinationDeep == Point.Zero && vanillaResult![vanillaResult.Count - 1]?.SystemId != destination.SystemId)
        {
            Interlocked.Increment(ref _vanillaTruncated);
            if (Interlocked.Read(ref _vanillaTruncated) <= 20)
            {
                Log.Info($"Compare: the game's route from {source?.SystemId} to {destination.SystemId} (range {jumpRange / 1e6f:0.#}M, deep start {sourceDeep.X},{sourceDeep.Y}) ends at system {vanillaResult[vanillaResult.Count - 1]?.SystemId}, {vanillaResult.Count} systems; ours has {list.Count}.");
            }
        }
        if (vanillaFailed && candidateFailed)
        {
            Interlocked.Increment(ref _bothFailed);
        }
        else if (vanillaFailed)
        {
            Interlocked.Increment(ref _vanillaOnlyFailed);
        }
        else if (candidateFailed)
        {
            Interlocked.Increment(ref _candidateOnlyFailed);
        }
        else
        {
            Interlocked.Increment(ref _bothFound);
            if (vanillaTime > 0)
            {
                double ratio = time / vanillaTime;
                Ratio.Add(ratio);
                if (ratio > 1.01)
                {
                    Interlocked.Increment(ref _longer);
                }
                else if (ratio < 0.99)
                {
                    Interlocked.Increment(ref _shorter);
                }
            }
        }
    }

    public static void RecordTimes(long vanillaTicks, Dictionary<int, float>? vanillaResult, Galaxy? galaxy, bool[]? excluded, StarSystem? start, float jumpRange)
    {
        int phase = Phase();
        long t0 = Stopwatch.GetTimestamp();
        bool handled = Replacement.TryTimes(galaxy, excluded, start, jumpRange, out Dictionary<int, float> times, out Outcome outcome);
        long candidateTicks = Stopwatch.GetTimestamp() - t0;
        Cell cell = Cells[0, phase];
        Interlocked.Increment(ref cell.Outcomes[(int)outcome]);
        if (!handled)
        {
            Interlocked.Increment(ref _skipped);
            return;
        }
        cell.Vanilla.Add(Us(vanillaTicks));
        cell.Candidate.Add(Us(candidateTicks));
        int v = vanillaResult?.Count ?? 0;
        Interlocked.Add(ref _timesVanillaReached, v);
        Interlocked.Add(ref _timesCandidateReached, times.Count);
        if (times.Count < v)
        {
            Interlocked.Increment(ref _timesFewer);
        }
        else if (times.Count > v)
        {
            Interlocked.Increment(ref _timesMore);
        }
    }

    private static string Fmt(double us) => us >= 1000 ? $"{us / 1000:0.##} ms" : $"{us:0.#} us";

    private static string Pct(long part, long whole) => whole <= 0 ? "-" : $"{100.0 * part / whole:0.#}%";

    public static void Report()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Compare report (vanilla answers, the replacement is run beside it; all times per call):");
        string[] kinds = { "CalculateSystemTimes", "FindPath" };
        for (int k = 0; k < 2; k++)
        {
            for (int p = 0; p < Phases; p++)
            {
                Cell c = Cells[k, p];
                if (c.Vanilla.N == 0)
                {
                    continue;
                }
                long lookups = c.Outcomes[(int)Outcome.Hit] + c.Outcomes[(int)Outcome.Built] + c.Outcomes[(int)Outcome.AStar];
                string label = $"{(p == 0 ? 0 : PhaseBounds[p - 1]):0}{(p < PhaseBounds.Length ? $"-{PhaseBounds[p]:0} s" : " s and later")}";
                sb.AppendLine($"  {kinds[k]}, {label}: {c.Vanilla.N} calls; vanilla total {Fmt(c.Vanilla.Total)} (p50 {Fmt(c.Vanilla.Percentile(.5))}, p90 {Fmt(c.Vanilla.Percentile(.9))}, p99 {Fmt(c.Vanilla.Percentile(.99))}, max {Fmt(c.Vanilla.Max)}); "
                    + $"replacement total {Fmt(c.Candidate.Total)} (p50 {Fmt(c.Candidate.Percentile(.5))}, p90 {Fmt(c.Candidate.Percentile(.9))}, p99 {Fmt(c.Candidate.Percentile(.99))}, max {Fmt(c.Candidate.Max)}); "
                    + $"x{c.Vanilla.Total / Math.Max(1e-9, c.Candidate.Total):0.0} faster; cached tree {Pct(c.Outcomes[(int)Outcome.Hit], lookups)}, tree built {Pct(c.Outcomes[(int)Outcome.Built], lookups)}, A* {Pct(c.Outcomes[(int)Outcome.AStar], lookups)}, same system {Pct(c.Outcomes[(int)Outcome.Same], c.Vanilla.N)}");
            }
        }
        long found = _bothFound;
        sb.AppendLine($"  FindPath answers: the game found no path but the replacement did for {_vanillaOnlyFailed}; the replacement found none where the game did for {_candidateOnlyFailed}; neither for {_bothFailed}. "
            + $"Both found one for {found}: replacement time / game time p10 {Ratio.Percentile(.1):0.000}, p50 {Ratio.Percentile(.5):0.000}, p90 {Ratio.Percentile(.9):0.000}, max {Ratio.Max:0.000}; longer by more than 1% {Pct(_longer, found)}, shorter by more than 1% {Pct(_shorter, found)}.");
        sb.AppendLine($"  CalculateSystemTimes answers: systems reached {_timesVanillaReached} by the game, {_timesCandidateReached} by the replacement; runs where it reached fewer {_timesFewer}, more {_timesMore}. Calls the replacement left to the game: {_skipped}. Routes of the game that stop short of the destination without a deep-space end: {_vanillaTruncated}.");
        sb.Append("  " + Stats.Summary());
        Log.Info(sb.ToString());
    }
}
