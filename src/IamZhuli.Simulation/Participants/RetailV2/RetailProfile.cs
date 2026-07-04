using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Participants.RetailV2;

/// <summary>散户决策时读取的共享市场上下文。</summary>
public sealed class RetailMarketContext
{
    public Price? LastPrice { get; set; }
    public Price? BestBid { get; set; }
    public Price? BestAsk { get; set; }
    public decimal UpperLimit { get; set; }
    public decimal LowerLimit { get; set; }
    /// <summary>近期收益率(近 N tick),正=涨。</summary>
    public decimal RecentReturn { get; set; }
    /// <summary>近期波动率(标准差/均值)。</summary>
    public decimal Volatility { get; set; }
    /// <summary>近期成交量放大倍数(当前/均值,>1=放量)。</summary>
    public decimal VolumeSpike { get; set; }
    /// <summary>全局情绪指数。</summary>
    public MarketSentiment Sentiment { get; set; } = new();
    /// <summary>内在价值。</summary>
    public decimal IntrinsicValue { get; set; }
    /// <summary>流通盘(手)。</summary>
    public int FloatShares { get; set; }
}

/// <summary>画像类型枚举。</summary>
public enum ProfileType
{
    AggressiveMomentum,   // 激进跟风客
    MildMomentum,         // 温和跟风客
    ValueInvestor,        // 稳健价投
    StopLoss,             // 恐慌止损者(被套在场)
    BargainHunter,        // 抄底猎手
    Speculator,           // 短线投机客
    Herd,                 // 羊群效应型
    NewsDriven            // 消息驱动型(二期)
}

/// <summary>
/// 散户画像基类。每个实例是一个独立账户 + 异质化行为参数。
/// 子类实现 Decide(决定本 tick 是否下单),基类处理下单执行、止损/止盈离场判定。
/// </summary>
public abstract class RetailProfile
{
    private static long _idSeq;
    public long InstanceId { get; }
    public ProfileType Type { get; }
    public Account Account { get; }
    public bool IsActive { get; private set; }   // 是否在场(离场后销户)
    public long EntryTick { get; private set; }

    // —— 异质化参数(同类画像个体间有抖动)——
    public decimal RiskPreference { get; }        // 风险偏好 0~1
    public int PositionSize { get; }              // 单次下单量级(手)
    protected readonly decimal _triggerJitter;    // 触发阈值抖动
    private readonly Random _rng;

    protected RetailProfile(ProfileType type, Account account, decimal riskPref,
                            int positionSize, decimal triggerJitter, Random rng)
    {
        InstanceId = ++_idSeq;
        Type = type; Account = account; RiskPreference = riskPref;
        PositionSize = positionSize; _triggerJitter = triggerJitter; _rng = rng;
    }

    /// <summary>进场:标记活跃,记录进场 tick。</summary>
    public void Activate(long tick) { IsActive = true; EntryTick = tick; }

    /// <summary>每 tick 决策:子类实现是否下单。</summary>
    public void Act(TradingSession session, RetailMarketContext ctx, long currentTick)
    {
        if (!IsActive) return;
        // 先检查离场条件(止损/止盈/情绪消退)
        if (ShouldExit(ctx, currentTick)) { Exit(session); return; }
        // 再决策下单
        Decide(session, ctx);
    }

    /// <summary>子类实现:本 tick 是否下单、下什么单。</summary>
    protected abstract void Decide(TradingSession session, RetailMarketContext ctx);

    /// <summary>离场判定:止损/止盈/情绪消退。子类可重写。</summary>
    protected virtual bool ShouldExit(RetailMarketContext ctx, long currentTick)
    {
        if (Account.Position.Total.Value == 0 && Account.Cash <= 1m) return true;   // 没钱没货
        return false;
    }

    /// <summary>离场:平掉剩余持仓(若有),标记不活跃。</summary>
    protected void Exit(TradingSession session)
    {
        // 剩余持仓按现价市价清仓
        if (Account.Position.Available.Value > 0)
        {
            try { session.Submit(new OrderRequest(Account.Id, Side.Sell, OrderType.Market,
                Price.Zero, new Quantity(Account.Position.Available.Value))); }
            catch { }
        }
        IsActive = false;
    }

    // —— 下单辅助(失败静默)——
    protected void BuyLimit(TradingSession s, decimal price, int qty)
    {
        if (qty <= 0) return;
        try { s.Submit(new OrderRequest(Account.Id, Side.Buy, OrderType.Limit, Align(price), new Quantity(qty))); }
        catch { }
    }
    protected void SellLimit(TradingSession s, decimal price, int qty)
    {
        if (qty <= 0 || Account.Position.Available.Value < qty) return;
        try { s.Submit(new OrderRequest(Account.Id, Side.Sell, OrderType.Limit, Align(price), new Quantity(qty))); }
        catch { }
    }
    protected void BuyMarket(TradingSession s, int qty)
    {
        if (qty <= 0) return;
        try { s.Submit(new OrderRequest(Account.Id, Side.Buy, OrderType.Market, Price.Zero, new Quantity(qty))); }
        catch { }
    }
    protected void SellMarket(TradingSession s, int qty)
    {
        if (qty <= 0 || Account.Position.Available.Value < qty) return;
        try { s.Submit(new OrderRequest(Account.Id, Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty))); }
        catch { }
    }
    protected static decimal Align(decimal v) => Math.Round(Math.Round(v / 0.01m) * 0.01m, 2);
    /// <summary>Price? → decimal(空则返回 fallback)。</summary>
    protected static decimal Val(Price? p, decimal fallback) => p?.Value ?? fallback;
    protected double Rand() => _rng.NextDouble();
    protected int RandInt(int lo, int hi) => _rng.Next(lo, hi);
}
