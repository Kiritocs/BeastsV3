using System;
using System.Collections.Generic;
using System.Linq;

namespace BeastsV3.Analytics.Web;

// Rolling statistics and A/B comparison over map records.
public static class CompareStats
{
    // Averages, percentiles and extremes over the most recent `window` maps.
    public static RollingStats BuildRollingStats(IReadOnlyList<MapAnalyticsRecord> history, int window)
    {
        var slice = (history ?? [])
            .OrderByDescending(x => x.CompletedAtUtc)
            .Take(Math.Max(1, window))
            .ToArray();

        if (slice.Length == 0) return new RollingStats();

        var capturedValues = slice.Select(x => x.CapturedChaos).OrderBy(x => x).ToArray();
        var avgCaptured = slice.Average(x => x.CapturedChaos);
        var variance = slice.Select(x =>
        {
            var diff = x.CapturedChaos - avgCaptured;
            return diff * diff;
        }).Average();

        var best = slice.OrderByDescending(x => x.CapturedChaos).First();
        var worst = slice.OrderBy(x => x.CapturedChaos).First();

        return new RollingStats
        {
            WindowMapCount = slice.Length,
            AvgCapturedChaos = avgCaptured,
            AvgNetChaos = slice.Average(x => x.NetChaos),
            AvgRedsPerMap = slice.Average(x => x.RedBeastsFound),
            AvgDurationSeconds = slice.Average(x => x.DurationSeconds),
            MedianCapturedChaos = Percentile(capturedValues, 0.5d),
            P90CapturedChaos = Percentile(capturedValues, 0.9d),
            P95CapturedChaos = Percentile(capturedValues, 0.95d),
            VarianceCapturedChaos = variance,
            StdDevCapturedChaos = Math.Sqrt(variance),
            BestCapturedChaos = best.CapturedChaos,
            WorstCapturedChaos = worst.CapturedChaos,
            BestAreaName = best.AreaName ?? string.Empty,
            WorstAreaName = worst.AreaName ?? string.Empty,
        };
    }

    // Compares two saved sessions, optionally matching areas and trimming outliers.
    public static CompareSessionsResponse Compare(SavedSessionData aData, SavedSessionData bData, CompareSessionsRequest request)
    {
        if (aData == null || bData == null)
            return new CompareSessionsResponse { Success = false, Code = "not_found", Message = "Session not found." };

        var aMaps = (aData.MapHistory ?? []).ToList();
        var bMaps = (bData.MapHistory ?? []).ToList();

        if (request?.MatchAreas == true)
        {
            var aAreas = new HashSet<string>(aMaps.Select(GetAreaKey), StringComparer.OrdinalIgnoreCase);
            var bAreas = new HashSet<string>(bMaps.Select(GetAreaKey), StringComparer.OrdinalIgnoreCase);
            aMaps = aMaps.Where(x => bAreas.Contains(GetAreaKey(x))).ToList();
            bMaps = bMaps.Where(x => aAreas.Contains(GetAreaKey(x))).ToList();
        }

        var trimPercent = Math.Clamp(request?.TrimPercent ?? 0, 0, 45);
        if (trimPercent > 0)
        {
            aMaps = TrimMapsByNet(aMaps, trimPercent);
            bMaps = TrimMapsByNet(bMaps, trimPercent);
        }

        var a = BuildMetrics(aMaps);
        var b = BuildMetrics(bMaps);
        var delta = new CompareSessionMetrics
        {
            Count = b.Count - a.Count,
            DurationSeconds = b.DurationSeconds - a.DurationSeconds,
            CapturedChaos = b.CapturedChaos - a.CapturedChaos,
            CostChaos = b.CostChaos - a.CostChaos,
            NetChaos = b.NetChaos - a.NetChaos,
            Reds = b.Reds - a.Reds,
            NetPerMinuteChaos = b.NetPerMinuteChaos - a.NetPerMinuteChaos,
            CapturedPerMinuteChaos = b.CapturedPerMinuteChaos - a.CapturedPerMinuteChaos,
            NetPerMapChaos = b.NetPerMapChaos - a.NetPerMapChaos,
            CapturedPerMapChaos = b.CapturedPerMapChaos - a.CapturedPerMapChaos,
            CostPerMapChaos = b.CostPerMapChaos - a.CostPerMapChaos,
            RedsPerMap = b.RedsPerMap - a.RedsPerMap,
        };

        var minMaps = Math.Max(1, request?.MinMaps ?? 30);
        var sampleOk = a.Count >= minMaps && b.Count >= minMaps;
        var winner = delta.NetPerMinuteChaos >= 0 ? "B" : "A";

        return new CompareSessionsResponse
        {
            Success = true,
            Code = "ok",
            Message = sampleOk ? "Comparison complete." : $"Low sample size. Need at least {minMaps} maps per bucket.",
            SampleOk = sampleOk,
            Recommendation = $"Bucket {winner} has better net/min.",
            SessionA = a,
            SessionB = b,
            Delta = delta,
        };
    }

    // Totals and per-map/per-minute rates for a set of maps.
    private static CompareSessionMetrics BuildMetrics(IReadOnlyList<MapAnalyticsRecord> maps)
    {
        var count = maps?.Count ?? 0;
        var duration = maps?.Sum(x => x.DurationSeconds) ?? 0d;
        var captured = maps?.Sum(x => x.CapturedChaos) ?? 0d;
        var cost = maps?.Sum(x => x.CostChaos) ?? 0d;
        var net = maps?.Sum(x => x.NetChaos) ?? 0d;
        var reds = maps?.Sum(x => x.RedBeastsFound) ?? 0d;
        var minutes = duration / 60d;

        return new CompareSessionMetrics
        {
            Count = count,
            DurationSeconds = duration,
            CapturedChaos = captured,
            CostChaos = cost,
            NetChaos = net,
            Reds = reds,
            NetPerMinuteChaos = minutes > 0 ? net / minutes : 0d,
            CapturedPerMinuteChaos = minutes > 0 ? captured / minutes : 0d,
            NetPerMapChaos = count > 0 ? net / count : 0d,
            CapturedPerMapChaos = count > 0 ? captured / count : 0d,
            CostPerMapChaos = count > 0 ? cost / count : 0d,
            RedsPerMap = count > 0 ? reds / count : 0d,
        };
    }

    // Drops the highest and lowest `trimPercent` of maps by net chaos.
    private static List<MapAnalyticsRecord> TrimMapsByNet(IReadOnlyList<MapAnalyticsRecord> maps, int trimPercent)
    {
        if (maps == null || maps.Count == 0 || trimPercent <= 0)
            return maps?.ToList() ?? [];

        var cut = (int)Math.Floor(maps.Count * trimPercent / 100d);
        if (cut <= 0) return maps.ToList();

        var sorted = maps.OrderBy(x => x.NetChaos).ToArray();
        var start = Math.Min(cut, sorted.Length - 1);
        var end = Math.Max(start + 1, sorted.Length - cut);

        return sorted.Skip(start).Take(end - start).ToList();
    }

    // Normalized area name, falling back to the area hash.
    private static string GetAreaKey(MapAnalyticsRecord map)
    {
        var area = map?.AreaName;
        if (!string.IsNullOrWhiteSpace(area)) return area.Trim().ToLowerInvariant();
        return (map?.AreaHash ?? string.Empty).Trim().ToLowerInvariant();
    }

    // Linearly interpolated percentile of a sorted list.
    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues == null || sortedValues.Count == 0) return 0d;
        if (sortedValues.Count == 1) return sortedValues[0];

        var rank = percentile * (sortedValues.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedValues[lower];

        var weight = rank - lower;
        return sortedValues[lower] + (sortedValues[upper] - sortedValues[lower]) * weight;
    }
}
