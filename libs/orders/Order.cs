namespace Sole.Orders;

public enum OrderStatus { Requested, Entered, Held, Sold, Refunded, Rejected }

public class Order
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid RaffleId { get; set; }
    public string RaffleCode { get; set; } = "";
    public string Reference { get; set; } = "";
    public int Pairs { get; set; }
    public long TotalCents { get; set; }
    public OrderStatus Status { get; set; }
    public string? Reason { get; set; }
    public string? ClaimNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PairsReleasedAt { get; set; }
}

public class OrderOptions
{
    /// <summary>Latency knob for the final mark. Compose sets it so the refund race window is wide enough to hit on purpose.</summary>
    public int FinalMarkDelayMs { get; set; }
}

public record CreateOrderRequest(string? RaffleCode, string? Reference, long Pairs);

public record OrderDto(
    Guid Id, Guid OwnerId, string RaffleCode, string Reference, int Pairs, long TotalCents, string Status,
    string? Reason, string? ClaimNumber, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? PairsReleasedAt)
{
    public static OrderDto From(Order b) => new(
        b.Id, b.OwnerId, b.RaffleCode, b.Reference, b.Pairs, b.TotalCents, b.Status.ToString(),
        b.Reason, b.ClaimNumber, b.CreatedAt, b.UpdatedAt, b.PairsReleasedAt);
}
