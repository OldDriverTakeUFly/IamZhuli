using IamZhuli.Core;
using IamZhuli.Engine;

namespace IamZhuli.Engine.Tests;

/// <summary>
/// 限价单撮合:价格优先、时间优先、排队、穿价、撤单。
/// </summary>
public class LimitOrderMatchingTest
{
    [Fact]
    public void LimitBuyBelowBestAsk_RestsInBook()
    {
        var e = EngineTestExtensions.NewEngine();
        e.Place(e.SellLimit("M1", 10.51m, 100));

        var order = e.BuyLimit("P", 10.50m, 100);
        var result = e.Place(order);

        Assert.Equal(OrderStatus.Active, result.FinalStatus);
        Assert.Equal(100, result.RemainingQty.Value);
        Assert.False(result.HasFills);
        Assert.Equal(new Price(10.50m), e.View.BestBid);  // 挂在买一
    }

    [Fact]
    public void LimitBuyCrossesBestAsk_FillsImmediatelyAtMakerPrice()
    {
        // 限价买单价格 >= 最优卖价 → 立即成交,成交价=卖方价格(被动方)
        var e = EngineTestExtensions.NewEngine();
        e.Place(e.SellLimit("M1", 10.51m, 100));

        // 玩家挂 10.52 的买单(高于卖一 10.51),应吃掉卖一,成交价 10.51
        var result = e.Place(e.BuyLimit("P", 10.52m, 100));

        Assert.Equal(OrderStatus.Filled, result.FinalStatus);
        Assert.Equal(100, result.TotalFilled.Value);
        Assert.Equal(new Price(10.51m), result.AverageFillPrice);   // 成交价=被动方
        Assert.Equal(new Price(10.51m), e.LastPrice);
    }

    [Fact]
    public void LimitBuyHigherThanMultipleAsks_SweepsUpToLimitPrice()
    {
        // 限价单也能吃穿多档,但只吃到限价为止。
        // 卖盘:10.51(100)+10.52(100)+10.53(100)+10.54(100)。
        // 限价买 250@10.53 → 吃光 10.51、10.52 各 100,再吃 10.53 的 50,剩 50 仍挂 10.53。
        var e = EngineTestExtensions.NewEngine();
        e.Place(e.SellLimit("M1", 10.51m, 100));
        e.Place(e.SellLimit("M1", 10.52m, 100));
        e.Place(e.SellLimit("M1", 10.53m, 100));
        e.Place(e.SellLimit("M1", 10.54m, 100));  // 超出限价,完全不被吃

        var result = e.Place(e.BuyLimit("P", 10.53m, 250));

        Assert.Equal(250, result.TotalFilled.Value);
        Assert.Equal(new Price(10.53m), e.LastPrice);      // 吃到 10.53 停
        // 10.53 那档还剩 50 手挂在卖一;10.54 未受影响
        Assert.Equal(new Price(10.53m), e.View.BestAsk);
        var asks = e.View.TopAsks(5);
        Assert.Equal(new Price(10.53m), asks[0].Price);
        Assert.Equal(50, asks[0].TotalQty.Value);
        Assert.Equal(new Price(10.54m), asks[1].Price);
    }

    [Fact]
    public void SamePrice_TimePriority_FirstInFirstFilled()
    {
        // 同价位先挂者优先成交(时间优先)
        var e = EngineTestExtensions.NewEngine();
        var first = e.SellLimit("A", 10.51m, 100);
        e.Place(first);
        e.Place(e.SellLimit("B", 10.51m, 100));   // 后挂,同价位排队在后

        var result = e.Place(e.BuyMarket("P", 100));

        // 应先吃 A 的单
        Assert.Equal(new ParticipantId("A"), result.Trades[0].MakerId);
        Assert.Equal(100, first.FilledQty.Value);
    }

    [Fact]
    public void PricePriority_HigherBuyFilledBeforeLower()
    {
        // 两个买单排队,高的在前;来卖单时先满足高价买单
        var e = EngineTestExtensions.NewEngine();
        var high = e.BuyLimit("A", 10.52m, 100);
        e.Place(high);
        e.Place(e.BuyLimit("B", 10.50m, 100));

        Assert.Equal(new Price(10.52m), e.View.BestBid);  // 高价是买一

        var result = e.Place(e.SellMarket("S", 100));
        Assert.Equal(new ParticipantId("A"), result.Trades[0].MakerId);
    }

    [Fact]
    public void Cancel_RestingOrder_RemovedFromBook()
    {
        var e = EngineTestExtensions.NewEngine();
        var order = e.BuyLimit("P", 10.50m, 100);
        e.Place(order);

        Assert.True(e.Cancel(order.Id, out var cancelled));
        Assert.NotNull(cancelled);
        Assert.Equal(OrderStatus.Cancelled, cancelled!.Status);
        Assert.Null(e.View.BestBid);   // 撤单后买盘空
    }

    [Fact]
    public void Cancel_PartiallyFilledOrder_RemovesRemaining()
    {
        var e = EngineTestExtensions.NewEngine();
        e.Place(e.SellLimit("M1", 10.51m, 40));
        var order = e.BuyLimit("P", 10.51m, 100);
        var result = e.Place(order);   // 成交 40,剩 60 挂簿

        Assert.Equal(40, result.TotalFilled.Value);
        Assert.Equal(60, result.RemainingQty.Value);

        Assert.True(e.Cancel(order.Id, out var c));
        Assert.Equal(OrderStatus.Cancelled, c!.Status);
    }

    [Fact]
    public void CancelAll_RemovesAllOrdersOfParticipant()
    {
        var e = EngineTestExtensions.NewEngine();
        e.Place(e.BuyLimit("P", 10.50m, 100));
        e.Place(e.BuyLimit("P", 10.49m, 100));
        e.Place(e.BuyLimit("Q", 10.48m, 100));

        int n = e.CancelAll(new ParticipantId("P"));

        Assert.Equal(2, n);
        Assert.Equal(new Price(10.48m), e.View.BestBid);   // 只剩 Q
    }

    [Fact]
    public void OrderBook_TopOfBook_ReturnsAggregatedDepth()
    {
        var e = EngineTestExtensions.NewEngine();
        e.Place(e.BuyLimit("P", 10.50m, 100));
        e.Place(e.BuyLimit("Q", 10.50m, 200));   // 同价位累计
        e.Place(e.BuyLimit("R", 10.49m, 150));

        var bids = e.View.TopBids(5);

        Assert.Equal(2, bids.Count);   // 两个价位
        Assert.Equal(new Price(10.50m), bids[0].Price);
        Assert.Equal(300, bids[0].TotalQty.Value);   // 100+200
        Assert.Equal(new Price(10.49m), bids[1].Price);
        Assert.Equal(150, bids[1].TotalQty.Value);
    }
}
