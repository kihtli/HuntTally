using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace HuntTally;

public enum MarkRank : byte
{
    Unknown = 0,
    B = 1,
    A = 2,
    S = 3,
    SS = 4,
}

public readonly record struct MarkInfo(uint NameId, string Name, MarkRank Rank);

/// <summary>
/// A kill the plugin has decided to count, as published to
/// <see cref="KillTracker.OnKill"/> and over IPC.
///
/// Carries the context that cannot be re-derived after the fact: a kill can be
/// held for up to eight seconds waiting on the game's reward confirmation, by
/// which time the player may have left the zone, so the territory and the time
/// of death travel with the event rather than being read when it is handled.
/// </summary>
public readonly record struct KillDetail(
    MarkInfo Mark, uint TerritoryId, uint InstanceId, string? Expansion, string World, DateTime Time);

/// <summary>
/// Builds a lookup of every hunt mark in the game straight from the
/// NotoriousMonster Excel sheet. Doing it this way rather than hardcoding a
/// name list means new marks are picked up automatically on patch day.
/// </summary>
public static class MarkData
{
    private static Dictionary<uint, MarkInfo>? cache;

    /// <summary>Keyed by BNpcName row id, which is what IBattleNpc.NameId returns.</summary>
    public static IReadOnlyDictionary<uint, MarkInfo> Marks => cache ??= Build();

    private static Dictionary<uint, MarkInfo> Build()
    {
        var result = new Dictionary<uint, MarkInfo>();

        var sheet = Service.Data.GetExcelSheet<NotoriousMonster>();
        if (sheet is null)
        {
            Service.Log.Error("Could not load the NotoriousMonster sheet; mark detection is disabled.");
            return result;
        }

        // Resolved once rather than per row: GetExcelSheet is a lookup, not a
        // parse, but there is no reason to repeat it a few thousand times.
        var nameSheet = Service.Data.GetExcelSheet<BNpcName>();
        if (nameSheet is null)
        {
            Service.Log.Error("Could not load the BNpcName sheet; mark detection is disabled.");
            return result;
        }

        foreach (var row in sheet)
        {
            var nameId = row.BNpcName.RowId;
            if (nameId == 0)
                continue;

            var nameRow = nameSheet.GetRowOrDefault(nameId);
            if (nameRow is null)
                continue;

            // Lumina API note: on older Dalamud API levels this is
            // .Singular.ToDalamudString().TextValue instead.
            var name = Capitalise(nameRow.Value.Singular.ExtractText());
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var rank = row.Rank switch
            {
                1 => MarkRank.B,
                2 => MarkRank.A,
                3 => MarkRank.S,
                4 => MarkRank.SS,
                _ => MarkRank.Unknown,
            };

            // Several marks share a BNpcName row across ranks in odd cases;
            // first write wins, which is fine since the rank is identical.
            result.TryAdd(nameId, new MarkInfo(nameId, name, rank));
        }

        Service.Log.Information($"Loaded {result.Count} hunt marks from Excel.");
        return result;
    }

    private static string Capitalise(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return char.ToUpperInvariant(value[0]) + value[1..];
    }

    public static string RankLabel(MarkRank rank) => rank switch
    {
        MarkRank.B => "B",
        MarkRank.A => "A",
        MarkRank.S => "S",
        MarkRank.SS => "SS",
        _ => "?",
    };
}
