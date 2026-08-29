using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace HuntTally;

/// <summary>
/// Watches the object table for hunt marks and counts a kill when one we have
/// personally seen alive drops to 0 HP.
///
/// Why this approach rather than hooking the death packet: the object table is
/// stable public API that does not need opcode updates every patch. The cost is
/// that we poll, so a mark that dies and despawns inside one poll interval could
/// be missed. 250ms is comfortably faster than the game's corpse despawn.
/// </summary>
public sealed class KillTracker : IDisposable
{
    /// <summary>
    /// Per-object state.
    ///
    /// Note that <see cref="SawAlive"/> and <see cref="Resolved"/> are separate
    /// on purpose. Up to 1.2 a single Counted flag meant both "already scored"
    /// and "decided not to score", and the dead-on-first-sight branch set it -
    /// so any object whose HP read 0 before the server populated its stats was
    /// permanently disabled, and its real death minutes later was dropped in
    /// silence. Not-yet-seen-alive is now simply an undecided state.
    /// </summary>
    private sealed class Tracked
    {
        /// <summary>Observed at above 0 HP at least once, so not someone else's corpse.</summary>
        public bool SawAlive;

        /// <summary>Player was in combat while the mark was alive and nearby.</summary>
        public bool PlayerInCombat;

        /// <summary>The mark's own combat flag was set while it was alive.</summary>
        public bool MarkInCombat;

        /// <summary>Player targeted the mark, or it targeted the player.</summary>
        public bool Targeted;

        /// <summary>
        /// One of the local player's actions resolved against this mark. When
        /// the damage hook is live this is the only signal that grants credit,
        /// because it is the same thing the game uses.
        /// </summary>
        public bool Damaged;

        /// <summary>Scoring decision made - counted or deliberately rejected.</summary>
        public bool Resolved;

        /// <summary>
        /// The death has been published to the mark-death feed. Separate from
        /// <see cref="Resolved"/> because that feed fires before any credit
        /// decision, and the credit decision can stay pending for seconds or
        /// be retried across several polls.
        /// </summary>
        public bool DeathAnnounced;

        public DateTime LastSeen;
    }

    /// <summary>A death waiting on the game to confirm it rewarded us.</summary>
    private sealed class PendingKill
    {
        public MarkInfo Info;
        public CharacterProfile Profile = null!;
        public uint Territory;

        /// <summary>Carried for IPC subscribers only; not part of the stored tally.</summary>
        public uint InstanceId;
        public string World = string.Empty;
        public string? Expansion;
        public DateTime DiedAtUtc;
        public DateTime DiedAt;
    }

    private const double PollSeconds = 0.25;
    private const double ForgetAfterSeconds = 120;

    /// <summary>
    /// How far before an observed death a confirmation may have arrived. The
    /// game sends it as the mark dies, which can be up to a poll interval
    /// before we notice, so this only needs to cover that plus jitter.
    /// </summary>
    private const double RewardLookbackSeconds = 3;

    /// <summary>How long to wait for a confirmation before giving up on a kill.</summary>
    private const double RewardWaitSeconds = 8;

    private readonly Configuration config;
    private readonly CharacterContext characters;
    private readonly DamageWatch damage;
    private readonly RewardWatch reward;
    private readonly List<PendingKill> pending = new();
    private readonly Dictionary<ulong, Tracked> tracked = new();
    private readonly List<ulong> staleBuffer = new();
    private DateTime lastPoll = DateTime.MinValue;

    /// <summary>
    /// Latches "the player was in combat at some point since the last poll".
    /// Sampled every frame rather than once per poll - see OnUpdate.
    /// </summary>
    private bool combatSincePoll;

    public event Action<KillDetail>? OnKill;

    /// <summary>
    /// Every mark death this plugin observes, credited or not, fired as soon as
    /// the death is seen. <see cref="OnKill"/> is the credited subset and
    /// arrives later, once the game has confirmed the reward.
    ///
    /// Both are raised unconditionally; whoever consumes them decides which one
    /// matters. Nothing here is affected by the IPC settings.
    /// </summary>
    public event Action<KillDetail>? OnMarkDeath;

    /// <summary>Kills declined this session for want of a reward confirmation.</summary>
    public long DroppedUnconfirmed { get; private set; }

    public KillTracker(
        Configuration config, CharacterContext characters, DamageWatch damage, RewardWatch reward)
    {
        this.config = config;
        this.characters = characters;
        this.damage = damage;
        this.reward = reward;

        Service.Framework.Update += OnUpdate;
        Service.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        Service.Framework.Update -= OnUpdate;
        Service.ClientState.TerritoryChanged -= OnTerritoryChanged;
    }

    /// <summary>
    /// Object ids are reused across zones, so state from the old zone must not
    /// survive into the new one. 1.2 relied on LocalPlayer being null during
    /// the loading screen to do this implicitly; this says it outright.
    /// </summary>
    private void OnTerritoryChanged(uint territory)
    {
        tracked.Clear();
        damage.Clear();
    }

    private void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        // Sampled every frame, not once per poll. Killing a mark in a single
        // hit holds a combat state for a few milliseconds at most; a 4Hz sample
        // never sees it, which is exactly why those kills went uncounted.
        // Reading a condition flag is an array index, so this is free.
        if (Service.Condition[ConditionFlag.InCombat])
            combatSincePoll = true;

        var now = DateTime.UtcNow;
        if ((now - lastPoll).TotalSeconds < PollSeconds)
            return;
        lastPoll = now;

        // Before anything touches the object table. A pending kill has already
        // captured everything it needs, so a despawning corpse, a zone change
        // or a logout must not be allowed to discard one that is only waiting
        // on its confirmation.
        ResolvePending(now);
        reward.Prune(now);

        // API 15 removed IClientState.LocalPlayer; it lives on the object table now.
        var player = Service.Objects.LocalPlayer;
        if (player is null || !Service.ClientState.IsLoggedIn)
        {
            // Between zones or logged out. Drop state rather than risk
            // counting a stale entry when the object table is repopulated.
            tracked.Clear();
            damage.Clear();
            combatSincePoll = false;
            return;
        }

        // Consumed once per poll: "in combat at any point since the previous
        // poll", not "in combat at this instant".
        var inCombat = combatSincePoll;
        combatSincePoll = false;
        var playerPos = player.Position;
        var playerId = player.GameObjectId;
        var playerTarget = player.TargetObjectId;
        var territory = Service.ClientState.TerritoryType;
        var maxDistanceSq = config.MaxDistance * config.MaxDistance;

        // Indexes [0, 199] only. Battle NPCs always live in that range, so the
        // ~250 client-object slots above it are not worth walking four times a
        // second.
        foreach (var obj in Service.Objects.CharacterManagerObjects)
        {
            if (obj.ObjectKind != ObjectKind.BattleNpc)
                continue;
            if (obj is not IBattleNpc npc)
                continue;
            if (!MarkData.Marks.TryGetValue(npc.NameId, out var info))
                continue;

            // Silent: this used to log per poll per mark, which is four lines a
            // second forever for anyone with a rank switched off.
            if (!config.ShouldTrack(info.Rank))
                continue;

            // An object is in the table before the server fills in its stats.
            // Until MaxHp is populated, CurrentHp of 0 means "not loaded yet",
            // not "dead", and acting on it is how kills get lost.
            if (npc.MaxHp == 0)
                continue;

            // Distance gates acquisition only, never retention. Up to 2.0.0 an
            // out-of-range mark was skipped before its entry was even touched,
            // so tagging one and running off meant the death was never observed
            // and the kill was dropped in silence. Once a mark is being watched
            // it stays watched wherever it goes, for as long as the client
            // keeps it loaded.
            if (!tracked.TryGetValue(npc.GameObjectId, out var entry))
            {
                var distanceSq = Vector3.DistanceSquared(playerPos, npc.Position);
                if (distanceSq > maxDistanceSq)
                    continue;

                entry = new Tracked();
                tracked[npc.GameObjectId] = entry;
                Service.Log.Information(
                    $"Now watching {info.Name} ({MarkData.RankLabel(info.Rank)}), "
                    + $"{Math.Sqrt(distanceSq):F0}y away, "
                    + $"{npc.CurrentHp}/{npc.MaxHp} hp.");
            }

            // Keep the timestamp fresh even once resolved, so pruning measures
            // despawn rather than time-since-decision.
            entry.LastSeen = now;
            if (entry.Resolved)
                continue;

            // Copied onto the entry as soon as it is seen, so the signal
            // outlives DamageWatch's short memory window and is still there
            // when the mark eventually dies.
            if (damage.IsActive && damage.WasDamagedByPlayer(npc.EntityId))
                entry.Damaged = true;

            if (npc.CurrentHp > 0)
            {
                // Require seeing it alive at least once so that walking up to a
                // corpse someone else killed does not score.
                entry.SawAlive = true;

                if (inCombat)
                    entry.PlayerInCombat = true;

                // Both of these are only ever consulted by the opt-in strict
                // mode. Neither grants credit on its own.
                if (npc.StatusFlags.HasFlag(StatusFlags.InCombat))
                    entry.MarkInCombat = true;

                if (playerTarget == npc.GameObjectId || npc.TargetObjectId == playerId)
                    entry.Targeted = true;

                continue;
            }

            // HP is zero and stats are loaded: this is a death.

            // Never seen alive - most likely someone else's corpse, but it can
            // also be an object we met mid-spawn. Decide nothing and let a
            // later poll settle it.
            if (!entry.SawAlive)
                continue;

            // Published before any credit decision and independently of it, so
            // consumers tracking a train hear about a mark dying even when
            // somebody else killed it. Guarded by its own flag: the credit path
            // below can re-enter this block on later polls.
            if (!entry.DeathAnnounced)
            {
                entry.DeathAnnounced = true;

                OnMarkDeath?.Invoke(new KillDetail(
                    info, territory, ResolveInstanceId(),
                    Categories.ExpansionForTerritory(territory),
                    ResolveCurrentWorld(player), DateTime.Now));
            }

            // Combat seen during the interval in which it died counts too. For
            // a mark killed in one hit, the only combat window that ever
            // existed is inside that interval and is already over by the time
            // this poll observes the corpse. Only matters on the fallback path.
            if (inCombat)
                entry.PlayerInCombat = true;

            var rejection = RejectionReason(entry);
            if (rejection is not null)
            {
                Service.Log.Information($"{info.Name} died but {rejection}; not counting.");
                entry.Resolved = true;
                continue;
            }

            var profile = characters.Current;
            if (profile is null)
            {
                // Do not resolve here: if the profile is briefly unavailable we
                // want the next poll to retry rather than discard the kill.
                Service.Log.Warning(
                    $"{info.Name} died but no character profile is active; will retry.");
                continue;
            }

            entry.Resolved = true;

            // Resolved here rather than once per poll: this is an Excel lookup
            // and a string allocation, and it is only ever used by a kill.
            var candidate = new PendingKill
            {
                Info = info,
                Profile = profile,
                Territory = territory,
                InstanceId = ResolveInstanceId(),
                World = ResolveCurrentWorld(player),
                Expansion = Categories.ExpansionForTerritory(territory),
                DiedAtUtc = now,
                DiedAt = DateTime.Now,
            };

            // B ranks are never confirmed - they pay out through the Hunt board
            // bill, not the kill - so waiting on a message that will not come
            // would drop every one of them.
            if (!config.RequireRewardMessage || !Configuration.ExpectsRewardConfirmation(info.Rank))
            {
                Record(candidate);
                continue;
            }

            // Held until the game says it rewarded us. Everything up to here
            // establishes that we hit a mark and it died; only the log message
            // reflects the contribution threshold that decides whether it
            // actually counted.
            pending.Add(candidate);
        }

        Prune(now);
    }

    /// <summary>
    /// Matches deaths awaiting confirmation against the rewards the game has
    /// reported, and gives up on those that never get one.
    ///
    /// Runs outside the object loop because a corpse can despawn while its kill
    /// is still waiting, and the decision has to be made either way.
    /// </summary>
    private void ResolvePending(DateTime now)
    {
        if (pending.Count == 0)
            return;

        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var candidate = pending[i];

            if (reward.TryClaim(candidate.DiedAtUtc, RewardLookbackSeconds))
            {
                pending.RemoveAt(i);
                Record(candidate);
                continue;
            }

            if ((now - candidate.DiedAtUtc).TotalSeconds <= RewardWaitSeconds)
                continue;

            pending.RemoveAt(i);
            DroppedUnconfirmed++;

            // Warning rather than information: this is the plugin declining to
            // count something you killed, which is worth noticing if it starts
            // happening for a whole rank.
            Service.Log.Warning(
                $"{candidate.Info.Name} ({MarkData.RankLabel(candidate.Info.Rank)}) died and you "
                + "hit it, but the game never confirmed a reward; not counting. "
                + $"({DroppedUnconfirmed} dropped this session.)");
        }
    }

    private void Record(PendingKill candidate)
    {
        config.RecordKill(
            candidate.Profile, candidate.Info, candidate.Territory,
            candidate.Expansion, candidate.World, candidate.DiedAt);

        OnKill?.Invoke(new KillDetail(
            candidate.Info, candidate.Territory, candidate.InstanceId,
            candidate.Expansion, candidate.World, candidate.DiedAt));

        Service.Log.Information(
            $"Counted a kill: {candidate.Info.Name} ({MarkData.RankLabel(candidate.Info.Rank)})"
            + (candidate.Expansion is null ? "." : $", {candidate.Expansion}."));
    }

    /// <summary>
    /// The public instance number of the current zone, or 0 when the zone is
    /// not instanced.
    ///
    /// Captured at the moment of the kill rather than read when the event is
    /// handled: a kill can be held for several seconds waiting on the reward
    /// confirmation, and the player may have moved instance by then.
    /// </summary>
    private static unsafe uint ResolveInstanceId()
    {
        try
        {
            var ui = UIState.Instance();
            if (ui is null)
                return 0;

            return ui->PublicInstance.IsInstancedArea() ? ui->PublicInstance.InstanceId : 0u;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Could not read the public instance number.");
            return 0;
        }
    }

    private static string ResolveCurrentWorld(IPlayerCharacter player)
    {
        try
        {
            return player.CurrentWorld.Value.Name.ExtractText();
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Could not resolve current world.");
            return string.Empty;
        }
    }

    /// <summary>
    /// Why this kill should not be counted, or null to count it.
    ///
    /// With the damage hook live, the test is "did one of my actions resolve
    /// against it". That is immune to both failure modes of the fallback: it
    /// does not miss a mark killed in a single hit, and it does not accept a
    /// mark someone else killed while you merely had it targeted.
    ///
    /// It is still not the game's own rule. The game additionally requires a
    /// contribution threshold that is not documented and not readable here, so
    /// a single glancing hit on an S rank counts here while the game awards
    /// nothing. This over-counts in that case.
    ///
    /// Without the hook it degrades to the pre-2.1 heuristic rather than
    /// counting nothing.
    /// </summary>
    private string? RejectionReason(Tracked entry)
    {
        if (!config.RequireCombat)
            return null;

        if (damage.IsActive && config.UseDamageDetection)
            return entry.Damaged ? null : "none of your actions ever hit it";

        if (!entry.PlayerInCombat)
            return "you were never in combat near it";

        if (config.StrictCredit && !(entry.MarkInCombat && entry.Targeted))
            return $"strict credit was not met (mark in combat: {entry.MarkInCombat}, "
                   + $"targeted: {entry.Targeted})";

        return null;
    }

    private void Prune(DateTime now)
    {
        damage.Prune(now);

        if (tracked.Count == 0)
            return;

        staleBuffer.Clear();
        foreach (var (id, entry) in tracked)
        {
            if ((now - entry.LastSeen).TotalSeconds > ForgetAfterSeconds)
                staleBuffer.Add(id);
        }

        foreach (var id in staleBuffer)
            tracked.Remove(id);
    }
}
