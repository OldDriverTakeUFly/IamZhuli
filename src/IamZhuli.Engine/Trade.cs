using IamZhuli.Core;

namespace IamZhuli.Engine;

/// <summary>
/// 一笔成交。成交价 = 被动方(挂单方)价格。
/// TakerId 为主动方(发起撮合的市价/穿价单),MakerId 为被动方(已在簿中排队的单)。
/// </summary>
public readonly record struct Trade(
    TradeId Id,
    long Sequence,              // 发生时序,与订单共享同一全局序号空间
    Price Price,
    Quantity Quantity,
    Side TakerSide,             // 主动方方向(主动买/主动卖)
    ParticipantId TakerId,
    OrderId TakerOrderId,
    ParticipantId MakerId,
    OrderId MakerOrderId);

/// <summary>
/// 一笔新订单撮合的结果。包含产生的成交列表、订单最终状态、可能的错误。
/// </summary>
public sealed class OrderResult
{
    public OrderId OrderId { get; init; }
    public IReadOnlyList<Trade> Trades { get; init; } = Array.Empty<Trade>();
    public OrderStatus FinalStatus { get; init; }

    /// <summary>该订单本次加权平均成交价(无成交则为 Zero)。</summary>
    public Price AverageFillPrice { get; init; }

    /// <summary>剩余未成交数量(市价单剩余→Expired,限价单剩余→挂簿)。</summary>
    public Quantity RemainingQty { get; init; }

    public bool HasFills => Trades.Count > 0;
    public Quantity TotalFilled => Trades.Count == 0
        ? Quantity.Zero
        : new(Trades.Sum(t => t.Quantity.Value));

    public override string ToString() =>
        $"#{OrderId}: {Trades.Count}笔成交 均价{AverageFillPrice} 状态{FinalStatus} 剩{RemainingQty}";
}
