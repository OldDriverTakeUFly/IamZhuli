using IamZhuli.Core;

namespace IamZhuli.Factors;

/// <summary>一个价格档位(单层报价)。Quantity 为该档累计挂单量(手)。</summary>
public readonly record struct QuoteLevel(Price Price, Quantity Quantity);

/// <summary>
/// 市场数据快照——因子模块的唯一输入面。
/// 与数据源解耦:既可由模拟器的 TradingSession 适配,也可由真实行情推送填充。
/// 约定:BidLevels 降序(最佳/最高买盘在前),AskLevels 升序(最佳/最低卖盘在前)。
/// </summary>
public interface IMarketDataSnapshot
{
    Price? LastPrice { get; }
    Price? BestBid { get; }
    Price? BestAsk { get; }
    IReadOnlyList<QuoteLevel> BidLevels { get; }
    IReadOnlyList<QuoteLevel> AskLevels { get; }
}

/// <summary>
/// 可变快照:测试构造、真实行情增量填充都用它。每次 tick 重置后填入新数据。
/// </summary>
public sealed class MarketDataSnapshot : IMarketDataSnapshot
{
    public Price? LastPrice { get; set; }
    public Price? BestBid { get; set; }
    public Price? BestAsk { get; set; }
    public List<QuoteLevel> BidLevels { get; set; } = new();
    public List<QuoteLevel> AskLevels { get; set; } = new();

    IReadOnlyList<QuoteLevel> IMarketDataSnapshot.BidLevels => BidLevels;
    IReadOnlyList<QuoteLevel> IMarketDataSnapshot.AskLevels => AskLevels;

    /// <summary>便捷构造:用现成档位列表建一个快照(测试用)。空列表则 Best 为 null。</summary>
    public static MarketDataSnapshot Of(Price? last, IEnumerable<QuoteLevel> bids, IEnumerable<QuoteLevel> asks)
    {
        var bidList = bids.ToList();
        var askList = asks.ToList();
        return new MarketDataSnapshot
        {
            LastPrice = last,
            BestBid = bidList.Count > 0 ? bidList[0].Price : null,
            BestAsk = askList.Count > 0 ? askList[0].Price : null,
            BidLevels = bidList,
            AskLevels = askList,
        };
    }
}
