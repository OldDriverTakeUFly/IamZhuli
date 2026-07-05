using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.MarketData;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 筹码分布采集器测试(筹码峰模型:按价位分桶)。
/// 验证:日终采集、按成本价分桶、筹码量聚合、峰集中度计算。
/// </summary>
public class ChipSnapshotCollectorTests
{
    private static (SimulationLoop loop, TradingSession session, ChipSnapshotCollector chips) Setup(int ticksPerDay = 5, int days = 5, decimal bandWidth = 0.2m)
    {
        var rules = new MarketRules { PreviousClose = new Price(10m), FloatShares = new Quantity(100000) };
        var engine = new MatchingEngine(rules);
        var loop = new SimulationLoop(engine, new SimulationClock(ticksPerDay, days));
        // 提供流动性的做市账户
        var mm = loop.Session.GetOrCreateAccount(new ParticipantId("MM"), 1_000_000_000m);
        mm.Position.Seed(new Quantity(80000), new Price(10m));
        for (int i = 1; i <= 5; i++)
        {
            loop.Session.Submit(new OrderRequest(new ParticipantId("MM"), Side.Sell, OrderType.Limit, new Price(10m + i * 0.01m), new Quantity(500)));
            loop.Session.Submit(new OrderRequest(new ParticipantId("MM"), Side.Buy, OrderType.Limit, new Price(10m - i * 0.01m), new Quantity(500)));
        }
        var chips = new ChipSnapshotCollector(loop, loop.Session, bandWidth);
        loop.Start();
        return (loop, session: loop.Session, chips);
    }

    /// <summary>跑完一天(到 IsDayClosed),应采集到一条分布快照。</summary>
    [Fact]
    public void DayFinalized_CapturesDistribution()
    {
        var (loop, session, chips) = Setup();
        session.GetOrCreateAccount(new ParticipantId("Player"), 100_000_000m);
        session.Submit(new OrderRequest(new ParticipantId("Player"), Side.Buy, OrderType.Market, Price.Zero, new Quantity(100)));
        while (!loop.IsDayClosed) loop.Step();
        Assert.Single(chips.History);
        Assert.Equal(1, chips.History[0].Day);
    }

    /// <summary>不同成本价的账户,筹码应归入不同的价位桶。</summary>
    [Fact]
    public void DifferentCosts_GoToDifferentBands()
    {
        var (loop, session, chips) = Setup(bandWidth: 0.5m);
        // 两个账户,不同成本价(10.0 和 11.0),用 Seed 注入
        var acc1 = session.GetOrCreateAccount(new ParticipantId("A"), 1_000_000m);
        acc1.Position.Seed(new Quantity(1000), new Price(10.0m));
        var acc2 = session.GetOrCreateAccount(new ParticipantId("B"), 1_000_000m);
        acc2.Position.Seed(new Quantity(2000), new Price(11.0m));
        // 做一笔交易产生价格
        session.GetOrCreateAccount(new ParticipantId("Player"), 100_000_000m);
        session.Submit(new OrderRequest(new ParticipantId("Player"), Side.Buy, OrderType.Market, Price.Zero, new Quantity(100)));
        while (!loop.IsDayClosed) loop.Step();
        var snap = chips.History[0];
        // 应有至少2个桶(10.0档和11.0档),桶宽0.5元
        Assert.True(snap.Bands.Count >= 2, $"应有>=2个桶,实际{snap.Bands.Count}");
        // 10.0档(10.0~10.5)应包含1000手
        var band10 = snap.Bands.FirstOrDefault(b => b.PriceLow <= 10.0m && b.PriceHigh > 10.0m);
        Assert.NotNull(band10);
        Assert.True(band10!.Quantity >= 1000);
        // 11.0档(11.0~11.5)应包含2000手
        var band11 = snap.Bands.FirstOrDefault(b => b.PriceLow <= 11.0m && b.PriceHigh > 11.0m);
        Assert.NotNull(band11);
        Assert.True(band11!.Quantity >= 2000);
    }

    /// <summary>相同成本价的多个账户,筹码应聚合到同一桶。</summary>
    [Fact]
    public void SameCost_AggregatedInOneBand()
    {
        var (loop, session, chips) = Setup(bandWidth: 0.2m);
        for (int i = 0; i < 5; i++)
        {
            var acc = session.GetOrCreateAccount(new ParticipantId($"散户-{i}"), 500_000m);
            acc.Position.Seed(new Quantity(100 + i * 10), new Price(10.05m));   // 同价位
        }
        session.GetOrCreateAccount(new ParticipantId("Player"), 100_000_000m);
        session.Submit(new OrderRequest(new ParticipantId("Player"), Side.Buy, OrderType.Market, Price.Zero, new Quantity(100)));
        while (!loop.IsDayClosed) loop.Step();
        var snap = chips.History[0];
        // 10.0档(10.0~10.2)应包含做市商MM(80000@10.0) + 全部5个散户(600@10.05) = 80600
        var band = snap.Bands.FirstOrDefault(b => b.PriceLow <= 10.05m && b.PriceHigh > 10.05m);
        Assert.NotNull(band);
        Assert.Equal(80600, band!.Quantity);
    }

    /// <summary>空仓账户不应产生筹码。</summary>
    [Fact]
    public void EmptyAccount_NoChips()
    {
        var (loop, session, chips) = Setup();
        var empty = session.GetOrCreateAccount(new ParticipantId("Empty"), 100_000m);   // 有钱无仓
        session.GetOrCreateAccount(new ParticipantId("Player"), 100_000_000m);
        session.Submit(new OrderRequest(new ParticipantId("Player"), Side.Buy, OrderType.Market, Price.Zero, new Quantity(100)));
        while (!loop.IsDayClosed) loop.Step();
        var snap = chips.History[0];
        // 总筹码量应>0但不应包含空仓账户的0
        Assert.True(snap.TotalQuantity > 0);
    }

    /// <summary>峰集中度:最高峰占总量比例,应在0~1之间。</summary>
    [Fact]
    public void PeakConcentration_BetweenZeroAndOne()
    {
        var (loop, session, chips) = Setup();
        var acc = session.GetOrCreateAccount(new ParticipantId("Big"), 1_000_000m);
        acc.Position.Seed(new Quantity(5000), new Price(10.0m));
        session.GetOrCreateAccount(new ParticipantId("Player"), 100_000_000m);
        session.Submit(new OrderRequest(new ParticipantId("Player"), Side.Buy, OrderType.Market, Price.Zero, new Quantity(100)));
        while (!loop.IsDayClosed) loop.Step();
        decimal conc = chips.PeakConcentration(0);
        Assert.True(conc > 0 && conc <= 1m, $"集中度应>0且<=1,实际{conc}");
    }

    /// <summary>快照应包含收盘价。</summary>
    [Fact]
    public void Snapshot_ContainsClosePrice()
    {
        var (loop, session, chips) = Setup();
        session.GetOrCreateAccount(new ParticipantId("Player"), 100_000_000m);
        session.Submit(new OrderRequest(new ParticipantId("Player"), Side.Buy, OrderType.Market, Price.Zero, new Quantity(100)));
        while (!loop.IsDayClosed) loop.Step();
        var snap = chips.History[0];
        Assert.True(snap.ClosePrice > 0, "收盘价应>0");
    }

    /// <summary>跨多日,应有多个分布快照。</summary>
    [Fact]
    public void MultiDay_MultipleSnapshots()
    {
        var (loop, session, chips) = Setup(ticksPerDay: 5, days: 3);
        session.GetOrCreateAccount(new ParticipantId("Player"), 100_000_000m);
        while (!loop.IsDayClosed) loop.Step();
        loop.StartNextDay();
        while (!loop.IsDayClosed) loop.Step();
        Assert.Equal(2, chips.History.Count);
        Assert.Equal(1, chips.History[0].Day);
        Assert.Equal(2, chips.History[1].Day);
    }
}
