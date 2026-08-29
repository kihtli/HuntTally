# Hunt Tally

A Dalamud plugin that keeps a permanent, per-mark count of every hunt mark you get credit for killing. Unlike the in-game achievements, it keeps counting after the achievement is done.

## Building

You need the .NET 9 SDK and XIVLauncher installed, with the game having been launched through XIVLauncher at least once (the SDK resolves `Dalamud.dll` from `%AppData%\XIVLauncher\addon\Hooks\dev`).

```
dotnet build -c Release
```

On macOS or Linux, `./build-macos.sh` finds Dalamud for you and sets `DALAMUD_HOME`.

The packaged output lands in `bin/Release/HuntTally/`. To load it:

1. `/xlsettings` → Experimental → enable dev plugin loading if it isn't already.
2. Add `bin/Release/HuntTally/HuntTally.dll` under Dev Plugin Locations, or drop the folder in `%AppData%\XIVLauncher\devPlugins\`.
3. `/xlplugins` → Dev Tools → load it.

## Usage

- `/hunttally` — open the tally window
- `/hunttally config` — settings

## How detection works

Every 250ms the plugin scans the object table for battle NPCs whose `NameId` appears in the game's `NotoriousMonster` Excel sheet. That sheet is the game's own list of marks and their ranks, so new marks are picked up automatically when a patch adds them — there's no name list to maintain.

A kill is counted when a mark that the plugin has personally observed *alive* transitions to 0 HP. The "saw it alive first" requirement stops you scoring by walking up to someone else's corpse.

The detection radius gates **acquisition only**. Once a mark is being watched it stays watched at any distance, for as long as the game keeps it loaded — so tagging a mark and running off still counts. (Run far enough and the client unloads the object entirely; at that point nothing in the object table can see it die.)

Credit comes from your own actions. The plugin hooks `ActionEffectHandler.Receive` — the game telling the client "this caster's action resolved against these targets" — and counts a mark when one of *your* actions resolved against it while it was alive. That gets the awkward cases right: a mark killed in a single hit counts, a mark you tagged as it was already dying counts, and a mark someone else killed while you merely had it targeted does not.

That is a *necessary* condition for credit, not a sufficient one — the game also applies a contribution threshold. So a kill is finally counted only once the game itself confirms it:

> You have been rewarded for your contribution in slaying the mark.

That is `LogMessage` row **4442**, matched by row id rather than by text, so it works in every client language without a translation table. It is the only signal reachable from the client that reflects the contribution threshold — hitting a mark says you took part, this says it counted.

The message names no mark, so confirmations are paired to deaths by time and claimed one apiece: two marks dying together with two rewards still totals two, even if attribution between those two could swap. A death waits up to 8 seconds for its confirmation, and confirmations arriving slightly ahead of the poll that spots the death are eligible too.

**Only A and S ranks are gated on it.** B ranks are paid through the Hunt board bill you hand in, not through the kill, so no confirmation is ever sent for one — waiting would drop every B rank. They are counted as soon as they die. The evidence for that split: the `LogMessage` sheet contains exactly one mark reward message and no B-rank variant of it.

SS never arises. `NotoriousMonster` holds ranks 1, 2 and 3 only, so the plugin never sees an SS mark or its minions. The SS handling elsewhere in the code is forward-compatibility, since mark data is read from the sheet rather than hardcoded — if a patch adds SS entries they will be picked up, and the reward rule will want revisiting then.

Every target of your action counts, not only those taking damage. A debuff or a DoT application puts you on the enmity table and earns credit exactly as a hit does.

If the hook cannot be created — most likely after a game patch — the plugin falls back to the pre-2.1 heuristic (were you in combat while the mark was alive and nearby) rather than counting nothing. On that path, combat state is sampled every frame and latched rather than read once per poll, so a one-hit kill that holds combat for a few milliseconds is still seen. The settings window shows which signal is live and counts the actions it has seen, so a hook that stops firing is visible rather than silent, and there is a switch to force the fallback if it ever loads but misbehaves.

Objects appear in the table before the server fills in their stats, so an object whose `MaxHp` is still 0 is treated as "not loaded yet" rather than dead. Without that distinction a mark first seen mid-spawn reads as a corpse, and its real death later is missed.

SS marks award credit toward the S-rank achievements, so they share the S counter throughout — counting, seeding and every total. The mark table still shows them as SS.

## Limitations, honestly stated

**Reward confirmation is the backstop, and it can be switched off.** Waiting for the game's own message is what stops marks you touched but were not rewarded for from being counted. If a rank ever stops emitting that message, its kills would be dropped instead — so settings reports confirmations seen and kills dropped, every drop is logged as a warning, and the check can be turned off. Watch the drop counter for a session before trusting it.

**Hitting a mark is not the same as earning credit.** With confirmation switched off, the plugin falls back to counting anything your action resolved against. It cannot see the contribution threshold, so a glancing hit on an S rank counts locally and earns nothing in game.

Because of that, settings compares each counter against what its achievement last reported and says so plainly — either "in step with your achievements" or a list of counters that have run ahead, with the gap. Lower that counter's baseline to bring it back in line.

Read the gap right after a seeding run: marks killed since the last reading are legitimately part of it too, so only a fresh comparison isolates genuine over-count. Counters whose achievement is already complete are excluded, since a finished achievement reports no usable total.

On the fallback path the older caveat applies instead: a mark that dies near you while you fight something else can count, and a mark you hit without entering a visible combat state may not.

**Damage detection is a hook**, so unlike the rest of the plugin it can break on a patch. The address comes from FFXIVClientStructs rather than a signature kept in this repo, so a game update is usually fixed by updating Dalamud rather than this plugin. Only your own actions are read — a kill where a pet or summon landed the only hit falls back to the combat check.

**Strict credit** applies to the fallback path only, and is greyed out while damage detection is live. It tightens the combat proxy: the mark must also have been in combat itself, and targeting must have gone both ways. That removes the main false positive — a mark dying nearby while you fight something else — at the cost of rejecting a real kill where you never got a target on the mark.

**Polling can miss things.** If a mark dies and despawns entirely within one 250ms window, or you're loading a zone at the moment of death, it won't be counted. In practice corpses linger far longer than that, but a very laggy S-rank zerg is the realistic failure case. Lower `PollSeconds` in `KillTracker.cs` if you want to trade CPU for certainty.

**No retroactive data.** Counting starts when you install it. Seed your existing totals from your achievements, or set the baseline for a rank by hand in settings, then let it run forward from there.

**Completed achievements cannot be seeded from.** Once an achievement is finished the game stops reporting a running total for it, which is exactly the case this plugin exists to cover. The settings window says so per counter after a seeding run; set those baselines by hand.

**Instances and world visits** are recorded by territory and home world in the detail log, but the totals don't distinguish them.

## Data and saving

Config changes and kills are queued and written at most once every 20 seconds, plus immediately on logout and on plugin unload. Writing serialises every character, mark record and history entry, so doing it inline on each kill meant a large synchronous disk write on the framework thread mid-fight.

Identity is the character's content id, so a rename or world transfer keeps one profile rather than splitting into two.

## IPC for other plugins

Hunt Tally publishes counted kills over Dalamud IPC. A message goes out exactly when the plugin decides a kill counts, so subscribers get **"you were credited with this mark"** — not "a mark near you died". Everything the plugin does to establish credit has already happened by then: the mark died, one of your own actions had resolved against it, and for A and S ranks the game confirmed the reward.

| Gate | Signature | Meaning |
|---|---|---|
| `HuntTally.ApiVersion` | `Func<int>` | Contract version. Currently `1`. |
| `HuntTally.OnKill` | `Action<string, uint, int, uint, uint, long>` | `name, nameId, rank, territoryId, instanceId, unixSecondsUtc` |

- `nameId` is the `BNpcName` row id — the stable key; `name` is a convenience.
- `rank` is `1` = B, `2` = A, `3` = S, matching the game's `NotoriousMonster.Rank` column.
- `instanceId` is the public instance number of the zone, or `0` when the zone is not instanced. Marks spawn per instance, so this is what distinguishes two kills of the same mark at the same moment — a train is an instance-scoped thing.
- `territoryId`, `instanceId` and the timestamp travel with the event because a kill can be held up to eight seconds waiting on the reward confirmation, by which time the player may have left the zone. Do not read "current territory" when handling it.
- Payload is primitives only. Each plugin loads into its own assembly context, so a consumer cannot reference this plugin's types.

`ApiVersion` is bumped only if an existing gate's signature or meaning changes; adding a new gate does not bump it. Read it once at startup and refuse to run against a major you do not know.

If a future field is needed, it arrives as a **new gate** (`HuntTally.OnKill2`) rather than by widening this one, so an existing consumer keeps working untouched.

```csharp
// Consumer side.
var version = pluginInterface.GetIpcSubscriber<int>("HuntTally.ApiVersion");
try
{
    if (version.InvokeFunc() != 1)
        return;                     // contract we do not understand
}
catch (IpcNotReadyError)
{
    return;                         // Hunt Tally not installed or not loaded yet
}

var onKill = pluginInterface
    .GetIpcSubscriber<string, uint, int, uint, uint, long, object>("HuntTally.OnKill");

void Handle(string name, uint nameId, int rank, uint territoryId, uint instanceId, long unixSeconds)
{
    // instanceId == 0 means the zone is not instanced.
}

onKill.Subscribe(Handle);
// and on dispose:
onKill.Unsubscribe(Handle);
```

Note the subscriber's generic list carries a trailing `object` for the unused return type, while the handler takes only the five payload arguments.

Load order does not matter. If Hunt Tally loads later, the subscription simply starts receiving messages once it does; `InvokeFunc` on `ApiVersion` throws `IpcNotReadyError` until then, so check it lazily rather than only once at startup if you want to tolerate Hunt Tally being enabled mid-session.

### What the kill feed contains

`HuntTally.OnKill` has two modes, chosen by the user in settings under **Send every mark death over IPC**:

| Setting | The gate sends | Timing |
|---|---|---|
| Off (default) | Only marks you were credited with | After the game confirms the reward — under a second typically, up to eight |
| On | **Every** mark death observed, credited or not | As soon as the death is seen |

There is one gate, not two. Turning the setting on makes an already-installed consumer receive the wider feed **without any change on its side** — which is the point of doing it this way. The credited feed is suppressed while it is on, so a death produces exactly one message in either mode and no de-duplication is needed.

Two consequences worth designing around:

- **A consumer cannot tell which mode it is receiving.** The setting is off by default so the credited-only contract holds unless a user deliberately changes it, but a plugin that strictly needs "credited" cannot assume it. If that matters, treat the feed as "a mark died, possibly credited" and confirm credit another way.
- **Both modes respect the plugin's rank filters.** A user who has switched off B ranks in settings emits no B ranks on either.

The tally itself is never affected by this setting — it stays strictly credit-based regardless.

### Confirming the IPC works

**Registration** — `/xldata` → **Data Share & Call Gate**. Both gates appear in the table:

| Name | Action | Func | # | Subscriber |
|---|---|---|---|---|
| `HuntTally.ApiVersion` | — | registered | 0 | |
| `HuntTally.OnKill` | — | — | 0 | |

`HuntTally.OnKill` shows nothing under **Action** or **Func**, and that is correct: an event gate is only ever sent on, never registered against. The **#** column is the live subscriber count, which is what tells you a consumer has attached.

**Startup** — `/xllog` carries `IPC available: "HuntTally.ApiVersion" and "HuntTally.OnKill" (api 1).` If IPC failed to register, an error is logged there instead and the plugin carries on without it.

**Delivery** — `/hunttally ipc` subscribes to the plugin's own gates the way another plugin would, reports what the `ApiVersion` gate returned over IPC and the current subscriber count, then echoes each kill to chat as it arrives:

```
[Hunt Tally] IPC echo on. ApiVersion gate returned 1, kill gate has 1 subscriber(s).
[Hunt Tally] IPC received: Ixion (id 8909, rank 3) territory 621, instance 2, 21:04:11.
```

Run it again to unsubscribe. It is a faithful test because the payload is primitives — nothing about delivery depends on the subscriber living in the same assembly. What it does not exercise is a consumer in a *different* load context; only your friend's plugin can prove that end.

Messages are sent on the framework thread, so handlers may touch game state directly.

## API compatibility

Written against Dalamud API 15 (SDK 15.x, verified against Dalamud 15.0.3.x). The spots most likely to need an edit on a different API level:

- `MarkData` and `CharacterContext` use `SeString.ExtractText()`; older Lumina wants `.ToDalamudString().TextValue`.
- `Service.Data.GetExcelSheet<T>()` and `GetRowOrDefault` changed shape when Lumina moved to `Lumina.Excel.Sheets`.
- `CharacterContext.ResolveContentId` reads `PlayerState.Instance()->ContentId`; `FallbackIdentity` in the same file is a drop-in replacement if that member moves, at the cost of splitting a profile on rename or transfer.
- `AchievementSeeder` uses `Achievement.ProgressAchievementId`, `ProgressCurrent`, `ProgressRequestState`, `IsLoaded()` and `IsComplete()` from FFXIVClientStructs. The id check is what makes a progress read trustworthy — see below.

## Why the seeder checks the achievement id

Achievement progress arrives asynchronously: you request an id, and some frames later a shared block on the client holds the answer. Serialising your own requests is not enough to identify your own reply, because the game's own achievement window and any other plugin can request progress at the same time.

That matters more than it sounds, because seeding only ever raises a baseline. A value read from someone else's reply is not a transient glitch — it is permanent, and nothing short of editing the baseline by hand will bring it back down. So the reply's `ProgressAchievementId` is checked against the requested id before the value is used.
