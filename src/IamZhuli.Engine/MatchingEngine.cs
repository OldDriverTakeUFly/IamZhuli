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

    /// <summary>撤销某参与者的全部挂单(如玩家关卡通不过/退出)。返回被撤订单数。</summary>
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
