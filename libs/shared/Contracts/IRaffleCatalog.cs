namespace Sole.Shared.Contracts;

public record RaffleInfo(Guid Id, string Code, long PriceCents, DateTimeOffset DrawAt, bool Open);

/// <summary>
/// What Orders may ask Raffles, without referencing it. Raffles implements it over its own tables;
/// Orders never reads another module's schema.
/// </summary>
public interface IRaffleCatalog
{
    Task<RaffleInfo?> FindAsync(string code, CancellationToken ct);
}
