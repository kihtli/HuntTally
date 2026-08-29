using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Configuration;
using Newtonsoft.Json;

namespace HuntTally;

[Serializable]
public class MarkRecord
{
    public uint NameId { get; set; }
    public string Name { get; set; } = string.Empty;
    public MarkRank Rank { get; set; }
    public int Count { get; set; }
    public DateTime FirstKill { get; set; }
    public DateTime LastKill { get; set; }
}

[Serializable]
public class KillEntry
{
    public DateTime Time { get; set; }
    public string Name { get; set; } = string.Empty;
    public MarkRank Rank { get; set; }
    public uint TerritoryId { get; set; }
    public string Expansion { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
}

/// <summary>
/// One character's tally. Counters live here rather than globally because
/// achievements are per-character: seeding reads the logged-in character's
/// progress, and mixing that into a shared pool would produce nonsense.
/// </summary>
[Serializable]
public class CharacterProfile
{
    public ulong ContentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string World { get; set; } = string.Empty;
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    public Dictionary<uint, MarkRecord> Records { get; set; } = new();
    public Dictionary<string, int> CategoryCounted { get; set; } = new();
    public Dictionary<string, int> CategoryBaselines { get; set; } = new();
    public List<KillEntry> History { get; set; } = new();

    /// <summary>What each achievement last reported, from the most recent seeding run.</summary>
    public Dictionary<string, int> AchievementReads { get; set; } = new();

    /// <summary>
    /// Which achievements were already complete when last read. A finished
    /// achievement stops reporting a running total, so it cannot be compared
    /// against and is excluded from drift.
    /// </summary>
    public Dictionary<string, bool> AchievementComplete { get; set; } = new();

    public DateTime AchievementReadAt { get; set; }

    public string Display =>
        string.IsNullOrEmpty(Name) ? $"Character {ContentId:X}" : $"{Name} ({World})";

    public int CountedFor(string key) => CategoryCounted.GetValueOrDefault(key);
    public int BaselineFor(string key) => CategoryBaselines.GetValueOrDefault(key);
    public int TotalFor(string key) => CountedFor(key) + BaselineFor(key);
    public int GrandTotal() => Categories.Overall.Sum(TotalFor);

    /// <summary>
    /// How far this tally has run ahead of what the achievement last reported,
    /// or 0 when there is nothing usable to compare against.
    ///
    /// The game awards mark credit only above a contribution threshold the
    /// client cannot see, so a tally built from "one of my actions hit it"
    /// creeps upward. This is how that becomes visible instead of silent.
    ///
    /// Note the figure is only exact at the moment of the read: marks killed
    /// since then are legitimately included in it too, so it wants a fresh
    /// seeding run to be read as pure over-count.
    /// </summary>
    public int DriftFor(string key)
    {
        if (AchievementComplete.GetValueOrDefault(key))
            return 0;
        if (!AchievementReads.TryGetValue(key, out var reported))
            return 0;

        return Math.Max(0, TotalFor(key) - reported);
    }

    public bool HasAchievementReads => AchievementReads.Count > 0 || AchievementComplete.Count > 0;
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>How long a queued change may sit unwritten before it is flushed.</summary>
    private const double SaveIntervalSeconds = 20;

    public int Version { get; set; } = 4;

    /// <summary>Keyed by content id, which survives renames and world transfers.</summary>
    public Dictionary<ulong, CharacterProfile> Characters { get; set; } = new();

    // --- Settings, shared across characters ---
    public int HistoryLimit { get; set; } = 5000;
    public bool RequireCombat { get; set; } = true;

    /// <summary>
    /// Read kill credit from the player's own actions rather than from combat
    /// state. On by default; this is the accurate signal.
    ///
    /// It exists as a switch because it depends on a hook. If a patch leaves
    /// the hook loading but misbehaving - firing for some actions and not
    /// others - the symptom is kills quietly going uncounted, and there has to
    /// be a way back to the old heuristic without uninstalling.
    /// </summary>
    public bool UseDamageDetection { get; set; } = true;

    /// <summary>
    /// Additionally require that the mark's own combat flag was set and that it
    /// was in a targeting relationship with the player. Off by default: it is a
    /// genuinely tighter proxy for kill credit, but it can reject a real kill
    /// (ground-targeted damage only, or a mark that melts before you ever
    /// target it), so opting in is the user's call.
    /// </summary>
    public bool StrictCredit { get; set; }

    /// <summary>
    /// Only count a mark once the game has confirmed the reward in the log.
    ///
    /// This is the one signal that reflects the contribution threshold, so it
    /// is the only way to stop counting marks you touched but were not
    /// rewarded for. On by default, and applies only to the ranks actually
    /// rewarded on the kill - see <see cref="ExpectsRewardConfirmation"/>.
    /// </summary>
    public bool RequireRewardMessage { get; set; } = true;

    /// <summary>
    /// Whether a kill of this rank is expected to produce the game's reward
    /// confirmation, and can therefore be gated on it.
    ///
    /// Only A and S ranks are rewarded on the kill itself. B ranks pay out
    /// through the Hunt board bill handed in afterwards, so no confirmation
    /// ever arrives for one, and gating them would drop every B rank kill. The
    /// evidence: the game's LogMessage sheet contains exactly one mark reward
    /// message and no B-rank variant of it.
    ///
    /// SS is absent because NotoriousMonster has no SS entries at all - ranks
    /// 1, 2 and 3 only - so the plugin never sees one. The SS case is kept
    /// elsewhere in the code against a patch adding them, since mark data is
    /// read from the sheet rather than hardcoded. Should that happen, an SS
    /// kill buckets to S and would be gated; revisit this then.
    /// </summary>
    public static bool ExpectsRewardConfirmation(MarkRank rank) =>
        Bucket(rank) is MarkRank.A or MarkRank.S;

    public float MaxDistance { get; set; } = 100f;
    public bool ChatOnKill { get; set; } = true;

    /// <summary>
    /// Make the IPC kill feed report every mark death rather than only the
    /// marks you were credited with.
    ///
    /// This changes what the existing HuntTally.OnKill gate sends; it does not
    /// add a second gate, so a consumer receives the wider feed without any
    /// change on its side. Deaths are sent as they happen rather than after the
    /// reward confirmation, and the credited feed is suppressed while this is
    /// on, so a death still produces exactly one message.
    ///
    /// Off by default: the credited-only feed is the documented contract, and
    /// a consumer cannot tell which mode it is being sent. Turning this on
    /// changes what an already-installed consumer sees.
    ///
    /// It never touches the tally, which stays strictly credit-based.
    /// </summary>
    public bool PublishAllMarkDeaths { get; set; }
    public bool TrackB { get; set; } = true;
    public bool TrackA { get; set; } = true;
    public bool TrackS { get; set; } = true;
    public bool AutoSeedOnLogin { get; set; } = true;

    public DateTime LastSeeded { get; set; }
    public Dictionary<string, uint> ResolvedAchievements { get; set; } = new();

    // --- Version 2 fields, kept only so existing data can be migrated ---
    public Dictionary<uint, MarkRecord> Records { get; set; } = new();
    public Dictionary<string, int> CategoryCounted { get; set; } = new();
    public Dictionary<string, int> CategoryBaselines { get; set; } = new();
    public List<KillEntry> History { get; set; } = new();
    public bool LegacyMigrated { get; set; }

    // Private fields: Newtonsoft's default contract only serialises public
    // members, so none of this reaches the config file.
    private int revision;
    private bool dirty;
    private DateTime lastSaveUtc = DateTime.MinValue;

    /// <summary>
    /// Bumped on every change. Views cache their derived data against this and
    /// rebuild only when it moves, instead of recomputing on every frame.
    /// </summary>
    [JsonIgnore]
    public int Revision => revision;

    /// <summary>
    /// Records that something changed. This does NOT write to disk: writing
    /// serialises every character, mark record and history entry, and doing
    /// that inline on each kill meant a multi-megabyte synchronous write on the
    /// framework thread mid-fight. <see cref="Flush"/> does the writing.
    /// </summary>
    public void MarkChanged()
    {
        revision++;
        dirty = true;
    }

    /// <summary>
    /// Writes queued changes to disk, at most once per interval. Call with
    /// <paramref name="force"/> on logout and on plugin dispose, where waiting
    /// would mean losing the change.
    /// </summary>
    public void Flush(bool force = false)
    {
        if (!dirty)
            return;

        var now = DateTime.UtcNow;
        if (!force && (now - lastSaveUtc).TotalSeconds < SaveIntervalSeconds)
            return;

        dirty = false;
        lastSaveUtc = now;
        Service.Interface.SavePluginConfig(this);
    }

    /// <summary>
    /// Folds SS into S. SS marks award credit toward the S-rank achievements,
    /// so they share the S counter throughout - counting, seeding and display.
    /// The raw rank is still kept on every mark record and kill log entry, so
    /// the mark table can show SS for what it is.
    /// </summary>
    public static MarkRank Bucket(MarkRank rank) => rank == MarkRank.SS ? MarkRank.S : rank;

    /// <summary>
    /// The category counter a kill of this rank increments, or null for a rank
    /// we have no counter for. Marks of unknown rank are gated out by
    /// <see cref="ShouldTrack"/> long before this, so null is defensive.
    /// </summary>
    public static string? CategoryKeyFor(MarkRank rank) => Bucket(rank) switch
    {
        MarkRank.B => Categories.B,
        MarkRank.A => Categories.A,
        MarkRank.S => Categories.S,
        _ => null,
    };

    public bool ShouldTrack(MarkRank rank) => Bucket(rank) switch
    {
        MarkRank.B => TrackB,
        MarkRank.A => TrackA,
        MarkRank.S => TrackS,
        _ => false,
    };

    public CharacterProfile GetOrCreate(ulong contentId, string name, string world)
    {
        if (!Characters.TryGetValue(contentId, out var profile))
        {
            profile = new CharacterProfile
            {
                ContentId = contentId,
                FirstSeen = DateTime.Now,
            };
            Characters[contentId] = profile;
            Service.Log.Information($"Started tracking a new character: {name} ({world}).");
            MarkChanged();
        }

        // Refresh so a rename or transfer updates the label rather than
        // creating a second profile.
        if (!string.IsNullOrEmpty(name) && profile.Name != name)
        {
            profile.Name = name;
            MarkChanged();
        }

        if (!string.IsNullOrEmpty(world) && profile.World != world)
        {
            profile.World = world;
            MarkChanged();
        }

        profile.LastSeen = DateTime.Now;

        MigrateLegacyInto(profile);
        return profile;
    }

    /// <summary>
    /// Account totals cover only the characters this installation has seen.
    /// A character never logged into with the plugin, or played on another
    /// machine, is not represented.
    /// </summary>
    public int AccountTotalFor(string key) => Characters.Values.Sum(p => p.TotalFor(key));

    public int AccountGrandTotal() => Categories.Overall.Sum(AccountTotalFor);

    public int TotalFor(string key, CharacterProfile? profile) =>
        profile is null ? AccountTotalFor(key) : profile.TotalFor(key);

    public int CountedFor(string key, CharacterProfile? profile) =>
        profile is null ? Characters.Values.Sum(p => p.CountedFor(key)) : profile.CountedFor(key);

    public int BaselineFor(string key, CharacterProfile? profile) =>
        profile is null ? Characters.Values.Sum(p => p.BaselineFor(key)) : profile.BaselineFor(key);

    /// <summary>Per-mark records merged across every known character.</summary>
    public IEnumerable<MarkRecord> AggregateRecords()
    {
        var merged = new Dictionary<uint, MarkRecord>();

        foreach (var record in Characters.Values.SelectMany(p => p.Records.Values))
        {
            if (!merged.TryGetValue(record.NameId, out var target))
            {
                merged[record.NameId] = new MarkRecord
                {
                    NameId = record.NameId,
                    Name = record.Name,
                    Rank = record.Rank,
                    Count = record.Count,
                    FirstKill = record.FirstKill,
                    LastKill = record.LastKill,
                };
                continue;
            }

            target.Count += record.Count;
            if (record.FirstKill != default && (target.FirstKill == default || record.FirstKill < target.FirstKill))
                target.FirstKill = record.FirstKill;
            if (record.LastKill > target.LastKill)
                target.LastKill = record.LastKill;
        }

        return merged.Values;
    }

    public void RecordKill(
        CharacterProfile profile, MarkInfo info, uint territory, string? expansion, string world, DateTime when)
    {
        if (!profile.Records.TryGetValue(info.NameId, out var record))
        {
            record = new MarkRecord
            {
                NameId = info.NameId,
                Name = info.Name,
                Rank = info.Rank,
                FirstKill = when,
            };
            profile.Records[info.NameId] = record;
        }

        record.Count++;
        record.LastKill = when;
        if (record.FirstKill == default)
            record.FirstKill = when;

        var rankKey = CategoryKeyFor(info.Rank);
        if (rankKey is not null)
        {
            Increment(profile, rankKey);

            // B ranks have no per-expansion achievement, so no subset counter.
            if (expansion is not null && rankKey != Categories.B)
                Increment(profile, $"{expansion}.{rankKey}");
        }

        profile.History.Add(new KillEntry
        {
            Time = when,
            Name = info.Name,
            Rank = info.Rank,
            TerritoryId = territory,
            Expansion = expansion ?? string.Empty,
            World = world,
        });

        if (profile.History.Count > HistoryLimit)
            profile.History.RemoveRange(0, profile.History.Count - HistoryLimit);

        MarkChanged();
    }

    private void Increment(CharacterProfile profile, string key)
    {
        profile.CategoryCounted[key] = profile.CategoryCounted.GetValueOrDefault(key) + 1;
        Service.Log.Information(
            $"{profile.Display} {key}: counted {profile.CategoryCounted[key]}, "
            + $"baseline {profile.BaselineFor(key)}, total {profile.TotalFor(key)}.");
    }

    /// <summary>
    /// Moves version 2 data, which had no notion of characters, onto the first
    /// character seen after the upgrade.
    ///
    /// This is a guess: the old data could have come from several characters.
    /// It lands on whoever logs in first, and the manual baselines in settings
    /// are how you correct it if that guess is wrong.
    /// </summary>
    private void MigrateLegacyInto(CharacterProfile profile)
    {
        if (LegacyMigrated)
            return;

        var hasLegacy = Records.Count > 0 || CategoryCounted.Count > 0 || CategoryBaselines.Count > 0;
        if (!hasLegacy)
        {
            LegacyMigrated = true;
            MarkChanged();
            return;
        }

        foreach (var (id, record) in Records)
            profile.Records.TryAdd(id, record);

        foreach (var (key, value) in CategoryCounted)
            profile.CategoryCounted[key] = profile.CategoryCounted.GetValueOrDefault(key) + value;

        foreach (var (key, value) in CategoryBaselines)
            profile.CategoryBaselines[key] = Math.Max(profile.BaselineFor(key), value);

        profile.History.AddRange(History);

        Records.Clear();
        CategoryCounted.Clear();
        CategoryBaselines.Clear();
        History.Clear();
        LegacyMigrated = true;

        Service.Log.Information(
            $"Migrated pre-character tally onto {profile.Display}. "
            + "If that data came from more than one character, correct it in settings.");

        MarkChanged();
        Flush(force: true);
    }
}
