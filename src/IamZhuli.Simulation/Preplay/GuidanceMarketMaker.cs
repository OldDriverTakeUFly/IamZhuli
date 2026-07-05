using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.MarketData;
using IamZhuli.Simulation.Participants;
using IamZhuli.Simulation.Scenarios;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Preplay;

/// <summary>
/// 预演专用引导做市商。按K线剧本的目标价锚定现价,并同步给采集器。
/// </summary>
public sealed class GuidanceMarketMaker : IParticipant
{
    public ParticipantId Id { get; }
    private readonly MarketScenario _scenario;
    private readonly List<decimal> _dailyTargets;
    private readonly Account _account;
    private readonly MarketDataCollector? _collector;

    public GuidanceMarketMaker(TradingSession session, ParticipantId id, MarketScenario scenario,
                               MarketDataCollector? collector = null)
    {
        Id = id;
        _scenario = scenario;
        _dailyTargets = scenario.DailyTargets();
        _account = session.GetOrCreateAccount(id, 100_000_000_000m);
        _account.Position.Seed(new Quantity(500000), scenario.StartPrice);
        _collector = collector;
    }

    public void Act(TradingSession session, SimulationClock clock, Random rng)
    {
        int dayIdx = Math.Min(clock.CurrentDay - 1, _dailyTargets.Count - 1);
        if (dayIdx < 0 || dayIdx >= _dailyTargets.Count) return;
        decimal target = _dailyTargets[dayIdx];

        decimal noise = (decimal)(rng.NextDouble() - 0.5) * 0.04m;
        var price = new Price(Math.Round(target + noise, 2));
        session.Engine.SetLastPrice(price);

        decimal spread = 0.02m;
        int depth = 2000;
        TryPlace(session, Side.Buy, Math.Round(target - spread, 2), depth);
        TryPlace(session, Side.Sell, Math.Round(target + spread, 2), depth);
        TryPlace(session, Side.Buy, Math.Round(target - spread * 2, 2), depth);
        TryPlace(session, Side.Sell, Math.Round(target + spread * 2, 2), depth);

        // 同步给采集器:价格 + 模拟成交量(预演快速跑,用估算量填充,让K线有真实的量)
        int simVolume = 2000 + rng.Next(0, 4000);   // 每 tick ~2000~6000 手(模拟真实交易量)
        _collector?.SetPriceForPreplay(price, simVolume);
    }

    private void TryPlace(TradingSession s, Side side, decimal price, int qty)
    {
        try { s.Submit(new OrderRequest(Id, side, OrderType.Limit, new Price(price), new Quantity(qty))); }
        catch { }
    }

    public void OnNewDay() { }
}

