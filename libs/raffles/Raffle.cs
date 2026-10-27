using System.Security.Cryptography;
using System.Text;

namespace Sole.Raffles;

public enum RaffleStatus { Open, Drawn }

public class Raffle
{
    public Guid Id { get; set; }
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Colorway { get; set; } = "";
    public string Brand { get; set; } = "";
    public DateTimeOffset DrawAt { get; set; }
    public long PriceCents { get; set; }
    public int PairsTotal { get; set; }
    public int PairsLeft { get; set; }
    public RaffleStatus Status { get; set; }
    public DateTimeOffset? DrawnAt { get; set; }
    /// <summary>Chosen when the raffle opens and kept secret until the draw. Anyone can check it against <see cref="SeedHash"/>.</summary>
    public string Seed { get; set; } = "";
    /// <summary>SHA-256 of the seed, published from the start: the commitment that makes the draw checkable.</summary>
    public string SeedHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public enum EntryOutcome { Entered, Won, Lost }

/// <summary>One entry per person per raffle. The outcome is written only by the draw.</summary>
public class RaffleEntry
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid RaffleId { get; set; }
    public int Pairs { get; set; }
    public string Reference { get; set; } = "";
    public EntryOutcome Outcome { get; set; }
    /// <summary>Place in the draw order (1 = first picked). Set by the draw, for winners and losers alike.</summary>
    public int? Rank { get; set; }
    public DateTimeOffset EnteredAt { get; set; }
}

public enum HoldStatus { Held, Released }

public class PairHold
{
    public Guid OrderId { get; set; }
    public Guid OwnerId { get; set; }
    public Guid RaffleId { get; set; }
    public int Pairs { get; set; }
    public HoldStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReleasedAt { get; set; }
    public string? ReleaseReason { get; set; }
}

/// <summary>
/// The draw, as pure functions so anyone can recompute it. Entries are ordered by SHA-256(seed ":" orderId); the
/// pairs are then handed out in that order, each entry taking its pairs when they still fit (first fit). The seed is
/// committed (as its hash) before any entry exists, so the operator cannot choose the order after seeing the entries.
/// </summary>
public static class RaffleDraw
{
    public static string NewSeed() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Commitment(string seed) => Hash(seed);

    public static string Key(string seed, Guid orderId) => Hash($"{seed}:{orderId}");

    public static bool Matches(string seed, string seedHash) =>
        string.Equals(Commitment(seed), seedHash, StringComparison.OrdinalIgnoreCase);

    /// <summary>The entries in draw order, with the outcome of each (true = won). Ties on the key cannot happen for distinct ids in practice; the id breaks them anyway.</summary>
    public static IReadOnlyList<(Guid OrderId, int Pairs, bool Won, int Rank)> Allocate(string seed, IEnumerable<(Guid OrderId, int Pairs)> entries, int pairsLeft)
    {
        var result = new List<(Guid, int, bool, int)>();
        var remaining = pairsLeft;
        var rank = 0;
        foreach (var entry in entries.OrderBy(e => Key(seed, e.OrderId), StringComparer.Ordinal).ThenBy(e => e.OrderId))
        {
            rank++;
            var won = entry.Pairs <= remaining;
            if (won)
                remaining -= entry.Pairs;
            result.Add((entry.OrderId, entry.Pairs, won, rank));
        }
        return result;
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
