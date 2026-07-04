using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 跟风客(追涨杀跌)。现价短期快速上涨→追涨买入;短期急跌→恐慌卖出(但不占主导,主跌靠止损盘)。
/// 是拉升时的顺风、出货时的接盘侠。
/// 触发:近 window 涨幅 > 阈值(如 +1.5%)→ 按 BestAsk 附近买入;涨幅越大买得越多。
/// </summary>
public sealed class MomentumChaser : RetailCrowd
{
    public override ParticipantId Id { get; }
    private readonly decimal _chaseThreshold;   // 触发追涨的涨幅阈值

    public MomentumChaser(Account account, SharedRetailState state, ParticipantId id,
                          Price intrinsicValue, int strength, decimal chaseThreshold = 0.015m)
        : base(account, state, intrinsicValue, strength)
    {
        Id = id; _chaseThreshold = chaseThreshold;
    }

    public override void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        var v = ViewOf(session, clock);
        if (v.LastPrice is not { } price) return;
        if (v.TickOfDay < 2) return;   // 开盘前几个 tick 不行动,等行情走出来

        var mom = State.Momentum;
        if (mom is not { } m) return;

        // —— 追涨:涨幅超阈值,按卖一附近买入 ——
        if (m > _chaseThreshold && v.BestAsk is { } ask)
        {
            // 活跃度 × 力量 × 涨幅放大 → 下单量
            double intensity = Math.Min(1.0, (double)(m / _chaseThreshold) - 1) * Activity;
            if (rng.NextDouble() < intensity * 0.5)   // 概率性下单,避免每 tick 都买
            {
                int qty = (int)(Strength * intensity * (0.3 + rng.NextDouble() * 0.7));
                qty = Math.Max(10, RoundToLot(qty));
                // 挂在卖一或微高于卖一,确保能成交(追涨急切)
                var buyPrice = new Price(Math.Min(ask.Value + 0.02m, v.UpperLimit));
                TryLimitBuy(session, buyPrice, qty);
            }
        }
        // —— 杀跌:急跌时少量恐慌卖出(主力杀跌由 StopLoss 负责) ——
        else if (m < -_chaseThreshold * 1.5m && v.BestBid is { } bid)
        {
            double intensity = Math.Min(1.0, (double)(-m / _chaseThreshold) - 1) * Activity * 0.4;
            if (rng.NextDouble() < intensity * 0.3)
            {
                int qty = (int)(Strength * intensity * (0.3 + rng.NextDouble() * 0.7));
                qty = Math.Max(10, RoundToLot(qty));
                var sellPrice = new Price(Math.Max(bid.Value - 0.02m, v.LowerLimit));
                TryLimitSell(session, sellPrice, qty);
            }
        }
    }

    private static int RoundToLot(int n) => (n / 10) * 10;   // 凑整十手
}
