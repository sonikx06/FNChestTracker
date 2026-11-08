// Development tool: a simulated teammate. It is a full client (own SQLite log, tracking engine and
// SyncClient) that joins a party by code, follows shared matches and opens chests on a timer.
// Usage: dotnet run --project tools/TeammateSim -- <serverUrl> <inviteCode> [name] [chests] [secondsBetween]
using ChestTracker.Core.Abstractions;
using ChestTracker.Core.Data;
using ChestTracker.Core.Tracking;
using ChestTracker.Sync;

if (args.Length < 2)
{
    Console.WriteLine("Usage: TeammateSim <serverUrl> <inviteCode> [name] [chests] [secondsBetween]");
    return 1;
}

var server = args[0];
var code = args[1];
var name = args.Length > 2 ? args[2] : "Bot";
var chests = args.Length > 3 ? int.Parse(args[3]) : 5;
var interval = args.Length > 4 ? double.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture) : 3;

var dir = Path.Combine(Path.GetTempPath(), "chesttracker-teammate-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
var (db, _) = DatabaseBootstrapper.Open(Path.Combine(dir, "chests.db"), new BackupService(Path.Combine(dir, "backups")));
var engine = new TrackingEngine(new ChestRepository(db), SystemClock.Instance);
engine.Initialize();
await using var client = new SyncClient(engine, new SyncOutbox(db), new InMemoryAccountStore());
client.SharedMatchStarted += (_, e) => Console.WriteLine($"[{name}] shared match {e.Match.Id} started, joined={e.Joined}");
client.SharedMatchEnded += (_, _) => Console.WriteLine($"[{name}] shared match ended");
client.Activity += (_, a) => Console.WriteLine($"[{name}] activity: {a.DisplayName} {a.Delta:+#;-#}");
client.Start();

await client.RegisterAsync(server, name);
var deadline = DateTime.UtcNow.AddSeconds(20);
while (client.Status != SyncStatus.Online && DateTime.UtcNow < deadline) await Task.Delay(100);
var party = await client.JoinPartyAsync(code);
Console.WriteLine($"[{name}] joined '{party.Name}' ({party.Members.Count} members)");

for (var i = 0; i < chests; i++)
{
    await Task.Delay(TimeSpan.FromSeconds(interval));
    engine.AddChestManual(fromHotkey: true);
    Console.WriteLine($"[{name}] chest {i + 1}/{chests} (local match count {engine.GetSnapshot().MatchCount}, pending {client.PendingCount})");
}

await Task.Delay(TimeSpan.FromSeconds(2));
await client.FlushAsync();
var state = client.Party!;
Console.WriteLine($"[{name}] team lifetime verified={state.TeamLifetime.Verified} reported={state.TeamLifetime.Reported}");
foreach (var m in state.Members)
{
    Console.WriteLine($"   {m.DisplayName,-12} online={m.Online} match={m.Match?.Verified} lifetime={m.Lifetime?.Verified}/{m.Lifetime?.Reported}");
}

Console.WriteLine($"[{name}] staying online; press Ctrl+C to quit");
await Task.Delay(Timeout.Infinite);
return 0;
