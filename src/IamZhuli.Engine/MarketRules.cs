using IamZhuli.Core;

namespace IamZhuli.Engine;

/// <summary>
/// 单只股票的市场约束(涨跌停、最小变动价位)。撮合引擎据此过滤非法价格。
/// </summary>
public sealed class MarketRules
{
    /// <summary>最小变动价位(默认 0.01 元)。</summary>
    public Price TickSize { get; init; } = new(0.01m);

    /// <summary>前收盘价,涨跌停基准。日切时由采集器更新为上一日收盘价。</summary>
    public Price PreviousClose { get; set; }

    /// <summary>涨跌停比例(默认 10%)。</summary>
    public decimal PriceLimitRatio { get; init; } = 0.10m;

    /// <summary>流通盘(手),换手率 = 成交量 / 流通盘。per-stock 常量。</summary>
    public Quantity FloatShares { get; init; } = new(200000);

    public Price UpperLimit => new((PreviousClose.Value * (1 + PriceLimitRatio)).RoundToTick(TickSize));
    public Price LowerLimit => new((PreviousClose.Value * (1 - PriceLimitRatio)).RoundToTick(TickSize));

    /// <summary>
    /// 主动单按 makerPrice 成交是否被涨跌停允许。
    /// 涨停价本身的成交允许(价格可"达到"涨停),但越过涨停禁止。
    /// 即:买单成交价不得"超过"涨停价;卖单成交价不得"低于"跌停价。
    /// </summary>
    public bool CanTradeAt(Side takerSide, Price makerPrice)
    {
        if (takerSide == Side.Buy && makerPrice > UpperLimit) return false;   // 买越过涨停
        if (takerSide == Side.Sell && makerPrice < LowerLimit) return false;  // 卖越过跌停
        return true;
    }

    /// <summary>
    /// 挂单是否允许进入订单簿(新开仓)。
    /// 涨停后禁止新买单挂簿(开多仓)、跌停后禁止新卖单挂簿(开空仓)。
    /// 判断依据:当前现价是否已触及该方向极限价。
    /// </summary>
    public bool CanRestOpen(Side side, Price orderPrice, Price? currentLast)
    {
        // 挂买单价格超过涨停 → 不允许(价格会越界)
        if (side == Side.Buy && orderPrice > UpperLimit) return false;
        if (side == Side.Sell && orderPrice < LowerLimit) return false;
        // 若现价已封涨停,禁止新买单挂簿(开多仓);跌停同理
        if (currentLast is { } last)
        {
            if (side == Side.Buy && last >= UpperLimit) return false;
            if (side == Side.Sell && last <= LowerLimit) return false;
        }
        return true;
    }
}

internal static class PriceMath
{
    /// <summary>将价格对齐到最小变动价位的整数倍。</summary>
    public static decimal RoundToTick(this decimal value, Price tickSize)
    {
        var t = tickSize.Value;
        return Math.Round(value / t) * t;
    }
}
