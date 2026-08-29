using System;
using Dalamud.Plugin.Ipc;

namespace HuntTally;

/// <summary>
/// Publishes kills to other plugins over Dalamud IPC.
///
/// One event gate, whose contents depend on
/// <see cref="Configuration.PublishAllMarkDeaths"/>:
///
///   off (default) - only marks you were credited with, sent once the game has
///                   confirmed the reward. This is the documented contract.
///   on            - every mark death the plugin observes, credited or not,
///                   sent as soon as the death is seen.
///
/// The two are mutually exclusive by construction, so a death produces exactly
/// one message in either mode and a consumer never has to de-duplicate.
///
/// Payload is primitives only. Each plugin is loaded into its own assembly
/// context, so a consumer cannot reference HuntTally's own types; anything
/// richer would fail to resolve on the far side.
/// </summary>
public sealed class IpcProvider : IDisposable
{
    /// <summary>
    /// Bumped when an existing gate's signature changes. Consumers should read
    /// it once and refuse to run against a major they do not know. Note this
    /// does not describe which mode the kill gate is in - see
    /// <see cref="Configuration.PublishAllMarkDeaths"/>.
    /// </summary>
    public const int ApiVersion = 1;

    private const string ApiVersionGate = "HuntTally.ApiVersion";
    private const string OnKillGate = "HuntTally.OnKill";

    private readonly Configuration config;

    private ICallGateProvider<int>? apiVersion;
    private ICallGateProvider<string, uint, int, uint, uint, long, object>? onKill;

    private ICallGateSubscriber<string, uint, int, uint, uint, long, object>? echoSubscriber;
    private Action<string, uint, int, uint, uint, long>? echoHandler;

    public IpcProvider(Configuration config)
    {
        this.config = config;

        try
        {
            apiVersion = Service.Interface.GetIpcProvider<int>(ApiVersionGate);
            apiVersion.RegisterFunc(() => ApiVersion);

            onKill = Service.Interface
                .GetIpcProvider<string, uint, int, uint, uint, long, object>(OnKillGate);

            Service.Log.Information(
                $"IPC available: \"{ApiVersionGate}\" and \"{OnKillGate}\" (api {ApiVersion}). "
                + $"Kill gate reports {ModeDescription}.");
        }
        catch (Exception ex)
        {
            apiVersion = null;
            onKill = null;
            Service.Log.Error(ex, "Could not register IPC; the plugin runs normally without it.");
        }
    }

    /// <summary>Live subscriber count on the kill gate, for diagnostics.</summary>
    public int SubscriberCount => onKill?.SubscriptionCount ?? 0;

    /// <summary>Whether the self-test echo is currently subscribed.</summary>
    public bool EchoEnabled => echoHandler is not null;

    private string ModeDescription =>
        config.PublishAllMarkDeaths ? "every mark death" : "credited kills only";

    /// <summary>
    /// A kill the plugin counted. Suppressed while the all-deaths mode is on,
    /// because that mode has already sent this death at the moment it happened.
    /// </summary>
    public void PublishCredited(KillDetail kill)
    {
        if (!config.PublishAllMarkDeaths)
            Send(kill);
    }

    /// <summary>
    /// Any mark death, credited or not. Sent only while the all-deaths mode is
    /// on, and it then replaces the credited feed rather than adding to it.
    /// </summary>
    public void PublishMarkDeath(KillDetail kill)
    {
        if (config.PublishAllMarkDeaths)
            Send(kill);
    }

    private void Send(KillDetail kill)
    {
        if (onKill is null)
            return;

        // Never allowed to throw into the tracker: a badly behaved subscriber
        // must not be able to stop a kill being recorded.
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

    /// <summary>
    /// Subscribes to this plugin's own gates exactly as another plugin would,
    /// so the feed can be watched making the full round trip through Dalamud's
    /// call gate rather than just proving the gate was registered.
    ///
    /// Diagnostic only. It is a faithful test because the payload is
    /// primitives: nothing about delivery depends on the subscriber living in
    /// the same assembly.
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

        return $"IPC echo on. ApiVersion gate returned {version}, kill gate has "
               + $"{SubscriberCount} subscriber(s) and reports {ModeDescription}.";
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
