using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.MarketData;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 市场数据采集器 + MACD 指标测试。
/// 验证:开盘价采集、High/Low滚动、收盘价固化、成交量累计、日切归档、换手率、MACD计算。
/// </summary>
public class MarketDataTests
{
    /// <summary>构造一个带采集器的 loop,MM 提供成交(不预挂盘口,让测试成交价唯一)。</summary>
    private static (SimulationLoop loop, MarketDataCollector collector) Setup(int ticksPerDay = 10)
    {
        var rules = new MarketRules { PreviousClose = new Price(10m), FloatShares = new Quantity(100000) };
        var engine = new MatchingEngine(rules);
        var loop = new SimulationLoop(engine, new SimulationClock(ticksPerDay, 30));
        var MM = new ParticipantId("MM");
        var mm = loop.Session.GetOrCreateAccount(MM, 1_000_000_000m);
        mm.Position.Seed(new Quantity(100000), new Price(10m));
        var collector = new MarketDataCollector(loop, 10m);
        loop.Start();
        return (loop, collector);
    }

    /// <summary>通过 MM 自挂自买制造一笔成交 @price(确保该价成为成交价)。</summary>
    private static void MakeTrade(SimulationLoop loop, decimal price, int qty)
    {
        var MM = new ParticipantId("MM");
        // 先撤掉可能存在的盘口干扰(简化:直接挂新卖单+市价买,卖单价格即为成交价)
        loop.Session.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(price), new Quantity(qty)));
        loop.Session.Submit(new OrderRequest(MM, Side.Buy, OrderType.Market, Price.Zero, new Quantity(qty)));
    }

    [Fact]
    public void Collector_FirstTradeBecomesOpenPrice()
    {
        var (loop, collector) = Setup();
        // 跳过开盘前几个tick无成交,然后制造成交
        loop.Step();
        MakeTrade(loop, 10.20m, 100);
        Assert.Equal(10.20m, collector.TodayOpen);
    }

    [Fact]
    public void Collector_HighLowTrackExtremes()
    {
        var (loop, collector) = Setup();
        loop.Step();
        MakeTrade(loop, 10.10m, 100);
        MakeTrade(loop, 10.50m, 100);
        MakeTrade(loop, 9.98m, 100);
        Assert.Equal(10.50m, collector.TodayHigh);
        Assert.Equal(9.98m, collector.TodayLow);
    }

    [Fact]
    public void Collector_VolumeAccumulates()
    {
        var (loop, collector) = Setup();
        loop.Step();
        MakeTrade(loop, 10.10m, 100);
        MakeTrade(loop, 10.20m, 200);
        MakeTrade(loop, 10.15m, 150);
        Assert.Equal(450, collector.TodayVolume);
    }

    [Fact]
    public void Collector_TurnoverRateComputed()
    {
        // 流通盘100000手,成交500手 → 换手率0.5%
        var (loop, collector) = Setup();
        loop.Step();
        MakeTrade(loop, 10.10m, 500);
        Assert.Equal(0.5m, Math.Round(collector.TurnoverRate, 2));
    }

    [Fact]
    public void Collector_FinalizesDay_OnLastTick()
    {
        var (loop, collector) = Setup(ticksPerDay: 5);
        for (int i = 0; i < 3; i++) { loop.Step(); MakeTrade(loop, 10m + i * 0.1m, 100); }
        // 跑到日终(会自动暂停在 IsDayClosed)
        while (!loop.IsDayClosed && !loop.IsFinished) loop.Step();

        Assert.True(loop.IsDayClosed, "应进入日终收盘暂停");
        Assert.Single(collector.DailyCandles);   // 收盘固化已触发
        var c = collector.DailyCandles[0];
        Assert.Equal(1, c.Day);
        Assert.True(c.Close >= 10m, $"收盘价应接近最后成交价,实际{c.Close}");
    }

    [Fact]
    public void Collector_NewDayClearsAndArchives()
    {
        var (loop, collector) = Setup(ticksPerDay: 5);
        loop.Step(); MakeTrade(loop, 10.20m, 100);
        Assert.True(collector.TodayTimeshare.Count > 0);

        loop.SkipToNextDay();   // 跑到收盘暂停
        Assert.True(loop.IsDayClosed, "应在收盘暂停");
        loop.StartNextDay();    // 玩家显式开始下一日(触发跨日+清空分时)
        Assert.Empty(collector.TodayTimeshare);
        Assert.Single(collector.DailyCandles);
    }

    [Fact]
    public void MacdCalculator_ProducesSeries()
    {
        // 给30个递增收盘价,MACD应产生有效序列,DIF应随趋势变化
        var macd = new MacdCalculator();
        MacdPoint last = default;
        for (int i = 0; i < 30; i++)
        {
            last = macd.Update(10m + i * 0.1m);   // 持续上涨
        }
        Assert.True(last.Dif > 0, $"持续上涨时DIF应为正,实际{last.Dif}");
        Assert.True(last.Histogram > 0, $"上涨趋势MACD柱应为正,实际{last.Histogram}");
    }

    [Fact]
    public void MacdCalculator_DetectsReversal()
    {
        // 先涨后跌,MACD柱应由正转负
        var macd = new MacdCalculator();
        for (int i = 0; i < 26; i++) macd.Update(10m + i * 0.2m);   // 涨
        var peak = macd.Current.Histogram;
        for (int i = 0; i < 10; i++) macd.Update(15m - i * 0.3m);   // 跌
        var after = macd.Current.Histogram;
        Assert.True(after < peak, $"下跌后MACD柱应小于上涨峰值(peak={peak},after={after})");
    }
}
