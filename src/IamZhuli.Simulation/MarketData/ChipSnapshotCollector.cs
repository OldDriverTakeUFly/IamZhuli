using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.MarketData;

/// <summary>单个价位区间的筹码量。</summary>
public sealed record PriceBandChip(decimal PriceLow, decimal PriceHigh, int Quantity);

/// <summary>某日收盘时的筹码分布(筹码峰):按价格区间分桶的筹码总量。
/// 不记录"谁持有"(保持真实,主力持仓不可见),只记录"哪些价位有多少筹码"。</summary>
public sealed record DayChipDistribution(
    int Day,
    decimal ClosePrice,
    int TotalQuantity,
    List<PriceBandChip> Bands);

/// <summary>
/// 筹码分布采集器。日终时遍历所有持仓账户,按各账户的加权成本价把筹码归入价位桶,
/// 形成"筹码峰"分布。真实市场里筹码分布就是按成本价聚合的——
/// 谁在什么价位买了多少,才是主力判断支撑/压力的依据。
/// </summary>
public sealed class ChipSnapshotCollector
{
    private readonly SimulationLoop _loop;
    private readonly TradingSession _session;
    private readonly List<DayChipDistribution> _history = new();

    /// <summary>价位桶宽度(元)。如0.2元一档,10.0~10.2归一桶。</summary>
    private readonly decimal _bandWidth;

    public IReadOnlyList<DayChipDistribution> History => _history;

    public ChipSnapshotCollector(SimulationLoop loop, TradingSession session, decimal bandWidth = 0.01m)
    {
        _loop = loop;
        _session = session;
        _bandWidth = bandWidth;
        loop.OnDayFinalized += OnDayFinalized;
    }

    /// <summary>导入预演期间的逐日筹码历史(day 重编为负数,与历史K线对齐)。</summary>
    public void ImportHistory(IReadOnlyList<DayChipDistribution> preplayHistory)
    {
        int n = preplayHistory.Count;
        for (int i = 0; i < n; i++)
        {
            var d = preplayHistory[i];
            // 预演 day 1~N → -N~-1(与 PreloadHistory 的K线编号一致)
            _history.Add(d with { Day = i - n });
        }
    }

    /// <summary>手动采集当前所有账户的筹码分布(用于盘前/预演结束后的基线快照)。</summary>
    public void SnapshotNow(int day)
    {
        var price = _session.Engine.LastPrice?.Value ?? 0m;
        CollectInternal(day, price);
    }

    private void CollectInternal(int day, decimal closePrice)
    {
        var buckets = new Dictionary<decimal, int>();
        foreach (var acc in _session.AllAccounts)
        {
            int qty = acc.Position.Total.Value;
            if (qty <= 0) continue;
            decimal cost = acc.Position.AverageCost.Value;
            if (cost <= 0) continue;
            decimal bandLow = Math.Floor(cost / _bandWidth) * _bandWidth;
            buckets.TryGetValue(bandLow, out int existing);
            buckets[bandLow] = existing + qty;
        }
        var bands = buckets
            .OrderBy(kv => kv.Key)
            .Select(kv => new PriceBandChip(
                Math.Round(kv.Key, 2),
                Math.Round(kv.Key + _bandWidth, 2),
                kv.Value))
            .ToList();
        int total = bands.Sum(b => b.Quantity);
        _history.Add(new DayChipDistribution(day, Math.Round(closePrice, 2), total, bands));
    }

    private void OnDayFinalized(int day)
    {
        var price = _session.Engine.LastPrice?.Value ?? 0m;
        CollectInternal(day, price);
    }

    /// <summary>筹码集中度:最高峰所在桶的筹码占比(0~1)。峰越尖=筹码越集中。</summary>
    public decimal PeakConcentration(int dayIndex)
    {
        if (dayIndex < 0 || dayIndex >= _history.Count) return 0;
        var snap = _history[dayIndex];
        if (snap.TotalQuantity <= 0) return 0;
        int maxBand = snap.Bands.Count > 0 ? snap.Bands.Max(b => b.Quantity) : 0;
        return (decimal)maxBand / snap.TotalQuantity;
    }
}
