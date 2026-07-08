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
    /// <summary>近期均价(MA,算乖离率用),窗口不足时为 null。</summary>
    public decimal? RecentAveragePrice { get; set; }
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
    NewsDriven,           // 消息驱动型(二期)
    TechnicalBouncer      // 超跌反弹客(看乖离率,博技术反抽)
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
        // 通用浮盈止盈:有浮盈的持仓按概率部分卖出(兑现利润,形成拉升卖压)
        TryProfitTaking(session, ctx);
        // 通用止损:浮亏超阈值时按概率减仓(恐慌情绪加速)
        TryStopLoss(session, ctx);
        // 通用空头平仓(有空头持仓时检查止盈)
        TryCoverShort(session, ctx);
        // 再决策下单
        Decide(session, ctx);
    }

    /// <summary>通用浮盈止盈:浮盈越大,卖出概率越高。
    /// _profitTakingSensitivity 由子类设置(跟风客/投机客更激进,价投/羊群温和)。</summary>
    protected decimal _profitTakingSensitivity = 0.5m;   // 默认中等,子类可覆盖
    private void TryProfitTaking(TradingSession session, RetailMarketContext ctx)
    {
        if (ctx.LastPrice is null) return;
        int available = Account.Position.Available.Value;
        if (available <= 0) return;
        decimal cost = Account.Position.AverageCost.Value;
        if (cost <= 0) return;
        decimal profit = (ctx.LastPrice.Value.Value - cost) / cost;
        if (profit <= 0.02m) return;   // 浮盈<2%不止盈
        // 浮盈越高,止盈概率越大:2%=5%,10%=25%,20%=45%
        // 概率 = _profitTakingSensitivity × profit × 2.0
        double prob = Math.Min(0.5, (double)(_profitTakingSensitivity * profit * 2.0m));
        if (Rand() >= prob) return;
        // 卖出持仓的一部分(1/3~1/2),不是全清(真实散户分批止盈)
        int sellQty = available * RandInt(1, 2) / 3;
        if (sellQty > 0) SellMarket(session, sellQty);
    }

    /// <summary>止损阈值(亏损比例,超过则触发止损)。0=不止损(长线价投)。
    /// 子类设置:短线客灵敏(0.03~0.05),长线迟钝(0.10~0.15),价投=0。</summary>
    protected decimal _stopLossThreshold = 0.08m;   // 默认8%,子类可覆盖

    /// <summary>是否具备做空能力(投机客/羊群型为true,其他false)。</summary>
    protected bool _canShortSell = false;

    /// <summary>空头止盈:有空头浮盈时按概率平仓。</summary>
    private void TryCoverShort(TradingSession session, RetailMarketContext ctx)
    {
        if (!Account.Position.HasShort) return;
        if (ctx.LastPrice is null) return;
        decimal cost = Account.Position.ShortCost.Value;
        if (cost <= 0) return;
        decimal profit = (cost - ctx.LastPrice.Value.Value) / cost;   // 做空盈亏(正=赚)
        if (profit < 0.03m) return;   // 盈利<3%不平
        double prob = profit > 0.08m ? 0.3 : 0.1;
        if (Rand() >= prob) return;
        int qty = Account.Position.ShortQty.Value;
        try { session.Submit(new OrderRequest(Account.Id, Side.Buy, OrderType.Market,
            Price.Zero, new Quantity(qty), IsShort: true)); }
        catch { }
    }

    /// <summary>做空辅助:限价做空卖出(子类调用)。</summary>
    protected void ShortSell(TradingSession s, decimal price, int qty)
    {
        if (qty <= 0 || !_canShortSell) return;
        try { s.Submit(new OrderRequest(Account.Id, Side.Sell, OrderType.Limit,
            new Price(Math.Round(price, 2)), new Quantity(qty), IsShort: true)); }
        catch { /* 保证金不足忽略 */ }
    }

    /// <summary>通用止损:浮亏超阈值时按概率减仓。恐慌情绪下阈值降低、概率升高。
    /// 和止盈对称——越亏越想跑,恐慌时加速。短线客阈值低,长线阈值高。</summary>
    private void TryStopLoss(TradingSession session, RetailMarketContext ctx)
    {
        if (_stopLossThreshold <= 0) return;   // 价投不止损
        if (ctx.LastPrice is null) return;
        int available = Account.Position.Available.Value;
        if (available <= 0) return;
        decimal cost = Account.Position.AverageCost.Value;
        if (cost <= 0) return;
        decimal loss = (cost - ctx.LastPrice.Value.Value) / cost;
        if (loss <= 0) return;   // 没亏损

        // 恐慌情绪降低止损阈值(恐惧时更敏感):恐慌态阈值×0.7
        decimal threshold = ctx.Sentiment.IsPanic ? _stopLossThreshold * 0.7m : _stopLossThreshold;
        if (loss < threshold) return;

        // 亏损越大,止损概率越高:刚到阈值=20%,亏2倍=50%
        double prob = Math.Min(0.6, 0.2 + (double)((loss - threshold) / threshold) * 0.3);
        // 恐慌时概率翻倍
        if (ctx.Sentiment.IsPanic) prob = Math.Min(0.8, prob * 1.5);
        if (Rand() >= prob) return;

        // 止损卖出:亏损大时清仓,刚到阈值时卖一半
        int sellQty = loss > threshold * 1.5m ? available : available / 2;
        if (sellQty > 0) SellMarket(session, sellQty);
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
