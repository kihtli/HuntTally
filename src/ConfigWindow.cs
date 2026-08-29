using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace HuntTally.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Configuration config;
    private readonly AchievementSeeder seeder;
    private readonly CharacterContext characters;
    private readonly DamageWatch damage;
    private readonly RewardWatch reward;
    private readonly KillTracker tracker;
    private bool confirmReset;

    public ConfigWindow(
        Configuration config, AchievementSeeder seeder, CharacterContext characters,
        DamageWatch damage, RewardWatch reward, KillTracker tracker)
        : base("Hunt Tally Settings###HuntTallyConfig")
    {
        this.config = config;
        this.seeder = seeder;
        this.characters = characters;
        this.damage = damage;
        this.reward = reward;
        this.tracker = tracker;
        Size = new Vector2(500, 600);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public void Dispose() { }

    public override void Draw()
    {
        DrawDetectionSection();

        ImGui.Separator();
        DrawRankSection();

        ImGui.Separator();
        DrawSeedingSection();

        ImGui.Separator();
        var limit = config.HistoryLimit;
        if (ImGui.InputInt("Detail log entries kept", ref limit, 500))
        {
            config.HistoryLimit = Math.Clamp(limit, 100, 100000);
            config.MarkChanged();
        }
        Tooltip("Totals are never truncated. This only limits the timestamped per-kill log.");

        ImGui.Separator();
        DrawResetSection();
    }

    private void DrawDetectionSection()
    {
        var damageInUse = DrawCreditStatus();

        var requireCombat = config.RequireCombat;
        var label = damageInUse
            ? "Only count marks I hit"
            : "Only count marks I was in combat for";

        if (ImGui.Checkbox(label, ref requireCombat))
        {
            config.RequireCombat = requireCombat;
            config.MarkChanged();
        }
        Tooltip(damageInUse
            ? "A mark counts when one of your own actions resolved against it while it was "
              + "alive - so a mark you killed in a single hit counts, and a mark someone else "
              + "killed while you merely had it targeted does not.\n\n"
              + "This is not quite the game's own rule. The game also requires a minimum "
              + "contribution before it awards seals and achievement progress, and that "
              + "threshold is not readable here. One glancing hit on an S rank will count "
              + "here even when the game gives you nothing."
            : "A proxy for kill credit, not the real thing. It cannot tell whether you did "
              + "enough damage to be rewarded, only that you were in combat while the mark was "
              + "alive and nearby.\n\n"
              + "Combat is sampled every frame rather than once per poll, so a mark killed in "
              + "a single hit - which holds combat for only a few milliseconds - is still "
              + "seen. Merely having a mark targeted is not enough on its own, or marks that "
              + "other people killed while you had them selected would count.");

        // Strict credit exists only to tighten the combat proxy. Reading the
        // player's own actions is already stricter and more accurate than
        // anything it can add, so it does nothing while that is live.
        using (ImRaii.Disabled(damageInUse))
        {
            var strict = config.StrictCredit;
            if (ImGui.Checkbox("Strict credit", ref strict))
            {
                config.StrictCredit = strict;
                config.MarkChanged();
            }
        }
        Tooltip(damageInUse
            ? "Not needed while damage detection is active - reading your own actions is "
              + "already stricter than this check."
            : "Narrows the check above: the mark must also have been in combat itself, and "
              + "targeting must have gone both ways at some point. This cuts out marks that "
              + "died nearby while you were fighting something else entirely, which the looser "
              + "check can let through.\n\n"
              + "The trade is that it can reject a real kill: a mark you only ever hit with "
              + "ground-targeted damage, or one that melts before you get a target on it. Off "
              + "by default for that reason.");

        DrawRewardConfirmation();

        var distance = config.MaxDistance;
        if (ImGui.SliderFloat("Detection radius (yalms)", ref distance, 20f, 200f, "%.0f"))
        {
            config.MaxDistance = distance;
            config.MarkChanged();
        }
        Tooltip("How close a mark has to be for the plugin to start watching it. Once it is "
                + "being watched it stays watched at any distance, for as long as the game "
                + "keeps it loaded - so tagging a mark and running off still counts.");

        var chat = config.ChatOnKill;
        if (ImGui.Checkbox("Print a chat message on each kill", ref chat))
        {
            config.ChatOnKill = chat;
            config.MarkChanged();
        }

        var allDeaths = config.PublishAllMarkDeaths;
        if (ImGui.Checkbox("Send every mark death over IPC", ref allDeaths))
        {
            config.PublishAllMarkDeaths = allDeaths;
            config.MarkChanged();
        }
        Tooltip("Changes what the plugin sends to other plugins: every mark death rather than "
                + "only the ones you were credited with, so a plugin following a hunt train "
                + "hears about a mark even when somebody else killed it.\n\n"
                + "It reuses the same feed rather than adding a second one, so a plugin "
                + "already listening picks this up with no change on its side. Deaths are "
                + "sent as they happen instead of after the reward confirmation, and the "
                + "credited feed is switched off while this is on, so nothing arrives twice."
                + "\n\nOff by default, because a listening plugin cannot tell which of the "
                + "two it is being sent. Your tally is never affected either way - it stays "
                + "strictly credit-based.");
    }

    /// <summary>
    /// Says out loud which credit signal is in use. A hook that stops firing
    /// after a patch would otherwise show up only as kills quietly going
    /// uncounted, which is exactly the failure this plugin keeps having to fix.
    /// </summary>
    /// <summary>
    /// The reward confirmation toggle, with the two counters that make it
    /// diagnosable. If a rank ever stops emitting the confirmation, the drop
    /// count is what says so before the totals quietly go wrong.
    /// </summary>
    private void DrawRewardConfirmation()
    {
        var require = config.RequireRewardMessage;
        if (ImGui.Checkbox("Only count A and S ranks the game says it rewarded", ref require))
        {
            config.RequireRewardMessage = require;
            config.MarkChanged();
        }
        Tooltip("Waits for \"You have been rewarded for your contribution in slaying the "
                + "mark.\" before counting a kill. That message reflects the game's own credit "
                + "decision, including the contribution threshold nothing else can see - so an "
                + "A or S rank you hit once and were not rewarded for stops being counted.\n\n"
                + "B ranks are exempt. They are paid through the Hunt board bill rather than "
                + "the kill, so no confirmation is ever sent for one and waiting would drop "
                + "every B rank. They are counted as soon as they die.\n\n"
                + "Matched by log message id, so it works in any client language.\n\n"
                + "The message names no mark, so confirmations are paired to deaths by time "
                + "and claimed one apiece. Two marks dying together still total two.\n\n"
                + "Turn this off if kills stop being counted - the drop counter beside it is "
                + "how you would notice.");

        ImGui.SameLine();
        if (!config.RequireRewardMessage)
        {
            ImGui.TextDisabled("(off)");
            return;
        }

        var dropped = tracker.DroppedUnconfirmed;
        if (dropped == 0)
            ImGui.TextDisabled($"({reward.Seen} confirmed)");
        else
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                $"({reward.Seen} confirmed, {dropped} dropped)");
    }

    private bool DrawCreditStatus()
    {
        if (!damage.IsActive)
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), "Damage detection: unavailable");
            ImGui.TextDisabled(damage.Status);
            Tooltip("The hook into the game's action handler could not be created, most likely "
                    + "because a game patch moved it. Kill credit falls back to the combat "
                    + "heuristic, which is what versions before 2.1 used. See the Dalamud log "
                    + "for the reason.");
            ImGui.Spacing();
            return false;
        }

        var use = config.UseDamageDetection;
        if (ImGui.Checkbox("Use damage detection", ref use))
        {
            config.UseDamageDetection = use;
            config.MarkChanged();
        }
        Tooltip("Reads kill credit from your own actions, which is the same rule the game "
                + "uses. Leave this on.\n\n"
                + "Turn it off only if it starts misbehaving after a game patch - that would "
                + "show up as the counter beside this option staying still during a fight, and "
                + "kills going uncounted. Off falls back to the combat heuristic used before "
                + "2.1.");

        ImGui.SameLine();
        if (config.UseDamageDetection)
            ImGui.TextDisabled($"({damage.EventsSeen} of your actions seen)");
        else
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), "(off - using combat fallback)");

        ImGui.Spacing();
        return config.UseDamageDetection;
    }

    private void DrawRankSection()
    {
        ImGui.Text("Ranks to track");

        var b = config.TrackB;
        if (ImGui.Checkbox("B ranks", ref b)) { config.TrackB = b; config.MarkChanged(); }
        var a = config.TrackA;
        if (ImGui.Checkbox("A ranks", ref a)) { config.TrackA = a; config.MarkChanged(); }
        var s = config.TrackS;
        if (ImGui.Checkbox("S and SS ranks", ref s)) { config.TrackS = s; config.MarkChanged(); }
        Tooltip("SS marks award credit toward the S-rank achievements, so they share the S "
                + "counter. The mark table still shows them as SS.");
    }

    private void DrawSeedingSection()
    {
        ImGui.Text("Seed from achievements");
        ImGui.SameLine();
        HelpMarker("Achievements report a cumulative number with no per-mark detail, so each "
                   + "value is stored as a baseline added on top of the kills counted here. "
                   + "Seeding only ever raises a total, never lowers it.\n\n"
                   + "A completed achievement no longer reports a running total, so it cannot "
                   + "be used as a source. Those are called out below - set the baseline by "
                   + "hand for them.");

        var auto = config.AutoSeedOnLogin;
        if (ImGui.Checkbox("Check on login", ref auto))
        {
            config.AutoSeedOnLogin = auto;
            config.MarkChanged();
        }

        ImGui.BeginDisabled(seeder.IsRunning);
        if (ImGui.Button("Seed now"))
            seeder.Start();
        ImGui.SameLine();
        if (ImGui.Button("Re-resolve names"))
            seeder.ResolveAll();
        ImGui.EndDisabled();

        // Outside the disabled block: a disabled item does not take hover, so a
        // tooltip attached to the button itself never appeared while running.
        ImGui.SameLine();
        HelpMarker("Achievement names are matched by prefix, taking the highest roman numeral "
                   + "tier. Re-resolve after an expansion adds a new tier.");

        if (!string.IsNullOrEmpty(seeder.Status))
            ImGui.TextDisabled(seeder.Status);

        if (config.LastSeeded != default)
            ImGui.TextDisabled($"Last seeded {config.LastSeeded:yyyy-MM-dd HH:mm}");

        var profile = characters.Current;
        if (profile is null)
        {
            ImGui.TextDisabled("Log in to seed. Achievement progress is per-character.");
            return;
        }

        ImGui.TextDisabled($"Editing {profile.Display}");

        DrawSeedTable(profile);
        DrawDrift(profile);
        DrawSeedOutcomes();
    }

    private void DrawSeedTable(CharacterProfile profile)
    {
        if (!ImGui.BeginTable("##seeds", 4,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Counter", ImGuiTableColumnFlags.WidthFixed, 55);
        ImGui.TableSetupColumn("Achievement");
        ImGui.TableSetupColumn("Seeded", ImGuiTableColumnFlags.WidthFixed, 110);
        ImGui.TableSetupColumn("Total", ImGuiTableColumnFlags.WidthFixed, 45);
        ImGui.TableHeadersRow();

        foreach (var def in SeedDefinitions.All)
        {
            var key = def.CategoryKey;
            var resolved = config.ResolvedAchievements.GetValueOrDefault(key);

            ImGui.TableNextRow();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(key);

            // Name comes from the seeder's cache. Reading it from the Excel
            // sheet here meant a sheet lookup and a string allocation per row
            // per frame for as long as this window was open.
            ImGui.TableNextColumn();
            if (resolved == 0)
                ImGui.TextDisabled($"{def.NamePrefix} (unresolved)");
            else
                ImGui.TextUnformatted(seeder.NameOf(key));

            // Baseline stays editable: it is the only way to correct a bad read
            // or to fill in a counter whose achievement is already complete.
            ImGui.TableNextColumn();
            var baseline = profile.BaselineFor(key);
            ImGui.SetNextItemWidth(-1);
            ImGui.PushID(key);
            if (ImGui.InputInt("##base", ref baseline, 1))
            {
                profile.CategoryBaselines[key] = Math.Max(0, baseline);
                config.MarkChanged();
            }
            ImGui.PopID();

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(profile.TotalFor(key).ToString());
        }

        ImGui.EndTable();
    }

    /// <summary>
    /// Shows where the local tally has run ahead of the game's own count.
    ///
    /// The plugin counts a mark when one of your actions hits it, but the game
    /// only awards credit above a contribution threshold it does not expose. A
    /// tally therefore creeps upward, and without this the only symptom is a
    /// number that is quietly wrong forever.
    /// </summary>
    private void DrawDrift(CharacterProfile profile)
    {
        ImGui.Spacing();

        if (!profile.HasAchievementReads)
        {
            ImGui.TextDisabled("Seed once to compare this tally against your achievements.");
            return;
        }

        var drifted = new List<string>();
        foreach (var def in SeedDefinitions.All)
        {
            if (profile.DriftFor(def.CategoryKey) > 0)
                drifted.Add(def.CategoryKey);
        }

        if (drifted.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.55f, 0.85f, 0.55f, 1f),
                "In step with your achievements.");
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f),
                $"Ahead of your achievements in {drifted.Count} "
                + (drifted.Count == 1 ? "counter:" : "counters:"));

            foreach (var key in drifted)
            {
                var reported = profile.AchievementReads.GetValueOrDefault(key);
                ImGui.TextDisabled(
                    $"    {key}: tally {profile.TotalFor(key)}, achievement {reported} "
                    + $"(+{profile.DriftFor(key)})");
            }

            ImGui.TextDisabled("Lower that counter's baseline above to bring it back in line.");
        }

        if (profile.AchievementReadAt != default)
            ImGui.TextDisabled($"Compared against readings from {profile.AchievementReadAt:yyyy-MM-dd HH:mm}.");

        Tooltip("The plugin counts a mark when one of your actions hits it. The game also "
                + "requires a minimum contribution before it awards credit, and that threshold "
                + "is not readable from the client - so a mark you barely touched counts here "
                + "and not in game, and the tally creeps upward.\n\n"
                + "Marks killed since the reading above are counted in this gap too, so seed "
                + "again for a clean comparison. Whatever remains straight after a seeding run "
                + "is genuine over-count.\n\n"
                + "Counters whose achievement is already complete are excluded: a finished "
                + "achievement reports no usable total, so there is nothing to compare with.");
    }

    private void DrawSeedOutcomes()
    {
        if (seeder.Outcomes.Count == 0)
            return;

        ImGui.Spacing();
        ImGui.Text("Last run");

        foreach (var def in SeedDefinitions.All)
        {
            if (!seeder.Outcomes.TryGetValue(def.CategoryKey, out var outcome))
                continue;

            // Anything the user has to act on is worth more than grey text.
            var needsAction = outcome.StartsWith("Complete", StringComparison.Ordinal);
            if (needsAction)
                ImGui.TextColored(new Vector4(1f, 0.8f, 0.35f, 1f), $"{def.CategoryKey}: {outcome}");
            else
                ImGui.TextDisabled($"{def.CategoryKey}: {outcome}");
        }
    }

    private void DrawResetSection()
    {
        if (!confirmReset)
        {
            if (ImGui.Button("Reset all characters"))
                confirmReset = true;
            return;
        }

        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f),
            "Deletes every character's tally. This cannot be undone.");
        if (ImGui.Button("Confirm reset"))
        {
            config.Characters.Clear();
            config.LastSeeded = default;

            // The context caches a profile object. Without this it would keep
            // handing out the one just detached from the configuration, and
            // kills would be recorded onto an orphan.
            characters.Invalidate();

            config.MarkChanged();
            config.Flush(force: true);
            confirmReset = false;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel"))
            confirmReset = false;
    }

    private static void HelpMarker(string text)
    {
        ImGui.TextDisabled("(?)");
        Tooltip(text);
    }

    private static void Tooltip(string text)
    {
        if (!ImGui.IsItemHovered())
            return;
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 25f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }
}
