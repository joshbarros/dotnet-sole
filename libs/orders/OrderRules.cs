namespace Sole.Orders;

public static class OrderRules
{
    public const int MaxPairsPerOrder = 6;
    public const int MaxReferenceLength = 40;
    public const long ServiceFeeBasisPoints = 1_200;

    /// <summary>Returns an error message, or null when the request is acceptable.</summary>
    public static string? Validate(string? reference, long pairs)
    {
        if (string.IsNullOrWhiteSpace(reference))
            return "Entrant reference is required.";
        if (reference.Trim().Length > MaxReferenceLength)
            return $"Entrant reference must be at most {MaxReferenceLength} characters.";
        if (pairs < 1)
            return "An order needs at least 1 pair.";
        // Compared as long on purpose: narrowing 4294967297 to int would wrap to 1 and pass.
        if (pairs > MaxPairsPerOrder)
            return $"An order takes at most {MaxPairsPerOrder} pairs.";
        return null;
    }

    /// <summary>Face value plus a 12% service fee truncated to a whole cent, in cents. Overflow throws instead of wrapping.</summary>
    public static long TotalCents(long priceCents, int pairs)
    {
        var face = checked(priceCents * pairs);
        return checked(face + face * ServiceFeeBasisPoints / 10_000);
    }
}
