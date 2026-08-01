using IamZhuli.Core;

namespace IamZhuli.Engine;

/// <summary>
/// 撮合引擎。维护一只股票的订单簿,处理新订单、撤单,产出成交。
/// 规则:价格优先、时间优先;成交价=被动方价格;现价=最近成交价。
/// </summary>
public sealed class MatchingEngine
{
    private readonly OrderBook _book = new();
    private readonly MarketRules _rules;
    private long _orderSeq;     // 全局序号(时间戳)
    private long _tradeSeq;
    private long _orderIdSeq;

    public MarketRules Rules => _rules;
    public IReadOnlyOrderBook View => _bookView;
    private readonly OrderBookSnapshotView _bookView;

    public Price? LastPrice => _book.LastTradePrice;

    public MatchingEngine(MarketRules rules)
    {
        _rules = rules;
        _bookView = new OrderBookSnapshotView(_book);
    }

    /// <summary>分配并构造一个订单(供外部使用统一的序号)。</summary>
    public Order CreateOrder(ParticipantId participant, Side side, OrderType type,
                             Price price, Quantity qty)
        => new(new OrderId(++_orderIdSeq), participant, side, type, price, qty, ++_orderSeq);

    /// <summary>将限价单挂入订单簿(不撮合,用于集中竞价收集阶段)。</summary>
    public void RestOrder(Order order) => _book.Rest(order);

    /// <summary>
    /// 提交订单进入撮合。返回撮合结果(成交列表、最终状态、剩余量)。
    /// 限价单剩余挂簿;市价单剩余作废(Expired)。
    /// </summary>
    public OrderResult Submit(Order order)
    {
        if (order.IsDone) throw new InvalidOperationException("订单已完结,不能提交。");

        var opposite = order.Side == Side.Buy ? Side.Sell : Side.Buy;
        var trades = new List<Trade>();

        // —— 1. 立即撮合:吃掉可成交的对手盘 ——
        // 限价买单:买价 >= 最优卖价 即可吃;市价买单:无条件吃(直到涨跌停/空簿)。
        while (!order.IsFilled)
        {
            var bestLevel = _book.BestLevel(opposite);
            if (bestLevel == null) break;  // 对手盘空

            Price makerPrice = bestLevel.Price;
            if (!CanCross(order, makerPrice, out string? rejectReason)) break;

            // 涨跌停检查:主动方按 makerPrice 成交,若越过涨跌停则停止吃单
            if (!_rules.CanTradeAt(order.Side, makerPrice)) break;

            var maker = bestLevel.Peek();
            var fillQty = new Quantity(Math.Min(order.RemainingQty.Value, maker.RemainingQty.Value));

            var actualFill = order.Fill(fillQty);
            maker.Fill(fillQty);

            var trade = new Trade(
                new TradeId(++_tradeSeq), _orderSeq,
                makerPrice, actualFill,
                order.Side, order.Participant, order.Id,
                maker.Participant, maker.Id);
            trades.Add(trade);
            _book.RecordTrade(makerPrice);

            if (maker.IsFilled || maker.RemainingQty.IsZero)
                _book.Remove(maker);
        }

        // —— 2. 处理剩余 ——
        OrderStatus finalStatus;
        Quantity remaining;
        if (order.IsFilled)
        {
            finalStatus = OrderStatus.Filled;
            remaining = Quantity.Zero;
        }
        else if (order.Type == OrderType.Limit)
        {
            // 限价单剩余挂簿排队
            // 涨跌停检查:挂单越界或现价已封板则拒绝挂簿(作废)
            if (!_rules.CanRestOpen(order.Side, order.Price, _book.LastTradePrice))
            {
                finalStatus = OrderStatus.Expired;
                remaining = order.RemainingQty;
            }
            else
            {
                _book.Rest(order);
                finalStatus = OrderStatus.Active;
                remaining = order.RemainingQty;
            }
        }
        else
        {
            // 市价单剩余作废
            finalStatus = OrderStatus.Expired;
            remaining = order.RemainingQty;
        }

        decimal avg = trades.Count == 0 ? 0m
            : trades.Sum(t => t.Price.Value * t.Quantity.Value) / trades.Sum(t => t.Quantity.Value);

        return new OrderResult
        {
            OrderId = order.Id,
            Trades = trades,
            FinalStatus = finalStatus,
            AverageFillPrice = new Price(Math.Round(avg, 4)),
            RemainingQty = remaining
        };
    }

    /// <summary>主动单是否可吃掉 makerPrice 这一档。</summary>
    private bool CanCross(Order taker, Price makerPrice, out string? reason)
    {
        reason = null;
        if (taker.Type == OrderType.Market) return true;
        if (taker.Side == Side.Buy)  return taker.Price >= makerPrice;
        else                         return taker.Price <= makerPrice;
    }

    /// <summary>撤销一个挂簿订单。返回是否成功(不在簿中则失败)。</summary>
    public bool Cancel(OrderId orderId, out Order? cancelled)
    {
        if (_book.TryGetOrder(orderId, out var order) && order != null && !order.IsDone)
        {
            _book.Remove(order);
            order.Status = OrderStatus.Cancelled;
            cancelled = order;
            return true;
        }
        cancelled = null;
        return false;
    }

    /// <summary>清空订单簿(日切隔夜挂单清零)。返回被撤销的订单。</summary>
    public List<Order> ClearBook() => _book.Clear();

    /// <summary>
    /// 集合竞价:基于当前订单簿的所有挂单,按"最大成交量原则"撮出唯一价格。
    /// 算法:遍历可能的成交价(所有买价∩卖价),找使成交量最大的价格。
    /// 返回 (开盘价, 成交量);无交叉则返回 null。
    /// </summary>
    public (Price Price, int Volume)? CallAuction()
    {
        var bids = View.TopBids(50);
        var asks = View.TopAsks(50);
        if (bids.Count == 0 || asks.Count == 0) return null;

        // 收集所有候选价格(买价和卖价的交集范围)
        decimal bestAsk = asks[^1].Price.Value;   // 最低卖价(asks是升序,最后一个是最优=最低? 不对)
        // asks 从 TopAsks 返回的是升序(卖1最低在前),bids降序(买1最高在前)
        decimal lowestAsk = asks[0].Price.Value;
        decimal highestBid = bids[0].Price.Value;
        if (highestBid < lowestAsk) return null;   // 无交叉,无法集合竞价

        // 遍历候选价(从最高买到最低卖),找最大成交量
        Price? bestPrice = null;
        int bestVol = 0;
        // 候选价格集:所有买价 + 所有卖价(取并集中落在交叉区间的)
        var candidatePrices = new HashSet<decimal>();
        foreach (var b in bids) candidatePrices.Add(b.Price.Value);
        foreach (var a in asks) candidatePrices.Add(a.Price.Value);
        candidatePrices.Add(highestBid);   // 确保边界

        foreach (decimal candidate in candidatePrices)
        {
            // 在该价格下:买单中价格>=candidate的总量,卖单中价格<=candidate的总量
            int buyVol = bids.Where(b => b.Price.Value >= candidate).Sum(b => b.TotalQty.Value);
            int sellVol = asks.Where(a => a.Price.Value <= candidate).Sum(a => a.TotalQty.Value);
            int matched = Math.Min(buyVol, sellVol);
            if (matched > bestVol || (matched == bestVol && candidate < (bestPrice?.Value ?? decimal.MaxValue)))
            {
                // 成交量更大,或成交量相同取更接近前收盘的价格(简化:取较小的)
                bestVol = matched;
                bestPrice = new Price(candidate);
            }
        }

        if (bestPrice == null || bestVol == 0) return null;
        return (bestPrice.Value, bestVol);
    }

    /// <summary>设置现价(集合竞价后确立开盘价用)。</summary>
    public void SetLastPrice(Price price) => _book.RecordTrade(price);

    /// <summary>在指定价格一次性撮合所有交叉订单(集中竞价撮合)。
    /// 买单价格≥auctionPrice 的吃卖单价格≤auctionPrice 的,直到一方耗尽。
    /// 成交价统一为 auctionPrice(最大成交量原则确定的价格)。
    /// 返回所有成交记录。撮合后未成交的限价单保留在簿中。</summary>
    public List<Trade> SweepAtPrice(Price auctionPrice)
    {
        var trades = new List<Trade>();
        decimal p = auctionPrice.Value;

        while (true)
        {
            var bestBid = _book.BestLevel(Side.Buy);
            var bestAsk = _book.BestLevel(Side.Sell);
            // 双方都必须存在且价格交叉(买价≥P 且 卖价≤P)
            if (bestBid == null || bestAsk == null) break;
            if (bestBid.Price.Value < p || bestAsk.Price.Value > p) break;

            var bidOrder = bestBid.Peek();
            var askOrder = bestAsk.Peek();
            int fillQty = Math.Min(bidOrder.RemainingQty.Value, askOrder.RemainingQty.Value);
            if (fillQty <= 0) break;

            var qty = new Quantity(fillQty);
            bidOrder.Fill(qty);
            askOrder.Fill(qty);

            // 成交价统一为竞价价格 auctionPrice
            var trade = new Trade(
                new TradeId(++_tradeSeq), _orderSeq,
                auctionPrice, qty,
                Side.Buy, bidOrder.Participant, bidOrder.Id,
                askOrder.Participant, askOrder.Id);
            trades.Add(trade);

            // 清理已完成的订单
            if (bidOrder.IsFilled || bidOrder.RemainingQty.IsZero)
                _book.Remove(bidOrder);
            if (askOrder.IsFilled || askOrder.RemainingQty.IsZero)
                _book.Remove(askOrder);
        }

        if (trades.Count > 0)
            _book.RecordTrade(auctionPrice);

        return trades;
    }
    public int CancelAll(ParticipantId participant)
    {
        // 简化实现:遍历索引撤单(M1 阶段挂单量不大,够用)
        int n = 0;
        // 由于 Remove 会修改集合,先收集 ID
        var toCancel = _book.AllRestingOrderIds(participant).ToList();
        foreach (var id in toCancel)
        {
            if (Cancel(id, out _)) n++;
        }
        return n;
    }

    /// <summary>枚举指定参与者在簿的全部挂单(含详情)。委托给 OrderBook。</summary>
    public IEnumerable<Order> OrdersFor(ParticipantId participant) => _book.OrdersFor(participant);

    /// <summary>撤销指定参与者某方向的全部挂单(撤买/撤卖)。</summary>
    public int CancelAllBySide(ParticipantId participant, Side side)
    {
        var toCancel = _book.OrdersFor(participant)
            .Where(o => o.Side == side)
            .Select(o => o.Id)
            .ToList();
        int n = 0;
        foreach (var id in toCancel)
        {
            if (Cancel(id, out _)) n++;
        }
        return n;
    }
}

/// <summary>订单簿只读视图(供展示层取盘口五档)。</summary>
public interface IReadOnlyOrderBook
{
    Price? BestBid { get; }
    Price? BestAsk { get; }
    Price? LastPrice { get; }
    IReadOnlyList<(Price Price, Quantity TotalQty)> TopBids(int depth);
    IReadOnlyList<(Price Price, Quantity TotalQty)> TopAsks(int depth);
}

internal sealed class OrderBookSnapshotView : IReadOnlyOrderBook
{
    private readonly OrderBook _book;
    public OrderBookSnapshotView(OrderBook book) => _book = book;
    public Price? BestBid => _book.BestBid;
    public Price? BestAsk => _book.BestAsk;
    public Price? LastPrice => _book.LastTradePrice;
    public IReadOnlyList<(Price, Quantity)> TopBids(int depth) => _book.TopOfBook(Side.Buy, depth);
    public IReadOnlyList<(Price, Quantity)> TopAsks(int depth) => _book.TopOfBook(Side.Sell, depth);
}
