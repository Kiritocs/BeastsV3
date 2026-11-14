using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Shared;
using ExileCore;

namespace BeastsV3.Analytics;

// Reads the allocated atlas passives (ServerData.AtlasPassiveSkillIds) and classifies the
// tree into a data-collection cohort. The read is wrapped: ServerData is null outside a
// game session, and analytics must never take the plugin down.
public static class AtlasTree
{
    // Node ids from GGG's 3.30.0 atlas tree export (the 3.30 rework replaced every older id).
    // tools/check-atlas-nodes.py validates them against the export each league.

    // Endangered Species notable: beasts are more likely to be less common varieties.
    private const ushort EndangeredSpecies = 11040;

    // Its path: 3 x "8% chance to contain an additional Red Beast" = 24%.
    private static readonly ushort[] EndangeredSpeciesPath = [10514, 14008, 61936];

    // Herd Mentality notable: yellow beasts have 25% chance to be replaced with red beasts.
    private const ushort HerdMentality = 49338;

    // Its path: 3 x "1 additional Yellow Beast" = +3.
    private static readonly ushort[] HerdMentalityPath = [2231, 4439, 29464];

    // Hunting Grounds: "Red Beasts have 100% increased chance to be from <chosen family>".
    // It biases which beast appears, so it disqualifies a map from the baseline cohort. The
    // family is a choice on the node and cannot be told from the node id. The export lists the
    // choices as variant effects, not nodes: 16876 The Deep, 36075 The Wilds, 31688 The Caverns,
    // 31858 The Sands.
    public const ushort HuntingGrounds = 7018;

    private static readonly ushort[] ClassificationNodes = [HuntingGrounds];

    // Cohort A: rates are measured under this tree, not an unmodified one.
    private static readonly ushort[] ReferenceRequired =
        [EndangeredSpecies, .. EndangeredSpeciesPath, HerdMentality, .. HerdMentalityPath];

    public static IReadOnlyList<ushort> ReferenceTreeRequiredNodes => ReferenceRequired;
    public static IReadOnlyList<ushort> ClassificationNodeIds => ClassificationNodes;

    // Display name of a classification node.
    public static string ClassificationFamily(ushort nodeId) =>
        nodeId == HuntingGrounds ? "Hunting Grounds" : null;

    // Reads the allocated atlas passives. Empty array when unavailable, so a failed read degrades.
    public static ushort[] ReadAllocatedNodes(GameController game)
    {
        try
        {
            var ids = game?.Game?.IngameState?.ServerData?.AtlasPassiveSkillIds;
            if (ids == null) return [];

            return ids.Distinct().OrderBy(x => x).ToArray();
        }
        catch (Exception ex)
        {
            Log.Debug($"Atlas passive read failed. {ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    // Builds the per-map snapshot. Capture when a map opens, not when it closes
    public static AtlasSnapshot Capture(GameController game)
    {
        var nodes = ReadAllocatedNodes(game);
        return new AtlasSnapshot
        {
            AllocatedNodes = nodes,
            TreeUrl = nodes.Length > 0 ? AtlasTreeUrl.Encode(nodes) : string.Empty,
            Cohort = ClassifyCohort(nodes),
            IsStrictReferenceMatch = IsStrictReferenceMatch(nodes),
        };
    }

    // Assigns the data-collection cohort from the allocated set, so there is nothing to spoof.
    //
    //   A - reference tree, no classification nodes  -> per-beast rates
    //   B - reference tree + one classification node    -> that family's multiplier
    //   C - reference tree + two or more of them        -> how the boosts stack
    //   D - anything else                            -> counts, tier curves, EV only
    public static string ClassifyCohort(IReadOnlyCollection<ushort> nodes)
    {
        if (nodes == null || nodes.Count == 0) return "unknown";

        var set = nodes as HashSet<ushort> ?? [.. nodes];

        // Required nodes must all be present; extra non-classification nodes are fine,
        // since they change how many beasts spawn rather than which.
        if (!ReferenceRequired.All(set.Contains)) return "D";

        var classificationCount = ClassificationNodes.Count(set.Contains);
        return classificationCount switch
        {
            0 => "A",
            1 => "B",
            _ => "C",
        };
    }

    // True when the tree is exactly the reference set and nothing else. Recorded so
    // cohort purity can be tightened later without recollecting data.
    public static bool IsStrictReferenceMatch(IReadOnlyCollection<ushort> nodes)
    {
        if (nodes == null || nodes.Count != ReferenceRequired.Length) return false;
        var set = nodes as HashSet<ushort> ?? [.. nodes];
        return ReferenceRequired.All(set.Contains);
    }

    // Human-readable status for the settings banner.
    public static string DescribeCohort(AtlasSnapshot snapshot)
    {
        if (snapshot == null || snapshot.AllocatedNodes.Length == 0)
            return "Atlas tree not readable yet.";

        var set = new HashSet<ushort>(snapshot.AllocatedNodes);

        switch (snapshot.Cohort)
        {
            case "A":
                return "Reference tree - contributing to baseline spawn rates.";
            case "B":
                {
                    var node = ClassificationNodes.First(set.Contains);
                    return $"Reference tree + {ClassificationFamily(node)} boost - " +
                           "contributing to that classification's multiplier.";
                }
            case "C":
                {
                    var families = ClassificationNodes.Where(set.Contains)
                        .Select(ClassificationFamily);
                    return $"Reference tree + {string.Join(", ", families)} - " +
                           "contributing to classification stacking data.";
                }
            default:
                {
                    var missing = ReferenceRequired.Count(x => !set.Contains(x));
                    return $"Off-reference tree ({missing} reference node(s) missing) - " +
                           "contributing to beast counts and EV data.";
                }
        }
    }
}
