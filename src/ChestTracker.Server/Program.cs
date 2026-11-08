using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ChestTracker.Protocol;
using ChestTracker.Server;
using ChestTracker.Server.Data;
using ChestTracker.Server.Hubs;
using ChestTracker.Server.Infrastructure;
using ChestTracker.Server.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

// Command line:
//   ChestTracker.Server                         run the server
//   ChestTracker.Server --backup                write one backup and exit
//   ChestTracker.Server --restore <file> [--replace]   restore a backup and exit
//   ChestTracker.Server --version               print version and exit
if (args.Contains("--version"))
{
    Console.WriteLine($"ChestTracker.Server {ServerInfo.Version} (protocol {ProtocolInfo.Version})");
    return 0;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Self-contained single-file builds: configuration lives next to the executable.
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.Configure<ServerOptions>(builder.Configuration.GetSection(ServerOptions.Section));
var serverOptions = builder.Configuration.GetSection(ServerOptions.Section).Get<ServerOptions>() ?? new ServerOptions();
builder.Logging.AddProvider(new FileLoggerProvider(
    Path.GetFullPath(serverOptions.LogDirectory, AppContext.BaseDirectory), serverOptions.LogRetentionDays));

builder.Services.AddSingleton(sp =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Postgres");
    return string.IsNullOrWhiteSpace(connectionString)
        ? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured (see appsettings.json).")
        : NpgsqlDataSource.Create(connectionString);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SigningKeyProvider>();
builder.Services.AddSingleton<SchemaMigrator>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<PartyService>();
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<AdminService>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddSingleton<IUserIdProvider, SubjectUserIdProvider>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// TLS is normally terminated by a reverse proxy (nginx/Caddy). Trust its forwarded headers only from known proxies.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var proxy in serverOptions.TrustedProxies)
    {
        if (IPAddress.TryParse(proxy, out var ip)) o.KnownProxies.Add(ip);
    }
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<SigningKeyProvider>((o, keys) =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = AuthService.Issuer,
            ValidAudience = AuthService.Audience,
            IssuerSigningKey = keys.Key,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
        };
        o.Events = new JwtBearerEvents
        {
            // WebSockets cannot send headers: SignalR passes the token as a query parameter.
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments(Routes.PartyHub))
                {
                    ctx.Token = token;
                }

                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddSignalR(o =>
    {
        o.MaximumReceiveMessageSize = 256 * 1024;
        o.AddFilter<HubRateLimitFilter>();
        o.KeepAliveInterval = TimeSpan.FromSeconds(15);
        o.ClientTimeoutInterval = TimeSpan.FromSeconds(45);
    })
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<HubRateLimitFilter>();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) =>
        await ctx.HttpContext.Response.WriteAsJsonAsync(new ErrorDto(ErrorCodes.RateLimited, "Too many requests."), ct);

    static ServerOptions Opt(HttpContext ctx) => ctx.RequestServices.GetRequiredService<IOptions<ServerOptions>>().Value;
    static string Ip(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";

    // Invitation codes cannot be brute-forced: limited join attempts per player.
    o.AddPolicy("join", ctx => RateLimitPartition.GetFixedWindowLimiter(ctx.User.FindFirst("sub")?.Value ?? Ip(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Opt(ctx).JoinAttemptsPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Registration, login, token and admin requests per client IP.
    o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(Ip(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = Opt(ctx).AuthRequestsPerMinute, Window = TimeSpan.FromMinutes(1) }));
    // Everything else per player.
    o.AddPolicy("api", ctx => RateLimitPartition.GetSlidingWindowLimiter(ctx.User.FindFirst("sub")?.Value ?? Ip(ctx),
        _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = Opt(ctx).RequestsPerMinute, Window = TimeSpan.FromMinutes(1), SegmentsPerWindow = 6,
        }));
});

var app = builder.Build();
var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ChestTracker.Server");
log.LogInformation("ChestTracker.Server {Version} (protocol {Protocol}, min client {Min}) starting in {Env}",
    ServerInfo.Version, ProtocolInfo.Version, serverOptions.MinClientProtocol, app.Environment.EnvironmentName);

// The database may start after the server (containers, reboots): retry for up to a minute.
for (var attempt = 1; ; attempt++)
{
    try
    {
        await app.Services.GetRequiredService<SchemaMigrator>().MigrateAsync();
        break;
    }
    catch (Exception ex) when (ex is NpgsqlException or TimeoutException && attempt < 12)
    {
        log.LogWarning("Database not reachable ({Message}); retry {Attempt}/12 in 5 s", ex.Message, attempt);
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
}

var restoreIndex = Array.IndexOf(args, "--restore");
if (restoreIndex >= 0)
{
    var file = restoreIndex + 1 < args.Length ? args[restoreIndex + 1] : throw new ArgumentException("--restore needs a file");
    await app.Services.GetRequiredService<BackupService>().RestoreAsync(file, args.Contains("--replace"));
    Console.WriteLine($"Restored {file}");
    return 0;
}

if (args.Contains("--backup"))
{
    var info = await app.Services.GetRequiredService<BackupService>().CreateBackupAsync();
    Console.WriteLine($"Backup written: {info.FileName}");
    return 0;
}

_ = app.Services.GetRequiredService<SigningKeyProvider>(); // fail fast on missing key

app.UseForwardedHeaders();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseMiddleware<ProtocolVersionMiddleware>();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapApi();
app.MapHub<PartyHub>(Routes.PartyHub);

await app.RunAsync();
return 0;

public partial class Program;

internal sealed class SubjectUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User?.FindFirst("sub")?.Value;
}
