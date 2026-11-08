using System.Security.Claims;
using ChestTracker.Protocol;
using ChestTracker.Server.Hubs;
using ChestTracker.Server.Infrastructure;
using ChestTracker.Server.Services;
using Microsoft.Extensions.Options;

namespace ChestTracker.Server;

public static class Endpoints
{
    public static void MapApi(this WebApplication app)
    {
        app.MapGet("/api/status", (IOptions<ServerOptions> o, PresenceTracker presence, TimeProvider clock) =>
            Results.Ok(new ServerStatusDto(ServerInfo.Version, ProtocolInfo.Version, o.Value.MinClientProtocol, clock.GetUtcNow(),
                presence.OnlinePlayerCount, o.Value.AllowAccounts, o.Value.AllowGuests)));

        var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");
        auth.MapPost("/register", (RegisterRequest r, AuthService s) => Run(() => s.RegisterGuestAsync(r.DisplayName)));
        auth.MapPost("/accounts", (AccountRegisterRequest r, AuthService s) => Run(() => s.RegisterAccountAsync(r)));
        auth.MapPost("/login", (LoginRequest r, AuthService s) => Run(() => s.LoginAsync(r)));
        auth.MapPost("/token", (TokenRequest r, AuthService s) => Run(() => s.IssueTokenAsync(r)));

        var api = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("api");

        api.MapGet("/me", (ClaimsPrincipal u, AuthService s) => Run(() => s.GetAccountAsync(Id(u))));
        api.MapPut("/me", (RenameRequest r, ClaimsPrincipal u, PartyService s, AuthService a, Notifier n) => Run(async () =>
        {
            var party = await s.RenameAsync(Id(u), r.DisplayName);
            if (party is { } p) await n.BroadcastStateAsync(p);
            return await a.GetAccountAsync(Id(u));
        }));
        api.MapPost("/me/claim", (ClaimAccountRequest r, ClaimsPrincipal u, AuthService s) => Run(() => s.ClaimAccountAsync(Id(u), r)))
            .RequireRateLimiting("auth");
        api.MapDelete("/me", (ClaimsPrincipal u, PartyService s, Notifier n) => Run(async () =>
        {
            var player = Id(u);
            var party = await s.DeleteAccountAsync(player);
            if (party is { } p)
            {
                await n.MoveConnectionsAsync(player, p, null);
                await n.BroadcastStateAsync(p);
            }

            return new { deleted = true };
        }));

        api.MapPost("/parties", (CreatePartyRequest r, ClaimsPrincipal u, PartyService s, Notifier n) => Run(async () =>
        {
            var player = Id(u);
            var previous = await s.GetPartyIdAsync(player);
            var state = await s.CreatePartyAsync(player, r.Name, r.MaxPlayers);
            await n.MoveConnectionsAsync(player, previous, state.PartyId);
            if (previous is { } p) await n.BroadcastStateAsync(p);
            return state;
        }));

        api.MapPost("/parties/join", (JoinPartyRequest r, ClaimsPrincipal u, PartyService s, Notifier n) => Run(async () =>
        {
            var player = Id(u);
            var (state, previous) = await s.JoinPartyAsync(player, r.Code);
            await n.MoveConnectionsAsync(player, previous, state.PartyId);
            if (previous is { } p) await n.BroadcastStateAsync(p);
            await n.BroadcastStateAsync(state.PartyId);
            return state;
        })).RequireRateLimiting("join");

        api.MapGet("/parties/current", (ClaimsPrincipal u, PartyService s) => Run(() => s.GetStateForPlayerAsync(Id(u))));

        api.MapPost("/parties/current/leave", (ClaimsPrincipal u, PartyService s, Notifier n) => Run(async () =>
        {
            var player = Id(u);
            var party = await s.LeaveAsync(player);
            if (party is { } p)
            {
                await n.MoveConnectionsAsync(player, p, null);
                await n.BroadcastStateAsync(p);
            }

            return new { left = party is not null };
        }));

        api.MapPost("/parties/current/invite", (ClaimsPrincipal u, PartyService s, Notifier n) => Run(async () =>
        {
            var state = await s.RegenerateInviteAsync(Id(u));
            await n.BroadcastStateAsync(state.PartyId);
            return state;
        }));

        api.MapPost("/parties/current/kick/{playerId:guid}", (Guid playerId, ClaimsPrincipal u, PartyService s, Notifier n) => Run(async () =>
        {
            var party = await s.KickAsync(Id(u), playerId);
            await n.NotifyRemovedAsync(playerId, party);
            await n.MoveConnectionsAsync(playerId, party, null);
            await n.BroadcastStateAsync(party);
            return new { kicked = true };
        }));

        api.MapPost("/parties/current/transfer/{playerId:guid}", (Guid playerId, ClaimsPrincipal u, PartyService s, Notifier n) => Run(async () =>
        {
            var party = await s.TransferOwnershipAsync(Id(u), playerId);
            await n.BroadcastStateAsync(party);
            return new { transferred = true };
        }));

        api.MapPut("/parties/current/sharing", (SharingRequest r, ClaimsPrincipal u, PartyService s, Notifier n) => Run(async () =>
        {
            var party = await s.SetSharingAsync(Id(u), r.ShareCounts, r.ShareActivity);
            await n.BroadcastStateAsync(party);
            return new { updated = true };
        }));

        api.MapGet("/parties/current/leaderboard", (LeaderboardPeriod period, int? tzOffsetMinutes, ClaimsPrincipal u, PartyService s) =>
            Run(() => s.GetLeaderboardAsync(Id(u), period, tzOffsetMinutes ?? 0)));

        api.MapGet("/parties/current/matches", (int? limit, ClaimsPrincipal u, PartyService s) =>
            Run(() => s.GetHistoryAsync(Id(u), limit ?? 20)));

        // ------------------------------------------------------------ administration
        var admin = app.MapGroup("/api/admin").AddEndpointFilter<AdminKeyFilter>().RequireRateLimiting("auth");
        admin.MapGet("/stats", (AdminService s) => Run(s.GetStatsAsync));
        admin.MapGet("/parties", (AdminService s) => Run(s.GetActivePartiesAsync));
        admin.MapGet("/backups", (BackupService b) => Results.Ok(b.List()));
        admin.MapPost("/backups", (BackupService b, ILoggerFactory lf) => Run(async () =>
        {
            lf.CreateLogger("Admin").LogInformation("Admin requested a backup");
            return await b.CreateBackupAsync();
        }));
        admin.MapDelete("/players/{playerId:guid}", (Guid playerId, PartyService s, Notifier n, ILoggerFactory lf) => Run(async () =>
        {
            lf.CreateLogger("Admin").LogWarning("Admin deleted player {Player}", playerId);
            var party = await s.DeleteAccountAsync(playerId);
            await n.DisconnectPlayerAsync(playerId);
            if (party is { } p) await n.BroadcastStateAsync(p);
            return new { deleted = true };
        }));
    }

    private static Guid Id(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue("sub"), out var id) ? id : throw new PartyException(401, ErrorCodes.Unauthorized, "Missing subject.");

    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (PartyException ex)
        {
            return Results.Json(new ErrorDto(ex.Code, ex.Message), statusCode: ex.Status);
        }
    }
}
