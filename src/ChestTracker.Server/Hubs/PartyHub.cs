using ChestTracker.Protocol;
using ChestTracker.Server.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace ChestTracker.Server.Hubs;

/// <summary>
/// Real-time channel. Connections join the SignalR group of the caller's party (looked up server-side),
/// so clients can never subscribe to a party they are not a member of.
/// </summary>
[Authorize]
public sealed class PartyHub(PartyService parties, PresenceTracker presence, Notifier notifier, ILogger<PartyHub> logger) : Hub
{
    private Guid PlayerId => Guid.Parse(Context.UserIdentifier!);

    public override async Task OnConnectedAsync()
    {
        var player = PlayerId;
        var becameOnline = presence.Add(player, Context.ConnectionId);
        var partyId = await parties.GetPartyIdAsync(player);
        if (partyId is { } p)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, Notifier.Group(p));
            if (becameOnline)
            {
                await notifier.BroadcastStateAsync(p);
            }
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var player = PlayerId;
        if (presence.Remove(player, Context.ConnectionId) && await parties.GetPartyIdAsync(player) is { } p)
        {
            await notifier.BroadcastStateAsync(p);
        }

        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>Round-trip probe used by clients for the ping indicator.</summary>
    public PingResponse Ping() => new(DateTimeOffset.UtcNow);

    public async Task<PartyStateDto?> GetPartyState()
    {
        var partyId = await parties.GetPartyIdAsync(PlayerId);
        if (partyId is null)
        {
            return null;
        }

        // Re-assert group membership (e.g. after joining through REST on another connection).
        await Groups.AddToGroupAsync(Context.ConnectionId, Notifier.Group(partyId.Value));
        return await parties.GetStateAsync(partyId.Value);
    }

    public async Task<IReadOnlyList<EventAck>> SubmitEvents(List<ChestEventDto> events)
    {
        var result = await Guard(() => parties.SubmitEventsAsync(PlayerId, events));
        if (result.PartyId is { } p)
        {
            foreach (var a in result.Activity)
            {
                await Clients.OthersInGroup(Notifier.Group(p)).SendAsync(ClientMethods.Activity, a);
            }
        }

        // Late events of a former member update that party too.
        foreach (var party in result.ChangedParties)
        {
            await notifier.BroadcastStateAsync(party);
        }

        return result.Acks;
    }

    public async Task<SharedMatchDto> StartSharedMatch()
    {
        var match = await Guard(() => parties.StartSharedMatchAsync(PlayerId));
        var partyId = (await parties.GetPartyIdAsync(PlayerId))!.Value;
        await Clients.Group(Notifier.Group(partyId)).SendAsync(ClientMethods.SharedMatchStarted, match);
        await notifier.BroadcastStateAsync(partyId);
        return match;
    }

    public async Task RespondSharedMatch(Guid matchId, bool accept)
    {
        var (partyId, _) = await Guard(() => parties.RespondSharedMatchAsync(PlayerId, matchId, accept));
        await notifier.BroadcastStateAsync(partyId);
    }

    public async Task EndSharedMatch(Guid matchId)
    {
        var (partyId, match) = await Guard(() => parties.EndSharedMatchAsync(PlayerId, matchId));
        await Clients.Group(Notifier.Group(partyId)).SendAsync(ClientMethods.SharedMatchEnded, match);
        await notifier.BroadcastStateAsync(partyId);
    }

    private async Task<T> Guard<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (PartyException ex)
        {
            // HubException messages reach the client; keep the stable code first.
            throw new HubException($"{ex.Code}: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Hub call failed");
            throw;
        }
    }
}

/// <summary>Pushes party updates to connected members.</summary>
public sealed class Notifier(IHubContext<PartyHub> hub, PartyService parties, PresenceTracker presence)
{
    public static string Group(Guid partyId) => "party:" + partyId.ToString("N");

    public async Task BroadcastStateAsync(Guid partyId)
    {
        var state = await parties.GetStateAsync(partyId);
        if (state is not null)
        {
            await hub.Clients.Group(Group(partyId)).SendAsync(ClientMethods.PartyUpdated, state);
        }
    }

    /// <summary>Moves all of the player's live connections between party groups.</summary>
    public async Task MoveConnectionsAsync(Guid playerId, Guid? fromParty, Guid? toParty)
    {
        foreach (var connection in presence.Connections(playerId))
        {
            if (fromParty is { } f) await hub.Groups.RemoveFromGroupAsync(connection, Group(f));
            if (toParty is { } t) await hub.Groups.AddToGroupAsync(connection, Group(t));
        }
    }

    /// <summary>Tells a removed player's clients that their party membership is gone and drops them from groups.</summary>
    public async Task DisconnectPlayerAsync(Guid playerId)
    {
        await hub.Clients.User(playerId.ToString()).SendAsync(ClientMethods.RemovedFromParty, Guid.Empty);
    }

    public Task NotifyRemovedAsync(Guid playerId, Guid partyId) =>
        hub.Clients.User(playerId.ToString()).SendAsync(ClientMethods.RemovedFromParty, partyId);
}
