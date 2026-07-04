using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 持续做市商。每 tick 在现价附近维护窄价差盘口(挂买卖单),提供流动性。
/// 解决"初始挂单被吃光后盘口枯竭"问题——真实做市商就是持续双边挂单。
/// 不追求盈利,目的是让市场始终有深度、价格能连续变动。
/// </summary>
public sealed class MarketMaker : IParticipant
{
    public ParticipantId Id { get; }
    private readonly Account _account;
    private readonly Price _intrinsic;
    private readonly Random _rng;
    /// <summary>每档挂单量(手)。</summary>
    private readonly int _depthPerLevel;
    /// <summary>维护的档位数。</summary>
    private readonly int _levels;

    public MarketMaker(TradingSession session, ParticipantId id, Price intrinsicValue,
                       int initialHolding, int depthPerLevel = 300, int levels = 5, int? seed = null)
    {
        Id = id;
        _intrinsic = intrinsicValue;
        _account = session.GetOrCreateAccount(id, 5_000_000_000m);
        if (initialHolding > 0) _account.Position.Seed(new Quantity(initialHolding), intrinsicValue);
        _depthPerLevel = depthPerLevel;
        _levels = levels;
        _rng = new Random(seed ?? 7);
    }

    public void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        var view = session.Engine.View;
        decimal price = view.LastPrice?.Value ?? view.BestBid?.Value ?? view.BestAsk?.Value ?? _intrinsic.Value;
        // 每 tick 补挂:在现价上下各 _levels 档挂单(若该档深度不足)
        // 卖盘
        for (int i = 1; i <= _levels; i++)
        {
            decimal askPrice = Align(price + i * 0.01m);
            if (!LevelHasQty(view.TopAsks(_levels), askPrice, _depthPerLevel))
                TryPlace(session, Side.Sell, askPrice);
        }
        // 买盘
        for (int i = 1; i <= _levels; i++)
        {
            decimal bidPrice = Align(price - i * 0.01m);
            if (bidPrice < session.Engine.Rules.LowerLimit.Value) break;
            if (!LevelHasQty(view.TopBids(_levels), bidPrice, _depthPerLevel))
                TryPlace(session, Side.Buy, bidPrice);
        }
    }

    private bool LevelHasQty(IReadOnlyList<(Price Price, Quantity TotalQty)> levels, decimal target, int threshold)
    {
        foreach (var (p, q) in levels)
            if (Math.Abs(p.Value - target) < 0.005m) return q.Value >= threshold / 2;
        return false;
    }

    private void TryPlace(TradingSession s, Side side, decimal price)
    {
        try { s.Submit(new OrderRequest(Id, side, OrderType.Limit, new Price(price), new Quantity(_depthPerLevel))); }
        catch { /* 资金/持仓不足忽略 */ }
    }

    private static decimal Align(decimal v) => Math.Round(Math.Round(v / 0.01m) * 0.01m, 2);

    public void OnNewDay() { }
}
