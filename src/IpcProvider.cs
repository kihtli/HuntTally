using System;
using Dalamud.Plugin.Ipc;

namespace HuntTally;

/// <summary>
/// Publishes counted kills to other plugins over Dalamud IPC.
///
/// This is a thin adapter over <see cref="KillTracker.OnKill"/>. It does no
/// detection of its own: a message goes out exactly when the plugin decides a
/// kill counts, which is after the mark died, after one of the player's own
/// actions resolved against it, and - for A and S ranks - after the game
/// confirmed the reward. Consumers therefore get "you were credited with this
/// mark", not "a mark near you died".
///
/// Payload is primitives only. Each plugin is loaded into its own assembly
/// context, so a consumer cannot reference HuntTally's own types; anything
/// richer would fail to resolve on the far side.
/// </summary>
public sealed class IpcProvider : IDisposable
{
    /// <summary>
    /// Bumped when an existing gate's meaning or signature changes. Consumers
    /// should read it once and refuse to run against a major they do not know.
    /// Adding a new gate does not bump it.
    /// </summary>
    public const int ApiVersion = 1;

    private const string ApiVersionGate = "HuntTally.ApiVersion";
    private const string OnKillGate = "HuntTally.OnKill";

    private ICallGateProvider<int>? apiVersion;
    private ICallGateSubscriber<string, uint, int, uint, uint, long, object>? echoSubscriber;
    private Action<string, uint, int, uint, uint, long>? echoHandler;
    private ICallGateProvider<string, uint, int, uint, uint, long, object>? onKill;

    public IpcProvider()
    {
        try
        {
            apiVersion = Service.Interface.GetIpcProvider<int>(ApiVersionGate);
            apiVersion.RegisterFunc(() => ApiVersion);

            onKill = Service.Interface
                .GetIpcProvider<string, uint, int, uint, uint, long, object>(OnKillGate);

            Service.Log.Information(
                $"IPC available: \"{ApiVersionGate}\" and \"{OnKillGate}\" (api {ApiVersion}).");
        }
        catch (Exception ex)
        {
            apiVersion = null;
            onKill = null;
            Service.Log.Error(ex, "Could not register IPC; the plugin runs normally without it.");
        }
    }

    /// <summary>
    /// Fans a counted kill out to subscribers.
    ///
    /// Never allowed to throw into the tracker: a badly behaved subscriber must
    /// not be able to stop a kill being recorded, and recording has already
    /// happened by the time this runs.
    /// </summary>
    public void Publish(KillDetail kill)
    {
        if (onKill is null)
            return;

        try
        {
            onKill.SendMessage(
                kill.Mark.Name,
                kill.Mark.NameId,
                (int)kill.Mark.Rank,
                kill.TerritoryId,
                kill.InstanceId,
                new DateTimeOffset(kill.Time).ToUnixTimeSeconds());
        }
        catch (Exception ex)
        {
            Service.Log.Error(ex, $"An IPC subscriber threw while handling {kill.Mark.Name}.");
        }
    }

    /// <summary>Live subscriber count on the kill gate, for diagnostics.</summary>
    public int SubscriberCount => onKill?.SubscriptionCount ?? 0;

    /// <summary>Whether the self-test echo is currently subscribed.</summary>
    public bool EchoEnabled => echoHandler is not null;

    /// <summary>
    /// Subscribes to this plugin's own gates exactly as another plugin would,
    /// so a kill can be watched making the full round trip through Dalamud's
    /// call gate rather than just proving the gate was registered.
    ///
    /// Diagnostic only, and off unless asked for. It is a faithful test because
    /// the payload is primitives: nothing about it depends on the subscriber
    /// living in the same assembly.
    /// </summary>
    public string ToggleEcho()
    {
        if (echoHandler is not null)
        {
            echoSubscriber?.Unsubscribe(echoHandler);
            echoHandler = null;
            return "IPC echo off.";
        }

        try
        {
            echoSubscriber = Service.Interface
                .GetIpcSubscriber<string, uint, int, uint, uint, long, object>(OnKillGate);

            echoHandler = OnEcho;
            echoSubscriber.Subscribe(echoHandler);
        }
        catch (Exception ex)
        {
            echoHandler = null;
            Service.Log.Error(ex, "Could not subscribe to the kill gate.");
            return "IPC echo failed to subscribe; see /xllog.";
        }

        // Read our own version gate through IPC rather than the constant, so a
        // pass proves the Func round trip and not just that the field exists.
        string version;
        try
        {
            version = Service.Interface
                .GetIpcSubscriber<int>(ApiVersionGate).InvokeFunc().ToString();
        }
        catch (Exception ex)
        {
            version = $"unreadable ({ex.GetType().Name})";
        }

        return $"IPC echo on. ApiVersion gate returned {version}, "
               + $"kill gate has {SubscriberCount} subscriber(s). Kills will echo here.";
    }

    private void OnEcho(
        string name, uint nameId, int rank, uint territoryId, uint instanceId, long unixSeconds)
    {
        var when = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime();
        Service.Chat.Print(
            $"[Hunt Tally] IPC received: {name} (id {nameId}, rank {rank}) "
            + $"territory {territoryId}, instance {instanceId}, {when:HH:mm:ss}.");
    }

    public void Dispose()
    {
        try
        {
            if (echoHandler is not null)
            {
                echoSubscriber?.Unsubscribe(echoHandler);
                echoHandler = null;
            }

            apiVersion?.UnregisterFunc();
            onKill?.UnregisterAction();
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Could not cleanly unregister IPC.");
        }

        apiVersion = null;
        onKill = null;
    }
}
