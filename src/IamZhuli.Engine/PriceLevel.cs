using IamZhuli.Core;

namespace IamZhuli.Engine;

/// <summary>
/// 单价位层级。同价位的订单按到达顺序(FIFO)排队,保证时间优先。
/// </summary>
public sealed class PriceLevel
{
    public Price Price { get; }
    private readonly Queue<Order> _queue = new();
    public int OrderCount => _queue.Count;

    public PriceLevel(Price price) => Price = price;

    public void Enqueue(Order order) => _queue.Enqueue(order);

    public Order Peek() => _queue.Peek();

    /// <summary>弹出队首(已成交完或已撤)。返回 false 表示本层已空。</summary>
    public Order Dequeue() => _queue.Dequeue();

    public bool IsEmpty => _queue.Count == 0;

    /// <summary>该价位累计挂单量(剩余量之和)。</summary>
    public Quantity TotalQuantity => new(_queue.Sum(o => o.RemainingQty.Value));
}
