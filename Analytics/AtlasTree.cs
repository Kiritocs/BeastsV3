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
    // Node ids are hardcoded on purpose - they do not change between leagues.
    // tools/check-atlas-nodes.py validates them against GGG's tree export each league.

    // Natural Selection notable.
    private const ushort NaturalSelection = 23092;

    // Natural Selection wheel: 6 x "5% chance to contain an additional Red Beast" = 30%.
    private static readonly ushort[] NaturalSelectionWheel =
        [2624, 11142, 46840, 55725, 60761, 62888];

    // Big Game notable: yellow beasts have 15% chance to be replaced with red beasts.
    private const ushort BigGame = 13129;

    // Big Game smalls: 2 x "1 additional Yellow Beast" = +2.
    private static readonly ushort[] BigGameWheel = [16493, 25272];

    // Mighty Hunter notable. No spawn effect, but part of the reference tree.
    private const ushort MightyHunter = 1176;

    // The Hunt for X: "Red Beasts have 100% increased chance to be from <classification>".
    // These bias which beast appears, so they disqualify a map from the baseline cohort.
    public const ushort HuntForCraiceann = 11194; // The Deep
    public const ushort HuntForFarrul = 52011;    // The Wilds
    public const ushort HuntForFenumus = 31941;   // The Caverns
    public const ushort HuntForSaqawal = 46353;   // The Sands

    private static readonly ushort[] ClassificationNodes =
        [HuntForCraiceann, HuntForFarrul, HuntForFenumus, HuntForSaqawal];

    // Cohort A: rates are measured under this tree, not an unmodified one.
    private static readonly ushort[] ReferenceRequired =
        [NaturalSelection, .. NaturalSelectionWheel, BigGame, .. BigGameWheel, MightyHunter];

    public static IReadOnlyList<ushort> ReferenceTreeRequiredNodes => ReferenceRequired;
    public static IReadOnlyList<ushort> ClassificationNodeIds => ClassificationNodes;

    // Maps a classification node to the beast family it boosts.
    public static string ClassificationFamily(ushort nodeId) => nodeId switch
    {
        HuntForCraiceann => "The Deep",
        HuntForFarrul => "The Wilds",
        HuntForFenumus => "The Caverns",
        HuntForSaqawal => "The Sands",
        _ => null,
    };

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
    //   B - reference tree + exactly one Hunt node   -> that family's multiplier
    //   C - reference tree + two or more Hunt nodes  -> how the boosts stack
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
