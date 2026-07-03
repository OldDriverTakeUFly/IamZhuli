using IamZhuli.Core;
using IamZhuli.Engine;

namespace IamZhuli.Engine.Tests;

/// <summary>
/// 涨跌停约束测试。前收盘 10.00,±10% → [9.00, 11.00]。
/// 到达涨跌停后,该方向禁止开仓(只允许平仓)。
/// </summary>
public class PriceLimitTest
{
    private static MatchingEngine New() => EngineTestExtensions.NewEngine(10.00m, 0.10m);

    [Fact]
    public void Rules_ComputeUpperAndLowerLimits()
    {
        var e = New();
        Assert.Equal(new Price(11.00m), e.Rules.UpperLimit);
        Assert.Equal(new Price(9.00m), e.Rules.LowerLimit);
    }

    [Fact]
    public void BuyAtUpperLimit_BlockedWhenPriceAlreadyAtLimit()
    {
        // 把价格推到涨停 11.00
        var e = New();
        e.Place(e.SellLimit("M1", 11.00m, 1));
        e.Place(e.BuyMarket("P", 1));
        Assert.Equal(new Price(11.00m), e.LastPrice);

        // 现在价格在涨停,新的买单(开多仓)应被阻止挂簿
        var buy = e.BuyLimit("P2", 11.00m, 100);
        var result = e.Place(buy);

        Assert.Equal(OrderStatus.Expired, result.FinalStatus);
        Assert.Null(e.View.BestBid);   // 没挂进去
    }

    [Fact]
    public void SellAtLowerLimit_BlockedWhenPriceAlreadyAtLimit()
    {
        var e = New();
        // 推到跌停 9.00
        e.Place(e.BuyLimit("M1", 9.00m, 1));
        e.Place(e.SellMarket("P", 1));
        Assert.Equal(new Price(9.00m), e.LastPrice);

        var sell = e.SellLimit("P2", 9.00m, 100);
        var result = e.Place(sell);

        Assert.Equal(OrderStatus.Expired, result.FinalStatus);
        Assert.Null(e.View.BestAsk);
    }

    [Fact]
    public void CloseOrder_AtLimitStillAllowed()
    {
        // 已有持仓的人在涨停价卖出(平仓)应被允许 —— 涨停禁的是"开多仓",平多仓不受限
        // 体现:在涨停价挂卖单应能成功(因为卖出是减少多头/建立空头方向)
        // 注:本测试聚焦"卖单在涨停价可挂",对应平仓场景
        var e = New();
        e.Place(e.SellLimit("M1", 11.00m, 1));
        e.Place(e.BuyMarket("P", 1));   // 到涨停

        // 在涨停价挂卖单(卖出)→ 卖出不受涨停限制
        var sell = e.SellLimit("P2", 11.00m, 100);
        var result = e.Place(sell);
        Assert.Equal(OrderStatus.Active, result.FinalStatus);
        Assert.Equal(new Price(11.00m), e.View.BestAsk);
    }
}
