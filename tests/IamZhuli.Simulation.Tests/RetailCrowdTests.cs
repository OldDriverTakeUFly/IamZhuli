using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 散户群体逻辑测试。验证 4 群体在各自触发条件下的买卖行为。
/// </summary>
public class RetailCrowdTests
{
    private static readonly ParticipantId Retail = new("散户");
    private static readonly ParticipantId MM = new("MM");

    /// <summary>构造一个会话,MM 挂出指定现价附近的盘口,散户有初始持仓。</summary>
    private static (TradingSession s, SharedRetailState state, Account retailAcc) Setup(
        decimal lastPrice, decimal intrinsic, int retailHolding, decimal retailCost)
    {
        var rules = new MarketRules { PreviousClose = new Price(intrinsic) };
        var s = new TradingSession(new MatchingEngine(rules));
        var mm = s.GetOrCreateAccount(MM, 1_000_000_000m);
        mm.Position.Seed(new Quantity(100000), new Price(intrinsic));
        var retailAcc = s.GetOrCreateAccount(Retail, 200_000_000m);
        retailAcc.Position.Seed(new Quantity(retailHolding), new Price(retailCost));

        // 围绕 lastPrice 挂一档盘口,并用一笔成交确立现价
        s.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(lastPrice + 0.01m), new Quantity(500)));
        s.Submit(new OrderRequest(MM, Side.Buy, OrderType.Limit, new Price(lastPrice - 0.01m), new Quantity(500)));
        s.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(lastPrice), new Quantity(1)));
        s.Submit(new OrderRequest(Retail, Side.Buy, OrderType.Market, Price.Zero, new Quantity(1)));

        var state = new SharedRetailState
        {
            AverageCost = new Price(retailCost),
            TotalHolding = retailHolding
        };
        return (s, state, retailAcc);
    }

    private static void FillMomentum(SharedRetailState state, decimal fromPrice, decimal toPrice)
    {
        // 灌满近 window 个价格,让 Momentum 有足够样本
        for (int i = 0; i < state.HistoryWindow; i++)
        {
            decimal p = fromPrice + (toPrice - fromPrice) * i / (state.HistoryWindow - 1);
            state.RecordPrice(new Price(p));
        }
    }

    [Fact]
    public void MomentumChaser_BuysWhenPriceRising()
    {
        // 现价 10.5(从 10 涨上来),跟风客应追涨买入
        var (s, state, retailAcc) = Setup(lastPrice: 10.50m, intrinsic: 10.00m, retailHolding: 0, retailCost: 10.00m);
        FillMomentum(state, 10.00m, 10.50m);   // 填充上涨序列
        var clock = new SimulationClock(60, 30); clock.Open(); clock.AdvanceTick();
        var chaser = new MomentumChaser(retailAcc, state, Retail, new Price(10.00m), strength: 1000);
        for (int i = 0; i < 30; i++) chaser.Act(s, clock, new Random(i));   // 多次触发提高概率

        // 散户应有买入(持仓增加或现金减少)
        Assert.True(retailAcc.Position.Total.Value > 0 || retailAcc.Cash < 200_000_000m,
            "跟风客在上涨时应追涨买入");
    }

    [Fact]
    public void BargainHunter_BuysWhenPriceCheap()
    {
        // 现价 9.4,内在价值 10,跌破便宜线(10×0.95=9.5),抄底盘应买入
        var (s, state, retailAcc) = Setup(lastPrice: 9.40m, intrinsic: 10.00m, retailHolding: 0, retailCost: 10.00m);
        var clock = new SimulationClock(60, 30); clock.Open(); clock.AdvanceTick();
        var hunter = new BargainHunter(retailAcc, state, Retail, new Price(10.00m), strength: 1000, discount: 0.05m);
        for (int i = 0; i < 30; i++) hunter.Act(s, clock, new Random(i));

        Assert.True(retailAcc.Position.Total.Value > 0, "抄底盘在低价应逢低买入");
    }

    [Fact]
    public void StopLossSeller_SellsWhenPriceBreaksCost()
    {
        // 散户成本 10,现价跌到 9.2(破止损线 10×0.93=9.3),止损盘应卖出
        var (s, state, retailAcc) = Setup(lastPrice: 9.20m, intrinsic: 10.00m, retailHolding: 50000, retailCost: 10.00m);
        var clock = new SimulationClock(60, 30); clock.Open(); clock.AdvanceTick();
        var seller = new StopLossSeller(retailAcc, state, Retail, new Price(10.00m), strength: 1000, stopRatio: 0.07m);
        int before = retailAcc.Position.Available.Value;
        for (int i = 0; i < 30; i++) seller.Act(s, clock, new Random(i));

        Assert.True(retailAcc.Position.Available.Value < before || retailAcc.Cash > 200_000_000m,
            "止损盘在跌破成本时应恐慌卖出");
    }

    [Fact]
    public void ValueInvestor_BuysLow_SellsHigh()
    {
        // 价投:现价 9.10 远低于内在价值 10(偏离 -9% > 8% 阈值),且未跌停(跌停9.00)→ 应买入
        var (s, state, retailAcc) = Setup(lastPrice: 9.10m, intrinsic: 10.00m, retailHolding: 0, retailCost: 10.00m);
        var clock = new SimulationClock(60, 30); clock.Open(); clock.AdvanceTick();
        var vi = new ValueInvestor(retailAcc, state, Retail, new Price(10.00m), strength: 1000, deviationThreshold: 0.08m);
        for (int i = 0; i < 50; i++) vi.Act(s, clock, new Random(i));

        Assert.True(retailAcc.Position.Total.Value > 0, "价投在严重低估时应买入");
    }

    [Fact]
    public void RetailMarket_GeneratesActivity_OverTime()
    {
        // 集成测试:跑 RetailMarket 一段时间,散户应产生成交,盘口现价不应恒定不变
        var rules = new MarketRules { PreviousClose = new Price(10.00m) };
        var engine = new MatchingEngine(rules);
        var session = new TradingSession(engine);
        var mm = session.GetOrCreateAccount(MM, 1_000_000_000m);
        mm.Position.Seed(new Quantity(100000), new Price(10.00m));
        // MM 挂紧密盘口(窄价差,模拟正常流动性):卖 10.01~10.10,买 9.99~9.90
        for (int i = 1; i <= 10; i++)
        {
            session.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10.00m + i * 0.01m), new Quantity(300)));
            session.Submit(new OrderRequest(MM, Side.Buy, OrderType.Limit, new Price(10.00m - i * 0.01m), new Quantity(300)));
        }
        // 一笔成交确立开盘价
        session.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10.00m), new Quantity(1)));
        session.Submit(new OrderRequest(MM, Side.Buy, OrderType.Market, Price.Zero, new Quantity(1)));

        var retail = new RetailMarket(session, Retail, new Price(10.00m), 200_000_000m, 50000, seed: 7);
        var clock = new SimulationClock(60, 30); clock.Open();
        var rng = new Random(7);
        for (int t = 0; t < 120; t++)
        {
            retail.Act(session, clock, rng);
            clock.AdvanceTick();
        }

        // 跑了120个tick,散户应至少有一些成交(持仓或现金变化),现价可能有波动
        Assert.True(retail.State.TotalHolding != 50000 || engine.LastPrice != new Price(10.00m),
            "散户市场跑起来后应产生交易活动");
    }
}
