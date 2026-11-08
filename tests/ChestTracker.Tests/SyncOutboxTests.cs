using ChestTracker.Core.Data;
using ChestTracker.Core.Models;

namespace ChestTracker.Tests;

public class SyncOutboxTests : IDisposable
{
    private readonly TempDatabase _db = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Only_events_after_joining_a_party_are_queued_once()
    {
        var engine = _db.CreateEngine(_clock);
        var outbox = new SyncOutbox(_db.Db);
        engine.AddChestManual(false);              // before joining: private
        var party = Guid.NewGuid();
        outbox.BeginParty(party);

        engine.AddChestManual(false);
        engine.RemoveLastChest(false);
        engine.AdjustLifetime(50);                 // not a match event: never shared

        Assert.Equal(2, outbox.EnqueueNew());
        Assert.Equal(0, outbox.EnqueueNew());
        var items = outbox.Peek(10);
        Assert.Equal(new[] { ChestEventKind.Add, ChestEventKind.Undo }, items.Select(i => i.Kind));
        Assert.All(items, i => Assert.Equal(party, i.PartyId));
    }

    [Fact]
    public void Queue_survives_restart_and_removal_is_per_event()
    {
        var engine = _db.CreateEngine(_clock);
        var outbox = new SyncOutbox(_db.Db);
        outbox.BeginParty(Guid.NewGuid());
        engine.AddChestManual(false);
        engine.AddChestManual(false);
        outbox.EnqueueNew();
        var first = outbox.Peek(1).Single();
        outbox.Remove([first.EventId]);

        _db.Reopen();
        var reopened = new SyncOutbox(_db.Db);

        Assert.Equal(1, reopened.Count);
        Assert.Equal(0, reopened.EnqueueNew());
    }

    [Fact]
    public void Linking_a_match_tags_queued_and_future_events()
    {
        var engine = _db.CreateEngine(_clock);
        var outbox = new SyncOutbox(_db.Db);
        outbox.BeginParty(Guid.NewGuid());
        var match = engine.StartMatch();
        engine.AddChestManual(false);
        outbox.EnqueueNew();
        var shared = Guid.NewGuid();

        outbox.LinkMatch(match.Id, shared);
        engine.AddChestManual(false);
        outbox.EnqueueNew();

        Assert.All(outbox.Peek(10), i => Assert.Equal(shared, i.SharedMatchId));
        Assert.Equal(shared, outbox.GetSharedMatch(match.Id));
    }

    [Fact]
    public void Failed_delivery_is_retried_and_ending_party_drops_queue()
    {
        var engine = _db.CreateEngine(_clock);
        var outbox = new SyncOutbox(_db.Db);
        outbox.BeginParty(Guid.NewGuid());
        engine.AddChestManual(false);
        outbox.EnqueueNew();
        var item = outbox.Peek(1).Single();

        outbox.MarkFailed([item.EventId], "offline");
        Assert.Equal(1, outbox.Peek(1).Single().Attempts);

        outbox.EndParty();
        Assert.Equal(0, outbox.Count);
        Assert.Null(outbox.PartyId);
        engine.AddChestManual(false);
        Assert.Equal(0, outbox.EnqueueNew());
    }
}
