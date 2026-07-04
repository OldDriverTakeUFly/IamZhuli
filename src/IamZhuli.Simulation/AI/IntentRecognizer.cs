using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Sessions;

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
/// </summary>
public sealed class IntentRecognizer
{
    private readonly RecentMarketTracker _tracker;

    public IntentRecognizer(int window = 30)
    {
        _tracker = new RecentMarketTracker(window);
    }

    public RecentMarketTracker Tracker => _tracker;

    /// <summary>记录一笔成交(由 AIMainForce 订阅 Session.OnTrade 转发)。</summary>
    public void RecordTrade(int qty) => _tracker.RecordTrade(qty);

    /// <summary>每 tick 更新市场数据(由 AIMainForce.Act 调用)。</summary>
    public void Observe(TradingSession session)
    {
        var view = session.Engine.View;
        _tracker.RecordTick(
            lastPrice: view.LastPrice,
            bestBid: view.BestBid,
            bestAsk: view.BestAsk,
            bidDepth: SumDepth(view.TopBids(5)),
            askDepth: SumDepth(view.TopAsks(5)));
    }

    private static int SumDepth(IReadOnlyList<(Price Price, Quantity TotalQty)> levels)
        => levels.Sum(l => l.TotalQty.Value);

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

/// <summary>近期市场数据跟踪(价格、深度、成交量序列)。
/// 成交量按 tick 分桶:RecordTrade 累加到当前桶,RecordTick 推进桶。</summary>
public sealed class RecentMarketTracker
{
    private readonly Queue<decimal> _prices = new();
    private readonly Queue<int> _bidDepths = new();
    private readonly Queue<int> _askDepths = new();
    private readonly Queue<int> _volPerTick = new();   // 每 tick 的成交量桶
    private readonly int _window;
    private int _currentTickVol;   // 当前 tick 累计的成交量
    private decimal _recentLow = decimal.MaxValue;
    private bool _wasDropRecently;
    private int _dropCooldown;

    public RecentMarketTracker(int window) => _window = window;

    public int TickCount => _prices.Count;
    public decimal? Momentum => _prices.Count >= _window / 2
        ? (_prices.Count == 0 ? 0 : (_prices.Last() - _prices.First()) / Math.Max(_prices.First(), 0.01m))
        : null;

    /// <summary>近期(近 N tick)累计成交量。</summary>
    public int RecentTradeVolume => _volPerTick.Sum();
    /// <summary>历史平均每 tick 成交量。</summary>
    public double AvgVolume => _volPerTick.Count > 0 ? _volPerTick.Average() : 0;
    /// <summary>买卖盘深度失衡度:(买-卖)/(买+卖),正=买盘厚。</summary>
    public decimal BidAskDepthImbalance { get; private set; }
    public bool IsAtHigh { get; private set; }
    public bool WasRecentDrop => _wasDropRecently;

    /// <summary>记录一笔成交(累加到当前 tick 桶)。由 AIMainForce 订阅 OnTrade 调用。</summary>
    public void RecordTrade(int qty) => _currentTickVol += qty;

    public void RecordTick(Price? lastPrice, Price? bestBid, Price? bestAsk, int bidDepth, int askDepth)
    {
        decimal p = lastPrice?.Value ?? bestBid?.Value ?? bestAsk?.Value ?? 0;
        _prices.Enqueue(p);
        while (_prices.Count > _window) _prices.Dequeue();
        _bidDepths.Enqueue(bidDepth);
        _askDepths.Enqueue(askDepth);
        while (_bidDepths.Count > _window) _bidDepths.Dequeue();
        while (_askDepths.Count > _window) _askDepths.Dequeue();

        // 深度失衡
        int sumBid = _bidDepths.Sum(), sumAsk = _askDepths.Sum();
        BidAskDepthImbalance = (sumBid + sumAsk) == 0 ? 0 : (decimal)(sumBid - sumAsk) / (sumBid + sumAsk);

        // 成交量桶:把当前 tick 累计的成交量入队,重置当前桶
        _volPerTick.Enqueue(_currentTickVol);
        while (_volPerTick.Count > _window) _volPerTick.Dequeue();
        _currentTickVol = 0;

        // 高位判断:当前价高于近 window 均值的 102%
        if (_prices.Count >= _window / 2)
        {
            decimal avg = _prices.Average();
            IsAtHigh = p > avg * 1.02m;
            // V形:记录是否近期有过下跌
            if (p < _recentLow) _recentLow = p;
            if (_dropCooldown > 0) _dropCooldown--;
            if (p < avg * 0.99m) { _wasDropRecently = true; _dropCooldown = 10; }
            else if (_dropCooldown == 0) _wasDropRecently = false;
            if (p > avg) _recentLow = decimal.MaxValue;
        }
    }
}
