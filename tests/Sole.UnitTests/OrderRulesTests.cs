using Sole.Orders;
using Xunit;

namespace Sole.UnitTests;

public class OrderRulesTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData("", 1)]
    [InlineData("   ", 1)]
    [InlineData("ENT-1", 0)]
    [InlineData("ENT-1", -3)]
    public void EmptyInput_IsRejected(string? reference, long pairs) =>
        Assert.NotNull(OrderRules.Validate(reference, pairs));

    [Fact]
    public void OverLimit_IsRejected()
    {
        Assert.Null(OrderRules.Validate("ENT-1", OrderRules.MaxPairsPerOrder));
        Assert.NotNull(OrderRules.Validate("ENT-1", OrderRules.MaxPairsPerOrder + 1));
        Assert.NotNull(OrderRules.Validate(new string('x', OrderRules.MaxReferenceLength + 1), 1));
    }

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData((long)int.MaxValue + 1)]
    [InlineData(4_294_967_297L)] // (int) of this wraps to 1; it must not pass as one pair
    [InlineData(long.MaxValue)]
    public void IntegerOverflow_IsRejectedNotWrapped(long pairs)
    {
        Assert.NotNull(OrderRules.Validate("ENT-1", pairs));
        Assert.Equal(1, unchecked((int)4_294_967_297L)); // documents what the naive cast would have done
    }

    [Fact]
    public void HappyTotal_IsFaceValuePlusATwelvePercentServiceFee()
    {
        Assert.Null(OrderRules.Validate("ENT-1", 2));
        Assert.Equal(10_000L + 1_200L, OrderRules.TotalCents(5_000, 2)); // $50.00 x 2 + $12.00 fee
        Assert.Equal(1L, OrderRules.TotalCents(1, 1)); // the fee truncates to zero
        Assert.Equal(7L, OrderRules.TotalCents(7, 1)); // 12% of 7 is 0.84 cents, truncated to 0
        Assert.Equal(112L, OrderRules.TotalCents(100, 1)); // 100 + 12
    }

    [Fact]
    public void TotalArithmetic_ThrowsInsteadOfWrapping()
    {
        Assert.Throws<OverflowException>(() => OrderRules.TotalCents(long.MaxValue, 2));
        Assert.Throws<OverflowException>(() => OrderRules.TotalCents(long.MaxValue / 2, 2)); // the fee multiplication overflows
    }
}
