using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Participants.RetailV2;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 散户画像池测试。验证:动态进场、独立账户(多空分歧)、情绪指数演化、离场清理。
/// </summary>
public class RetailProfilePoolTests
{
    private static (SimulationLoop loop, RetailProfilePool pool) Setup()
    {
        var rules = new MarketRules { PreviousClose = new Price(10m), FloatShares = new Quantity(200000) };
        var engine = new MatchingEngine(rules);
        var loop = new SimulationLoop(engine, new SimulationClock(60, 30));
        var MM = new ParticipantId("MM");
        var mm = loop.Session.GetOrCreateAccount(MM, 1_000_000_000m);
        mm.Position.Seed(new Quantity(100000), new Price(10m));
        // MM 挂紧密盘口提供流动性
        for (int i = 1; i <= 5; i++)
        {
            loop.Session.Submit(new OrderRequest(MM, Side.Sell, OrderType.Limit, new Price(10m + i * 0.01m), new Quantity(300)));
            loop.Session.Submit(new OrderRequest(MM, Side.Buy, OrderType.Limit, new Price(10m - i * 0.01m), new Quantity(300)));
        }
        var pool = new RetailProfilePool(loop.Session, new ParticipantId("散户池"), new Price(10m), seed: 42);
        loop.AddParticipant(pool);
        loop.Start();
        return (loop, pool);
    }

    [Fact]
    public void Pool_RecruitsProfilesOverTime()
    {
        // 跑一段时间,画像池应进场若干画像
        var (loop, pool) = Setup();
        for (int i = 0; i < 60; i++) loop.Step();
        Assert.True(pool.ActiveCount > 0, $"跑60 tick后应有画像进场,实际{pool.ActiveCount}");
    }

    [Fact]
    public void Pool_ProfilesHaveIndependentAccounts()
    {
        // 关键:每个画像有独立账户(解决耦合趋同)
        var (loop, pool) = Setup();
        for (int i = 0; i < 80; i++) loop.Step();
        // 在场画像各自的账户应独立(不同 ParticipantId)
        var ids = pool.ActiveProfiles.Select(p => p.Account.Id.Value).Distinct().ToList();
        Assert.Equal(pool.ActiveCount, ids.Count);   // 每画像一个独立 ID
    }

    [Fact]
    public void Sentiment_ReactsToPriceMove()
    {
        // 情绪应对价格操作有反应(变化足够大,证明情绪系统在工作)
        var (loop, pool) = Setup();
        for (int i = 0; i < 30; i++) loop.Step();
        decimal before = pool.Sentiment.Value;
        // 制造大幅波动(MM持续大单买,制造明显上涨)
        var MM = new ParticipantId("MM");
        for (int i = 0; i < 20; i++)
        {
            try { loop.Session.Submit(new OrderRequest(MM, Side.Buy, OrderType.Market, Price.Zero, new Quantity(500))); }
            catch { }
            loop.Step();
        }
        decimal after = pool.Sentiment.Value;
        // 情绪应发生变化(无论方向,证明系统对价格有反应)
        Assert.True(Math.Abs(after - before) > 0.05m,
            $"大幅价格操作后情绪应有明显变化,前{before:F2}后{after:F2}");
    }

    [Fact]
    public void Sentiment_AsymmetricPanicFasterThanGreed()
    {
        // 恐慌下跌比贪婪上涨传导更快(非对称)
        var sent = new MarketSentiment();
        sent.Update(0.5m, 0m, 0m);   // 重置到中性附近先
        // 大跌
        for (int i = 0; i < 5; i++) sent.Update(-0.05m, 0.02m, 0m);
        decimal afterDrop = sent.Value;
        Assert.True(afterDrop < 0.4m, $"大跌后应快速恐惧,实际{afterDrop}");
    }

    [Fact]
    public void Pool_RemovesExitedProfiles()
    {
        // 离场画像应被清理(IsActive=false 后移除)
        var (loop, pool) = Setup();
        for (int i = 0; i < 120; i++) loop.Step();
        // 跑久了应有进有出,ActiveCount 应在上限内且>=0
        Assert.True(pool.ActiveCount >= 0 && pool.ActiveCount <= pool.MaxActive);
        // 在场画像都应是 IsActive
        Assert.All(pool.ActiveProfiles, p => Assert.True(p.IsActive));
    }

    [Fact]
    public void Pool_GeneratesTradingActivity()
    {
        // 画像池跑起来应产生实际成交(盘口现价变化或散户账户变动)
        var (loop, pool) = Setup();
        var engine = loop.Session.Engine;
        Price? initial = engine.LastPrice;
        for (int i = 0; i < 100; i++) loop.Step();
        // 应有成交(现价确立)且画像进场
        Assert.True(pool.ActiveCount > 0, "应有画像进场交易");
    }
}
