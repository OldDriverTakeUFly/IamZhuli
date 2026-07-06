using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.MarketData;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Tests;

/// <summary>
/// 复盘数据采集器测试。
/// 验证:关键帧快照间隔、交易日志留存、二分查找快照。
/// </summary>
public class ReplayCollectorTests
{
    private static (SimulationLoop loop, TradingSession session, ReplayCollector replay, Account player) Setup(int ticksPerDay = 60, int days = 2)
    {
        var rules = new MarketRules { PreviousClose = new Price(10m), FloatShares = new Quantity(100000) };
        var engine = new MatchingEngine(rules);
        var loop = new SimulationLoop(engine, new SimulationClock(ticksPerDay, days));
        var mm = loop.Session.GetOrCreateAccount(new ParticipantId("MM"), 1_000_000_000m);
        mm.Position.Seed(new Quantity(80000), new Price(10m));
        for (int i = 1; i <= 5; i++)
        {
            loop.Session.Submit(new OrderRequest(new ParticipantId("MM"), Side.Sell, OrderType.Limit, new Price(10m + i * 0.01m), new Quantity(500)));
            loop.Session.Submit(new OrderRequest(new ParticipantId("MM"), Side.Buy, OrderType.Limit, new Price(10m - i * 0.01m), new Quantity(500)));
        }
        var player = loop.Session.GetOrCreateAccount(new ParticipantId("Player"), 100_000_000m);
        var replay = new ReplayCollector(loop, loop.Session,
            () => new[] { ("玩家", player) },
            () => 0m);
        loop.Start();
        return (loop, loop.Session, replay, player);
    }

    [Fact]
    public void Snapshots_CapturedEvery20Ticks()
    {
        var (loop, _, replay, _) = Setup(ticksPerDay: 60, days: 1);
        while (!loop.IsDayClosed) loop.Step();   // 跑完60 tick
        // 60 tick / 20 = 3 个快照(tick 0, 20, 40)
        Assert.True(replay.Snapshots.Count >= 3, $"应有>=3个快照,实际{replay.Snapshots.Count}");
        // 间隔应为20
        for (int i = 1; i < replay.Snapshots.Count; i++)
            Assert.Equal(20, replay.Snapshots[i].TickIndex - replay.Snapshots[i-1].TickIndex);
    }

    [Fact]
    public void Trades_With_Identity_Captured()
    {
        var (loop, session, replay, _) = Setup();
        // 玩家买入产生成交
        session.Submit(new OrderRequest(new ParticipantId("Player"), Side.Buy, OrderType.Market, Price.Zero, new Quantity(100)));
        while (!loop.IsDayClosed) loop.Step();
        // 应有交易记录,且带身份
        Assert.True(replay.Trades.Count > 0, "应有交易记录");
        var t = replay.Trades[0];
        Assert.True(!string.IsNullOrEmpty(t.TakerId), "交易应记录主动方身份");
        Assert.True(!string.IsNullOrEmpty(t.MakerId), "交易应记录被动方身份");
    }

    [Fact]
    public void FindSnapshotIndex_BinarySearch()
    {
        var (loop, _, replay, _) = Setup(ticksPerDay: 60, days: 1);
        while (!loop.IsDayClosed) loop.Step();
        // 找 tick=25 应返回最近的 <=25 的快照(tick=20,index=1)
        int idx = replay.FindSnapshotIndex(25);
        Assert.True(idx >= 0);
        Assert.True(replay.Snapshots[idx].TickIndex <= 25);
        // 找超出范围的 tick 应返回最后一个
        int lastIdx = replay.FindSnapshotIndex(99999);
        Assert.Equal(replay.Snapshots.Count - 1, lastIdx);
    }

    [Fact]
    public void Snapshot_Contains_Participants()
    {
        var (loop, _, replay, _) = Setup();
        while (!loop.IsDayClosed) loop.Step();
        Assert.True(replay.Snapshots.Count > 0);
        var snap = replay.Snapshots[0];
        Assert.True(snap.Participants.Count > 0, "快照应包含参与方");
        Assert.Equal("玩家", snap.Participants[0].Name);
    }

    [Fact]
    public void Snapshot_Contains_OrderBook()
    {
        var (loop, _, replay, _) = Setup();
        while (!loop.IsDayClosed) loop.Step();
        var snap = replay.Snapshots[0];
        // 做市商挂了5档买卖,快照应记录到
        Assert.True(snap.TopBids.Count > 0, "快照应包含买盘");
        Assert.True(snap.TopAsks.Count > 0, "快照应包含卖盘");
    }
}
