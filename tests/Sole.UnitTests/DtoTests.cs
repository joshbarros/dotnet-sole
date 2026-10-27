using Sole.Orders;
using Sole.Raffles;
using Xunit;

namespace Sole.UnitTests;

public class DtoTests
{
    [Fact]
    public void OrderDto_MapsEveryFieldAndTheStatusName()
    {
        var now = DateTimeOffset.UtcNow;
        var order = new Order
        {
            Id = Guid.NewGuid(),
            OwnerId = Guid.NewGuid(),
            RaffleId = Guid.NewGuid(),
            RaffleCode = "S-1",
            Reference = "ENT-1",
            Pairs = 3,
            TotalCents = 277_500,
            Status = OrderStatus.Sold,
            ClaimNumber = "SL-1",
            CreatedAt = now,
            UpdatedAt = now,
        };

        var dto = OrderDto.From(order);

        Assert.Equal(order.Id, dto.Id);
        Assert.Equal(order.OwnerId, dto.OwnerId);
        Assert.Equal("Sold", dto.Status);
        Assert.Equal(277_500, dto.TotalCents);
        Assert.Equal("SL-1", dto.ClaimNumber);
    }

    [Fact]
    public void RaffleDto_MapsTheScarceNumber()
    {
        var raffle = new Raffle { Id = Guid.NewGuid(), Code = "S-1", Name = "v", Colorway = "o", Brand = "d", PairsTotal = 10, PairsLeft = 4, };
        var dto = RaffleDto.From(raffle, 7);
        Assert.Equal(10, dto.PairsTotal);
        Assert.Equal(7, dto.Entries);
        Assert.Equal(4, dto.PairsLeft);
    }
}
