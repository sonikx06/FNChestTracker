using ChestTracker.Protocol;
using ChestTracker.Sync;

namespace ChestTracker.Tests;

/// <summary>The one projection the Party page and the overlay both show.</summary>
public class PartyStatisticsTests
{
    private static readonly Guid Me = Guid.NewGuid();
    private static readonly Guid Mate = Guid.NewGuid();

    private static MemberDto Member(Guid id, string name, CountPair? match, CountPair? lifetime) =>
        new(id, name, PartyRole.Member, true, true, true, match, lifetime, ParticipantResponse.Accepted);

    private static PartyStateDto Party(bool sharedMatch, CountPair teamMatch, CountPair teamLifetime, params MemberDto[] members) =>
        new(Guid.NewGuid(), "Squad", Me, 4, "ABCDEFGH", DateTimeOffset.UtcNow.AddHours(1),
            members.ToList(), sharedMatch ? new SharedMatchDto(Guid.NewGuid(), SharedMatchStatus.Active, Me, DateTimeOffset.UtcNow, null) : null,
            teamMatch, teamLifetime, DateTimeOffset.UtcNow);

    [Fact]
    public void Team_match_and_team_total_are_never_confused()
    {
        // The reported case: the Party page shows Team · match 7 and Team · total 11.
        var party = Party(true, new CountPair(7, 7), new CountPair(11, 11),
            Member(Me, "Sonik", new CountPair(4, 4), new CountPair(6, 6)),
            Member(Mate, "Mia", new CountPair(3, 3), new CountPair(5, 5)));
        var s = PartyStatistics.From(party, Me)!;
        Assert.True(s.SharedMatchActive);
        Assert.Equal("7", PartyStatistics.Format(s.TeamMatch));
        Assert.Equal("11", PartyStatistics.Format(s.TeamLifetime));
        Assert.Equal(new CountPair(4, 4), s.MyMatch);
        Assert.Equal(new CountPair(6, 6), s.MyLifetime);
        Assert.Equal(["Sonik", "Mia"], s.Members.Select(m => m.Name));
        Assert.True(s.Members[0].IsMe);
    }

    [Fact]
    public void Without_a_shared_match_there_is_no_team_match_number_but_the_total_stays()
    {
        var party = Party(false, new CountPair(0, 0), new CountPair(11, 13), Member(Me, "Sonik", null, new CountPair(6, 8)));
        var s = PartyStatistics.From(party, Me)!;
        Assert.False(s.SharedMatchActive);
        Assert.Null(s.TeamMatch);
        Assert.Equal("—", PartyStatistics.Format(s.TeamMatch));
        Assert.Equal("11 (13)", PartyStatistics.Format(s.TeamLifetime));   // verified (reported) kept apart
        Assert.Null(s.Members[0].Match);
    }

    [Fact]
    public void Hidden_counts_and_leaving_the_party()
    {
        var party = Party(true, new CountPair(2, 2), new CountPair(9, 9), Member(Me, "Sonik", null, null));
        Assert.Equal("—", PartyStatistics.Format(PartyStatistics.From(party, Me)!.MyLifetime));
        Assert.Null(PartyStatistics.From(null, Me));
    }
}
