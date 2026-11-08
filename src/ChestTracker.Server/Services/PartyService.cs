using ChestTracker.Protocol;
using ChestTracker.Server.Data;
using Microsoft.Extensions.Options;
using Npgsql;

namespace ChestTracker.Server.Services;

public sealed record Membership(Guid PartyId, PartyRole Role);

/// <param name="PartyId">The sender's current party (live activity goes there), null when not in a party.</param>
/// <param name="ChangedParties">Parties whose statistics changed (current and/or former parties).</param>
public sealed record SubmitResult(Guid? PartyId, IReadOnlyList<EventAck> Acks, IReadOnlyList<ActivityDto> Activity, IReadOnlyList<Guid> ChangedParties)
{
    public bool Changed => ChangedParties.Count > 0;
}

public sealed record MembershipPeriod(Guid PartyId, DateTimeOffset Start, DateTimeOffset? End);

/// <summary>
/// All party rules and persistence. Every public method validates the caller's membership and role
/// against the database inside a transaction; it never trusts party ids or roles sent by clients.
/// </summary>
public sealed class PartyService(NpgsqlDataSource db, IOptions<ServerOptions> options, PresenceTracker presence, TimeProvider clock)
{
    private ServerOptions O => options.Value;

    // ================================================================ profile

    public async Task<ProfileDto> GetProfileAsync(Guid playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        var name = await c.ScalarAsync<string>("SELECT display_name FROM players WHERE id = @p", null, ("p", playerId))
                   ?? throw NotFound();
        var membership = await GetMembershipAsync(c, null, playerId);
        return new ProfileDto(playerId, name, membership?.PartyId);
    }

    public async Task<Guid?> RenameAsync(Guid playerId, string displayName)
    {
        var name = CleanName(displayName, ErrorCodes.InvalidRequest);
        await using var c = await db.OpenConnectionAsync();
        await c.ExecAsync("UPDATE players SET display_name = @n WHERE id = @p", null, ("n", name), ("p", playerId));
        return (await GetMembershipAsync(c, null, playerId))?.PartyId;
    }

    /// <summary>Deletes the player and every event they reported. Owned parties pass to the longest member or are deleted.</summary>
    /// <returns>The party the player was in (to notify remaining members).</returns>
    public async Task<Guid?> DeleteAccountAsync(Guid playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var partyId = await LeaveCoreAsync(c, tx, playerId, "account_deleted");
        // Parties this player still owns (should not exist after leaving, but be safe).
        await c.ExecAsync("DELETE FROM parties WHERE owner_id = @p", tx, ("p", playerId));
        await c.ExecAsync("DELETE FROM players WHERE id = @p", tx, ("p", playerId));
        await tx.CommitAsync();
        return partyId;
    }

    // ================================================================ parties

    public async Task<PartyStateDto> CreatePartyAsync(Guid playerId, string name, int maxPlayers)
    {
        var partyName = CleanName(name, ErrorCodes.InvalidRequest);
        var max = Math.Clamp(maxPlayers <= 0 ? O.MaxPartySize : maxPlayers, 1, Math.Max(1, O.MaxPartySize));
        var now = clock.GetUtcNow();
        var partyId = Guid.NewGuid();

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        await LeaveCoreAsync(c, tx, playerId, "switched_party");
        await c.ExecAsync("""
            INSERT INTO parties (id, name, owner_id, max_players, invite_code, invite_expires_at, created_at)
            VALUES (@id, @n, @o, @m, @code, @exp, @t)
            """, tx, ("id", partyId), ("n", partyName), ("o", playerId), ("m", max), ("code", InviteCodes.Generate()),
            ("exp", now.AddHours(O.InviteTtlHours)), ("t", now));
        await c.ExecAsync("INSERT INTO party_members (party_id, player_id, role, joined_at) VALUES (@pa, @pl, @r, @t)", tx,
            ("pa", partyId), ("pl", playerId), ("r", PartyRole.Owner), ("t", now));
        await OpenPeriodAsync(c, tx, partyId, playerId, now);
        await tx.CommitAsync();
        return await GetStateAsync(c, null, partyId);
    }

    public async Task<(PartyStateDto State, Guid? PreviousParty)> JoinPartyAsync(Guid playerId, string code)
    {
        var normalized = InviteCodes.Normalize(code) ?? throw new PartyException(400, ErrorCodes.InvalidCode, "Invalid invitation code.");
        var now = clock.GetUtcNow();

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        // Row lock serializes concurrent joins so max_players cannot be exceeded.
        var party = (await c.ListAsync("""
            SELECT id, max_players FROM parties
            WHERE invite_code = @c AND invite_expires_at > @now
            FOR UPDATE
            """, tx, r => (Id: r.GetGuid(0), Max: r.GetInt32(1)), ("c", normalized), ("now", now))).FirstOrDefault();
        if (party.Id == Guid.Empty)
        {
            throw new PartyException(404, ErrorCodes.InvalidCode, "The invitation code is wrong or expired.");
        }

        var current = await GetMembershipAsync(c, tx, playerId);
        if (current?.PartyId == party.Id)
        {
            await tx.CommitAsync();
            return (await GetStateAsync(c, null, party.Id), null);
        }

        var count = await c.ScalarAsync<long>("SELECT COUNT(*) FROM party_members WHERE party_id = @p AND left_at IS NULL", tx, ("p", party.Id));
        if (count >= party.Max)
        {
            throw new PartyException(409, ErrorCodes.PartyFull, "The party is full.");
        }

        var previous = await LeaveCoreAsync(c, tx, playerId, "switched_party");
        await c.ExecAsync("""
            INSERT INTO party_members (party_id, player_id, role, joined_at) VALUES (@pa, @pl, 0, @t)
            ON CONFLICT (party_id, player_id) DO UPDATE SET left_at = NULL, joined_at = @t, role = 0
            """, tx, ("pa", party.Id), ("pl", playerId), ("t", now));
        await OpenPeriodAsync(c, tx, party.Id, playerId, now); // a rejoin starts a new, separate period

        var active = await GetActiveMatchAsync(c, tx, party.Id);
        if (active is not null)
        {
            await c.ExecAsync("""
                INSERT INTO shared_match_participants (match_id, player_id, response) VALUES (@m, @p, 0)
                ON CONFLICT DO NOTHING
                """, tx, ("m", active.Id), ("p", playerId));
        }

        await tx.CommitAsync();
        return (await GetStateAsync(c, null, party.Id), previous);
    }

    public async Task<Guid?> LeaveAsync(Guid playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var partyId = await LeaveCoreAsync(c, tx, playerId, "left");
        await tx.CommitAsync();
        return partyId;
    }

    public async Task<PartyStateDto> RegenerateInviteAsync(Guid playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        var m = await RequireOwnerAsync(c, null, playerId);
        await c.ExecAsync("UPDATE parties SET invite_code = @c, invite_expires_at = @e WHERE id = @p", null,
            ("c", InviteCodes.Generate()), ("e", clock.GetUtcNow().AddHours(O.InviteTtlHours)), ("p", m.PartyId));
        return await GetStateAsync(c, null, m.PartyId);
    }

    public async Task<Guid> KickAsync(Guid ownerId, Guid targetId)
    {
        if (ownerId == targetId)
        {
            throw new PartyException(400, ErrorCodes.InvalidRequest, "Use leave instead.");
        }

        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var m = await RequireOwnerAsync(c, tx, ownerId);
        var now = clock.GetUtcNow();
        var removed = await c.ExecAsync("UPDATE party_members SET left_at = @t WHERE party_id = @p AND player_id = @x AND left_at IS NULL", tx,
            ("t", now), ("p", m.PartyId), ("x", targetId));
        if (removed == 0)
        {
            throw new PartyException(404, ErrorCodes.NotMember, "Player is not in the party.");
        }

        await ClosePeriodAsync(c, tx, m.PartyId, targetId, now, "kicked");
        await tx.CommitAsync();
        return m.PartyId;
    }

    public async Task<Guid> TransferOwnershipAsync(Guid ownerId, Guid targetId)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var m = await RequireOwnerAsync(c, tx, ownerId);
        var target = await GetMembershipAsync(c, tx, targetId);
        if (target?.PartyId != m.PartyId)
        {
            throw new PartyException(404, ErrorCodes.NotMember, "Player is not in the party.");
        }

        await SetOwnerAsync(c, tx, m.PartyId, targetId);
        await tx.CommitAsync();
        return m.PartyId;
    }

    public async Task<Guid> SetSharingAsync(Guid playerId, bool shareCounts, bool shareActivity)
    {
        await using var c = await db.OpenConnectionAsync();
        var m = await RequireMemberAsync(c, null, playerId);
        await c.ExecAsync("UPDATE party_members SET share_counts = @sc, share_activity = @sa WHERE party_id = @p AND player_id = @x", null,
            ("sc", shareCounts), ("sa", shareActivity), ("p", m.PartyId), ("x", playerId));
        return m.PartyId;
    }

    public async Task<PartyStateDto> GetStateForPlayerAsync(Guid playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        var m = await RequireMemberAsync(c, null, playerId);
        return await GetStateAsync(c, null, m.PartyId);
    }

    public async Task<PartyStateDto?> GetStateAsync(Guid partyId)
    {
        await using var c = await db.OpenConnectionAsync();
        var exists = await c.ScalarAsync<long>("SELECT COUNT(*) FROM parties WHERE id = @p", null, ("p", partyId));
        return exists == 0 ? null : await GetStateAsync(c, null, partyId);
    }

    public async Task<Guid?> GetPartyIdAsync(Guid playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        return (await GetMembershipAsync(c, null, playerId))?.PartyId;
    }

    // ========================================================= shared matches

    public async Task<SharedMatchDto> StartSharedMatchAsync(Guid playerId)
    {
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var m = await RequireOwnerAsync(c, tx, playerId);
        await c.ExecAsync("SELECT 1 FROM parties WHERE id = @p FOR UPDATE", tx, ("p", m.PartyId));
        var existing = await GetActiveMatchAsync(c, tx, m.PartyId);
        if (existing is not null)
        {
            await tx.CommitAsync();
            return existing;
        }

        var match = new SharedMatchDto(Guid.NewGuid(), SharedMatchStatus.Active, playerId, clock.GetUtcNow(), null);
        await c.ExecAsync("INSERT INTO shared_matches (id, party_id, started_by, status, started_at) VALUES (@id, @p, @b, 1, @t)", tx,
            ("id", match.Id), ("p", m.PartyId), ("b", playerId), ("t", match.StartedAt));
        await c.ExecAsync("""
            INSERT INTO shared_match_participants (match_id, player_id, response, responded_at)
            SELECT @m, player_id, CASE WHEN player_id = @owner THEN 1 ELSE 0 END, CASE WHEN player_id = @owner THEN @t END
            FROM party_members WHERE party_id = @p AND left_at IS NULL
            """, tx, ("m", match.Id), ("owner", playerId), ("t", match.StartedAt), ("p", m.PartyId));
        await tx.CommitAsync();
        return match;
    }

    public async Task<(Guid PartyId, SharedMatchDto Match)> RespondSharedMatchAsync(Guid playerId, Guid matchId, bool accept)
    {
        await using var c = await db.OpenConnectionAsync();
        var m = await RequireMemberAsync(c, null, playerId);
        var active = await GetActiveMatchAsync(c, null, m.PartyId);
        if (active is null || active.Id != matchId)
        {
            throw new PartyException(404, ErrorCodes.NotFound, "No such active shared match.");
        }

        await c.ExecAsync("""
            INSERT INTO shared_match_participants (match_id, player_id, response, responded_at) VALUES (@m, @p, @r, @t)
            ON CONFLICT (match_id, player_id) DO UPDATE SET response = @r, responded_at = @t
            """, null, ("m", matchId), ("p", playerId), ("r", accept ? ParticipantResponse.Accepted : ParticipantResponse.Declined),
            ("t", clock.GetUtcNow()));
        return (m.PartyId, active);
    }

    public async Task<(Guid PartyId, SharedMatchDto Match)> EndSharedMatchAsync(Guid playerId, Guid matchId)
    {
        await using var c = await db.OpenConnectionAsync();
        var m = await RequireOwnerAsync(c, null, playerId);
        var now = clock.GetUtcNow();
        var updated = await c.ExecAsync("UPDATE shared_matches SET status = 2, ended_at = @t WHERE id = @id AND party_id = @p AND status = 1", null,
            ("t", now), ("id", matchId), ("p", m.PartyId));
        if (updated == 0)
        {
            throw new PartyException(404, ErrorCodes.NotFound, "No such active shared match.");
        }

        var match = (await c.ListAsync("SELECT id, status, started_by, started_at, ended_at FROM shared_matches WHERE id = @id", null,
            ReadMatch, ("id", matchId))).Single();
        return (m.PartyId, match);
    }

    // ================================================================= events

    /// <summary>
    /// Validates and stores a batch of events. Processing is idempotent: an event id that was already stored
    /// is acknowledged as <see cref="AckStatus.Duplicate"/> and changes nothing.
    /// </summary>
    public async Task<SubmitResult> SubmitEventsAsync(Guid playerId, IReadOnlyList<ChestEventDto> events)
    {
        if (events.Count > O.MaxEventsPerBatch)
        {
            throw new PartyException(400, ErrorCodes.InvalidRequest, $"At most {O.MaxEventsPerBatch} events per batch.");
        }

        var now = clock.GetUtcNow();
        await using var c = await db.OpenConnectionAsync();
        await using var tx = await c.BeginTransactionAsync();
        var membership = await GetMembershipAsync(c, tx, playerId);
        var currentParty = membership?.PartyId;
        var shareActivity = currentParty is { } cp && await c.ScalarAsync<bool>(
            "SELECT share_activity FROM party_members WHERE party_id = @p AND player_id = @x", tx, ("p", cp), ("x", playerId));
        var displayName = await c.ScalarAsync<string>("SELECT display_name FROM players WHERE id = @x", tx, ("x", playerId)) ?? "";
        var matchCache = new Dictionary<Guid, (Guid PartyId, DateTimeOffset Start, DateTimeOffset? End, bool Participant)?>();
        var periodCache = new Dictionary<Guid, List<MembershipPeriod>>();
        var grace = TimeSpan.FromSeconds(Math.Max(0, O.MembershipGraceSeconds));

        var acks = new List<EventAck>(events.Count);
        var activity = new List<ActivityDto>();
        var changed = new HashSet<Guid>();

        // An event belongs to a party only if the sender was a member of it at the event's own time.
        async Task<bool> WasMemberAsync(Guid party, DateTimeOffset at)
        {
            if (!periodCache.TryGetValue(party, out var periods))
            {
                periods = await GetPeriodsAsync(c, tx, playerId, party);
                periodCache[party] = periods;
            }

            // An open period has no upper bound here: future timestamps are already limited by MaxFutureSkewMinutes.
            return periods.Any(p => at >= p.Start - grace && (p.End is not { } end || at <= end + grace));
        }

        foreach (var e in events)
        {
            // Idempotency: the event id is the key.
            var existing = (await c.ListAsync("SELECT player_id, verified FROM chest_events WHERE event_id = @id", tx,
                r => (Player: r.GetGuid(0), Verified: r.GetBoolean(1)), ("id", e.EventId))).FirstOrDefault();
            if (existing.Player != Guid.Empty)
            {
                acks.Add(existing.Player == playerId
                    ? new EventAck(e.EventId, AckStatus.Duplicate, existing.Verified, null)
                    : new EventAck(e.EventId, AckStatus.Rejected, false, "id_conflict"));
                continue;
            }

            var invalid = ValidateShape(e, now);
            if (invalid is not null)
            {
                acks.Add(new EventAck(e.EventId, AckStatus.Rejected, false, invalid));
                continue;
            }

            // Undo target (validated below); an undo always belongs to the party of the chest it removes.
            (Guid Player, short Kind, bool Verified, Guid Party) target = default;
            if (e.Kind == EventKind.Undo)
            {
                target = (await c.ListAsync("SELECT player_id, kind, verified, party_id FROM chest_events WHERE event_id = @id", tx,
                    r => (r.GetGuid(0), r.GetInt16(1), r.GetBoolean(2), r.GetGuid(3)), ("id", e.UndoesEventId!.Value))).FirstOrDefault();
                if (target.Player != Guid.Empty && (target.Player != playerId || target.Kind != (short)EventKind.Add))
                {
                    acks.Add(new EventAck(e.EventId, AckStatus.Rejected, false, "invalid_undo_target"));
                    continue;
                }
            }

            var partyId = target.Player != Guid.Empty ? target.Party : e.PartyId ?? currentParty;
            if (partyId is not { } pid)
            {
                acks.Add(new EventAck(e.EventId, AckStatus.Rejected, false, ErrorCodes.NotMember));
                continue;
            }

            if (!await WasMemberAsync(pid, e.ClientTimestamp))
            {
                // Recorded before joining or after leaving: never counted for that party.
                acks.Add(new EventAck(e.EventId, AckStatus.Rejected, false, pid == currentParty ? "outside_membership" : ErrorCodes.NotMember));
                continue;
            }

            string? flag = null;
            var verified = true;

            // Shared match attribution, decided by the event's own timestamp (offline events land in the right match).
            Guid? sharedMatchId = null;
            if (e.SharedMatchId is { } sid)
            {
                if (!matchCache.TryGetValue(sid, out var info))
                {
                    info = (await c.ListAsync("""
                        SELECT s.party_id, s.started_at, s.ended_at,
                               EXISTS (SELECT 1 FROM shared_match_participants p WHERE p.match_id = s.id AND p.player_id = @x AND p.response = 1)
                        FROM shared_matches s WHERE s.id = @id
                        """, tx, r => ((Guid, DateTimeOffset, DateTimeOffset?, bool)?)(r.GetGuid(0), r.Ts(1), r.NTs(2), r.GetBoolean(3)),
                        ("id", sid), ("x", playerId))).FirstOrDefault();
                    matchCache[sid] = info;
                }

                var matchGrace = TimeSpan.FromMinutes(O.SharedMatchGraceMinutes);
                if (info is { } i && i.PartyId == pid && i.Participant
                    && e.ClientTimestamp >= i.Start - matchGrace && e.ClientTimestamp <= (i.End ?? now) + matchGrace)
                {
                    sharedMatchId = sid;
                }
                else
                {
                    flag = "outside_shared_match";
                }
            }

            if (e.Kind == EventKind.Correction)
            {
                verified = false;
                flag = "manual_correction";
            }
            else if (e.Kind == EventKind.Add)
            {
                var tooClose = await c.ScalarAsync<bool>("""
                    SELECT EXISTS (SELECT 1 FROM chest_events
                                   WHERE player_id = @x AND kind = 0 AND verified
                                     AND client_ts > @from AND client_ts < @to)
                    """, tx, ("x", playerId), ("from", e.ClientTimestamp.AddSeconds(-O.MinSecondsBetweenAdds)),
                    ("to", e.ClientTimestamp.AddSeconds(O.MinSecondsBetweenAdds)));
                if (tooClose)
                {
                    verified = false;
                    flag = "rate_limited";
                }
            }
            else if (target.Player == Guid.Empty)
            {
                verified = false;
                flag = "unknown_undo_target";
            }
            else
            {
                verified = target.Verified;
            }

            var inserted = await c.ExecAsync("""
                INSERT INTO chest_events (event_id, player_id, party_id, shared_match_id, local_match_id, kind, source, delta,
                                          undoes_event_id, confidence, client_ts, server_ts, verified, flag)
                VALUES (@id, @x, @p, @s, @lm, @k, @src, @d, @u, @conf, @ct, @st, @v, @f)
                ON CONFLICT DO NOTHING
                """, tx, ("id", e.EventId), ("x", playerId), ("p", pid), ("s", sharedMatchId), ("lm", e.LocalMatchId),
                ("k", e.Kind), ("src", e.Source), ("d", e.Delta), ("u", e.UndoesEventId), ("conf", e.Confidence),
                ("ct", e.ClientTimestamp), ("st", now), ("v", verified), ("f", flag));
            if (inserted == 0)
            {
                // Only the partial unique index on undoes_event_id can conflict here.
                acks.Add(new EventAck(e.EventId, AckStatus.Rejected, false, "already_undone"));
                continue;
            }

            changed.Add(pid);
            acks.Add(new EventAck(e.EventId, AckStatus.Accepted, verified, flag));
            if (pid == currentParty && shareActivity && e.Kind != EventKind.Correction
                && e.ClientTimestamp > now.AddSeconds(-O.LiveActivityWindowSeconds))
            {
                activity.Add(new ActivityDto(playerId, displayName, e.Kind, e.Source, e.Delta, e.ClientTimestamp, sharedMatchId));
            }
        }

        await tx.CommitAsync();
        return new SubmitResult(currentParty, acks, activity, changed.ToList());
    }

    private string? ValidateShape(ChestEventDto e, DateTimeOffset now)
    {
        if (e.EventId == Guid.Empty || e.LocalMatchId == Guid.Empty) return "invalid_id";
        switch (e.Kind)
        {
            case EventKind.Add when e.Delta != 1:
            case EventKind.Undo when e.Delta != -1 || e.UndoesEventId is null:
            case EventKind.Correction when e.Delta == 0 || Math.Abs(e.Delta) > 9999:
                return "invalid_delta";
            case EventKind.Add or EventKind.Undo or EventKind.Correction:
                break;
            default:
                return "invalid_kind";
        }

        if (!Enum.IsDefined(e.Source)) return "invalid_source";
        if (e.ClientTimestamp > now.AddMinutes(O.MaxFutureSkewMinutes)) return "timestamp_in_future";
        if (e.ClientTimestamp < now.AddDays(-O.MaxEventAgeDays)) return "timestamp_too_old";
        return null;
    }

    // ===================================================== leaderboard/history

    public async Task<IReadOnlyList<LeaderboardEntryDto>> GetLeaderboardAsync(Guid playerId, LeaderboardPeriod period, int tzOffsetMinutes)
    {
        await using var c = await db.OpenConnectionAsync();
        var m = await RequireMemberAsync(c, null, playerId);
        var offset = TimeSpan.FromMinutes(Math.Clamp(tzOffsetMinutes, -14 * 60, 14 * 60));
        var localNow = clock.GetUtcNow().ToOffset(offset);
        DateTimeOffset? from = period switch
        {
            LeaderboardPeriod.Day => new DateTimeOffset(localNow.Date, offset),
            LeaderboardPeriod.Week => new DateTimeOffset(localNow.Date.AddDays(-(((int)localNow.DayOfWeek + 6) % 7)), offset),
            _ => null,
        };

        var rows = await c.ListAsync("""
            SELECT pm.player_id, pl.display_name,
                   COALESCE(SUM(CASE WHEN e.verified AND e.kind IN (0, 1) THEN e.delta END), 0)::int,
                   COALESCE(SUM(e.delta), 0)::int
            FROM party_members pm
            JOIN players pl ON pl.id = pm.player_id
            LEFT JOIN chest_events e ON e.party_id = pm.party_id AND e.player_id = pm.player_id
                                     AND (@from::timestamptz IS NULL OR e.client_ts >= @from::timestamptz)
            WHERE pm.party_id = @p AND pm.left_at IS NULL AND pm.share_counts
            GROUP BY pm.player_id, pl.display_name
            ORDER BY 3 DESC, 4 DESC, pl.display_name
            """, null, r => (Id: r.GetGuid(0), Name: r.GetString(1), V: r.GetInt32(2), R: r.GetInt32(3)),
            ("p", m.PartyId), ("from", from));

        var result = new List<LeaderboardEntryDto>();
        var rank = 0;
        (int, int)? previous = null;
        for (var i = 0; i < rows.Count; i++)
        {
            if (previous != (rows[i].V, rows[i].R)) rank = i + 1;
            previous = (rows[i].V, rows[i].R);
            result.Add(new LeaderboardEntryDto(rank, rows[i].Id, rows[i].Name, rows[i].V, rows[i].R));
        }

        return result;
    }

    public async Task<IReadOnlyList<SharedMatchHistoryDto>> GetHistoryAsync(Guid playerId, int limit)
    {
        await using var c = await db.OpenConnectionAsync();
        var m = await RequireMemberAsync(c, null, playerId);
        var matches = await c.ListAsync("""
            SELECT id, status, started_by, started_at, ended_at FROM shared_matches
            WHERE party_id = @p ORDER BY started_at DESC LIMIT @n
            """, null, ReadMatch, ("p", m.PartyId), ("n", Math.Clamp(limit, 1, 200)));

        var result = new List<SharedMatchHistoryDto>();
        foreach (var match in matches)
        {
            var participants = await GetParticipantStatsAsync(c, null, m.PartyId, match.Id);
            var team = new CountPair(participants.Sum(p => p.Counts?.Verified ?? 0), participants.Sum(p => p.Counts?.Reported ?? 0));
            result.Add(new SharedMatchHistoryDto(match.Id, match.StartedAt, match.EndedAt, participants, team));
        }

        return result;
    }

    // ================================================================ helpers

    private async Task<PartyStateDto> GetStateAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid partyId)
    {
        var party = (await c.ListAsync("SELECT name, owner_id, max_players, invite_code, invite_expires_at FROM parties WHERE id = @p", tx,
            r => (Name: r.GetString(0), Owner: r.GetGuid(1), Max: r.GetInt32(2), Code: r.IsDBNull(3) ? null : r.GetString(3), Exp: r.NTs(4)),
            ("p", partyId))).Single();
        var active = await GetActiveMatchAsync(c, tx, partyId);

        var members = await c.ListAsync("""
            SELECT pm.player_id, pl.display_name, pm.role, pm.share_counts, pm.share_activity,
                   COALESCE(SUM(CASE WHEN e.verified AND e.kind IN (0, 1) THEN e.delta END), 0)::int,
                   COALESCE(SUM(e.delta), 0)::int,
                   COALESCE(SUM(CASE WHEN e.verified AND e.kind IN (0, 1) AND e.shared_match_id = @m THEN e.delta END), 0)::int,
                   COALESCE(SUM(CASE WHEN e.shared_match_id = @m THEN e.delta END), 0)::int,
                   (SELECT sp.response FROM shared_match_participants sp WHERE sp.match_id = @m AND sp.player_id = pm.player_id)
            FROM party_members pm
            JOIN players pl ON pl.id = pm.player_id
            LEFT JOIN chest_events e ON e.party_id = pm.party_id AND e.player_id = pm.player_id
            WHERE pm.party_id = @p AND pm.left_at IS NULL
            GROUP BY pm.player_id, pl.display_name, pm.role, pm.share_counts, pm.share_activity, pm.joined_at
            ORDER BY pm.role DESC, pm.joined_at
            """, tx, r =>
        {
            var share = r.GetBoolean(3);
            return new MemberDto(
                r.GetGuid(0), r.GetString(1), (PartyRole)r.GetInt16(2), presence.IsOnline(r.GetGuid(0)), share, r.GetBoolean(4),
                share && active is not null ? new CountPair(r.GetInt32(7), r.GetInt32(8)) : null,
                share ? new CountPair(r.GetInt32(5), r.GetInt32(6)) : null,
                r.IsDBNull(9) ? null : (ParticipantResponse)r.GetInt16(9));
        }, ("p", partyId), ("m", active?.Id ?? Guid.Empty));

        // Same definition as the final match result in history, so a member leaving mid-match changes nothing.
        var participants = active is null ? [] : await GetParticipantStatsAsync(c, tx, partyId, active.Id);
        var teamMatch = new CountPair(participants.Sum(p => p.Counts?.Verified ?? 0), participants.Sum(p => p.Counts?.Reported ?? 0));
        // Everything accepted for this party, by current and former members alike (events keep the party they were
        // recorded in). Players who hide their counts are left out, as before; deleted accounts take their events with them.
        var teamLifetime = (await c.ListAsync("""
            SELECT COALESCE(SUM(CASE WHEN e.verified AND e.kind IN (0, 1) THEN e.delta END), 0)::int, COALESCE(SUM(e.delta), 0)::int
            FROM chest_events e
            JOIN party_members pm ON pm.party_id = e.party_id AND pm.player_id = e.player_id
            WHERE e.party_id = @p AND pm.share_counts
            """, tx, r => new CountPair(r.GetInt32(0), r.GetInt32(1)), ("p", partyId))).Single();
        return new PartyStateDto(partyId, party.Name, party.Owner, party.Max, party.Code, party.Exp, members, active,
            teamMatch, teamLifetime, clock.GetUtcNow());
    }

    /// <summary>
    /// Per-participant counts of one shared match: every player who accepted it (including one who left the party
    /// afterwards), only events attributed to that match, hidden counts (share_counts off) excluded.
    /// </summary>
    private static Task<List<ParticipantStatsDto>> GetParticipantStatsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid partyId, Guid matchId) =>
        c.ListAsync("""
            SELECT sp.player_id, pl.display_name, COALESCE(pm.share_counts, false),
                   COALESCE(SUM(CASE WHEN e.verified AND e.kind IN (0, 1) THEN e.delta END), 0)::int,
                   COALESCE(SUM(e.delta), 0)::int
            FROM shared_match_participants sp
            JOIN players pl ON pl.id = sp.player_id
            LEFT JOIN party_members pm ON pm.party_id = @p AND pm.player_id = sp.player_id
            LEFT JOIN chest_events e ON e.shared_match_id = sp.match_id AND e.player_id = sp.player_id
            WHERE sp.match_id = @m AND sp.response = 1
            GROUP BY sp.player_id, pl.display_name, pm.share_counts
            ORDER BY pl.display_name
            """, tx, r => new ParticipantStatsDto(r.GetGuid(0), r.GetString(1),
                r.GetBoolean(2) ? new CountPair(r.GetInt32(3), r.GetInt32(4)) : null),
            ("p", partyId), ("m", matchId));

    /// <summary>Removes the player from their current party, handing ownership over or deleting an empty party.</summary>
    private async Task<Guid?> LeaveCoreAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid playerId, string reason)
    {
        var m = await GetMembershipAsync(c, tx, playerId);
        if (m is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        await c.ExecAsync("UPDATE party_members SET left_at = @t WHERE party_id = @p AND player_id = @x", tx,
            ("t", now), ("p", m.PartyId), ("x", playerId));
        await ClosePeriodAsync(c, tx, m.PartyId, playerId, now, reason);

        if (m.Role == PartyRole.Owner)
        {
            var successor = await c.ScalarAsync<Guid>("""
                SELECT player_id FROM party_members WHERE party_id = @p AND left_at IS NULL ORDER BY joined_at LIMIT 1
                """, tx, ("p", m.PartyId));
            if (successor == Guid.Empty)
            {
                await c.ExecAsync("DELETE FROM parties WHERE id = @p", tx, ("p", m.PartyId));
            }
            else
            {
                await SetOwnerAsync(c, tx, m.PartyId, successor);
            }
        }

        return m.PartyId;
    }

    private static async Task OpenPeriodAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid partyId, Guid playerId, DateTimeOffset at)
    {
        await c.ExecAsync("""
            INSERT INTO party_membership_periods (party_id, player_id, started_at) VALUES (@p, @x, @t)
            ON CONFLICT (party_id, player_id) WHERE ended_at IS NULL DO NOTHING
            """, tx, ("p", partyId), ("x", playerId), ("t", at));
    }

    private static Task<int> ClosePeriodAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid partyId, Guid playerId, DateTimeOffset at, string reason) =>
        c.ExecAsync("""
            UPDATE party_membership_periods SET ended_at = @t, end_reason = @r
            WHERE party_id = @p AND player_id = @x AND ended_at IS NULL
            """, tx, ("t", at), ("r", reason), ("p", partyId), ("x", playerId));

    /// <summary>All membership periods of a player in one party (oldest first).</summary>
    private static Task<List<MembershipPeriod>> GetPeriodsAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid playerId, Guid partyId) =>
        c.ListAsync("""
            SELECT party_id, started_at, ended_at FROM party_membership_periods
            WHERE player_id = @x AND party_id = @p ORDER BY started_at
            """, tx, r => new MembershipPeriod(r.GetGuid(0), r.Ts(1), r.NTs(2)), ("x", playerId), ("p", partyId));

    private static async Task SetOwnerAsync(NpgsqlConnection c, NpgsqlTransaction tx, Guid partyId, Guid newOwner)
    {
        await c.ExecAsync("UPDATE party_members SET role = 0 WHERE party_id = @p", tx, ("p", partyId));
        await c.ExecAsync("UPDATE party_members SET role = 1 WHERE party_id = @p AND player_id = @x", tx, ("p", partyId), ("x", newOwner));
        await c.ExecAsync("UPDATE parties SET owner_id = @x WHERE id = @p", tx, ("p", partyId), ("x", newOwner));
    }

    private static async Task<Membership?> GetMembershipAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid playerId) =>
        (await c.ListAsync("SELECT party_id, role FROM party_members WHERE player_id = @x AND left_at IS NULL", tx,
            r => new Membership(r.GetGuid(0), (PartyRole)r.GetInt16(1)), ("x", playerId))).FirstOrDefault();

    private static async Task<Membership> RequireMemberAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid playerId) =>
        await GetMembershipAsync(c, tx, playerId) ?? throw new PartyException(403, ErrorCodes.NotMember, "You are not in a party.");

    private static async Task<Membership> RequireOwnerAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid playerId)
    {
        var m = await RequireMemberAsync(c, tx, playerId);
        return m.Role == PartyRole.Owner ? m : throw new PartyException(403, ErrorCodes.NotOwner, "Only the party owner can do this.");
    }

    private static async Task<SharedMatchDto?> GetActiveMatchAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid partyId) =>
        (await c.ListAsync("SELECT id, status, started_by, started_at, ended_at FROM shared_matches WHERE party_id = @p AND status = 1", tx,
            ReadMatch, ("p", partyId))).FirstOrDefault();

    private static SharedMatchDto ReadMatch(NpgsqlDataReader r) =>
        new(r.GetGuid(0), (SharedMatchStatus)r.GetInt16(1), r.GetGuid(2), r.Ts(3), r.NTs(4));

    private static PartyException NotFound() => new(404, ErrorCodes.NotFound, "Not found.");

    internal static string CleanName(string? name, string code)
    {
        var cleaned = new string((name ?? "").Where(ch => !char.IsControl(ch)).ToArray()).Trim();
        if (cleaned.Length is < 1 or > 32)
        {
            throw new PartyException(400, code, "Names must be 1–32 characters.");
        }

        return cleaned;
    }
}
