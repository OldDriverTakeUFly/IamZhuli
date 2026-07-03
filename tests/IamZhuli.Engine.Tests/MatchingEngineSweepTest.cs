using IamZhuli.Core;
using IamZhuli.Engine;

namespace IamZhuli.Engine.Tests;

/// <summary>
/// 黄金测试:精确还原设计文档第 2.5 节"吃货推价"数值例子。
/// 卖盘挂 5 档(10.51~10.55,量 100/200/400/600/800),玩家市价买 1500 手。
/// 预期:逐档吃穿,现价 10.50→10.55,加权均价 = 10.5352。
/// </summary>
public class MatchingEngineSweepTest
{
    [Fact]
    public void MarketBuy1500_SweepsFiveAskLevels_PriceJumpsTo10_55_WeightedAvgCorrect()
    {
        // —— 盘口设置(对照设计文档) ——
        // 初始现价 10.50(用一笔基础成交确立)
        var e = EngineTestExtensions.NewEngine();

        // 先挂出五档卖盘
        e.Place(e.SellLimit("M1", 10.51m, 100));
        e.Place(e.SellLimit("M1", 10.52m, 200));
        e.Place(e.SellLimit("M1", 10.53m, 400));
        e.Place(e.SellLimit("M1", 10.54m, 600));
        e.Place(e.SellLimit("M1", 10.55m, 200));   // 注意:文档是 200,不是 800
        // 再挂买单确立 10.50 现价
        e.Place(e.BuyLimit("M1", 10.50m, 1));
        e.Place(e.SellMarket("seed", 1));          // 一手成交 @10.50

        Assert.Equal(new Price(10.50m), e.LastPrice);

        // —— 玩家市价买 1500 手 ——
        var result = e.Place(e.BuyMarket("Player", 1500));

        // 预期成交:100@10.51 + 200@10.52 + 400@10.53 + 600@10.54 + 200@10.55 = 1500
        Assert.Equal(1500, result.TotalFilled.Value);
        Assert.Equal(OrderStatus.Filled, result.FinalStatus);
        Assert.True(result.RemainingQty.IsZero);

        // 5 笔成交,价格依次 10.51~10.55
        Assert.Equal(5, result.Trades.Count);
        Assert.Equal(new Price(10.51m), result.Trades[0].Price);
        Assert.Equal(new Price(10.52m), result.Trades[1].Price);
        Assert.Equal(new Price(10.53m), result.Trades[2].Price);
        Assert.Equal(new Price(10.54m), result.Trades[3].Price);
        Assert.Equal(new Price(10.55m), result.Trades[4].Price);

        // 现价 = 最近成交价 = 10.55
        Assert.Equal(new Price(10.55m), e.LastPrice);

        // 加权均价 = (100*10.51 + 200*10.52 + 400*10.53 + 600*10.54 + 200*10.55) / 1500
        decimal expected =
            (100 * 10.51m + 200 * 10.52m + 400 * 10.53m + 600 * 10.54m + 200 * 10.55m) / 1500m;
        Assert.Equal(Math.Round(expected, 4), result.AverageFillPrice.Value);
    }

    [Fact]
    public void MarketSellSweepsBidLevels_PriceStepsDownEachLevel()
    {
        // 反向:大卖单逐档吃买盘,价格下台阶
        var e = EngineTestExtensions.NewEngine();
        e.Place(e.BuyLimit("M1", 10.49m, 100));
        e.Place(e.BuyLimit("M1", 10.48m, 200));
        e.Place(e.BuyLimit("M1", 10.47m, 400));

        var result = e.Place(e.SellMarket("Player", 700));

        Assert.Equal(700, result.TotalFilled.Value);
        // 依次吃 10.49→10.48→10.47
        Assert.Equal(new Price(10.49m), result.Trades[0].Price);
        Assert.Equal(new Price(10.48m), result.Trades[1].Price);
        Assert.Equal(new Price(10.47m), result.Trades[2].Price);
        Assert.Equal(new Price(10.47m), e.LastPrice);
    }

    [Fact]
    public void MarketOrderLargerThanBook_RemainingExpires()
    {
        // 市价单吃完整本订单簿仍有剩余 → 剩余作废(Expired)
        var e = EngineTestExtensions.NewEngine();
        e.Place(e.SellLimit("M1", 10.51m, 100));

        var result = e.Place(e.BuyMarket("Player", 500));

        Assert.Equal(100, result.TotalFilled.Value);
        Assert.Equal(400, result.RemainingQty.Value);
        Assert.Equal(OrderStatus.Expired, result.FinalStatus);
    }
}
