using IamZhuli.Core;
using IamZhuli.Engine;

namespace IamZhuli.Engine.Tests;

/// <summary>
/// 测试辅助:便捷构造引擎与下单。
/// 默认前收盘价 10.00,涨跌停 ±10% → [9.00, 11.00]。
/// </summary>
internal static class EngineTestExtensions
{
    public static MatchingEngine NewEngine(decimal prevClose = 10.00m, decimal limitRatio = 0.10m)
    {
        var rules = new MarketRules
        {
            PreviousClose = new Price(prevClose),
            PriceLimitRatio = limitRatio,
            TickSize = new Price(0.01m)
        };
        return new MatchingEngine(rules);
    }

    public static Order BuyLimit(this MatchingEngine e, string who, decimal price, int qty)
        => e.CreateOrder(new ParticipantId(who), Side.Buy, OrderType.Limit, new Price(price), new Quantity(qty));

    public static Order SellLimit(this MatchingEngine e, string who, decimal price, int qty)
        => e.CreateOrder(new ParticipantId(who), Side.Sell, OrderType.Limit, new Price(price), new Quantity(qty));

    public static Order BuyMarket(this MatchingEngine e, string who, int qty)
        => e.CreateOrder(new ParticipantId(who), Side.Buy, OrderType.Market, Price.Zero, new Quantity(qty));

    public static Order SellMarket(this MatchingEngine e, string who, int qty)
        => e.CreateOrder(new ParticipantId(who), Side.Sell, OrderType.Market, Price.Zero, new Quantity(qty));

    /// <summary>提交并返回结果,省去每次调 Submit 的样板。</summary>
    public static OrderResult Place(this MatchingEngine e, Order o) => e.Submit(o);
}
