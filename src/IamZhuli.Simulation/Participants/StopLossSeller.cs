using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 止损盘。价格跌破散户平均持仓成本 -X%→恐慌卖出。
/// 是洗盘的燃料:主力打压时,止损盘涌出加速下跌。
/// 关键:只能卖"历史持仓"(T+1 已由 Account 的 Available 保证),当天追涨买入的跑不掉。
/// 触发:现价 < 散户成本 × (1 - 止损线),且散户有可卖持仓。
/// </summary>
public sealed class StopLossSeller : RetailCrowd
{
    public override ParticipantId Id { get; }
    private readonly decimal _stopRatio;   // 跌破成本多少止损(如 0.07 = 跌7%)

    public StopLossSeller(Account account, SharedRetailState state, ParticipantId id,
                          Price intrinsicValue, int strength, decimal stopRatio = 0.07m)
        : base(account, state, intrinsicValue, strength)
    {
        Id = id; _stopRatio = stopRatio;
    }

    public override void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        var v = ViewOf(session, clock);
        if (v.LastPrice is not { } price) return;
        if (State.AverageCost.Value <= 0) return;          // 散户没持仓,没东西止损
        if (Account.Position.Available.Value <= 0) return; // 可卖的货

        decimal stopLine = State.AverageCost.Value * (1 - _stopRatio);
        if (price.Value >= stopLine) return;   // 还没破止损线

        // 恐慌程度:用相对成本的跌幅衡量(跌得越深越恐慌)
        decimal lossRatio = (State.AverageCost.Value - price.Value) / State.AverageCost.Value;
        double intensity = Math.Min(1.0, (double)lossRatio * 8) * Activity;
        // 跌破止损线后基础概率提升(已恐慌),随跌幅递增
        if (rng.NextDouble() < intensity * 0.5 + 0.1)
        {
            // 止损量不超过可卖持仓的一部分(恐慌但非一次性清仓)
            int maxSell = Math.Min(Account.Position.Available.Value,
                                   (int)(Strength * intensity * (0.4 + rng.NextDouble() * 0.6)));
            maxSell = Math.Max(0, (maxSell / 10) * 10);
            if (maxSell <= 0) return;
            // 挂在买一或更低(急于脱手,愿意低价卖)
            var sellPrice = v.BestBid is { } b
                ? new Price(Math.Max(b.Value - 0.01m, v.LowerLimit))
                : new Price(price.Value);
            TryLimitSell(session, sellPrice, maxSell);
        }
    }
}
