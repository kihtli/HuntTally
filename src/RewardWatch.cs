using System;
using System.Collections.Generic;
using Dalamud.Game.Chat;

namespace HuntTally;

/// <summary>
/// Listens for the game confirming that you were rewarded for a mark.
///
/// "You have been rewarded for your contribution in slaying the mark." is
/// LogMessage row 4442. This is the only signal available to the client that
/// reflects the game's actual credit decision, including the contribution
/// threshold that nothing else can see - hitting a mark tells you that you
/// participated, this tells you that it counted.
///
/// The row id is matched rather than the text, so this works in every client
/// language without a translation table.
///
/// The message names no mark, so it confirms that <em>a</em> reward happened,
/// not which one. Confirmations are therefore paired to deaths by time and
/// consumed one apiece: two marks dying together with two rewards still totals
/// two, even if the per-mark attribution between them could swap.
/// </summary>
public sealed class RewardWatch : IDisposable
{
    /// <summary>LogMessage row for the hunt mark reward confirmation.</summary>
    public const uint RewardLogMessageId = 4442;

    /// <summary>
    /// How long an unclaimed confirmation stays claimable. Only has to outlast
    /// the gap between the message arriving and the next poll noticing the
    /// death, plus the wait for a confirmation that arrives late.
    /// </summary>
    private const double RememberSeconds = 30;

    private readonly List<DateTime> unclaimed = new();

    /// <summary>Confirmations seen this session. Surfaced in settings.</summary>
    public long Seen { get; private set; }

    public RewardWatch()
    {
        Service.Chat.LogMessage += OnLogMessage;
    }

    public void Dispose()
    {
        Service.Chat.LogMessage -= OnLogMessage;
    }

    private void OnLogMessage(ILogMessage message)
    {
        if (message.LogMessageId != RewardLogMessageId)
            return;

        Seen++;
        unclaimed.Add(DateTime.UtcNow);

        // The message carries no mark name in its text, but the packet may
        // still populate an entity. Logged rather than used: if it turns out to
        // be filled in, marks can be matched exactly instead of by time.
        var target = message.TargetEntity;
        var name = target is null ? string.Empty : target.Name.ExtractText();
        Service.Log.Information(
            $"Reward confirmed by the game (#{Seen})"
            + (string.IsNullOrEmpty(name) ? "." : $", target entity \"{name}\"."));
    }

    /// <summary>
    /// Claims a confirmation for a death observed at <paramref name="diedAtUtc"/>.
    ///
    /// The confirmation routinely arrives before the poll notices the death, so
    /// anything from slightly before that moment onward is eligible; the
    /// earliest match is taken so that a run of deaths claims a run of
    /// confirmations in order.
    /// </summary>
    public bool TryClaim(DateTime diedAtUtc, double lookbackSeconds)
    {
        var earliest = diedAtUtc.AddSeconds(-lookbackSeconds);

        for (var i = 0; i < unclaimed.Count; i++)
        {
            if (unclaimed[i] < earliest)
                continue;

            unclaimed.RemoveAt(i);
            return true;
        }

        return false;
    }

    public void Prune(DateTime now)
    {
        if (unclaimed.Count == 0)
            return;

        unclaimed.RemoveAll(t => (now - t).TotalSeconds > RememberSeconds);
    }

    public void Clear() => unclaimed.Clear();
}
