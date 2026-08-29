using System;
using System.Threading;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using HuntTally.Windows;

namespace HuntTally;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/hunttally";

    /// <summary>
    /// Achievement data is not ready the instant the login event fires, and the
    /// seeder times out per request rather than hanging, so a late start is
    /// safer than an early one.
    /// </summary>
    private static readonly TimeSpan LoginSeedDelay = TimeSpan.FromSeconds(10);

    private readonly WindowSystem windowSystem = new("HuntTally");
    private readonly Configuration config;
    private readonly MainWindow mainWindow;
    private readonly ConfigWindow configWindow;
    private readonly KillTracker tracker;
    private readonly AchievementSeeder seeder;
    private readonly CharacterContext characters;
    private readonly DamageWatch damage;
    private readonly RewardWatch reward;
    private readonly IpcProvider ipc;
    private readonly CancellationTokenSource disposal = new();

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        pluginInterface.Create<Service>();

        config = Service.Interface.GetPluginConfig() as Configuration ?? new Configuration();

        characters = new CharacterContext(config);
        seeder = new AchievementSeeder(config, characters);

        // Constructed before the tracker: the tracker asks it on every poll
        // whether the precise signal is available.
        damage = new DamageWatch();
        reward = new RewardWatch();

        mainWindow = new MainWindow(config, characters);
        windowSystem.AddWindow(mainWindow);

        tracker = new KillTracker(config, characters, damage, reward);

        configWindow = new ConfigWindow(config, seeder, characters, damage, reward, tracker);
        windowSystem.AddWindow(configWindow);
        // Subscribed separately from the chat notice: other plugins should be
        // told about a kill whether or not the user wants it printed.
        ipc = new IpcProvider(config);
        tracker.OnKill += ipc.PublishCredited;
        tracker.OnMarkDeath += ipc.PublishMarkDeath;
        tracker.OnKill += AnnounceKill;

        Service.ClientState.Login += OnLogin;
        Service.ClientState.Logout += OnLogout;
        Service.Framework.Update += OnFrameworkUpdate;

        // Resolving walks the whole Achievement sheet. One tick later costs
        // nothing and keeps it off the plugin-load path.
        Service.Framework.RunOnTick(seeder.ResolveAll, TimeSpan.Zero, 0, disposal.Token);

        if (Service.ClientState.IsLoggedIn && config.AutoSeedOnLogin)
            ScheduleSeed();

        Service.Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the hunt tally. \"/hunttally config\" for settings, "
                          + "\"/hunttally ipc\" to test the IPC feed.",
        });

        Service.Interface.UiBuilder.Draw += windowSystem.Draw;
        Service.Interface.UiBuilder.OpenMainUi += ToggleMain;
        Service.Interface.UiBuilder.OpenConfigUi += ToggleConfig;
    }

    public void Dispose()
    {
        disposal.Cancel();

        tracker.OnKill -= AnnounceKill;
        tracker.OnKill -= ipc.PublishCredited;
        tracker.OnMarkDeath -= ipc.PublishMarkDeath;
        tracker.Dispose();
        ipc.Dispose();

        // After the tracker, which reads both on every poll.
        damage.Dispose();
        reward.Dispose();

        Service.ClientState.Login -= OnLogin;
        Service.ClientState.Logout -= OnLogout;
        Service.Framework.Update -= OnFrameworkUpdate;
        seeder.Dispose();

        Service.Interface.UiBuilder.Draw -= windowSystem.Draw;
        Service.Interface.UiBuilder.OpenMainUi -= ToggleMain;
        Service.Interface.UiBuilder.OpenConfigUi -= ToggleConfig;

        Service.Commands.RemoveHandler(CommandName);

        windowSystem.RemoveAllWindows();
        mainWindow.Dispose();
        configWindow.Dispose();

        // Saving is queued rather than immediate, so the last changes of the
        // session only reach disk because of this.
        config.Flush(force: true);

        characters.Dispose();
        disposal.Dispose();
    }

    /// <summary>Writes queued config changes, at the interval Flush enforces.</summary>
    private void OnFrameworkUpdate(IFramework framework) => config.Flush();

    private void OnLogin()
    {
        if (config.AutoSeedOnLogin)
            ScheduleSeed();
    }

    private void OnLogout(int type, int code) => config.Flush(force: true);

    /// <summary>
    /// RunOnTick rather than Task.Delay: the continuation of a Task runs on a
    /// thread-pool thread, and the seeder's state is read on the framework
    /// thread. It is also cancelled on dispose, so a plugin unloaded inside the
    /// delay does not start seeding afterwards.
    /// </summary>
    private void ScheduleSeed() =>
        Service.Framework.RunOnTick(seeder.Start, LoginSeedDelay, 0, disposal.Token);

    private void AnnounceKill(KillDetail kill)
    {
        if (!config.ChatOnKill)
            return;

        var info = kill.Mark;

        var profile = characters.Current;
        if (profile is null)
            return;

        var key = Configuration.CategoryKeyFor(info.Rank);
        if (key is null)
            return;

        Service.Chat.Print(
            $"[Hunt Tally] {info.Name} ({MarkData.RankLabel(info.Rank)}) — "
            + $"{profile.TotalFor(key)} {key} ranks on this character, "
            + $"{config.AccountTotalFor(key)} across all.");
    }

    private void OnCommand(string command, string args)
    {
        var arg = args.Trim();

        if (arg.Equals("config", StringComparison.OrdinalIgnoreCase))
            ToggleConfig();
        else if (arg.Equals("ipc", StringComparison.OrdinalIgnoreCase))
            Service.Chat.Print($"[Hunt Tally] {ipc.ToggleEcho()}");
        else
            ToggleMain();
    }

    private void ToggleMain() => mainWindow.Toggle();
    private void ToggleConfig() => configWindow.Toggle();
}
