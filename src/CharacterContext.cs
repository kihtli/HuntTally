using System;
using Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace HuntTally;

/// <summary>
/// Resolves the logged-in character to its profile.
///
/// Identity comes from the content id, which survives renames and world
/// transfers where a name-based key would split one character into two
/// profiles. Dalamud's IClientState.LocalContentId was removed in API 15, so
/// this reads it from PlayerState instead.
///
/// Resolving is not free - it costs an unsafe PlayerState read, a SeString to
/// string conversion for the name, and an Excel lookup plus another conversion
/// for the home world. The windows ask for the current profile several times
/// per frame, so the answer is cached and only recomputed when something that
/// could change it happens, or once a second as a safety net.
/// </summary>
public sealed class CharacterContext : IDisposable
{
    private const double RevalidateSeconds = 1;

    private readonly Configuration config;

    private CharacterProfile? cached;
    private DateTime lastResolveUtc = DateTime.MinValue;

    public CharacterContext(Configuration config)
    {
        this.config = config;

        Service.ClientState.Login += Invalidate;
        Service.ClientState.Logout += OnLogout;
        Service.ClientState.TerritoryChanged += OnTerritoryChanged;
    }

    public void Dispose()
    {
        Service.ClientState.Login -= Invalidate;
        Service.ClientState.Logout -= OnLogout;
        Service.ClientState.TerritoryChanged -= OnTerritoryChanged;
    }

    /// <summary>The active profile, or null when not logged in.</summary>
    public CharacterProfile? Current
    {
        get
        {
            // Cheap enough to check every time, and it is the common reason the
            // answer becomes null.
            if (!Service.ClientState.IsLoggedIn)
            {
                cached = null;
                return null;
            }

            var now = DateTime.UtcNow;
            if ((now - lastResolveUtc).TotalSeconds < RevalidateSeconds)
                return cached;

            // Set before resolving so a failed resolve is throttled too,
            // rather than retrying on every access.
            lastResolveUtc = now;
            cached = Resolve();
            return cached;
        }
    }

    /// <summary>
    /// Drops the cached profile. Called on login, logout and zone change, and
    /// by the settings window after a reset, which detaches every profile
    /// object from the configuration.
    /// </summary>
    public void Invalidate()
    {
        cached = null;
        lastResolveUtc = DateTime.MinValue;
    }

    private void OnLogout(int type, int code) => Invalidate();

    private void OnTerritoryChanged(uint territory) => Invalidate();

    private CharacterProfile? Resolve()
    {
        var player = Service.Objects.LocalPlayer;
        if (player is null)
            return null;

        var contentId = ResolveContentId();
        if (contentId == 0)
            return null;

        return config.GetOrCreate(contentId, player.Name.TextValue, ResolveHomeWorld(player));
    }

    /// <summary>
    /// Reads the local character's content id.
    ///
    /// If PlayerState does not expose ContentId under this name in your
    /// FFXIVClientStructs version, swap the body for FallbackIdentity(player)
    /// below, which is stable but splits a profile on rename or transfer.
    /// </summary>
    private static unsafe ulong ResolveContentId()
    {
        try
        {
            var state = PlayerState.Instance();
            return state is null ? 0 : state->ContentId;
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Could not read content id.");
            return 0;
        }
    }

    /// <summary>
    /// Name and home world hashed into a stable id. Only used if the content id
    /// is unavailable: a rename or world transfer produces a different value and
    /// therefore a separate profile, which is why it is not the default.
    /// </summary>
    private static ulong FallbackIdentity(IPlayerCharacter player)
    {
        var key = $"{player.Name.TextValue}@{ResolveHomeWorld(player)}";

        // FNV-1a, chosen only because it is short and deterministic across runs.
        ulong hash = 14695981039346656037;
        foreach (var c in key)
        {
            hash ^= c;
            hash *= 1099511628211;
        }

        return hash;
    }

    private static string ResolveHomeWorld(IPlayerCharacter? player)
    {
        if (player is null)
            return string.Empty;

        try
        {
            // Home world, not current: a character visiting another world is
            // still the same character and must not split into a new profile.
            return player.HomeWorld.Value.Name.ExtractText();
        }
        catch (Exception ex)
        {
            Service.Log.Warning(ex, "Could not resolve home world.");
            return string.Empty;
        }
    }
}
