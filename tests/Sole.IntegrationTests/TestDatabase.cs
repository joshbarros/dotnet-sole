using Sole.Orders;
using Sole.Raffles;
using Sole.Claims;
using Sole.Shared.Auth;
using Sole.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Sole.IntegrationTests;

public static class TestDatabase
{
    public static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("SOLE_TEST_DB")
        ?? "Host=localhost;Port=5487;Database=sole_test;Username=sole;Password=sole";

    private static DbContextOptions<T> Options<T>(string schema) where T : DbContext =>
        new DbContextOptionsBuilder<T>()
            .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__EFMigrationsHistory", schema))
            .Options;

    public static OrdersDbContext Orders() => new(Options<OrdersDbContext>(OrdersDbContext.SchemaName));
    public static RafflesDbContext Raffles() => new(Options<RafflesDbContext>(RafflesDbContext.SchemaName));
    public static ClaimsDbContext Claims() => new(Options<ClaimsDbContext>(ClaimsDbContext.SchemaName));

    /// <summary>Migrates the business modules and empties their tables. The identity schema is left alone: the API tests share it.</summary>
    public static async Task OpenAsync()
    {
        await using var orders = Orders();
        await using var pairs = Raffles();
        await using var claims = Claims();
        await orders.Database.MigrateAsync();
        await pairs.Database.MigrateAsync();
        await claims.Database.MigrateAsync();
        await orders.Database.ExecuteSqlRawAsync(
            "TRUNCATE orders.\"InboxMessages\", orders.\"OutboxMessages\", orders.\"Orders\", " +
            "raffles.\"InboxMessages\", raffles.\"OutboxMessages\", raffles.\"PairHolds\", raffles.\"RaffleEntries\", raffles.\"Raffles\", " +
            "claims.\"InboxMessages\", claims.\"OutboxMessages\", claims.\"Claims\" RESTART IDENTITY CASCADE;");
    }

    public static async Task<Raffle> AddRaffleAsync(int pairsTotal, long priceCents = 5_000, DateTimeOffset? drawAt = null, string? seed = null)
    {
        await using var db = Raffles();
        var raffle = new Raffle
        {
            Id = Guid.CreateVersion7(),
            Code = $"T-{Guid.NewGuid():N}"[..14],
            Name = "Test Raffle",
            Colorway = "Volt",
            Brand = "Nimbus",
            DrawAt = drawAt ?? DateTimeOffset.UtcNow.AddDays(7),
            PriceCents = priceCents,
            PairsTotal = pairsTotal,
            PairsLeft = pairsTotal,
            Status = RaffleStatus.Open,
            Seed = seed ?? RaffleDraw.NewSeed(),
            SeedHash = "pending",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        raffle.SeedHash = RaffleDraw.Commitment(raffle.Seed);
        db.Raffles.Add(raffle);
        await db.SaveChangesAsync();
        return raffle;
    }

    public static async Task<Order> AddOrderAsync(Raffle raffle, string reference, int pairs, OrderStatus status, Guid? ownerId = null)
    {
        await using var db = Orders();
        var order = new Order
        {
            Id = Guid.CreateVersion7(),
            OwnerId = ownerId ?? Guid.CreateVersion7(),
            RaffleId = raffle.Id,
            RaffleCode = raffle.Code,
            Reference = reference,
            Pairs = pairs,
            TotalCents = OrderRules.TotalCents(raffle.PriceCents, pairs),
            Status = status,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }
}

public sealed class TestUser(Guid? id, bool staff = false) : ICurrentUser
{
    public bool IsAuthenticated => id is not null;
    public Guid? Id => id;
    public bool IsStaff => staff;
}

public sealed class FixedCatalog(params Raffle[] raffles) : IRaffleCatalog
{
    public Task<RaffleInfo?> FindAsync(string code, CancellationToken ct) =>
        Task.FromResult(raffles.Where(s => s.Code == code).Select(s => new RaffleInfo(s.Id, s.Code, s.PriceCents, s.DrawAt, s.Status == RaffleStatus.Open)).FirstOrDefault());
}
