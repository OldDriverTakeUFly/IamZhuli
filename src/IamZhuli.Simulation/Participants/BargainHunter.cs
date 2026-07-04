using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 抄底盘。价格跌破"便宜区"(内在价值以下某幅度,或破散户平均成本)→逢低买入。
/// 提供下方承接,是下跌缓冲、也是拉升阻力。
/// 触发:现价 < 内在价值 × (1 - 折让) → 在现价/买一附近挂买单。
/// </summary>
public sealed class BargainHunter : RetailCrowd
{
    public override ParticipantId Id { get; }
    private readonly decimal _discount;   // 低于内在价值多少算"便宜"(如 0.05 = 低5%)

    public BargainHunter(Account account, SharedRetailState state, ParticipantId id,
                         Price intrinsicValue, int strength, decimal discount = 0.05m)
        : base(account, state, intrinsicValue, strength)
    {
        Id = id; _discount = discount;
    }

    public override void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        var v = ViewOf(session, clock);
        if (v.LastPrice is not { } price) return;

        decimal cheapLine = IntrinsicValue.Value * (1 - _discount);
        // 破散户成本也算便宜(散户被套后想摊薄成本)
        if (State.AverageCost.Value > 0)
            cheapLine = Math.Min(cheapLine, State.AverageCost.Value * (1 - _discount));

        if (price.Value >= cheapLine) return;   // 不够便宜,不抄底

        // 越便宜越积极抄
        decimal depth = (cheapLine - price.Value) / cheapLine;   // 跌得有多深
        double intensity = Math.Min(1.0, (double)depth * 10) * Activity;
        if (rng.NextDouble() < intensity * 0.4)
        {
            int qty = (int)(Strength * intensity * (0.5 + rng.NextDouble()));
            qty = Math.Max(10, (qty / 10) * 10);
            // 挂在现价或买一上方一点(逢低接,愿意稍高一点买入)
            var buyPrice = v.BestBid is { } b
                ? new Price(Math.Min(b.Value + 0.01m, price.Value))
                : new Price(price.Value);
            TryLimitBuy(session, buyPrice, qty);
        }
    }
}
