using Sole.Raffles;
using Xunit;

namespace Sole.UnitTests;

public class RaffleDrawTests
{
    private static (Guid, int)[] Entries(int n, int pairs = 1) =>
        Enumerable.Range(0, n).Select(i => (new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), pairs)).ToArray();

    [Fact]
    public void Commitment_IsTheSha256OfTheSeed_AndMatchesIsCaseInsensitive()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", RaffleDraw.Commitment("abc"));
        Assert.True(RaffleDraw.Matches("abc", "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"));
        Assert.False(RaffleDraw.Matches("abd", RaffleDraw.Commitment("abc")));
    }

    [Fact]
    public void NewSeed_Is128RandomBits_AndNeverRepeats()
    {
        var seeds = Enumerable.Range(0, 200).Select(_ => RaffleDraw.NewSeed()).ToHashSet();
        Assert.Equal(200, seeds.Count);
        Assert.All(seeds, s => Assert.Matches("^[0-9a-f]{32}$", s));
    }

    [Fact]
    public void TheDraw_DoesNotDependOnTheOrderTheEntriesArriveIn()
    {
        var entries = Entries(30);
        var a = RaffleDraw.Allocate("seed-1", entries, 10);
        var b = RaffleDraw.Allocate("seed-1", entries.Reverse(), 10);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ADifferentSeed_GivesADifferentOrder()
    {
        var entries = Entries(30);
        var a = RaffleDraw.Allocate("seed-1", entries, 10).Select(e => e.OrderId).ToArray();
        var b = RaffleDraw.Allocate("seed-2", entries, 10).Select(e => e.OrderId).ToArray();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Winners_NeverTakeMoreThanThePairsThatExist_AndRanksAreOneToN()
    {
        var entries = Enumerable.Range(0, 40).Select(i => (new Guid(i + 1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]), 1 + i % 3)).ToArray();
        var result = RaffleDraw.Allocate("seed-x", entries, 17);
        Assert.Equal(Enumerable.Range(1, 40), result.Select(r => r.Rank));
        Assert.True(result.Where(r => r.Won).Sum(r => r.Pairs) <= 17);
        Assert.Equal(40, result.Select(r => r.OrderId).Distinct().Count());
    }

    [Fact]
    public void FirstFit_AnEntryThatDoesNotFitLosesButASmallerOneBehindItCanStillWin()
    {
        // Whatever the order, with 2 pairs left a party of 3 can never win and parties of 1 and 2 can.
        var big = new Guid(1, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
        var small = new Guid(2, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
        var result = RaffleDraw.Allocate("any", [(big, 3), (small, 1)], 2);
        Assert.False(result.Single(r => r.OrderId == big).Won);
        Assert.True(result.Single(r => r.OrderId == small).Won);
    }

    [Fact]
    public void EveryEntryIsEquallyLikelyToWin_AcrossManySeeds()
    {
        // 20 entries, 5 pairs: each should win about 25% of the time. 4000 deterministic seeds keep the test stable.
        var entries = Entries(20);
        var wins = new int[20];
        for (var s = 0; s < 4000; s++)
            foreach (var r in RaffleDraw.Allocate($"fair-{s}", entries, 5).Where(r => r.Won))
                wins[Array.IndexOf(entries.Select(e => e.Item1).ToArray(), r.OrderId)]++;
        Assert.All(wins, w => Assert.InRange(w, 4000 * 0.25 * 0.85, 4000 * 0.25 * 1.15));
        Assert.Equal(4000 * 5, wins.Sum());
    }

    [Fact]
    public void Key_IsStableForASeedAndAnId() =>
        Assert.Equal(RaffleDraw.Key("s", Guid.Empty), RaffleDraw.Key("s", Guid.Empty));
}
