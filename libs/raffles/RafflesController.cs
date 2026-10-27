using Sole.Shared;
using Sole.Shared.Auth;
using Sole.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Sole.Raffles;

public record CreateRaffleRequest(string? Code, string? Name, string? Colorway, string? Brand, DateTimeOffset? DrawAt, long PairsTotal, long PriceCents);

/// <summary>SeedHash is public from the start; Seed only after the draw, so anyone can check the one against the other.</summary>
public record RaffleDto(Guid Id, string Code, string Name, string Colorway, string Brand, DateTimeOffset DrawAt, long PriceCents,
    int PairsTotal, int PairsLeft, string Status, string SeedHash, string? Seed, DateTimeOffset? DrawnAt, int Entries)
{
    public static RaffleDto From(Raffle s, int entries) => new(s.Id, s.Code, s.Name, s.Colorway, s.Brand, s.DrawAt, s.PriceCents,
        s.PairsTotal, s.PairsLeft, s.Status.ToString(), s.SeedHash, s.Status == RaffleStatus.Drawn ? s.Seed : null, s.DrawnAt, entries);
}

public record DrawEntryDto(Guid OrderId, int Pairs, string Outcome, int? Rank, string? Key);

public record DrawDto(string Code, string Status, string SeedHash, string? Seed, bool SeedMatchesHash, int Entries, int Winners, IReadOnlyList<DrawEntryDto> Mine);

/// <summary>What other modules may ask Raffles, over Raffles's own tables.</summary>
public sealed class RaffleCatalog(RafflesDbContext db) : IRaffleCatalog
{
    public async Task<RaffleInfo?> FindAsync(string code, CancellationToken ct) =>
        await db.Raffles.AsNoTracking().Where(s => s.Code == code)
            .Select(s => new RaffleInfo(s.Id, s.Code, s.PriceCents, s.DrawAt, s.Status == RaffleStatus.Open)).FirstOrDefaultAsync(ct);
}

[ApiController]
[Route("api/raffles")]
[Authorize]
public class RafflesController(RafflesDbContext db, ICurrentUser user) : ControllerBase
{
    private const int MaxPairsTotal = 5000;
    private const long MaxPriceCents = 1_000_000;

    [HttpGet]
    public async Task<IEnumerable<RaffleDto>> List(CancellationToken ct)
    {
        var raffles = await db.Raffles.AsNoTracking().OrderBy(s => s.DrawAt).ThenBy(s => s.Code).ToListAsync(ct);
        var counts = await db.Entries.AsNoTracking().GroupBy(e => e.RaffleId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        return raffles.Select(r => RaffleDto.From(r, counts.GetValueOrDefault(r.Id)));
    }

    [HttpGet("{code}")]
    public async Task<ActionResult<RaffleDto>> Get(string code, CancellationToken ct)
    {
        var raffle = await db.Raffles.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        return raffle is null ? NotFound() : RaffleDto.From(raffle, await db.Entries.CountAsync(e => e.RaffleId == raffle.Id, ct));
    }

    /// <summary>
    /// What a person needs to check the draw: the seed (once drawn) and its hash, and their own entries with the key
    /// they were ordered by. Staff see every entry. Recompute: key = SHA-256("seed:orderId"), sort ascending, hand out pairs first fit.
    /// </summary>
    [HttpGet("{code}/draw")]
    public async Task<ActionResult<DrawDto>> Draw(string code, CancellationToken ct)
    {
        var raffle = await db.Raffles.AsNoTracking().FirstOrDefaultAsync(s => s.Code == code, ct);
        if (raffle is null)
            return NotFound();

        var me = user.Id ?? throw new DomainException(401, "Sign in to see the draw.");
        var drawn = raffle.Status == RaffleStatus.Drawn;
        var entries = db.Entries.AsNoTracking().Where(e => e.RaffleId == raffle.Id);
        var total = await entries.CountAsync(ct);
        var winners = await entries.CountAsync(e => e.Outcome == EntryOutcome.Won, ct);
        var visible = await (user.IsStaff ? entries : entries.Where(e => e.OwnerId == me)).OrderBy(e => e.Rank).ThenBy(e => e.EnteredAt).ToListAsync(ct);
        return new DrawDto(raffle.Code, raffle.Status.ToString(), raffle.SeedHash, drawn ? raffle.Seed : null,
            drawn && RaffleDraw.Matches(raffle.Seed, raffle.SeedHash), total, winners,
            visible.Select(e => new DrawEntryDto(e.OrderId, e.Pairs, e.Outcome.ToString(), e.Rank, drawn ? RaffleDraw.Key(raffle.Seed, e.OrderId) : null)).ToList());
    }

    /// <summary>Opens a raffle. Staff only. The seed is generated here, and only its hash is shown until the draw.</summary>
    [HttpPost]
    [Authorize(Policy = Policies.ManageCatalog)]
    public async Task<ActionResult<RaffleDto>> Create(CreateRaffleRequest request, CancellationToken ct)
    {
        var code = request.Code?.Trim();
        if (string.IsNullOrEmpty(code) || code.Length > 20)
            throw new DomainException(400, "Code is required and at most 20 characters.");
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Colorway) || string.IsNullOrWhiteSpace(request.Brand))
            throw new DomainException(400, "Name, colorway and brand are required.");
        if (request.PairsTotal < 1 || request.PairsTotal > MaxPairsTotal)
            throw new DomainException(400, $"Pairs on sale must be between 1 and {MaxPairsTotal} pairs.");
        if (request.PriceCents < 1 || request.PriceCents > MaxPriceCents)
            throw new DomainException(400, $"Price must be between 1 and {MaxPriceCents} cents per pair.");
        var drawAt = request.DrawAt ?? DateTimeOffset.UtcNow.AddDays(7);
        if (drawAt <= DateTimeOffset.UtcNow)
            throw new DomainException(400, "The draw time must be in the future.");
        if (await db.Raffles.AnyAsync(s => s.Code == code, ct))
            throw new DomainException(409, $"Raffle '{code}' already exists.");

        var seed = RaffleDraw.NewSeed();
        var raffle = new Raffle
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = request.Name.Trim(),
            Colorway = request.Colorway.Trim(),
            Brand = request.Brand.Trim(),
            DrawAt = drawAt,
            PriceCents = request.PriceCents,
            PairsTotal = (int)request.PairsTotal,
            PairsLeft = (int)request.PairsTotal,
            Status = RaffleStatus.Open,
            Seed = seed,
            SeedHash = RaffleDraw.Commitment(seed),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Raffles.Add(raffle);
        await db.SaveChangesAsync(ct);
        return Created($"/api/raffles/{raffle.Code}", RaffleDto.From(raffle, 0));
    }
}
