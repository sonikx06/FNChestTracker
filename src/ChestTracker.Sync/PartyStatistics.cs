using System.Globalization;
using ChestTracker.Protocol;

namespace ChestTracker.Sync;

/// <summary>One member's numbers as shown on the Party page and the team overlay.</summary>
/// <param name="Match">Count in the active shared match (null: no shared match, or counts hidden).</param>
/// <param name="Lifetime">Count for this party over all time (null: counts hidden).</param>
public sealed record PartyMemberStatistics(Guid PlayerId, string Name, bool Online, bool IsMe, CountPair? Match, CountPair? Lifetime);

/// <summary>
/// The authoritative party numbers, projected once from the server's <see cref="PartyStateDto"/> so that the Party
/// page and the in-game overlay can never disagree:
/// <list type="bullet">
/// <item><see cref="TeamMatch"/>: the active shared match's total (null when no shared match is running);</item>
/// <item><see cref="TeamLifetime"/>: everything the party ever counted, former members included (server rule);</item>
/// <item><see cref="MyMatch"/> / <see cref="MyLifetime"/>: this player's share of those, as the server counts them.</item>
/// </list>
/// Every count keeps its verified/reported pair; <see cref="Format"/> renders "verified" or "verified (reported)".
/// </summary>
public sealed record PartyStatistics(
    string PartyName,
    bool SharedMatchActive,
    CountPair? TeamMatch,
    CountPair TeamLifetime,
    CountPair? MyMatch,
    CountPair? MyLifetime,
    IReadOnlyList<PartyMemberStatistics> Members)
{
    /// <summary>Null when not in a party.</summary>
    public static PartyStatistics? From(PartyStateDto? party, Guid? me)
    {
        if (party is null)
        {
            return null;
        }

        var active = party.ActiveMatch is not null;
        var members = party.Members
            .Select(m => new PartyMemberStatistics(m.PlayerId, m.DisplayName, m.Online, m.PlayerId == me, active ? m.Match : null, m.Lifetime))
            .ToList();
        var mine = members.FirstOrDefault(m => m.IsMe);
        return new PartyStatistics(party.Name, active, active ? party.TeamMatch : null, party.TeamLifetime, mine?.Match, mine?.Lifetime, members);
    }

    /// <summary>"verified", or "verified (reported)" when they differ; "—" for no value.</summary>
    public static string Format(CountPair? c) =>
        c is not { } v ? "—"
        : v.Verified == v.Reported ? v.Verified.ToString(CultureInfo.InvariantCulture)
        : $"{v.Verified} ({v.Reported})";
}
