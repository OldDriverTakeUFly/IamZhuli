using IamZhuli.Core;

namespace IamZhuli.Engine;

/// <summary>
/// 完整订单簿。买卖双边各用 SortedDictionary 按价格有序存放价位层级。
/// 买盘价格降序(最优买价在前),卖盘价格升序(最优卖价在前)。
/// 另维护 OrderId→Order 索引以支持 O(1) 撤单。
/// 订单簿只负责存取,不做撮合决策。
/// </summary>
public sealed class OrderBook
{
    // 买盘:价格降序 → 最优买价在第一
    private readonly SortedList<Price, PriceLevel> _bids = new(DescendingPriceComparer.Instance);
    // 卖盘:价格升序 → 最优卖价在第一
    private readonly SortedList<Price, PriceLevel> _asks = new();
    // 撤单索引
    private readonly Dictionary<OrderId, Order> _ordersById = new();

    public Price? LastTradePrice { get; private set; }

    private SortedList<Price, PriceLevel> BookFor(Side side) => side == Side.Buy ? _bids : _asks;

    /// <summary>把限价单挂入对应价位层级。</summary>
    public void Rest(Order order)
    {
        if (order.Type != OrderType.Limit)
            throw new ArgumentException("仅限价单可挂簿。", nameof(order));
        var book = BookFor(order.Side);
        if (!book.TryGetValue(order.Price, out var level))
        {
            level = new PriceLevel(order.Price);
            book[order.Price] = level;
        }
        level.Enqueue(order);
        _ordersById[order.Id] = order;
    }

    /// <summary>取指定方向的最优价位层级(买盘最高、卖盘最低),无则 null。</summary>
    public PriceLevel? BestLevel(Side side)
    {
        var book = BookFor(side);
        return book.Count == 0 ? null : book.Values[0];
    }

    /// <summary>最优买价,无则 null。</summary>
    public Price? BestBid => _bids.Count == 0 ? null : _bids.Keys[0];

    /// <summary>最优卖价,无则 null。</summary>
    public Price? BestAsk => _asks.Count == 0 ? null : _asks.Keys[0];

    /// <summary>查询订单(撤单用)。</summary>
    public bool TryGetOrder(OrderId id, out Order? order) => _ordersById.TryGetValue(id, out order);

    /// <summary>从簿中移除一个已完结/已撤的订单。需同时清理空价位。</summary>
    public void Remove(Order order)
    {
        var book = BookFor(order.Side);
        if (book.TryGetValue(order.Price, out var level))
        {
            // 队首应即为该订单(正常情况下只有队首会被吃/被撤)
            if (level.Peek().Id == order.Id) level.Dequeue();
            if (level.IsEmpty) book.Remove(order.Price);
        }
        _ordersById.Remove(order.Id);
    }

    /// <summary>移除并返回某方向最优价位的队首订单(撮合吃单用)。</summary>
    public Order? PopBest(Side side)
    {
        var level = BestLevel(side);
        if (level == null) return null;
        var order = level.Dequeue();
        if (level.IsEmpty) BookFor(side).Remove(level.Price);
        return order;
    }

    /// <summary>记录最新成交价。</summary>
    public void RecordTrade(Price price) => LastTradePrice = price;

    /// <summary>获取某方向前 N 档的(价格, 累计量)快照,用于展示盘口五档。</summary>
    public IReadOnlyList<(Price Price, Quantity TotalQty)> TopOfBook(Side side, int depth)
    {
        var book = BookFor(side);
        var result = new List<(Price, Quantity)>(Math.Min(depth, book.Count));
        for (int i = 0; i < depth && i < book.Count; i++)
        {
            var level = book.Values[i];
            result.Add((level.Price, level.TotalQuantity));
        }
        return result;
    }

    public int BidLevelCount => _bids.Count;
    public int AskLevelCount => _asks.Count;

    /// <summary>清空订单簿(日切时撤销所有隔夜挂单)。返回被撤销的订单列表(供账户释放冻结)。</summary>
    public List<Order> Clear()
    {
        var removed = _ordersById.Values.Where(o => !o.IsDone).ToList();
        foreach (var o in removed) o.Status = OrderStatus.Cancelled;
        _bids.Clear();
        _asks.Clear();
        _ordersById.Clear();
        return removed;
    }

    /// <summary>枚举指定参与者当前在簿的全部挂单 ID(用于批量撤单)。</summary>
    public IEnumerable<OrderId> AllRestingOrderIds(ParticipantId participant)
        => _ordersById.Values
            .Where(o => o.Participant.Equals(participant) && !o.IsDone)
            .Select(o => o.Id)
            .ToList();

    /// <summary>枚举指定参与者当前在簿的全部挂单(含详情,供"我的挂单"列表用)。
    /// 排序:买盘在前(按价格降序,最优买价在前),卖盘在后(按价格升序,最优卖价在前)。</summary>
    public IEnumerable<Order> OrdersFor(ParticipantId participant)
    {
        var orders = _ordersById.Values
            .Where(o => o.Participant.Equals(participant) && !o.IsDone).ToList();
        // 买盘按价格降序,卖盘按价格升序,买盘在前
        var buys = orders.Where(o => o.Side == Side.Buy).OrderByDescending(o => o.Price.Value);
        var sells = orders.Where(o => o.Side == Side.Sell).OrderBy(o => o.Price.Value);
        return buys.Concat(sells).ToList();
    }
}

/// <summary>价格降序比较器(用于买盘)。</summary>
internal sealed class DescendingPriceComparer : IComparer<Price>
{
    public static readonly DescendingPriceComparer Instance = new();
    public int Compare(Price x, Price y) => y.Value.CompareTo(x.Value);
}
