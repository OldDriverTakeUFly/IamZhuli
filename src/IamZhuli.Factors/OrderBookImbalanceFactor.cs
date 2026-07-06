namespace IamZhuli.Factors;

/// <summary>
/// 订单簿失衡因子(Order Book Imbalance, OBI)。
/// 经典微观结构因子:衡量买卖盘挂单量的相对压力。
///
/// OBI = (ΣbidQty - ΣaskQty) / (ΣbidQty + ΣaskQty),取值 [-1, 1]。
///   正值→买盘挂单厚于卖盘(看多压力);负值→卖盘更厚(看空压力);0→均衡。
///
/// 与 <see cref="MarketSignalTracker.BidAskDepthImbalance"/> 的关系:
/// 后者是滚动窗口内的累计失衡(平滑过的信号),本因子是**当前快照**的瞬时失衡,
/// 且支持按档位远近线性衰减加权(越靠前的档位权重越高,反映"最近报价更重要")。
/// </summary>
public sealed class OrderBookImbalanceFactor
{
    private readonly int _levels;
    private readonly bool _weighted;

    /// <param name="levels">取前 N 档计算(快照档位不足时按实际档数)。</param>
    /// <param name="weighted">true=按档位远近线性衰减加权(第 i 档权重 = levels-i+1);false=等权。</param>
    public OrderBookImbalanceFactor(int levels = 5, bool weighted = false)
    {
        if (levels <= 0) throw new ArgumentOutOfRangeException(nameof(levels));
        _levels = levels;
        _weighted = weighted;
    }

    /// <summary>计算当前快照的 OBI。无挂单时返回 0(视为均衡,避免除零)。</summary>
    public decimal Compute(IMarketDataSnapshot snapshot)
    {
        var bids = TakeLevels(snapshot.BidLevels);
        var asks = TakeLevels(snapshot.AskLevels);

        decimal bidWeighted = WeightedSum(bids);
        decimal askWeighted = WeightedSum(asks);
        decimal denom = bidWeighted + askWeighted;
        return denom == 0 ? 0 : (bidWeighted - askWeighted) / denom;
    }

    private IEnumerable<decimal> TakeLevels(IReadOnlyList<QuoteLevel> ls)
        => ls.Take(_levels).Select(l => (decimal)l.Quantity.Value);

    private decimal WeightedSum(IEnumerable<decimal> qtys)
    {
        if (!_weighted) return qtys.Sum();

        decimal sum = 0; int i = 1;
        foreach (var q in qtys)
        {
            // 第 i 档权重 = _levels - i + 1(线性衰减)
            sum += q * (_levels - i + 1);
            i++;
        }
        return sum;
    }
}
