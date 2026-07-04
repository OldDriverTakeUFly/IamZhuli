using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 价投。价格偏离内在价值时逆向操作:远低于价值→买入,远高于价值→卖出。
/// 提供市场稳定性,节奏慢但持续。是"理性"的对手:主力非理性拉升到高位时,价投会逢高止盈。
/// 触发:偏离度超过阈值(如 ±8%)→ 逆向挂单。
/// </summary>
public sealed class ValueInvestor : RetailCrowd
{
    public override ParticipantId Id { get; }
    private readonly decimal _deviationThreshold;   // 偏离多少才行动(如 0.08)

    public ValueInvestor(Account account, SharedRetailState state, ParticipantId id,
                         Price intrinsicValue, int strength, decimal deviationThreshold = 0.08m)
        : base(account, state, intrinsicValue, strength)
    {
        Id = id; _deviationThreshold = deviationThreshold;
    }

    public override void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        var v = ViewOf(session, clock);
        if (v.LastPrice is not { } price) return;
        if (IntrinsicValue.Value <= 0) return;

        decimal deviation = (price.Value - IntrinsicValue.Value) / IntrinsicValue.Value;

        // 价投:偏离越远越果断;基础节奏慢但严重偏离时提高概率
        double magnitude = Math.Abs((double)deviation);
        double actProb = Activity * Math.Min(0.6, magnitude * 3);

        if (deviation < -_deviationThreshold && rng.NextDouble() < actProb)
        {
            // 低估:逢低买入
            int qty = (int)(Strength * 0.4 * (0.5 + rng.NextDouble()));
            qty = Math.Max(10, (qty / 10) * 10);
            var buyPrice = v.BestAsk is { } a ? new Price(a.Value) : new Price(price.Value);
            TryLimitBuy(session, buyPrice, qty);
        }
        else if (deviation > _deviationThreshold && rng.NextDouble() < actProb
                 && Account.Position.Available.Value > 0)
        {
            // 高估:逢高卖出(需有持仓)
            int qty = Math.Min(Account.Position.Available.Value,
                               (int)(Strength * 0.4 * (0.5 + rng.NextDouble())));
            qty = Math.Max(10, (qty / 10) * 10);
            var sellPrice = v.BestBid is { } b ? new Price(b.Value) : new Price(price.Value);
            TryLimitSell(session, sellPrice, qty);
        }
    }
}
