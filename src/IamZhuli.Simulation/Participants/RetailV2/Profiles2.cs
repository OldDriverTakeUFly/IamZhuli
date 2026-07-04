using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Participants.RetailV2;

/// <summary>抄底猎手:急跌破位进场,反弹止盈离场。低位挂买墙。</summary>
public sealed class BargainHunterProfile : RetailProfile
{
    private readonly decimal _dipThreshold;   // 跌幅多少算"破位"可抄底
    public BargainHunterProfile(Account acc, decimal risk, int size, decimal jitter, Random rng)
        : base(ProfileType.BargainHunter, acc, risk, size, jitter, rng)
    {
        _dipThreshold = 0.04m + jitter * 0.04m;   // 跌 4%~8%
    }

    protected override void Decide(TradingSession s, RetailMarketContext ctx)
    {
        if (ctx.LastPrice is null) return;
        // 急跌破位 + 恐惧情绪抄底(逆人性,但抄底猎手偏好在恐慌中接货)
        if (ctx.RecentReturn < -_dipThreshold && Rand() < 0.3 * (double)RiskPreference)
        {
            int qty = RandInt(5, 15) * 10;
            decimal bid = Val(ctx.BestBid ?? ctx.LastPrice, ctx.IntrinsicValue);
            BuyLimit(s, bid - 0.01m, qty);   // 挂在买一或更低(逢低接),制造买墙
        }
    }

    protected override bool ShouldExit(RetailMarketContext ctx, long tick)
    {
        if (base.ShouldExit(ctx, tick)) return true;
        if (Account.Position.Total.Value == 0) return false;
        // 反弹盈利 3%~6% 止盈离场
        decimal profitRatio = Account.Position.AverageCost.Value > 0 && ctx.LastPrice is { } p
            ? (p.Value - Account.Position.AverageCost.Value) / Account.Position.AverageCost.Value : 0;
        if (profitRatio > 0.03m + _triggerJitter * 0.03m) return true;
        return false;
    }
}

/// <summary>短线投机客:波动放大进场,快进快出(T+1次日跑)。频繁挂撤、小单密集。</summary>
public sealed class SpeculatorProfile : RetailProfile
{
    public SpeculatorProfile(Account acc, decimal risk, int size, decimal jitter, Random rng)
        : base(ProfileType.Speculator, acc, risk, size, jitter, rng) { }

    protected override void Decide(TradingSession s, RetailMarketContext ctx)
    {
        if (ctx.LastPrice is null) return;
        // 波动放大 + 活跃 → 频繁小单,方向随短期动量
        if (ctx.Volatility > 0.015m && Rand() < 0.5 * (double)RiskPreference)
        {
            int qty = RandInt(1, 5) * 10;
            bool buy = ctx.RecentReturn > 0;
            var price = (buy ? (ctx.BestAsk ?? ctx.LastPrice) : (ctx.BestBid ?? ctx.LastPrice))?.Value ?? ctx.IntrinsicValue;
            if (buy) BuyLimit(s, price, qty);
            else SellLimit(s, price, qty);
        }
    }

    protected override bool ShouldExit(RetailMarketContext ctx, long tick)
    {
        if (base.ShouldExit(ctx, tick)) return true;
        // T+1 投机客:进场后存活不超过 2 个交易日(由 tick 数近似),超时清仓离场
        if (tick - EntryTick > ctx.FloatsharesToTicks()) return true;
        return false;
    }
}

/// <summary>羊群效应型:不直接看价格,看成交活跃度(传染载体),跟风滞后进场。</summary>
public sealed class HerdProfile : RetailProfile
{
    public HerdProfile(Account acc, decimal risk, int size, decimal jitter, Random rng)
        : base(ProfileType.Herd, acc, risk, size, jitter, rng) { }

    protected override void Decide(TradingSession s, RetailMarketContext ctx)
    {
        if (ctx.LastPrice is null) return;
        // 放量(成交活跃)+ 情绪偏激 → 跟着别人买(正反馈传染)
        if (ctx.VolumeSpike > 1.5m && ctx.Sentiment.Extremity > 0.15m && Rand() < 0.25 * (double)RiskPreference)
        {
            int qty = RandInt(3, 10) * 10;
            // 方向跟情绪:贪婪跟买,恐惧跟卖
            bool buy = ctx.Sentiment.Greed > 0.5m;
            if (buy) BuyMarket(s, qty);
            else SellMarket(s, qty);
        }
    }

    protected override bool ShouldExit(RetailMarketContext ctx, long tick)
    {
        if (base.ShouldExit(ctx, tick)) return true;
        // 情绪回归中性 + 有持仓 → 离场(羊群散去)
        if (ctx.Sentiment.Extremity < 0.1m && Account.Position.Total.Value > 0) return true;
        return false;
    }
}

/// <summary>消息驱动型(二期信息战):预留接口,POC 行为同羊群型,由消息事件触发。</summary>
public sealed class NewsDrivenProfile : RetailProfile
{
    public NewsDrivenProfile(Account acc, decimal risk, int size, decimal jitter, Random rng)
        : base(ProfileType.NewsDriven, acc, risk, size, jitter, rng) { }

    protected override void Decide(TradingSession s, RetailMarketContext ctx)
    {
        // POC:消息系统未接入,暂不主动下单(二期由 NewsShock 触发)
    }

    protected override bool ShouldExit(RetailMarketContext ctx, long tick)
    {
        if (base.ShouldExit(ctx, tick)) return true;
        // 无消息刺激一段时间后离场
        return tick - EntryTick > ctx.FloatsharesToTicks();
    }
}

/// <summary>RetailMarketContext 扩展方法(临时存放需要上下文的方法)。</summary>
public static class RetailContextExt
{
    /// <summary>把流通盘换算成 tick 数(投机客存活估算用)。简化:固定返回 120 tick。</summary>
    public static long FloatsharesToTicks(this RetailMarketContext ctx) => 120;
}
