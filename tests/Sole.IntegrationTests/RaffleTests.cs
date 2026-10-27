using Sole.Raffles;
using Sole.Shared.Messaging;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Sole.IntegrationTests;

public class RaffleTests
{
    private static async Task<EnterOutcome> EnterAsync(Raffle raffle, Guid order, Guid owner, int pairs = 1, DateTimeOffset? now = null)
    {
        await using var db = TestDatabase.Raffles();
        return await new RafflesHandler(db).EnterAsync(Guid.CreateVersion7(), new ReservePairs(order, owner, raffle.Id, pairs, "ENT-1"), default, now);
    }

    private static async Task<bool> DrawAsync(Raffle raffle, DateTimeOffset? at = null)
    {
        await using var db = TestDatabase.Raffles();
        return await new RafflesHandler(db).DrawAsync(raffle.Id, at ?? raffle.DrawAt.AddSeconds(1));
    }

    private static async Task<bool> LeaveAsync(Guid order, string reason = "refund")
    {
        await using var db = TestDatabase.Raffles();
        return await new RafflesHandler(db).ReleaseAsync(Guid.CreateVersion7(), new ReleasePairs(order, reason));
    }

    [Fact]
    public async Task EnteringTakesNoStock_AndOnePersonGetsOneEntry()
    {
        await TestDatabase.OpenAsync();
        var raffle = await TestDatabase.AddRaffleAsync(pairsTotal: 5);
        var owner = Guid.NewGuid();

        Assert.Equal(EnterOutcome.Entered, await EnterAsync(raffle, Guid.CreateVersion7(), owner));
        Assert.Equal(EnterOutcome.AlreadyEntered, await EnterAsync(raffle, Guid.CreateVersion7(), owner));
        Assert.Equal(EnterOutcome.Entered, await EnterAsync(raffle, Guid.CreateVersion7(), Guid.NewGuid()));

        await using var check = TestDatabase.Raffles();
        Assert.Equal(2, await check.Entries.CountAsync());
        Assert.Equal(5, await check.Raffles.Where(r => r.Id == raffle.Id).Select(r => r.PairsLeft).SingleAsync());
        Assert.Equal(0, await check.Holds.CountAsync());
        Assert.Equal(2, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.EnteredKey));
        Assert.Equal(1, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.ShortKey)); // the second entry by the same person
    }

    [Fact]
    public async Task AfterTheDrawTime_EntriesAreRefused()
    {
        await TestDatabase.OpenAsync();
        var raffle = await TestDatabase.AddRaffleAsync(pairsTotal: 5, drawAt: DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.Equal(EnterOutcome.Closed, await EnterAsync(raffle, Guid.CreateVersion7(), Guid.NewGuid(), now: raffle.DrawAt.AddSeconds(1)));

        await using var check = TestDatabase.Raffles();
        Assert.Equal(0, await check.Entries.CountAsync());
        Assert.Contains("closed", (await check.Outbox.SingleAsync(o => o.RoutingKey == Topology.ShortKey)).Payload);
    }

    [Fact]
    public async Task TheDraw_HoldsExactlyWhatTheCommittedOrderSays_AndRejectsTheRest()
    {
        await TestDatabase.OpenAsync();
        var raffle = await TestDatabase.AddRaffleAsync(pairsTotal: 4, seed: "fixed-seed-for-the-test");
        var orders = Enumerable.Range(0, 12).Select(_ => Guid.CreateVersion7()).ToArray();
        foreach (var (order, i) in orders.Select((o, i) => (o, i)))
            await EnterAsync(raffle, order, Guid.NewGuid(), pairs: 1 + i % 2);

        Assert.True(await DrawAsync(raffle));

        // anyone can recompute this from the seed, the hash and the order ids
        var expected = RaffleDraw.Allocate("fixed-seed-for-the-test", orders.Select((o, i) => (o, 1 + i % 2)), 4);
        await using var check = TestDatabase.Raffles();
        var entries = await check.Entries.ToListAsync();
        foreach (var (orderId, _, won, rank) in expected)
        {
            var entry = entries.Single(e => e.OrderId == orderId);
            Assert.Equal(won ? EntryOutcome.Won : EntryOutcome.Lost, entry.Outcome);
            Assert.Equal(rank, entry.Rank);
        }
        var winners = expected.Where(e => e.Won).ToList();
        Assert.Equal(winners.Count, await check.Holds.CountAsync());
        Assert.Equal(winners.Sum(w => w.Pairs), 4 - await check.Raffles.Where(r => r.Id == raffle.Id).Select(r => r.PairsLeft).SingleAsync());
        Assert.Equal(winners.Count, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
        Assert.Equal(12 - winners.Count, (await check.Outbox.Where(o => o.RoutingKey == Topology.ShortKey).ToListAsync()).Count(o => o.Payload.Contains("drawn")));
        Assert.Equal(RaffleStatus.Drawn, (await check.Raffles.SingleAsync(r => r.Id == raffle.Id)).Status);
    }

    [Fact]
    public async Task ADrawBeforeItsTime_DoesNothing_AndARaffleIsDrawnOnce()
    {
        await TestDatabase.OpenAsync();
        var raffle = await TestDatabase.AddRaffleAsync(pairsTotal: 2, drawAt: DateTimeOffset.UtcNow.AddHours(1));
        await EnterAsync(raffle, Guid.CreateVersion7(), Guid.NewGuid());

        Assert.False(await DrawAsync(raffle, DateTimeOffset.UtcNow));
        Assert.True(await DrawAsync(raffle));
        Assert.False(await DrawAsync(raffle));

        await using var check = TestDatabase.Raffles();
        Assert.Equal(1, await check.Holds.CountAsync());
    }

    [Fact]
    public async Task SeveralInstancesDrawingAtOnce_DrawEachRaffleExactlyOnce()
    {
        await TestDatabase.OpenAsync();
        var raffle = await TestDatabase.AddRaffleAsync(pairsTotal: 5);
        for (var i = 0; i < 20; i++)
            await EnterAsync(raffle, Guid.CreateVersion7(), Guid.NewGuid());

        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 5).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            await using var db = TestDatabase.Raffles();
            return await new RafflesHandler(db).DrawDueAsync(raffle.DrawAt.AddSeconds(1));
        })).ToArray();
        gate.SetResult();
        Assert.Equal(1, (await Task.WhenAll(tasks)).Sum());

        await using var check = TestDatabase.Raffles();
        Assert.Equal(5, await check.Holds.CountAsync());
        Assert.Equal(0, await check.Raffles.Where(r => r.Id == raffle.Id).Select(r => r.PairsLeft).SingleAsync());
        Assert.Equal(5, await check.Outbox.CountAsync(o => o.RoutingKey == Topology.HeldKey));
    }

    [Fact]
    public async Task LeavingBeforeTheDraw_RemovesTheEntry_SoItCannotWin()
    {
        await TestDatabase.OpenAsync();
        var raffle = await TestDatabase.AddRaffleAsync(pairsTotal: 1);
        var leaver = Guid.CreateVersion7();
        var stays = Guid.CreateVersion7();
        await EnterAsync(raffle, leaver, Guid.NewGuid());
        await EnterAsync(raffle, stays, Guid.NewGuid());

        Assert.True(await LeaveAsync(leaver));
        Assert.False(await LeaveAsync(leaver, "again"));
        Assert.True(await DrawAsync(raffle));

        await using var check = TestDatabase.Raffles();
        Assert.Equal(stays, await check.Holds.Select(h => h.OrderId).SingleAsync());
        Assert.False(await check.Entries.AnyAsync(e => e.OrderId == leaver));
    }

    [Fact]
    public async Task AfterTheDraw_AWinnerGivesItsPairsBackOnce_AndALoserHasNothingToGive()
    {
        await TestDatabase.OpenAsync();
        var raffle = await TestDatabase.AddRaffleAsync(pairsTotal: 1);
        var orders = new[] { Guid.CreateVersion7(), Guid.CreateVersion7() };
        foreach (var o in orders)
            await EnterAsync(raffle, o, Guid.NewGuid());
        await DrawAsync(raffle);

        await using var read = TestDatabase.Raffles();
        var winner = await read.Holds.Select(h => h.OrderId).SingleAsync();
        var loser = orders.Single(o => o != winner);

        Assert.False(await LeaveAsync(loser));
        Assert.True(await LeaveAsync(winner));
        Assert.False(await LeaveAsync(winner, "again"));
        Assert.Equal(1, await read.Raffles.Where(r => r.Id == raffle.Id).Select(r => r.PairsLeft).SingleAsync());
    }

    [Fact]
    public async Task LeavingAndTheDrawRacing_NeverLoseOrDoubleCountAPair()
    {
        for (var round = 0; round < 8; round++)
        {
            await TestDatabase.OpenAsync();
            var raffle = await TestDatabase.AddRaffleAsync(pairsTotal: 3);
            var orders = Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7()).ToArray();
            foreach (var o in orders)
                await EnterAsync(raffle, o, Guid.NewGuid());

            var gate = new TaskCompletionSource();
            var draw = Task.Run(async () => { await gate.Task; await DrawAsync(raffle); });
            var leave = Task.Run(async () => { await gate.Task; foreach (var o in orders.Take(3)) await LeaveAsync(o); });
            gate.SetResult();
            await Task.WhenAll(draw, leave);

            await using var check = TestDatabase.Raffles();
            var left = await check.Raffles.Where(r => r.Id == raffle.Id).Select(r => r.PairsLeft).SingleAsync();
            var held = await check.Holds.Where(h => h.Status == HoldStatus.Held).SumAsync(h => h.Pairs);
            Assert.Equal(3, left + held); // every pair is on the shelf or held, once
            // all three leavers are gone for good: no hold of theirs may still be Held, whichever side of the draw they landed on
            var stillHeld = await check.Holds.Where(h => h.Status == HoldStatus.Held).Select(h => h.OrderId).ToListAsync();
            Assert.DoesNotContain(stillHeld, o => orders.Take(3).Contains(o));
        }
    }
}
