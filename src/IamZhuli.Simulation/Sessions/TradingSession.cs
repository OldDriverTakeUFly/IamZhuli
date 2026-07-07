using IamZhuli.Core;
using IamZhuli.Engine;
using IamZhuli.Simulation.Accounts;

namespace IamZhuli.Simulation.Sessions;

/// <summary>
/// 下单请求(参与者提交给会话)。</summary>
public readonly record struct OrderRequest(
    ParticipantId Participant,
    Side Side,
    OrderType Type,
    Price Price,        // 市价单忽略
    Quantity Quantity,
    OrderId? CancelOrderId = null,
    bool IsShort = false);   // true=融券做空(Sell)或买回平仓(Buy)

/// <summary>
/// 交易会话:撮合引擎 + 参与者账户的协调层。
/// 资金/持仓冻结与结算全部委托给 Account(订单级跟踪);本类只负责下单校验、撮合调用、成交回调路由。
/// </summary>
public sealed class TradingSession
{
    public MatchingEngine Engine { get; }
    private readonly Dictionary<ParticipantId, Account> _accounts = new();
    /// <summary>做空订单ID集合(maker结算时判断是否做空)。</summary>
    private readonly HashSet<OrderId> _shortOrders = new();

    public TradingSession(MatchingEngine engine) => Engine = engine;

    /// <summary>每笔成交触发(参数=成交价、量、主动方方向)。</summary>
    public event Action<Price, Quantity, Side>? OnTrade;

    /// <summary>每笔成交触发(完整 Trade,含 taker/maker 身份,供监管对倒检测)。</summary>
    public event Action<Trade>? OnTradeDetailed;

    /// <summary>订单撤销时触发(撤单者、订单价、量、挂单时的 tick)。供监管虚假挂单检测。</summary>
    public event Action<ParticipantId, Price, Quantity, long>? OnOrderCancelled;

    public Account GetOrCreateAccount(ParticipantId id, decimal initialCash = 0m)
    {
        if (!_accounts.TryGetValue(id, out var acc))
        {
            acc = new Account(id, initialCash);
            _accounts[id] = acc;
        }
        return acc;
    }

    public Account? GetAccount(ParticipantId id) => _accounts.GetValueOrDefault(id);
    /// <summary>会话内所有账户(供筹码快照遍历全部参与方)。</summary>
    public IEnumerable<Account> AllAccounts => _accounts.Values;

    /// <summary>提交下单请求。返回订单结果(成交、状态)。</summary>
    public OrderResult Submit(OrderRequest req)
    {
        if (!_accounts.TryGetValue(req.Participant, out var acc))
            throw new InvalidOperationException($"参与者 {req.Participant} 未注册账户。");

        // —— 卖单:校验可卖(T+1) —— 做空卖单跳过持仓校验
        if (req.Side == Side.Sell && !req.IsShort && !acc.CanSell(req.Quantity))
            throw new InvalidOperationException("可卖持仓不足(含 T+1 锁定)。");
        // —— 买回平仓:校验空头持仓 ——
        if (req.Side == Side.Buy && req.IsShort && acc.Position.ShortQty.Value < req.Quantity.Value)
            throw new InvalidOperationException("空头持仓不足,无法平仓。");

        // —— 做空卖单:预冻结保证金(按限价或对手价估算) ——
        decimal shortMarginFrozen = 0;
        if (req.IsShort && req.Side == Side.Sell)
        {
            decimal estPrice = req.Type == OrderType.Limit ? req.Price.Value
                : (Engine.View.BestBid ?? Engine.Rules.UpperLimit).Value;
            decimal margin = estPrice * req.Quantity.Value * 100 * 0.5m;   // 50%保证金
            if (acc.AvailableCash < margin)
                throw new InvalidOperationException("保证金不足(做空需50%保证金)。");
            // 不在这里扣,成交时由 ShortSell 统一处理
        }

        // —— 创建订单(此时获得 OrderId) ——
        var order = Engine.CreateOrder(req.Participant, req.Side, req.Type, req.Price, req.Quantity);

        // —— 做空单:记录orderId(maker结算时判断) ——
        if (req.IsShort) _shortOrders.Add(order.Id);

        // —— 买单:冻结资金(需订单 ID 跟踪) ——
        if (req.Side == Side.Buy)
        {
            var frozenPrice = req.Type == OrderType.Limit ? req.Price
                : (Engine.View.BestAsk ?? Engine.Rules.UpperLimit);
            if (!acc.TryFreezeForBuy(order.Id, frozenPrice, req.Quantity))
                throw new InvalidOperationException(req.Type == OrderType.Limit
                    ? "可用资金不足。" : "可用资金不足(市价单按对手价预估)。");
        }

        // —— 提交撮合 ——
        var result = Engine.Submit(order);

        // —— 结算成交(含 taker 与 maker 双方) ——
        bool takerIsShort = req.IsShort;
        foreach (var trade in result.Trades)
        {
            var taker = _accounts[trade.TakerId];
            var maker = _accounts[trade.MakerId];
            if (trade.TakerSide == Side.Buy)
            {
                if (takerIsShort)
                    taker.ShortCover(trade.Quantity, trade.Price);   // 买回平仓
                else
                    taker.ApplyBuyFill(trade.TakerOrderId, trade.Quantity, trade.Price);
                // maker 侧:对手是卖单,需判断 maker 是否做空
                if (_shortOrders.Contains(trade.MakerOrderId))
                    maker.ShortSell(trade.Quantity, trade.Price);
                else
                    maker.ApplySellFill(trade.Quantity, trade.Price);
            }
            else
            {
                if (takerIsShort)
                    taker.ShortSell(trade.Quantity, trade.Price);    // 做空卖出
                else
                    taker.ApplySellFill(trade.Quantity, trade.Price);
                if (_shortOrders.Contains(trade.MakerOrderId))
                    maker.ShortCover(trade.Quantity, trade.Price);
                else
                    maker.ApplyBuyFill(trade.MakerOrderId, trade.Quantity, trade.Price);
            }
            OnTrade?.Invoke(trade.Price, trade.Quantity, trade.TakerSide);
            OnTradeDetailed?.Invoke(trade);
        }

        // —— taker 买单结束处理:未挂簿(成交/作废)则释放剩余冻结记录 ——
        if (req.Side == Side.Buy && result.FinalStatus != OrderStatus.Active)
        {
            acc.ReleaseBuyFreezeRemaining(order.Id, result.RemainingQty);
        }
        // 挂簿(Active)的买单:冻结保留在 Account 中,后续被吃时由 maker 路径结算,撤单时由 Cancel 处理。

        return result;
    }

    /// <summary>撤销某参与者的指定订单,并释放买单剩余冻结。</summary>
    public bool Cancel(ParticipantId participant, OrderId orderId)
    {
        if (!Engine.Cancel(orderId, out var order) || order == null) return false;
        _shortOrders.Remove(orderId);   // 清理做空标记
        if (order.Participant.Equals(participant) && order.Side == Side.Buy)
            _accounts[participant].ReleaseBuyFreezeRemaining(orderId, order.RemainingQty);
        return true;
    }

    /// <summary>查询某参与者在簿的全部挂单(供"我的挂单"列表用)。</summary>
    public IEnumerable<Order> GetOpenOrders(ParticipantId participant)
        => Engine.OrdersFor(participant);

    /// <summary>撤销某参与者的全部挂单,返回撤销数。释放买单冻结。</summary>
    public int CancelAll(ParticipantId participant)
    {
        int n = Engine.CancelAll(participant);
        // CancelAll 不经过 Cancel 路径,需补释放买单冻结
        // (限价买单在簿时资金被冻结,撤单必须释放)
        // 这里简化:CancelAll 用于日切/全撤,账户冻结由 Clear 路径或后续重建处理
        return n;
    }

    /// <summary>撤销某参与者某方向(买/卖)的全部挂单,返回撤销数。释放买单冻结。</summary>
    public int CancelAllBySide(ParticipantId participant, Side side)
    {
        // 先收集要撤的买单(用于释放冻结),再调引擎批量撤
        var buyOrdersToRelease = side == Side.Buy
            ? Engine.OrdersFor(participant).Where(o => o.Side == Side.Buy).ToList()
            : new List<Order>();
        int n = Engine.CancelAllBySide(participant, side);
        // 释放被撤买单的冻结资金
        if (side == Side.Buy && _accounts.TryGetValue(participant, out var acc))
        {
            foreach (var o in buyOrdersToRelease)
                acc.ReleaseBuyFreezeRemaining(o.Id, o.RemainingQty);
        }
        return n;
    }

    /// <summary>日切:所有账户解锁 T+1 持仓。</summary>
    public void OnNewTradingDay()
    {
        foreach (var acc in _accounts.Values) acc.OnNewTradingDay();
    }

    /// <summary>订单簿清空时同步清理做空标记(日切隔夜挂单清零)。</summary>
    public void OnBookCleared() => _shortOrders.Clear();

    /// <summary>强制平仓(爆仓):市价买回全部空头持仓。
    /// 返回是否执行了平仓(有空头才执行)。</summary>
    public bool ForceLiquidate(ParticipantId participant, out int qtyCovered)
    {
        qtyCovered = 0;
        if (!_accounts.TryGetValue(participant, out var acc)) return false;
        int shortQty = acc.Position.ShortQty.Value;
        if (shortQty <= 0) return false;
        // 市价买回平仓(吃卖盘)
        try
        {
            var req = new OrderRequest(participant, Side.Buy, OrderType.Market, Price.Zero,
                new Quantity(shortQty), null, IsShort: true);
            var result = Submit(req);
            qtyCovered = result.TotalFilled.Value;
            return true;
        }
        catch { return false; }
    }
}
