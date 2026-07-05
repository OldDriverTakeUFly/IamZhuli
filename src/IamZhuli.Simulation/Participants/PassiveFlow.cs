using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 被动资金流(底盘电流):每 tick 无视涨跌小额买入,模拟指数 ETF / 定投 / 养老金的细水长流。
/// 这是阴跌不死锁的关键——真实市场永远有一股"不看行情"的买盘兜底。
/// 资金充裕(聚合体,不跟踪盈亏),日买入量按流通盘的固定比例配额,不主导价格但保证底盘流动性。
/// </summary>
public sealed class PassiveFlow : IParticipant
{
    public ParticipantId Id { get; }
    private readonly TradingSession _session;
    private readonly Random _rng;

    /// <summary>每日买入配额(手)。默认流通盘的 0.1%,即日换手率的底盘。</summary>
    private readonly int _dailyBudget;
    private int _spentToday;

    /// <param name="floatShares">流通盘(手),用于推算日配额。</param>
    /// <param name="dailyRate">日买入占流通盘比例,默认 0.001(0.1%)。</param>
    public PassiveFlow(TradingSession session, ParticipantId id, int floatShares,
                       decimal dailyRate = 0.001m, int? seed = null)
    {
        Id = id;
        _session = session;
        _dailyBudget = Math.Max(20, (int)(floatShares * dailyRate));
        _rng = new Random(seed ?? Environment.TickCount);
        // 聚合被动资金:现金充裕,不会花完(30天最多买 floatShares×3% 的货)
        session.GetOrCreateAccount(id, 500_000_000m);
    }

    public void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        if (_spentToday >= _dailyBudget) return;
        // 每 tick 有 30% 概率做一笔被动买入(模拟全天零散的定投/ETF申赎)
        if (_rng.NextDouble() >= 0.30) return;

        int remaining = _dailyBudget - _spentToday;
        int qty = Math.Min(remaining, _rng.Next(1, 4));   // 1~3 手/笔
        if (qty <= 0) return;

        try
        {
            // 市价买入:确保成交、产生 OnTrade(唤醒其他参与者)
            // 量极小(1~3手),价格冲击可忽略,但能吃掉卖一档、阻止价格塌缩
            session.Submit(new OrderRequest(Id, Side.Buy, OrderType.Market, Price.Zero, new Quantity(qty)));
            _spentToday += qty;
        }
        catch { /* 盘口异常(如涨停封死)忽略 */ }
    }

    public void OnNewDay() => _spentToday = 0;
}
