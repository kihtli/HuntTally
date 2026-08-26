using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace HuntTally;

/// <summary>
/// Reads achievement progress and uses it to raise category baselines, never
/// to lower them.
///
/// An achievement reports one cumulative number with no per-mark breakdown, so
/// the value is stored as a baseline that sits alongside counted kills rather
/// than being distributed across the mark table.
/// </summary>
public sealed class AchievementSeeder : IDisposable
{
    private const double RequestTimeoutSeconds = 5;
    private const double GapBetweenRequestsSeconds = 1.5;

    private readonly Configuration config;
    private readonly CharacterContext characters;

    private readonly Dictionary<string, string> resolvedNames = new();
    private readonly Dictionary<string, string> outcomes = new();

    private Queue<SeedDefinition> pending = new();
    private SeedDefinition? awaiting;
    private uint awaitingId;
    private DateTime requestedAt;
    private DateTime nextRequestAt;
    private bool sawRequestedState;
    private bool running;
    private ulong pinnedContentId;

    public string Status { get; private set; } = string.Empty;
    public bool IsRunning => running;

    /// <summary>Achievement display names, resolved once and cached.</summary>
    public IReadOnlyDictionary<string, string> ResolvedNames => resolvedNames;

    /// <summary>What happened to each counter on the last run, for the settings window.</summary>
    public IReadOnlyDictionary<string, string> Outcomes => outcomes;

    public AchievementSeeder(Configuration config, CharacterContext characters)
    {
        this.config = config;
        this.characters = characters;
        Service.Framework.Update += OnUpdate;
    }

    public void Dispose() => Service.Framework.Update -= OnUpdate;

    /// <summary>
    /// Resolves every definition's achievement id from its name prefix and
    /// caches both the id and the display name. One pass over the sheet covers
    /// all nine definitions.
    /// </summary>
    public void ResolveAll()
    {
        var resolved = SeedDefinitions.ResolveAll();
        var changed = false;

        resolvedNames.Clear();

        foreach (var def in SeedDefinitions.All)
        {
            if (!resolved.TryGetValue(def.CategoryKey, out var hit))
            {
                Service.Log.Warning(
                    $"No achievement found starting with \"{def.NamePrefix}\" for {def.CategoryKey}.");
                if (config.ResolvedAchievements.Remove(def.CategoryKey))
                    changed = true;
                continue;
            }

            resolvedNames[def.CategoryKey] = hit.Name;

            if (config.ResolvedAchievements.GetValueOrDefault(def.CategoryKey) == hit.Id)
                continue;

            config.ResolvedAchievements[def.CategoryKey] = hit.Id;
            changed = true;
            Service.Log.Information($"{def.CategoryKey} -> \"{hit.Name}\" (#{hit.Id}).");
        }

        // Only touch the config when something actually moved, so a plain
        // startup does not queue a pointless write.
        if (changed)
            config.MarkChanged();
    }

    public string NameOf(string categoryKey)
    {
        if (resolvedNames.TryGetValue(categoryKey, out var name))
            return name;

        var id = config.ResolvedAchievements.GetValueOrDefault(categoryKey);
        return id == 0 ? "(not found)" : $"#{id}";
    }

    /// <summary>
    /// Begins a seeding run. Must be called on the framework thread - all the
    /// state it touches is read there.
    /// </summary>
    public void Start()
    {
        if (running)
            return;

        if (config.ResolvedAchievements.Count == 0 || resolvedNames.Count == 0)
            ResolveAll();

        var profile = characters.Current;
        if (profile is null)
        {
            Status = "Log in first: achievement progress is per-character.";
            return;
        }

        pending = new Queue<SeedDefinition>(
            SeedDefinitions.All.Where(d => config.ResolvedAchievements.ContainsKey(d.CategoryKey)));

        if (pending.Count == 0)
        {
            Status = "No achievements could be resolved.";
            return;
        }

        outcomes.Clear();
        awaiting = null;
        awaitingId = 0;
        sawRequestedState = false;
        pinnedContentId = profile.ContentId;
        nextRequestAt = DateTime.UtcNow;
        running = true;
        Status = $"Checking {pending.Count} achievements...";
    }

    private unsafe void OnUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        if (!running)
            return;

        if (!Service.ClientState.IsLoggedIn)
        {
            Abort("Not logged in.");
            return;
        }

        // A run belongs to one character. If identity changed underneath us,
        // stop rather than write one character's progress onto another.
        var profile = characters.Current;
        if (profile is not null && profile.ContentId != pinnedContentId)
        {
            Abort("Character changed; seeding stopped.");
            return;
        }

        // Null is transient - a loading screen keeps IsLoggedIn true while the
        // local player is briefly gone. Wait it out rather than abandoning the
        // run; an outstanding request still times out on its own once we
        // resume.
        if (profile is null)
            return;

        var achievement = Achievement.Instance();
        if (achievement is null)
        {
            Abort("Achievement data unavailable.");
            return;
        }

        var now = DateTime.UtcNow;

        if (awaiting is not null)
        {
            if (TryReadProgress(achievement, awaitingId, out var current))
            {
                Apply(profile, awaiting, current, IsComplete(achievement, awaitingId));
                Finish(now);
            }
            else if ((now - requestedAt).TotalSeconds > RequestTimeoutSeconds)
            {
                Service.Log.Warning($"Timed out reading achievement for {awaiting.CategoryKey}.");
                outcomes[awaiting.CategoryKey] = IsComplete(achievement, awaitingId)
                    ? "Complete - the game reported no readable progress."
                    : "No reply from the server.";
                Finish(now);
            }
            return;
        }

        if (now < nextRequestAt)
            return;

        if (pending.Count == 0)
        {
            config.LastSeeded = DateTime.Now;
            config.MarkChanged();
            config.Flush(force: true);
            running = false;
            Status = $"Seeded. Character total now {characters.Current?.GrandTotal() ?? 0}.";
            return;
        }

        var def = pending.Dequeue();
        var id = config.ResolvedAchievements[def.CategoryKey];

        try
        {
            achievement->RequestAchievementProgress(id);
            awaiting = def;
            awaitingId = id;
            requestedAt = now;
            sawRequestedState = false;
            Status = $"Reading {Categories.Label(def.CategoryKey)}...";
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"Failed to request achievement {id}.");
            outcomes[def.CategoryKey] = "Request failed.";
            Finish(now);
        }
    }

    private void Finish(DateTime now)
    {
        awaiting = null;
        awaitingId = 0;
        sawRequestedState = false;
        nextRequestAt = now.AddSeconds(GapBetweenRequestsSeconds);
    }

    /// <summary>
    /// Reads the reply for the outstanding request.
    ///
    /// The id is checked against the one we asked for. Serialising our own
    /// requests is not enough on its own: the game's own achievement window and
    /// any other plugin can request progress too, and reading someone else's
    /// reply would write a wrong baseline that <see cref="Apply"/> can never
    /// lower again.
    /// </summary>
    private unsafe bool TryReadProgress(Achievement* achievement, uint expectedId, out uint current)
    {
        current = 0;

        if (achievement->ProgressRequestState == Achievement.AchievementState.Requested)
        {
            sawRequestedState = true;
            return false;
        }

        if (!sawRequestedState)
            return false;

        if (achievement->ProgressAchievementId != expectedId)
            return false;

        current = achievement->ProgressCurrent;
        return true;
    }

    private static unsafe bool IsComplete(Achievement* achievement, uint id)
    {
        try
        {
            // The completed-achievement bitmap arrives from the server; before
            // it does, a lookup would be meaningless rather than merely wrong.
            return achievement->IsLoaded() && achievement->IsComplete((int)id);
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, $"Could not read completion state for achievement {id}.");
            return false;
        }
    }

    /// <summary>
    /// Applies a reading to the logged-in character only. Achievement progress
    /// is per-character, so seeding an account-wide pool would be meaningless:
    /// each alt has its own separate progress toward the same achievements.
    /// </summary>
    private void Apply(CharacterProfile profile, SeedDefinition def, uint achievementValue, bool complete)
    {
        var key = def.CategoryKey;
        var counted = profile.CountedFor(key);
        var baseline = profile.BaselineFor(key);
        var currentTotal = counted + baseline;

        // Kept whether or not it raises anything: this reading is the only
        // ground truth available for spotting a tally that has drifted above
        // the game's own count.
        profile.AchievementReads[key] = (int)achievementValue;
        profile.AchievementComplete[key] = complete;
        profile.AchievementReadAt = DateTime.Now;
        config.MarkChanged();

        if (achievementValue <= currentTotal)
        {
            // A finished achievement stops being a usable source: the game no
            // longer reports a running total for it, which is the exact case
            // this plugin exists to cover. Saying so is better than reporting
            // "unchanged" and letting it look like there was nothing to seed.
            outcomes[key] = complete
                ? $"Complete - no usable progress (read {achievementValue}); set the baseline by hand."
                : $"Unchanged - achievement reports {achievementValue}, tally already {currentTotal}.";

            Service.Log.Information(
                $"{profile.Display} {key}: achievement reports {achievementValue}, "
                + $"tally already {currentTotal}{(complete ? ", achievement complete" : string.Empty)}. Unchanged.");
            return;
        }

        var newBaseline = (int)achievementValue - counted;
        profile.CategoryBaselines[key] = newBaseline;
        config.MarkChanged();

        outcomes[key] = $"Raised {currentTotal} -> {achievementValue}.";

        Service.Log.Information(
            $"{profile.Display} {key}: raised {currentTotal} -> {achievementValue} "
            + $"(baseline {baseline} -> {newBaseline}).");
    }

    private void Abort(string reason)
    {
        running = false;
        pending.Clear();
        awaiting = null;
        awaitingId = 0;
        sawRequestedState = false;
        Status = reason;
    }
}
