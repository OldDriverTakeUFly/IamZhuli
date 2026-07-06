using IamZhuli.Core;

namespace IamZhuli.Factors;

/// <summary>
/// 成交量加权平均价(VWAP)因子及其价格偏离度。
/// VWAP = Σ(成交价 × 成交量) / Σ(成交量),是机构/算法交易的核心基准价。
///
/// 用法(典型反转/锚定因子):
///   - 价格远高于 VWAP → 短期超买,有回归压力(反转卖出信号)。
///   - 价格远低于 VWAP → 短期超卖,有反弹可能(反转买入信号)。
///
/// 数据源无关:成交明细由 <see cref="RecordTrade"/> 逐笔喂入,
/// <see cref="OnTick"/> 推进窗口桶(沿用 MarketSignalTracker 的 tick 分桶模型),
/// 与模拟器的 OnTrade 事件或真实行情的逐笔推送均兼容。
/// </summary>
public sealed class VwapFactor
{
    private readonly int _window;
    private readonly Queue<decimal> _turnover = new();   // 每个 tick 的成交额(价×量)
    private readonly Queue<int> _volume = new();          // 每个 tick 的成交量
    private decimal _currentTurnover;
    private int _currentVolume;

    public VwapFactor(int window = 30)
    {
        if (window <= 0) throw new ArgumentOutOfRangeException(nameof(window));
        _window = window;
    }

    /// <summary>记录一笔成交(累加到当前 tick 桶)。</summary>
    public void RecordTrade(Price price, int qty)
    {
        _currentTurnover += price.Value * qty;
        _currentVolume += qty;
    }

    /// <summary>推进一个 tick:把当前桶入队,重置当前桶。</summary>
    public void OnTick()
    {
        _turnover.Enqueue(_currentTurnover);
        _volume.Enqueue(_currentVolume);
        while (_turnover.Count > _window) _turnover.Dequeue();
        while (_volume.Count > _window) _volume.Dequeue();
        _currentTurnover = 0;
        _currentVolume = 0;
    }

    /// <summary>近 window 的 VWAP;窗口内无成交(量=0)时返回 null。</summary>
    public decimal? Vwap
    {
        get
        {
            decimal sumTurnover = _turnover.Sum();
            int sumVolume = _volume.Sum();
            return sumVolume == 0 ? null : sumTurnover / sumVolume;
        }
    }

    /// <summary>当前价相对 VWAP 的偏离度:(last - vwap) / vwap。无 VWAP 或无现价时返回 null。</summary>
    public decimal? Deviation(IMarketDataSnapshot snapshot)
    {
        var vwap = Vwap;
        if (vwap is not { } v) return null;
        decimal price = snapshot.LastPrice?.Value ?? snapshot.BestBid?.Value ?? snapshot.BestAsk?.Value ?? 0;
        if (v == 0 || price == 0) return null;
        return (price - v) / v;
    }
}
