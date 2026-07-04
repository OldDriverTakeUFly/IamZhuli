using IamZhuli.Core;
using IamZhuli.Simulation.Accounts;
using IamZhuli.Simulation.Sessions;

namespace IamZhuli.Simulation.Participants.RetailV2;

// ════════════════════════════════════════
// 跟风类:激进 + 温和
// ════════════════════════════════════════

/// <summary>激进跟风客:急涨追涨,情绪退潮即跑。追高被套则止损离场。</summary>
public sealed class AggressiveMomentumProfile : RetailProfile
{
    private readonly decimal _chaseThreshold;   // 追涨阈值(异质化,0.008~0.025)
    public AggressiveMomentumProfile(Account acc, decimal risk, int size, decimal jitter, Random rng)
        : base(ProfileType.AggressiveMomentum, acc, risk, size, jitter, rng)
    {
        _chaseThreshold = 0.008m + jitter * 0.017m;   // 0.8%~2.5%
    }

    protected override void Decide(TradingSession s, RetailMarketContext ctx)
    {
        if (ctx.LastPrice is null) return;
        // 上涨超阈值 + 情绪偏贪婪 → 概率追涨
        if (ctx.RecentReturn > _chaseThreshold && ctx.Sentiment.Greed > 0.55m)
        {
            double intensity = Math.Min(1.0, (double)(ctx.RecentReturn / _chaseThreshold) - 0.3)
                             * (double)ctx.Sentiment.Greed * (double)RiskPreference;
            if (Rand() < intensity * 0.55)
            {
                int qty = RandInt(5, 15) * 10;
                decimal ask = Val(ctx.BestAsk ?? ctx.LastPrice, ctx.IntrinsicValue);
                BuyLimit(s, ask + 0.01m, qty);   // 挂在卖一上方,急切追涨
            }
        }
    }

    protected override bool ShouldExit(RetailMarketContext ctx, long tick)
    {
        if (base.ShouldExit(ctx, tick)) return true;
        if (Account.Position.Total.Value == 0) return false;   // 空仓在场等机会
        // 追高被套超 5% 或情绪退潮 → 离场
        if (Account.Position.AverageCost.Value > 0 && ctx.LastPrice is { } p)
        {
            decimal loss = (Account.Position.AverageCost.Value - p.Value) / Account.Position.AverageCost.Value;
            if (loss > 0.05m) return true;
        }
        if (ctx.Sentiment.Value < 0.4m && Account.Position.Total.Value > 0) return true;
        return false;
    }
}

/// <summary>温和跟风客:持续温和上涨才进场,小幅回调即跑。节奏慢、量小。</summary>
public sealed class MildMomentumProfile : RetailProfile
{
    private readonly decimal _threshold;
    private int _consecutiveUp;   // 连续上涨 tick 计数
    public MildMomentumProfile(Account acc, decimal risk, int size, decimal jitter, Random rng)
        : base(ProfileType.MildMomentum, acc, risk, size, jitter, rng)
    {
        _threshold = 0.003m + jitter * 0.005m;   // 0.3%~0.8%
    }

    protected override void Decide(TradingSession s, RetailMarketContext ctx)
    {
        if (ctx.LastPrice is null) return;
        if (ctx.RecentReturn > _threshold) _consecutiveUp++; else _consecutiveUp = 0;
        // 连续温和上涨 + 账户有钱 → 慢慢小单买入
        if (_consecutiveUp > 3 && ctx.Sentiment.Greed > 0.5m && Rand() < 0.2 * (double)RiskPreference)
        {
            int qty = RandInt(2, 6) * 10;
            decimal bid = Val(ctx.BestBid ?? ctx.LastPrice, ctx.IntrinsicValue);
            BuyLimit(s, bid, qty);   // 挂买一,不急
        }
    }

    protected override bool ShouldExit(RetailMarketContext ctx, long tick)
    {
        if (base.ShouldExit(ctx, tick)) return true;
        if (Account.Position.Total.Value == 0) return false;
        if (ctx.RecentReturn < -_threshold && Account.Position.Total.Value > 0) return true;  // 小幅回调就跑
        return false;
    }
}

/// <summary>稳健价投:严重低估进场,价值回归离场。慢、稳、逆向。</summary>
public sealed class ValueInvestorProfile : RetailProfile
{
    private readonly decimal _discount;   // 低估阈值(偏离内在价值多少)
    public ValueInvestorProfile(Account acc, decimal risk, int size, decimal jitter, Random rng)
        : base(ProfileType.ValueInvestor, acc, risk, size, jitter, rng)
    {
        _discount = 0.06m + jitter * 0.06m;   // 低估 6%~12%
    }

    protected override void Decide(TradingSession s, RetailMarketContext ctx)
    {
        if (ctx.LastPrice is null || ctx.IntrinsicValue <= 0) return;
        decimal deviation = (ctx.LastPrice.Value.Value - ctx.IntrinsicValue) / ctx.IntrinsicValue;
        // 严重低估 → 逢低买
        if (deviation < -_discount && Rand() < 0.15 * (double)RiskPreference)
        {
            int qty = RandInt(3, 8) * 10;
            decimal bid = Val(ctx.BestBid ?? ctx.LastPrice, ctx.IntrinsicValue);
            BuyLimit(s, bid + 0.01m, qty);
        }
    }

    protected override bool ShouldExit(RetailMarketContext ctx, long tick)
    {
        if (base.ShouldExit(ctx, tick)) return true;
        if (Account.Position.Total.Value == 0) return false;
        // 价值回归(回到内在价值附近)→ 止盈离场
        if (ctx.LastPrice is { } p && p.Value >= ctx.IntrinsicValue * 0.99m) return true;
        return false;
    }
}

/// <summary>恐慌止损者:被套在场,价格跌破成本止损线则恐慌卖出离场。</summary>
public sealed class StopLossProfile : RetailProfile
{
    private readonly decimal _stopRatio;
    public StopLossProfile(Account acc, decimal risk, int size, decimal jitter, Random rng)
        : base(ProfileType.StopLoss, acc, risk, size, jitter, rng)
    {
        _stopRatio = 0.05m + jitter * 0.05m;   // 跌破成本 5%~10% 止损
    }

    protected override void Decide(TradingSession s, RetailMarketContext ctx)
    {
        // 止损盘不主动开仓,只在 ShouldExit 里恐慌卖出
    }

    protected override bool ShouldExit(RetailMarketContext ctx, long tick)
    {
        if (Account.Position.Total.Value == 0) return true;   // 没货了就离场
        if (Account.Position.Available.Value <= 0) return false;  // T+1锁着,跑不了
        if (ctx.LastPrice is null) return false;
        decimal cost = Account.Position.AverageCost.Value;
        if (cost <= 0) return false;
        decimal loss = (cost - ctx.LastPrice.Value.Value) / cost;
        // 跌破止损线 或 接近止损线且情绪恐慌 → 触发离场(基类 Exit 会市价清仓)
        return loss > _stopRatio || (loss > _stopRatio * 0.7m && ctx.Sentiment.IsPanic);
    }
}
