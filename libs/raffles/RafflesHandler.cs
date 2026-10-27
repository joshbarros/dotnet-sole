using Sole.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Sole.Raffles;

public enum EnterOutcome { Entered, Closed, AlreadyEntered, Duplicate }

/// <summary>
/// The only writer of Raffle.PairsLeft, of entries' outcomes and of pair holds.
/// Entering only records a place in the raffle. The draw, one transaction per raffle, hands the pairs out in the
/// committed order and holds them for the winners.
/// </summary>
public class RafflesHandler(RafflesDbContext db) : IMessageHandler
{
    public const string Name = "raffles";

    public Task HandleAsync(Delivery delivery, CancellationToken ct) => delivery.RoutingKey switch
    {
        Topology.ReserveKey => EnterAsync(delivery.MessageId, Json.Parse<ReservePairs>(delivery.Body), ct),
        Topology.ReleaseKey => ReleaseAsync(delivery.MessageId, Json.Parse<ReleasePairs>(delivery.Body), ct),
        _ => throw new InvalidOperationException($"Raffles does not handle '{delivery.RoutingKey}'."),
    };

    /// <summary>Records one entry per person per raffle, while the raffle is open and before its draw time.</summary>
    public async Task<EnterOutcome> EnterAsync(Guid messageId, ReservePairs command, CancellationToken ct = default, DateTimeOffset? now = null)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return EnterOutcome.Duplicate;
        }

        var at = now ?? DateTimeOffset.UtcNow;
        var raffle = await db.Raffles.AsNoTracking().Where(r => r.Id == command.RaffleId)
            .Select(r => new { r.Status, r.DrawAt }).FirstOrDefaultAsync(ct);
        if (raffle is null || raffle.Status != RaffleStatus.Open || raffle.DrawAt <= at)
        {
            db.Enqueue("raffles", Topology.ShortKey, command.OrderId,
                new PairsShort(command.OrderId, command.Reference, command.Pairs, 0, "closed"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return EnterOutcome.Closed;
        }

        // The unique index (RaffleId, OwnerId) is the rule: a second entry by the same person inserts nothing.
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO raffles."RaffleEntries" ("OrderId", "OwnerId", "RaffleId", "Pairs", "Reference", "Outcome", "EnteredAt")
            VALUES ({command.OrderId}, {command.OwnerId}, {command.RaffleId}, {command.Pairs}, {command.Reference}, 'Entered', {at})
            ON CONFLICT DO NOTHING
            """, ct);
        if (inserted == 0)
        {
            db.Enqueue("raffles", Topology.ShortKey, command.OrderId,
                new PairsShort(command.OrderId, command.Reference, command.Pairs, 0, "already"));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return EnterOutcome.AlreadyEntered;
        }

        db.Enqueue("raffles", Topology.EnteredKey, command.OrderId, new PairsEntered(command.OrderId, raffle.DrawAt));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return EnterOutcome.Entered;
    }

    /// <summary>
    /// Draws every raffle whose time has come. Each raffle is claimed by a conditional UPDATE (Open to Drawn), so
    /// several instances can run this at once and exactly one draws a given raffle. Returns the raffles drawn.
    /// </summary>
    public async Task<int> DrawDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var due = await db.Raffles.AsNoTracking().Where(r => r.Status == RaffleStatus.Open && r.DrawAt <= now)
            .OrderBy(r => r.DrawAt).Select(r => r.Id).ToListAsync(ct);
        var drawn = 0;
        foreach (var id in due)
            if (await DrawAsync(id, now, ct))
                drawn++;
        return drawn;
    }

    public async Task<bool> DrawAsync(Guid raffleId, DateTimeOffset now, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Raffles
            .Where(r => r.Id == raffleId && r.Status == RaffleStatus.Open && r.DrawAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, RaffleStatus.Drawn).SetProperty(r => r.DrawnAt, now), ct);
        if (claimed == 0)
        {
            await tx.CommitAsync(ct);
            return false; // another instance drew it, or it is not time yet
        }

        var raffle = await db.Raffles.AsNoTracking().FirstAsync(r => r.Id == raffleId, ct);
        // Lock every entry that is still in the draw. A withdrawal (a DELETE of an Entered entry) that commits before
        // this point simply is not selected; one that arrives after it waits, then finds the outcome changed and falls
        // through to releasing whatever the entry won.
        var entries = await db.Entries.FromSql($"""
            SELECT * FROM raffles."RaffleEntries" WHERE "RaffleId" = {raffleId} AND "Outcome" = 'Entered' ORDER BY "OrderId" FOR UPDATE
            """).ToListAsync(ct);
        var allocation = RaffleDraw.Allocate(raffle.Seed, entries.Select(e => (e.OrderId, e.Pairs)), raffle.PairsLeft);
        var byOrder = entries.ToDictionary(e => e.OrderId);
        var taken = 0;
        foreach (var (orderId, pairs, won, rank) in allocation)
        {
            var entry = byOrder[orderId];
            entry.Rank = rank;
            entry.Outcome = won ? EntryOutcome.Won : EntryOutcome.Lost;
            if (won)
            {
                taken += pairs;
                db.Holds.Add(new PairHold
                {
                    OrderId = orderId,
                    OwnerId = entry.OwnerId,
                    RaffleId = raffleId,
                    Pairs = pairs,
                    Status = HoldStatus.Held,
                    CreatedAt = now,
                });
                db.Enqueue("raffles", Topology.HeldKey, orderId, new PairsHeld(orderId, entry.OwnerId, raffleId, pairs, entry.Reference));
            }
            else
            {
                db.Enqueue("raffles", Topology.ShortKey, orderId,
                    new PairsShort(orderId, entry.Reference, pairs, raffle.PairsLeft - taken, "drawn", rank, entries.Count));
            }
        }

        if (taken > 0)
            await db.Raffles.Where(r => r.Id == raffleId && r.PairsLeft >= taken)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.PairsLeft, r => r.PairsLeft - taken), ct);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Leaving the raffle. Before the draw it just deletes the entry; after it, it gives back the pairs the entry won
    /// (exactly once), and a lost entry has nothing to give back.
    /// </summary>
    public async Task<bool> ReleaseAsync(Guid messageId, ReleasePairs command, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return false;
        }

        var left = await db.Entries.Where(e => e.OrderId == command.OrderId && e.Outcome == EntryOutcome.Entered).ExecuteDeleteAsync(ct);
        if (left == 1)
        {
            db.Enqueue("raffles", Topology.ReleasedKey, command.OrderId, new PairsReleased(command.OrderId, 0, command.Reason));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return true;
        }

        var hold = await db.Holds.AsNoTracking().FirstOrDefaultAsync(h => h.OrderId == command.OrderId, ct);
        var released = false;
        if (hold is not null)
        {
            var now = DateTimeOffset.UtcNow;
            var flipped = await db.Holds
                .Where(h => h.OrderId == command.OrderId && h.Status == HoldStatus.Held)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(h => h.Status, HoldStatus.Released)
                    .SetProperty(h => h.ReleasedAt, now)
                    .SetProperty(h => h.ReleaseReason, command.Reason), ct);
            if (flipped == 1)
            {
                await db.Raffles
                    .Where(s => s.Id == hold.RaffleId && s.PairsLeft + hold.Pairs <= s.PairsTotal)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.PairsLeft, x => x.PairsLeft + hold.Pairs), ct);
                db.Enqueue("raffles", Topology.ReleasedKey, command.OrderId, new PairsReleased(command.OrderId, hold.Pairs, command.Reason));
                released = true;
            }
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return released;
    }
}
