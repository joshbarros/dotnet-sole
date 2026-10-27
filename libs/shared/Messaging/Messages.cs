using System.Text.Json;

namespace Sole.Shared.Messaging;

public record ReservePairs(Guid OrderId, Guid OwnerId, Guid RaffleId, int Pairs, string Reference);
public record PairsHeld(Guid OrderId, Guid OwnerId, Guid RaffleId, int Pairs, string Reference);
public record PairsEntered(Guid OrderId, DateTimeOffset DrawAt);
/// <summary>Reason: "closed" (the draw has happened), "already" (one entry per person) or "drawn" (not picked).</summary>
public record PairsShort(Guid OrderId, string Reference, int RequestedPairs, int PairsLeft, string Reason = "drawn", int Rank = 0, int Entries = 0);
public record ReleasePairs(Guid OrderId, string Reason);
public record PairsReleased(Guid OrderId, int Pairs, string Reason);
public record ClaimIssued(Guid OrderId, string Number);
public record ClaimRejected(Guid OrderId, string Reason);

public record Delivery(Guid MessageId, string RoutingKey, string Body);

public interface IMessageHandler
{
    Task HandleAsync(Delivery delivery, CancellationToken ct);
}

/// <summary>Committed as rejected; the message must be dead-lettered, not retried.</summary>
public class PoisonMessageException(string message) : Exception(message);

/// <summary>The message arrived before the state it depends on; retry later.</summary>
public class OutOfOrderException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Parse<T>(string body) =>
        JsonSerializer.Deserialize<T>(body, Options) ?? throw new InvalidOperationException("Empty message body.");
}
