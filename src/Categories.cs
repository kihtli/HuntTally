using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace HuntTally;

/// <summary>
/// A counter is identified by a string key. Overall counters are "B", "A", "S".
/// Per-expansion counters are "ShB.A", "EW.S" and so on.
///
/// Expansion counters are SUBSETS of the overall counters, not additional
/// categories. A DT S-rank increments both "S" and "DT.S". The grand total
/// therefore only ever sums the overall keys.
/// </summary>
public static class Categories
{
    public const string B = "B";
    public const string A = "A";
    public const string S = "S";

    public static readonly string[] Overall = { S, A, B };

    /// <summary>ExVersion row id -> short code. Only expansions we track marks for.</summary>
    private static readonly Dictionary<uint, string> ExVersionCodes = new()
    {
        [3] = "ShB",
        [4] = "EW",
        [5] = "DT",
    };

    public static readonly string[] Expansions = { "ShB", "EW", "DT" };

    /// <summary>
    /// Territory -> expansion code, memoised. Territories repeat constantly as
    /// the player moves around, and the answer never changes within a session.
    /// </summary>
    private static readonly Dictionary<uint, string?> ExpansionCache = new();

    /// <summary>
    /// Which expansion a territory belongs to, or null if it is one we do not
    /// keep a separate counter for (ARR through StB).
    /// </summary>
    public static string? ExpansionForTerritory(uint territoryId)
    {
        if (ExpansionCache.TryGetValue(territoryId, out var cached))
            return cached;

        var code = Lookup(territoryId);
        ExpansionCache[territoryId] = code;
        return code;
    }

    private static string? Lookup(uint territoryId)
    {
        try
        {
            var row = Service.Data.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territoryId);
            if (row is null)
            {
                Service.Log.Warning($"Territory {territoryId} not found in TerritoryType.");
                return null;
            }

            var ex = row.Value.ExVersion.RowId;
            var code = ExVersionCodes.GetValueOrDefault(ex);

            if (code is null)
            {
                // Expected for ARR through StB, which have no separate counter.
                // Unexpected for a ShB/EW/DT zone, and this line is how we tell.
                Service.Log.Information(
                    $"Territory {territoryId} has ExVersion {ex}, which maps to no tracked expansion.");
            }

            return code;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, $"Could not resolve expansion for territory {territoryId}.");
            return null;
        }
    }

    public static string Label(string key) => key switch
    {
        B => "B ranks",
        A => "A ranks",
        S => "S ranks",
        _ => LabelForExpansionKey(key),
    };

    private static string LabelForExpansionKey(string key)
    {
        var parts = key.Split('.');
        return parts.Length == 2
            ? $"{parts[0]} {parts[1]} ranks"
            : $"{key} ranks";
    }
}

/// <summary>An achievement to seed a counter from, identified by name prefix.</summary>
public sealed record SeedDefinition(string CategoryKey, string NamePrefix);

/// <summary>The achievement a definition resolved to.</summary>
public sealed record ResolvedAchievement(uint Id, string Name);

public static class SeedDefinitions
{
    /// <summary>
    /// Matched by prefix rather than exact name because these achievements gain
    /// a new roman numeral tier each expansion. The highest-numbered tier is
    /// the one still accumulating, so that is the one worth reading.
    /// </summary>
    public static readonly SeedDefinition[] All =
    {
        new(Categories.B, "Straight Bs"),
        new(Categories.A, "Bring Your A Game"),
        new(Categories.S, "Bring Your S Game"),
        new("ShB.A", "Shadowbring Your A Game"),
        new("ShB.S", "Shadowbring Your S Game"),
        new("EW.A", "Take Your A Game Further"),
        new("EW.S", "Take Your S Game Further"),
        new("DT.A", "Dawn of a New A Game"),
        new("DT.S", "Dawn of a New S Game"),
    };

    private static readonly Dictionary<char, int> RomanValues = new()
    {
        ['I'] = 1, ['V'] = 5, ['X'] = 10,
    };

    /// <summary>
    /// Resolves every definition in a single pass over the Achievement sheet.
    ///
    /// The previous shape scanned the whole sheet once per definition, calling
    /// ExtractText on every row each time - nine full scans and roughly 27,000
    /// string allocations on plugin load. One pass costs a ninth of that, and
    /// the name is kept alongside the id so the settings window never has to
    /// re-read the sheet while drawing.
    /// </summary>
    public static Dictionary<string, ResolvedAchievement> ResolveAll()
    {
        var result = new Dictionary<string, ResolvedAchievement>();

        var sheet = Service.Data.GetExcelSheet<Achievement>();
        if (sheet is null)
        {
            Service.Log.Error("Could not load the Achievement sheet; seeding is unavailable.");
            return result;
        }

        var bestTier = new Dictionary<string, int>();

        foreach (var row in sheet)
        {
            string name;
            try
            {
                name = row.Name.ExtractText();
            }
            catch
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(name))
                continue;

            foreach (var def in All)
            {
                if (!name.StartsWith(def.NamePrefix, StringComparison.OrdinalIgnoreCase))
                    continue;

                var tail = name[def.NamePrefix.Length..].Trim();

                // Exact match with no numeral (e.g. "Straight Bs") counts as tier 0.
                var tier = tail.Length == 0 ? 0 : ParseRoman(tail);
                if (tier < 0)
                    continue;

                if (bestTier.TryGetValue(def.CategoryKey, out var seen) && seen >= tier)
                    continue;

                bestTier[def.CategoryKey] = tier;
                result[def.CategoryKey] = new ResolvedAchievement(row.RowId, name);
            }
        }

        return result;
    }

    /// <summary>Returns the value, or -1 if the text is not a roman numeral.</summary>
    private static int ParseRoman(string text)
    {
        var total = 0;
        var previous = 0;

        for (var i = text.Length - 1; i >= 0; i--)
        {
            if (!RomanValues.TryGetValue(char.ToUpperInvariant(text[i]), out var value))
                return -1;

            total += value < previous ? -value : value;
            previous = Math.Max(previous, value);
        }

        return total;
    }
}
