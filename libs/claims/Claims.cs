using Sole.Shared;
using Sole.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Sole.Claims;

public enum ClaimStatus { Issued, Rejected }

public class Claim
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public string? Number { get; set; }
    public ClaimStatus Status { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ClaimsDbContext(DbContextOptions<ClaimsDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "claims";

    public DbSet<Claim> Claims => Set<Claim>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Claim>(e =>
        {
            e.ToTable("Claims");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
        });
    }
}

public enum IssueOutcome { Issued, Rejected, Duplicate }

/// <summary>Issues the claim once the pairs are held. A poison reference is committed as rejected.</summary>
public class ClaimsHandler(ClaimsDbContext db) : IMessageHandler
{
    public const string Name = "claims";

    public async Task HandleAsync(Delivery delivery, CancellationToken ct)
    {
        if (delivery.RoutingKey != Topology.HeldKey)
            throw new InvalidOperationException($"Claims does not handle '{delivery.RoutingKey}'.");

        var outcome = await IssueAsync(delivery.MessageId, Json.Parse<PairsHeld>(delivery.Body), ct);
        if (outcome == IssueOutcome.Rejected)
            throw new PoisonMessageException("Reference carries the poison token; rejection is committed.");
    }

    public async Task<IssueOutcome> IssueAsync(Guid messageId, PairsHeld held, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (!await db.ClaimAsync(Name, messageId, ct))
        {
            await tx.CommitAsync(ct);
            return IssueOutcome.Duplicate;
        }

        IssueOutcome outcome;
        if (PoisonRule.IsPoison(held.Reference))
        {
            const string reason = "Reference contains the poison token.";
            db.Claims.Add(new Claim
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Status = ClaimStatus.Rejected,
                Reason = reason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("claims", Topology.ReleaseKey, held.OrderId, new ReleasePairs(held.OrderId, "poison"));
            db.Enqueue("claims", Topology.RejectedKey, held.OrderId, new ClaimRejected(held.OrderId, reason));
            outcome = IssueOutcome.Rejected;
        }
        else
        {
            var number = $"SL-{held.OrderId.ToString("N")[^12..].ToUpperInvariant()}";
            db.Claims.Add(new Claim
            {
                OrderId = held.OrderId,
                OwnerId = held.OwnerId,
                Number = number,
                Status = ClaimStatus.Issued,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            db.Enqueue("claims", Topology.IssuedKey, held.OrderId, new ClaimIssued(held.OrderId, number));
            outcome = IssueOutcome.Issued;
        }

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return outcome;
    }
}
