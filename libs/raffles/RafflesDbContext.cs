using Sole.Shared.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Sole.Raffles;

public class RafflesDbContext(DbContextOptions<RafflesDbContext> options) : ModuleDbContext(options, SchemaName)
{
    public const string SchemaName = "raffles";

    public DbSet<Raffle> Raffles => Set<Raffle>();
    public DbSet<PairHold> Holds => Set<PairHold>();
    public DbSet<RaffleEntry> Entries => Set<RaffleEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<Raffle>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Code).IsUnique();
            e.HasIndex(x => new { x.Status, x.DrawAt });
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Seed).HasMaxLength(64);
            e.Property(x => x.SeedHash).HasMaxLength(64);
            e.ToTable(t => t.HasCheckConstraint("CK_Raffles_Remaining", "\"PairsLeft\" >= 0 AND \"PairsLeft\" <= \"PairsTotal\""));
        });

        b.Entity<RaffleEntry>(e =>
        {
            e.ToTable("RaffleEntries");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.Reference).HasMaxLength(64);
            e.HasIndex(x => new { x.RaffleId, x.OwnerId }).IsUnique(); // one entry per person per raffle
            e.HasOne<Raffle>().WithMany().HasForeignKey(x => x.RaffleId);
        });

        b.Entity<PairHold>(e =>
        {
            e.ToTable("PairHolds");
            e.HasKey(x => x.OrderId);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Raffle>().WithMany().HasForeignKey(x => x.RaffleId);
        });
    }
}
