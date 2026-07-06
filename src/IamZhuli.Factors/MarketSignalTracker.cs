using IamZhuli.Core;

namespace IamZhuli.Factors;

/// <summary>
/// 近期市场数据滚动跟踪(价格、深度、成交量序列)。
/// 从 IamZhuli.Simulation.AI.RecentMarketTracker 迁移而来,逻辑保持一致;
/// 唯一变化:输入由散落的 Price?/int 参数改为 <see cref="IMarketDataSnapshot"/>,
/// 从而脱离模拟器的撮合引擎/会话依赖。
///
/// 成交量按 tick 分桶:<see cref="RecordTrade"/> 累加到当前桶,<see cref="RecordTick"/> 推进桶。
/// </summary>
public sealed class MarketSignalTracker
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

    public MarketSignalTracker(int window = 30) => _window = window;

    public int TickCount => _prices.Count;

    /// <summary>近 window 内涨跌幅;样本不足(window/2)返回 null。</summary>
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

    /// <summary>记录一笔成交(累加到当前 tick 桶)。</summary>
    public void RecordTrade(int qty) => _currentTickVol += qty;

    /// <summary>
    /// 每个 tick 推进一次:从快照取价/深度,落进滚动窗口。
    /// 深度 = 各档挂单量之和(模拟器侧通常传 5 档)。
    /// </summary>
    public void RecordTick(IMarketDataSnapshot snapshot)
    {
        decimal p = snapshot.LastPrice?.Value ?? snapshot.BestBid?.Value ?? snapshot.BestAsk?.Value ?? 0;
        _prices.Enqueue(p);
        while (_prices.Count > _window) _prices.Dequeue();

        int bidDepth = snapshot.BidLevels.Sum(l => l.Quantity.Value);
        int askDepth = snapshot.AskLevels.Sum(l => l.Quantity.Value);
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
