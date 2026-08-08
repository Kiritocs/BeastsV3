using System;

namespace BeastsV3.Beasts;

// A trackable beast: display name, entity metadata prefixes and its item-filter fragment.
public readonly record struct TrackedBeast(string Name, string[] MetadataPatterns, string RegexFragment);

// Every beast the plugin tracks, grouped by family.
public static class BeastCatalog
{
    public static readonly TrackedBeast[] All =
    [
        // Craicic (The Deep)
        new("Craicic Croaker",       ["Metadata/Monsters/LeagueBestiary/GemFrogBestiary"],                    "c cr"),
        new("Craicic Spider Crab",   ["Metadata/Monsters/LeagueBestiary/CrabSpiderBestiary"],                 "c sp"),
        new("Craicic Maw",           ["Metadata/Monsters/LeagueBestiary/FrogBestiary"],                       "cic m"),
        new("Craicic Sand Spitter",  ["Metadata/Monsters/LeagueBestiary/SandSpitterBestiary"],                "c san"),
        new("Craicic Savage Crab",   ["Metadata/Monsters/LeagueBestiary/CrabParasiteLargeBestiary_"],         "c sav"),
        new("Craicic Shield Crab",   ["Metadata/Monsters/LeagueBestiary/ShieldCrabBestiary"],                 "c sh"),
        new("Craicic Squid",         ["Metadata/Monsters/LeagueBestiary/SeaWitchSpawnBestiary"],              "sq"),
        new("Craicic Vassal",        ["Metadata/Monsters/LeagueBestiary/ParasiticSquidBestiary"],             "c v"),
        new("Craicic Watcher",       ["Metadata/Monsters/LeagueBestiary/SquidBestiary"],                      "c wa"),

        // Farric (The Wilds)
        new("Farric Tiger Alpha",         ["Metadata/Monsters/LeagueBestiary/TigerBestiary"],                 "c ti"),
        new("Farric Wolf Alpha",          ["Metadata/Monsters/LeagueBestiary/WolfBestiary"],                  "f a"),
        new("Farric Lynx Alpha",          ["Metadata/Monsters/LeagueBestiary/LynxBestiary"],                  "c l"),
        new("Farric Flame Hellion Alpha", ["Metadata/Monsters/LeagueBestiary/HellionBestiary"],               "c fl"),
        new("Farric Frost Hellion Alpha", ["Metadata/Monsters/LeagueBestiary/HellionBestiary2"],             "c fr"),
        new("Farric Magma Hound",         ["Metadata/Monsters/LeagueBestiary/HoundBestiary"],                 "ma h"),
        new("Farric Pit Hound",           ["Metadata/Monsters/LeagueBestiary/PitbullBestiary"],               "c pi"),
        new("Farric Chieftain",           ["Metadata/Monsters/LeagueBestiary/BestiaryMonkeyChiefBlood"],      "rric c"),
        new("Farric Ape",                 ["Metadata/Monsters/LeagueBestiary/MonkeyBloodBestiary"],           "c a"),
        new("Farric Goliath",             ["Metadata/Monsters/LeagueBestiary/BestiarySpiker"],                "c gol"),
        new("Farric Goatman",             ["Metadata/Monsters/LeagueBestiary/GoatmanLeapSlamBestiary"],       "c goa"),
        new("Farric Gargantuan",          ["Metadata/Monsters/LeagueBestiary/BeastCaveBestiary"],             "c ga"),
        new("Farric Taurus",              ["Metadata/Monsters/LeagueBestiary/BestiaryBull"],                  "ic ta"),
        new("Farric Ursa",                ["Metadata/Monsters/LeagueBestiary/DropBearBestiary"],              "c u"),
        new("Vicious Hound",              ["Metadata/Monsters/LeagueBestiary/PurgeHoundBestiary"],            "s ho"),

        // Fenumal (The Caverns)
        new("Fenumal Hybrid Arachnid",  ["Metadata/Monsters/LeagueBestiary/SpiderPlatedBestiary"],           "l hy"),
        new("Fenumal Plagued Arachnid", ["Metadata/Monsters/LeagueBestiary/SpiderPlagueBestiary"],           "l pla"),
        new("Fenumal Devourer",         ["Metadata/Monsters/LeagueBestiary/RootSpiderBestiary_"],            "mal d"),
        new("Fenumal Queen",            ["Metadata/Monsters/LeagueBestiary/InsectSpawnerBestiary"],          "l q"),
        new("Fenumal Widow",            ["Metadata/Monsters/LeagueBestiary/Spider5Bestiary"],                "l w"),
        new("Fenumal Scorpion",         ["Metadata/Monsters/LeagueBestiary/BlackScorpionBestiary"],          "l sco"),
        new("Fenumal Scrabbler",        ["Metadata/Monsters/LeagueBestiary/SandLeaperBestiary"],             "l scr"),

        // Saqawine (The Sands)
        new("Saqawine Rhex",        ["Metadata/Monsters/LeagueBestiary/Avians/MarakethBirdBestiary"],       "e rhe"),
        new("Saqawine Vulture",     ["Metadata/Monsters/LeagueBestiary/VultureBestiary"],                    "e vu"),
        new("Saqawine Cobra",       ["Metadata/Monsters/LeagueBestiary/SnakeBestiary1"],                     "ne co"),
        new("Saqawine Blood Viper", ["Metadata/Monsters/LeagueBestiary/SnakeBestiary2"],                     "ne b"),
        new("Saqawine Retch",       ["Metadata/Monsters/LeagueBestiary/KiwethBestiary"],                     "ne re"),
        new("Saqawine Rhoa",        ["Metadata/Monsters/LeagueBestiary/RhoaBestiary"],                       "ine rho"),
        new("Saqawine Chimeral",    ["Metadata/Monsters/LeagueBestiary/IguanaBestiary"],                     "ne ch"),

        // Spirit Bosses
        new("Saqawal, First of the Sky",    ["Metadata/Monsters/LeagueBestiary/MarakethBirdSpiritBoss"],      "al, f"),
        new("Craiceann, First of the Deep", ["Metadata/Monsters/LeagueBestiary/NessaCrabBestiarySpiritBoss"], "n, f"),
        new("Farrul, First of the Plains",  ["Metadata/Monsters/LeagueBestiary/TigerBestiarySpiritBoss"],     "ul, f"),
        new("Fenumus, First of the Night",  ["Metadata/Monsters/LeagueBestiary/SpiderPlatedBestiarySpiritBoss"], "s, f"),

        // Harvest T3 & special
        new("Wild Bristle Matron",   ["Metadata/Monsters/LeagueHarvest/Red/HarvestBeastT3MemoryLine_", "Metadata/Monsters/LeagueHarvest/Red/HarvestBeastT3"], "le m"),
        new("Wild Hellion Alpha",    ["Metadata/Monsters/LeagueHarvest/Red/HarvestHellionT3MemoryLine", "Metadata/Monsters/LeagueHarvest/Red/HarvestHellionT3"], "ld h"),
        new("Wild Brambleback",      ["Metadata/Monsters/LeagueHarvest/Red/HarvestBrambleHulkT3MemoryLine", "Metadata/Monsters/LeagueHarvest/Red/HarvestBrambleHulkT3"], "d bra"),
        new("Primal Cystcaller",     ["Metadata/Monsters/LeagueHarvest/Blue/HarvestGoatmanT3MemoryLine", "Metadata/Monsters/LeagueHarvest/Blue/HarvestGoatmanT3"], "cy"),
        new("Primal Rhex Matriarch", ["Metadata/Monsters/LeagueHarvest/Blue/HarvestRhexT3MemoryLine", "Metadata/Monsters/LeagueHarvest/Blue/HarvestRhexT3"], "x ma"),
        new("Primal Crushclaw",      ["Metadata/Monsters/LeagueHarvest/Blue/HarvestNessaCrabT3MemoryLine_", "Metadata/Monsters/LeagueHarvest/Blue/HarvestNessaCrabT3"], "l cru"),
        new("Vivid Vulture",         ["Metadata/Monsters/LeagueHarvest/Green/HarvestVultureParasiteT3MemoryLine", "Metadata/Monsters/LeagueHarvest/Green/HarvestVultureParasiteT3"], "id v"),
        new("Vivid Watcher",         ["Metadata/Monsters/LeagueHarvest/Green/HarvestSquidT3MemoryLine_", "Metadata/Monsters/LeagueHarvest/Green/HarvestSquidT3_"], "id w"),
        new("Vivid Abberarach",      ["Metadata/Monsters/LeagueHarvest/Green/HarvestPlatedScorpionT3MemoryLine", "Metadata/Monsters/LeagueHarvest/Green/HarvestPlatedScorpionT3"], "d ab"),
        new("Black Mórrigan",        ["Metadata/Monsters/LeagueAzmeri/GullGoliathBestiary_"],            "k m"),
    ];

    // Returns the family label for a beast name, or "Other".
    public static string GetFamily(string beastName)
    {
        if (string.IsNullOrWhiteSpace(beastName)) return "Other";

        if (beastName.StartsWith("Craicic", StringComparison.OrdinalIgnoreCase))
            return "The Deep";

        if (beastName.StartsWith("Farric", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(beastName, "Vicious Hound", StringComparison.OrdinalIgnoreCase))
            return "The Wilds";

        if (beastName.StartsWith("Fenumal", StringComparison.OrdinalIgnoreCase))
            return "The Caverns";

        if (beastName.StartsWith("Saqawine", StringComparison.OrdinalIgnoreCase))
            return "The Sands";

        if (beastName.StartsWith("Saqawal,", StringComparison.OrdinalIgnoreCase) ||
            beastName.StartsWith("Craiceann,", StringComparison.OrdinalIgnoreCase) ||
            beastName.StartsWith("Farrul,", StringComparison.OrdinalIgnoreCase) ||
            beastName.StartsWith("Fenumus,", StringComparison.OrdinalIgnoreCase))
            return "Spirit Bosses";

        if (string.Equals(beastName, "Wild Bristle Matron", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(beastName, "Wild Hellion Alpha", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(beastName, "Wild Brambleback", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(beastName, "Primal Cystcaller", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(beastName, "Primal Rhex Matriarch", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(beastName, "Black Mórrigan", StringComparison.OrdinalIgnoreCase))
            return "The Wilds";

        if (string.Equals(beastName, "Primal Crushclaw", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(beastName, "Vivid Watcher", StringComparison.OrdinalIgnoreCase))
            return "The Deep";

        if (string.Equals(beastName, "Vivid Vulture", StringComparison.OrdinalIgnoreCase))
            return "The Sands";

        if (string.Equals(beastName, "Vivid Abberarach", StringComparison.OrdinalIgnoreCase))
            return "The Caverns";

        return "Other";
    }
}
