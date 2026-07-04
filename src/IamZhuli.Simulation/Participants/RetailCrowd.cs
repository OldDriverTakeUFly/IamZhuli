using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;
using IamZhuli.Simulation.Time;

namespace IamZhuli.Simulation.Participants;

/// <summary>
/// 散户群体基类。4 个子群体(跟风/抄底/止损/价投)共享:
/// - 同一个散户账户(ParticipantId "散户"),持仓和资金共用
/// - 散户整体平均持仓成本(止损/抄底的决策基准)
/// - 各自的活跃度(受消息面调节,M3 暂用固定值)
/// 行为差异在 Act 中实现。每个 tick 各群体独立决策、各自注入少量订单。
/// </summary>
public abstract class RetailCrowd : IParticipant
{
    public abstract ParticipantId Id { get; }
    protected readonly Account Account;
    protected readonly Price IntrinsicValue;   // 内在价值(价投/抄底的基准)
    /// <summary>群体力量(决定每 tick 可能下单的量级)。</summary>
    public int Strength { get; set; }
    /// <summary>活跃度 0~1,受消息面调节。</summary>
    public double Activity { get; set; } = 0.5;
    /// <summary>散户整体平均持仓成本(群体间共享引用)。</summary>
    protected readonly SharedRetailState State;

    protected RetailCrowd(Account account, SharedRetailState state, Price intrinsicValue, int strength)
    {
        Account = account; State = state; IntrinsicValue = intrinsicValue; Strength = strength;
    }

    public abstract void Act(TradingSession session, SimulationClock clock, Random rng);

    /// <summary>便捷:发一笔限价单(失败静默忽略,如资金/持仓不足)。价格自动对齐到 tick。</summary>
    protected void TryLimitBuy(TradingSession s, Price price, int qty)
    {
        if (qty <= 0) return;
        try { s.Submit(new OrderRequest(Id, Side.Buy, OrderType.Limit, AlignToTick(s, price), new Quantity(qty))); }
        catch { /* 资金不足等,忽略 */ }
    }
    protected void TryLimitSell(TradingSession s, Price price, int qty)
    {
        if (qty <= 0) return;
        try { s.Submit(new OrderRequest(Id, Side.Sell, OrderType.Limit, AlignToTick(s, price), new Quantity(qty))); }
        catch { /* 可卖不足等,忽略 */ }
    }

    /// <summary>把价格对齐到最小变动价位(0.01)的整数倍,避免长尾小数污染盘口。</summary>
    protected static Price AlignToTick(TradingSession s, Price price)
    {
        const decimal tick = 0.01m;
        decimal aligned = Math.Round(price.Value / tick) * tick;
        return new Price(Math.Round(aligned, 2));
    }

    /// <summary>便捷:取盘口快照。</summary>
    protected MarketView ViewOf(TradingSession s, SimulationClock c) => new(
        s.Engine.View.LastPrice, s.Engine.View.BestBid, s.Engine.View.BestAsk,
        s.Engine.Rules.UpperLimit.Value, s.Engine.Rules.LowerLimit.Value,
        c.CurrentTickOfDay, c.TicksPerDay);
}

/// <summary>
/// 散户群体间共享的状态:整体平均持仓成本、近期价格序列(算短期涨跌)。
/// 所有群体持有同一个实例引用,任一群体的成交都会更新它。
/// </summary>
public sealed class SharedRetailState
{
    /// <summary>散户整体平均持仓成本(加权)。用于止损/抄底判定。</summary>
    public Price AverageCost { get; set; }
    /// <summary>散户总持仓(手)。用于判定是否还有可止损的货。</summary>
    public int TotalHolding { get; set; }
    /// <summary>近期价格(最近 N 个 tick),用于算短期涨跌幅驱动跟风客。</summary>
    private readonly Queue<decimal> _recentPrices = new();
    public int HistoryWindow { get; } = 20;

    public void RecordPrice(Price p)
    {
        _recentPrices.Enqueue(p.Value);
        while (_recentPrices.Count > HistoryWindow) _recentPrices.Dequeue();
    }

    /// <summary>近期(window 内)涨跌幅,返回 null 表示样本不足。</summary>
    public decimal? Momentum
    {
        get
        {
            if (_recentPrices.Count < HistoryWindow / 2) return null;
            var arr = _recentPrices.ToArray();
            decimal old = arr[0], now = arr[^1];
            if (old == 0) return null;
            return (now - old) / old;
        }
    }
}
