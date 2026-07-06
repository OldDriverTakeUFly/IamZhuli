using IamZhuli.Core;
using IamZhuli.Factors;

namespace IamZhuli.Factors.Tests;

/// <summary>
/// MarketSignalTracker 测试。用合成快照驱动,完全不依赖撮合引擎——
/// 这正验证了因子模块"数据源解耦"的设计目标。
/// </summary>
public class MarketSignalTrackerTests
{
    private static MarketDataSnapshot Snap(decimal price, int bidDepth, int askDepth)
        => MarketDataSnapshot.Of(
            new Price(price),
            bidDepth > 0 ? new[] { L(price - 0.01m, bidDepth) } : Array.Empty<QuoteLevel>(),
            askDepth > 0 ? new[] { L(price + 0.01m, askDepth) } : Array.Empty<QuoteLevel>());

    private static QuoteLevel L(decimal price, int qty) => new(new Price(price), new Quantity(qty));

    [Fact]
    public void ColdStart_MomentumIsNull_BeforeHalfWindow()
    {
        // 样本不足 window/2 时 Momentum 应为 null(无法判断趋势)
        var t = new MarketSignalTracker(window: 20);
        for (int i = 0; i < 9; i++) t.RecordTick(Snap(10m + i * 0.01m, 100, 100));
        Assert.Null(t.Momentum);
    }

    [Fact]
    public void MonotonicRise_YieldsPositiveMomentum()
    {
        // 单调上涨序列:Momentum 必为正
        var t = new MarketSignalTracker(window: 10);
        for (int i = 0; i < 10; i++) t.RecordTick(Snap(10m + i * 0.1m, 100, 100));
        Assert.True(t.Momentum > 0.05m, $"单调上涨动量应为正且显著,实际{t.Momentum}");
    }

    [Fact]
    public void MonotonicFall_YieldsNegativeMomentum()
    {
        var t = new MarketSignalTracker(window: 10);
        for (int i = 0; i < 10; i++) t.RecordTick(Snap(11m - i * 0.1m, 100, 100));
        Assert.True(t.Momentum < -0.05m, $"单调下跌动量应为负,实际{t.Momentum}");
    }

    [Fact]
    public void BidHeavierThanAsk_PositiveDepthImbalance()
    {
        // 买盘深度持续厚于卖盘 → 失衡度为正
        var t = new MarketSignalTracker(window: 10);
        for (int i = 0; i < 10; i++) t.RecordTick(Snap(10m, bidDepth: 1000, askDepth: 200));
        Assert.True(t.BidAskDepthImbalance > 0.5m, $"买盘厚失衡应>0.5,实际{t.BidAskDepthImbalance}");
    }

    [Fact]
    public void AskHeavierThanBid_NegativeDepthImbalance()
    {
        var t = new MarketSignalTracker(window: 10);
        for (int i = 0; i < 10; i++) t.RecordTick(Snap(10m, bidDepth: 200, askDepth: 1000));
        Assert.True(t.BidAskDepthImbalance < -0.5m, $"卖盘厚失衡应<-0.5,实际{t.BidAskDepthImbalance}");
    }

    [Fact]
    public void RecordTrade_AccumulatesIntoCurrentTick_ThenResetsAfterTick()
    {
        // 成交量分桶:RecordTrade 累加到当前 tick,RecordTick 后入队并重置
        var t = new MarketSignalTracker(window: 10);
        t.RecordTrade(100);
        t.RecordTrade(50);
        t.RecordTick(Snap(10m, 100, 100));   // 本 tick 累计 150 入队
        Assert.Equal(150, t.RecentTradeVolume);

        t.RecordTick(Snap(10m, 100, 100));   // 本 tick 无成交
        // 窗口内两桶:[150, 0],累计仍 150
        Assert.Equal(150, t.RecentTradeVolume);
    }

    [Fact]
    public void IsAtHigh_TrueWhenPriceAboveAverageBy2Percent()
    {
        var t = new MarketSignalTracker(window: 10);
        // 前 9 tick 在 10 元附近,第 10 tick 拉到 10.5(均值≈10.05,10.5 > 均值×1.02)
        for (int i = 0; i < 9; i++) t.RecordTick(Snap(10m, 100, 100));
        t.RecordTick(Snap(10.5m, 100, 100));
        Assert.True(t.IsAtHigh, "价格显著高于近期均值应判定为高位");
    }

    [Fact]
    public void EmptySnapshot_DoesNotThrow()
    {
        // 无任何档位的空盘口也不应崩溃(LastPrice 兜底为 0)
        var t = new MarketSignalTracker(window: 10);
        var empty = MarketDataSnapshot.Of(null, Array.Empty<QuoteLevel>(), Array.Empty<QuoteLevel>());
        t.RecordTick(empty);
        Assert.Equal(1, t.TickCount);
    }
}
