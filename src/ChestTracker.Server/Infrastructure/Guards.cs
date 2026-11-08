using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ChestTracker.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace ChestTracker.Server.Infrastructure;

/// <summary>
/// Rejects clients whose protocol is below <see cref="ServerOptions.MinClientProtocol"/> with HTTP 426.
/// Clients that send no version are treated as version 1 (the first release did not send one).
/// </summary>
public sealed class ProtocolVersionMiddleware(RequestDelegate next, IOptions<ServerOptions> options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        var checkedPath = path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/status") && !path.StartsWithSegments("/api/admin")
                          || path.StartsWithSegments(Routes.PartyHub);
        if (checkedPath)
        {
            var raw = context.Request.Headers[ProtocolInfo.Header].FirstOrDefault()
                      ?? context.Request.Query[ProtocolInfo.QueryParameter].FirstOrDefault();
            var version = int.TryParse(raw, out var v) ? v : 1;
            if (version < options.Value.MinClientProtocol)
            {
                context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
                await context.Response.WriteAsJsonAsync(new ErrorDto(ErrorCodes.ClientOutdated,
                    $"Client protocol {version} is too old; this server needs {options.Value.MinClientProtocol}. Update Chest Tracker."));
                return;
            }
        }

        await next(context);
    }
}

/// <summary>Admin endpoints require X-Admin-Key; without a configured key they answer 404 (not advertised).</summary>
public sealed class AdminKeyFilter(IOptions<ServerOptions> options, ILogger<AdminKeyFilter> logger) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var configured = options.Value.AdminApiKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            return Results.NotFound();
        }

        var provided = context.HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault() ?? "";
        var ok = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(provided)), SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
        if (!ok)
        {
            logger.LogWarning("Rejected admin request from {Ip}", context.HttpContext.Connection.RemoteIpAddress);
            return Results.Json(new ErrorDto(ErrorCodes.Unauthorized, "Admin key required."), statusCode: 401);
        }

        return await next(context);
    }
}

/// <summary>Per-player token bucket for hub method calls.</summary>
public sealed class HubRateLimitFilter(IOptions<ServerOptions> options) : IHubFilter
{
    private static readonly ConcurrentDictionary<string, Bucket> Buckets = new();

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        var key = context.Context.UserIdentifier ?? context.Context.ConnectionId;
        var bucket = Buckets.GetOrAdd(key, _ => new Bucket());
        if (!bucket.TryTake(Math.Max(1, options.Value.HubCallsPerSecond)))
        {
            throw new HubException($"{ErrorCodes.RateLimited}: Too many requests.");
        }

        return await next(context);
    }

    private sealed class Bucket
    {
        private double _tokens = double.NaN;
        private long _last = Environment.TickCount64;

        public bool TryTake(int perSecond)
        {
            lock (this)
            {
                var now = Environment.TickCount64;
                _tokens = double.IsNaN(_tokens) ? perSecond : Math.Min(perSecond, _tokens + ((now - _last) / 1000.0 * perSecond));
                _last = now;
                if (_tokens < 1) return false;
                _tokens--;
                return true;
            }
        }
    }
}
