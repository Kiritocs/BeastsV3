using System;
using System.Collections.Generic;
using System.Linq;

namespace BeastsV3.Beasts;

// Maps each red beast to the talisman base type it drops and that talisman's implicit.
public static class TalismanCatalog
{
    public static readonly TalismanInfo[] All =
    [
        // Craicic (The Deep)
        new("Craicic Croaker",      "Croaker Talisman",      "+1 to Level of all Skill Gems"),
        new("Craicic Spider Crab",  "Spider Crab Talisman",  "+15% to Quality of all Skill Gems"),
        new("Craicic Maw",          "Great Maw Talisman",    "15% increased Attributes"),
        new("Craicic Sand Spitter", "Sand Spitter Talisman", "12% increased Movement Speed"),
        new("Craicic Savage Crab",  "Savage Crab Talisman",  "Damage Penetrates 15% Cold Resistance"),
        new("Craicic Shield Crab",  "Shield Crab Talisman",  "30% increased Global Defences"),
        new("Craicic Squid",        "Squid Talisman",        "30% increased maximum Mana"),
        new("Craicic Vassal",       "Octopus Talisman",      "20% chance to Freeze Enemies for 1 second when they Hit you"),
        new("Craicic Watcher",      "Watcher Talisman",      "Damage Penetrates 15% Lightning Resistance"),

        // Farric (The Wilds)
        new("Farric Tiger Alpha",         "Tiger Talisman",         "8% increased Action Speed"),
        new("Farric Wolf Alpha",          "Wolf Alpha Talisman",    "+40% to Global Critical Strike Multiplier"),
        new("Farric Lynx Alpha",          "Lynx Talisman",          "+4% to maximum Lightning Resistance"),
        new("Farric Flame Hellion Alpha", "Flame Hellion Talisman", "+4% to maximum Fire Resistance"),
        new("Farric Frost Hellion Alpha", "Frost Hellion Talisman", "+4% to maximum Cold Resistance"),
        new("Farric Magma Hound",         "Magma Hound Talisman",   "Unaffected by Ignite"),
        new("Farric Pit Hound",           "Pitbull Talisman",       "Warcries Exert 1 additional Attack"),
        new("Farric Chieftain",           "Chieftain Talisman",     "16% increased Area of Effect"),
        new("Farric Ape",                 "Ape Talisman",           "+1 to Minimum Endurance, Frenzy and Power Charges"),
        new("Farric Goliath",             "Goliath Talisman",       "Projectiles Pierce 3 additional Targets"),
        new("Farric Goatman",             "Goatman Talisman",       "Hits ignore Enemy Physical Damage Reduction"),
        new("Farric Gargantuan",          "Gargantuan Talisman",    "15% increased maximum Life"),
        new("Farric Taurus",              "Taurus Talisman",        "+1 to Maximum Endurance Charges"),
        new("Farric Ursa",                "Ursa Talisman",          "30% increased Effect of your Marks"),

        // Fenumal (The Caverns)
        new("Fenumal Hybrid Arachnid",  "Hybrid Arachnid Talisman",  "Minions have +30% to Damage over Time Multiplier"),
        new("Fenumal Plagued Arachnid", "Plagued Arachnid Talisman", "35% increased Effect of Withered"),
        new("Fenumal Devourer",         "Devourer Talisman",         "Damage Penetrates 15% Fire Resistance"),
        new("Fenumal Queen",            "Carrion Queen Talisman",    "+1 to maximum number of Spectres"),
        new("Fenumal Widow",            "Black Widow Talisman",      "Utility Flasks gain 2 Charges every 3 seconds"),
        new("Fenumal Scorpion",         "Scorpion Talisman",         "+20% to Damage over Time Multiplier"),
        new("Fenumal Scrabbler",        "Scrabbler Talisman",        "+2 to Level of all Herald Skill Gems"),

        // Saqawine (The Sands)
        new("Saqawine Rhex",        "Rhex Talisman",        "100% of Cold and Lightning Damage from Hits taken as Fire Damage"),
        new("Saqawine Vulture",     "Vulture Talisman",     "Skills fire an additional Projectile"),
        new("Saqawine Cobra",       "Cobra Talisman",       "+1 to Maximum Frenzy Charges"),
        new("Saqawine Blood Viper", "Blood Viper Talisman", "20% increased Cooldown Recovery Rate"),
        new("Saqawine Retch",       "Retch Talisman",       "+1 to Maximum Power Charges"),
        new("Saqawine Rhoa",        "Rhoa Talisman",        "Gain 15% of Maximum Life as Extra Armour"),
        new("Saqawine Chimeral",    "Chimeral Talisman",    "30% increased Projectile Speed"),

        // Spirit Bosses
        new("Saqawal, First of the Sky",    "Saqawine Talisman", "100% increased Aspect of the Avian Buff Effect"),
        new("Craiceann, First of the Deep", "Craicic Talisman",  "100% increased Aspect of the Crab Buff Effect"),
        new("Farrul, First of the Plains",  "Farric Talisman",   "100% increased Aspect of the Cat Buff Effect"),
        new("Fenumus, First of the Night",  "Fenumal Talisman",  "100% increased Aspect of the Spider Debuff Effect"),
    ];

    private static readonly Dictionary<string, TalismanInfo> ByBeast =
        All.ToDictionary(x => x.BeastName, x => x, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, TalismanInfo> ByTalismanName =
        All.ToDictionary(x => x.TalismanName, x => x, StringComparer.OrdinalIgnoreCase);

    // Looks up a talisman by beast name; false for beasts that drop none.
    public static bool TryGetByBeast(string beastName, out TalismanInfo talisman)
    {
        if (!string.IsNullOrWhiteSpace(beastName)) return ByBeast.TryGetValue(beastName, out talisman);
        talisman = default;
        return false;
    }

    // Looks up a talisman by its base-type name.
    public static bool TryGetByTalismanName(string talismanName, out TalismanInfo talisman)
    {
        if (!string.IsNullOrWhiteSpace(talismanName)) return ByTalismanName.TryGetValue(talismanName, out talisman);
        talisman = default;
        return false;
    }

    public static bool HasTalisman(string beastName) => TryGetByBeast(beastName, out _);
}

// TalismanName matches the poe.ninja BaseType string used as the pricing join key.
public readonly record struct TalismanInfo(string BeastName, string TalismanName, string Implicit);
