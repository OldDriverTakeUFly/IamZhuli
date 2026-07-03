using IamZhuli.Core;

namespace IamZhuli.Engine;

/// <summary>订单方向。</summary>
public enum Side { Buy, Sell }

/// <summary>订单类型。</summary>
public enum OrderType
{
    /// <summary>限价单:指定价格,进入订单簿排队或立即成交。</summary>
    Limit,
    /// <summary>市价单:不限价格,立即按最优档成交,可吃穿多档。</summary>
    Market
}

/// <summary>订单生命周期状态。</summary>
public enum OrderStatus
{
    /// <summary>新建/活跃:在订单簿中排队或部分成交中。</summary>
    Active,
    /// <summary>已全部成交。</summary>
    Filled,
    /// <summary>已撤销(可能部分成交后撤)。</summary>
    Cancelled,
    /// <summary>市价单吃穿整本订单簿仍有剩余,剩余部分作废。</summary>
    Expired
}

/// <summary>
/// 订单。限价单带 Price;市价单 Price 为 Zero(无指定价)。
/// 时间戳用于时间优先(同价位先到先成交)。
/// </summary>
public sealed class Order
{
    public OrderId Id { get; }
    public ParticipantId Participant { get; }
    public Side Side { get; }
    public OrderType Type { get; }
    public Price Price { get; }
    public Quantity TotalQty { get; }
    public Quantity FilledQty { get; internal set; }
    public long Sequence { get; }       // 全局时间戳序号,保证时间优先
    public OrderStatus Status { get; internal set; }

    public Quantity RemainingQty => TotalQty - FilledQty;
    public bool IsFilled => FilledQty >= TotalQty;
    public bool IsDone => Status is OrderStatus.Filled or OrderStatus.Cancelled or OrderStatus.Expired;

    public Order(OrderId id, ParticipantId participant, Side side, OrderType type,
                 Price price, Quantity totalQty, long sequence)
    {
        if (totalQty.IsZero) throw new ArgumentException("订单数量必须大于 0。", nameof(totalQty));
        if (type == OrderType.Limit && price.Value <= 0)
            throw new ArgumentException("限价单价格必须大于 0。", nameof(price));
        Id = id; Participant = participant; Side = side; Type = type;
        Price = price; TotalQty = totalQty; Sequence = sequence;
        FilledQty = Quantity.Zero; Status = OrderStatus.Active;
    }

    /// <summary>记录一笔成交,返回该笔成交量。</summary>
    internal Quantity Fill(Quantity qty)
    {
        var fill = qty.Value > RemainingQty.Value ? RemainingQty : qty;
        FilledQty = new Quantity(FilledQty.Value + fill.Value);
        if (IsFilled) Status = OrderStatus.Filled;
        return fill;
    }

    public override string ToString() =>
        $"{Side} {Type} {TotalQty}@{(Type == OrderType.Market ? "MKT" : Price)} #{Id} [{Status}]";
}
