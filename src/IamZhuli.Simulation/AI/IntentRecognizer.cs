using IamZhuli.Core;
using IamZhuli.Factors;

namespace IamZhuli.Simulation.AI;

/// <summary>识别到的玩家意图类型。</summary>
public enum PlayerIntent { None, Accumulating, PushingUp, WashTrading, Spoofing, Distributing }

/// <summary>
/// 意图识别结果。各意图的置信度 0~1。
/// </summary>
public sealed class IntentAssessment
{
    public PlayerIntent Primary { get; init; } = PlayerIntent.None;
    public double Confidence { get; init; }
    /// <summary>识别理由(供复盘内心独白)。</summary>
    public string Reason { get; init; } = "";
    /// <summary>近 window 成交量(用于判断是否放量)。</summary>
    public int RecentVolume { get; init; }
    /// <summary>近 window 价格涨幅(正=涨)。</summary>
    public decimal Momentum { get; init; }
}

/// <summary>
/// 意图识别器:监测盘口信号,推测"大资金"(玩家)在干什么。
/// AI 像真人一样只能从盘口行为推断,不能直接读玩家账户。
///
/// 已重构:信号计算下沉到 IamZhuli.Factors.MarketSignalTracker,本类只保留
/// "主力意图"这一模拟器专有的语义判定,并通过 IMarketDataSnapshot 与数据源解耦。
/// </summary>
public sealed class IntentRecognizer
{
    private readonly MarketSignalTracker _tracker;

    public IntentRecognizer(int window = 30)
    {
        _tracker = new MarketSignalTracker(window);
    }

    public MarketSignalTracker Tracker => _tracker;

    /// <summary>记录一笔成交(由 AIMainForce 订阅 Session.OnTrade 转发)。</summary>
    public void RecordTrade(int qty) => _tracker.RecordTrade(qty);

    /// <summary>每 tick 更新市场数据。传入快照(模拟器由 SessionMarketDataSnapshot 适配,真实行情同理)。</summary>
    public void Observe(IMarketDataSnapshot snapshot) => _tracker.RecordTick(snapshot);

    /// <summary>评估当前玩家意图。</summary>
    public IntentAssessment Assess()
    {
        var m = _tracker;
        if (m.TickCount < 5) return new IntentAssessment();

        decimal momentum = m.Momentum ?? 0m;
        int recentVol = m.RecentTradeVolume;
        bool volumeSpike = recentVol > m.AvgVolume * 2.0 && m.AvgVolume > 0;
        bool priceSpike = momentum > 0.015m;        // 短期涨超1.5%
        bool priceDrop = momentum < -0.015m;

        // —— 推价:放量 + 上涨 ——
        if (priceSpike && volumeSpike)
            return new IntentAssessment { Primary = PlayerIntent.PushingUp, Confidence = 0.8,
                Reason = $"放量拉升:涨幅{momentum:P1} 成交{recentVol}手(均值{m.AvgVolume})", RecentVolume = recentVol, Momentum = momentum };

        // —— 持续吸筹:温和上涨 + 买盘深度持续厚于卖盘 ——
        if (momentum > 0.003m && m.BidAskDepthImbalance > 0.25m && m.TickCount > 15)
            return new IntentAssessment { Primary = PlayerIntent.Accumulating, Confidence = 0.6,
                Reason = $"疑似吸筹:温和上涨{momentum:P1} 买盘持续厚于卖盘(失衡{m.BidAskDepthImbalance:P0})",
                RecentVolume = recentVol, Momentum = momentum };

        // —— 出货:高位放量但价格不涨(甚至微跌) ——
        if (volumeSpike && Math.Abs(momentum) < 0.005m && m.IsAtHigh)
            return new IntentAssessment { Primary = PlayerIntent.Distributing, Confidence = 0.55,
                Reason = $"疑似出货:放量但价格滞涨(在高位,成交量{recentVol})", RecentVolume = recentVol, Momentum = momentum };

        // —— 洗盘:急跌后快速收回(V 形) ——
        if (m.WasRecentDrop && momentum > 0.005m)
            return new IntentAssessment { Primary = PlayerIntent.WashTrading, Confidence = 0.5,
                Reason = "疑似洗盘:急跌后快速收回(V形)", RecentVolume = recentVol, Momentum = momentum };

        return new IntentAssessment { Primary = PlayerIntent.None, Confidence = 0,
            Reason = "无明显大资金行为", RecentVolume = recentVol, Momentum = momentum };
    }
}
