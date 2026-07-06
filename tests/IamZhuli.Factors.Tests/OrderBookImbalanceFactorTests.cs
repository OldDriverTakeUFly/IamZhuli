using IamZhuli.Core;
using IamZhuli.Factors;

namespace IamZhuli.Factors.Tests;

/// <summary>
/// 订单簿失衡因子(OBI)测试。
/// </summary>
public class OrderBookImbalanceFactorTests
{
    private static QuoteLevel L(decimal price, int qty) => new(new Price(price), new Quantity(qty));

    [Fact]
    public void EqualDepth_OBIIsZero()
    {
        var f = new OrderBookImbalanceFactor(levels: 5);
        var snap = MarketDataSnapshot.Of(new Price(10m),
            new[] { L(9.99m, 100) }, new[] { L(10.01m, 100) });
        Assert.Equal(0m, f.Compute(snap));
    }

    [Fact]
    public void BidHeavier_OBIPositive()
    {
        var f = new OrderBookImbalanceFactor(levels: 5);
        var snap = MarketDataSnapshot.Of(new Price(10m),
            new[] { L(9.99m, 300) }, new[] { L(10.01m, 100) });
        // (300-100)/(300+100) = 0.5
        Assert.Equal(0.5m, f.Compute(snap));
    }

    [Fact]
    public void AskHeavier_OBINegative()
    {
        var f = new OrderBookImbalanceFactor(levels: 5);
        var snap = MarketDataSnapshot.Of(new Price(10m),
            new[] { L(9.99m, 100) }, new[] { L(10.01m, 300) });
        Assert.Equal(-0.5m, f.Compute(snap));
    }

    [Fact]
    public void OnlyBids_OBIIsOne()
    {
        // 只有买盘、无卖盘 → 极度失衡 +1
        var f = new OrderBookImbalanceFactor(levels: 5);
        var snap = MarketDataSnapshot.Of(new Price(10m),
            new[] { L(9.99m, 500) }, Array.Empty<QuoteLevel>());
        Assert.Equal(1m, f.Compute(snap));
    }

    [Fact]
    public void EmptyBook_OBIIsZero()
    {
        var f = new OrderBookImbalanceFactor(levels: 5);
        var snap = MarketDataSnapshot.Of(new Price(10m),
            Array.Empty<QuoteLevel>(), Array.Empty<QuoteLevel>());
        Assert.Equal(0m, f.Compute(snap));
    }

    [Fact]
    public void Weighted_PrioritizesFrontLevels()
    {
        // 同样总挂单量,但分布不同:加权时前面档位权重高
        // 等权:[200,100] 买 vs [100,200] 卖 → 各 300,等权 OBI=0
        // 加权(5档):买=200×5+100×4=1400, 卖=100×5+200×4=1300 → 正
        var fWeighted = new OrderBookImbalanceFactor(levels: 5, weighted: true);
        var fPlain = new OrderBookImbalanceFactor(levels: 5, weighted: false);
        var snap = MarketDataSnapshot.Of(new Price(10m),
            new[] { L(9.99m, 200), L(9.98m, 100) },
            new[] { L(10.01m, 100), L(10.02m, 200) });

        Assert.Equal(0m, fPlain.Compute(snap));                       // 等权均衡
        Assert.True(fWeighted.Compute(snap) > 0m, "加权应让前置买盘占优");
    }

    [Fact]
    public void LevelsCap_LimitsAggregatedDepth()
    {
        // 5 档因子不应把第 6 档算进去(即使快照有更多档位)
        var f = new OrderBookImbalanceFactor(levels: 1);
        var snap = MarketDataSnapshot.Of(new Price(10m),
            new[] { L(9.99m, 100), L(9.98m, 9999) },   // 第2档买盘巨大但不该计入
            new[] { L(10.01m, 100), L(10.02m, 9999) });
        Assert.Equal(0m, f.Compute(snap));   // 只看第1档:各100,均衡
    }
}
