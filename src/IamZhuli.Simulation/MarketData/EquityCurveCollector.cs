using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.MarketData;

/// <summary>权益曲线采集器。订阅每tick/每日,记录玩家/AI/机构B的权益序列。
/// 供积分系统算最大回撤/波动率/三方排名。</summary>
public sealed class EquityCurveCollector
{
    private readonly Account _player;
    private readonly Func<Account?> _aiAccount;
    private readonly Func<Account?> _instBAccount;
    private readonly Func<Price?> _markPrice;
    private readonly List<decimal> _playerEquity = new();
    private readonly List<decimal> _aiEquity = new();
    private readonly List<decimal> _instBEquity = new();

    public IReadOnlyList<decimal> PlayerEquity => _playerEquity;
    public IReadOnlyList<decimal> AiEquity => _aiEquity;
    public IReadOnlyList<decimal> InstBEquity => _instBEquity;

    public EquityCurveCollector(SimulationLoop loop, Account player,
        Func<Account?> aiAccount, Func<Account?> instBAccount, Func<Price?> markPrice)
    {
        _player = player;
        _aiAccount = aiAccount;
        _instBAccount = instBAccount;
        _markPrice = markPrice;
        loop.OnTick += _ => RecordSnapshot();
    }

    private void RecordSnapshot()
    {
        var mark = _markPrice() ?? new Price(10m);
        _playerEquity.Add(_player.TotalEquity(mark));
        var ai = _aiAccount();
        _aiEquity.Add(ai?.TotalEquity(mark) ?? 0);
        var instB = _instBAccount();
        _instBEquity.Add(instB?.TotalEquity(mark) ?? 0);
    }

    /// <summary>计算最大回撤(0~1,0=无回撤,越大越糟)。</summary>
    public static decimal MaxDrawdown(IReadOnlyList<decimal> equity)
    {
        if (equity.Count < 2) return 0;
        decimal peak = equity[0], maxDd = 0;
        foreach (var v in equity)
        {
            if (v > peak) peak = v;
            decimal dd = peak > 0 ? (peak - v) / peak : 0;
            if (dd > maxDd) maxDd = dd;
        }
        return maxDd;
    }

    /// <summary>计算波动率(权益序列的标准差/均值)。</summary>
    public static decimal Volatility(IReadOnlyList<decimal> equity)
    {
        if (equity.Count < 2) return 0;
        decimal mean = equity.Average();
        if (mean == 0) return 0;
        decimal sumSq = equity.Sum(v => (v - mean) * (v - mean));
        return (decimal)Math.Sqrt((double)(sumSq / equity.Count)) / mean;
    }
}
