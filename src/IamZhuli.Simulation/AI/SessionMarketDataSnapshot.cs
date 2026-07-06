using IamZhuli.Core;
using IamZhuli.Factors;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.AI;

/// <summary>
/// 把 <see cref="TradingSession"/> 的盘口适配成 <see cref="IMarketDataSnapshot"/>。
/// 这是模拟器侧唯一的"数据源胶水":Factors 模块通过它读盘口,自身不依赖引擎/会话。
/// 将来接真实行情时,写一个 RealtimeMarketDataSnapshot 实现同一接口即可,Factors 零改动。
///
/// 惰性求值:每次属性访问都现读引擎盘口,保证取到最新状态。
/// </summary>
internal sealed class SessionMarketDataSnapshot : IMarketDataSnapshot
{
    private const int Depth = 5;
    private readonly TradingSession _session;

    public SessionMarketDataSnapshot(TradingSession session) => _session = session;

    public Price? LastPrice => _session.Engine.View.LastPrice;
    public Price? BestBid => _session.Engine.View.BestBid;
    public Price? BestAsk => _session.Engine.View.BestAsk;

    public IReadOnlyList<QuoteLevel> BidLevels
        => _session.Engine.View.TopBids(Depth)
            .Select(t => new QuoteLevel(t.Price, t.TotalQty))
            .ToList();

    public IReadOnlyList<QuoteLevel> AskLevels
        => _session.Engine.View.TopAsks(Depth)
            .Select(t => new QuoteLevel(t.Price, t.TotalQty))
            .ToList();
}
