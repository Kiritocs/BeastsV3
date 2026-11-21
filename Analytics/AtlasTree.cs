using System;
using System.Collections.Generic;
using System.Linq;
using BeastsV3.Shared;
using ExileCore;

namespace BeastsV3.Analytics;

// Reads the allocated atlas passives (ServerData.AtlasPassiveSkillIds) and says whether they
// are the reference tree. Community numbers are measured on that one tree, so that they
// describe one setup instead of a blend. Only the Bestiary nodes matter: a tree is the
// reference tree when its allocated Bestiary nodes are exactly the reference ones, and every
// other atlas node is free.
//
// The collector classifies the tree itself from the uploaded nodes (beastsv3-telemetry
// src/cohort.js); this copy is for the banner and the local record. The two lists must
// agree: beast-calculator tools/check-node-sync.py compares them.
public static class AtlasTree
{
    public const string Reference = "reference";
    public const string Hunting = "hunting";
    public const string Other = "other";
    public const string Unknown = "unknown";

    // REFERENCE-NODES: the Bestiary atlas passives of the reference tree (3.30).
    //   2231, 4439, 29464                               1 additional Yellow Beast each
    //   3311, 10514, 14008, 31522, 32041, 35180, 61936  8% chance for an additional Red Beast each
    //   6518, 23371, 36760, 46850, 47657, 57339         Red Beasts 4% chance to appear in Pairs each
    //   10574 Bonded for Life    20% chance to create a copy of captured Beasts
    //   11040 Endangered Species Beasts are more likely to be less common varieties
    //   48405 Nose-to-Tail       captured Red Beasts may not be consumed at the Blood Altar
    //   49338 Herd Mentality     Yellow Beasts have 25% chance to be replaced with Red Beasts
    private static readonly ushort[] ReferenceNodes =
    [
        2231, 3311, 4439, 6518, 10514, 10574, 11040, 14008, 23371, 29464,
        31522, 32041, 35180, 36760, 46850, 47657, 48405, 49338, 57339, 61936,
    ];

    // Hunting Grounds changes which and how many red beasts a map has, so the collector keeps
    // maps with it apart, per family. Every other atlas passive is free: Apex Predators and the
    // other Bestiary passives do not change the beasts.
    private const ushort HuntingGroundsNode = 7018;

    // Reads the allocated atlas passives. Empty when unavailable, so a failed read degrades:
    // ServerData is null outside a game session, and analytics must never take the plugin down.
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

    // Builds the per-map snapshot. Capture when a map opens, not when it closes.
    public static AtlasSnapshot Capture(GameController game)
    {
        var nodes = ReadAllocatedNodes(game);
        return new AtlasSnapshot
        {
            AllocatedNodes = nodes,
            TreeUrl = nodes.Length > 0 ? AtlasTreeUrl.Encode(nodes) : string.Empty,
            Tree = Classify(nodes),
        };
    }

    // Reference: every reference passive. Hunting: those plus Hunting Grounds.
    // Other: a reference passive is missing. Unknown: the tree could not be read.
    public static string Classify(IReadOnlyCollection<ushort> nodes)
    {
        if (nodes == null || nodes.Count == 0) return Unknown;

        var allocated = nodes as HashSet<ushort> ?? [.. nodes];
        if (!ReferenceNodes.All(allocated.Contains)) return Other;
        return allocated.Contains(HuntingGroundsNode) ? Hunting : Reference;
    }

    // One line for the overlay: what this map's tree means for the community numbers.
    public static string Describe(AtlasSnapshot snapshot)
    {
        if (snapshot == null || snapshot.AllocatedNodes.Length == 0) return "Tree: not readable";
        if (snapshot.Tree == Reference) return "Tree: reference (counts for community numbers)";
        if (snapshot.Tree == Hunting) return "Tree: reference + Hunting Grounds (counts for community numbers)";

        var allocated = new HashSet<ushort>(snapshot.AllocatedNodes);
        var missing = ReferenceNodes.Count(x => !allocated.Contains(x));
        return $"Tree: {missing} reference Bestiary node(s) missing (does not count for community numbers)";
    }
}
