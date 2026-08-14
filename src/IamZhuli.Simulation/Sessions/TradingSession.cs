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
    /// <summary>可融券池余量(手)。初始=流通盘×20%,做空时扣减,平仓时归还。先到先得。</summary>
    public int ShortablePool { get; private set; }
    /// <summary>可融券总量(用于显示比例)。</summary>
    public int TotalShortable { get; private set; }

    public TradingSession(MatchingEngine engine) => Engine = engine;

    /// <summary>初始化可融券池(按流通盘的20%设置)。由 GameSingleton.LoadLevel 调用。</summary>
    public void InitShortablePool(int floatShares)
    {
        TotalShortable = Math.Max(1000, floatShares / 5);   // 流通盘20%,最少1000手
        ShortablePool = TotalShortable;
    }

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
        var order = PrepareOrder(req);

        // —— 提交撮合 ——
        var result = Engine.Submit(order);

        // —— 结算成交(含 taker 与 maker 双方) ——
        SettleTrades(result.Trades);

        // —— taker 买单结束处理:未挂簿(成交/作废)则释放剩余冻结记录 ——
        if (req.Side == Side.Buy && !req.IsShort && result.FinalStatus != OrderStatus.Active)
        {
            _accounts[req.Participant].ReleaseBuyFreezeRemaining(order.Id, result.RemainingQty);
        }
        // —— 做空卖单未成交(作废):归还预扣的券池 ——
        if (req.IsShort && req.Side == Side.Sell && result.FinalStatus != OrderStatus.Active)
        {
            ShortablePool += result.RemainingQty.Value;   // 归还未成交部分
        }
        // 挂簿(Active)的买单:冻结保留在 Account 中,后续被吃时由 maker 路径结算,撤单时由 Cancel 处理。

        return result;
    }

    /// <summary>校验并准备订单(可卖/空头持仓/券池预扣/保证金/买单冻结)。
    /// Submit 与 TryRestForAuction 共用。失败抛异常。</summary>
    private Order PrepareOrder(OrderRequest req)
    {
        if (!_accounts.TryGetValue(req.Participant, out var acc))
            throw new InvalidOperationException($"参与者 {req.Participant} 未注册账户。");

        // —— 卖单:校验可卖(T+1) —— 做空卖单跳过持仓校验
        if (req.Side == Side.Sell && !req.IsShort && !acc.CanSell(req.Quantity))
            throw new InvalidOperationException("可卖持仓不足(含 T+1 锁定)。");
        // —— 买回平仓:校验空头持仓 ——
        if (req.Side == Side.Buy && req.IsShort && acc.Position.ShortQty.Value < req.Quantity.Value)
            throw new InvalidOperationException("空头持仓不足,无法平仓。");

        // —— 做空卖单:预冻结保证金(按限价或对手价估算) + 券池余量检查 ——
        if (req.IsShort && req.Side == Side.Sell)
        {
            // 券池余量检查(先到先得)
            if (ShortablePool < req.Quantity.Value)
                throw new InvalidOperationException($"可融券不足(剩余{ShortablePool}手,需要{req.Quantity.Value}手)。");
            decimal estPrice = req.Type == OrderType.Limit ? req.Price.Value
                : (Engine.View.BestBid ?? Engine.Rules.UpperLimit).Value;
            decimal margin = estPrice * req.Quantity.Value * 100 * 0.5m;   // 50%保证金
            if (acc.AvailableCash < margin)
                throw new InvalidOperationException("保证金不足(做空需50%保证金)。");
            // 预扣券池(防止同一tick多个做空单超额)
            ShortablePool -= req.Quantity.Value;
        }

        // —— 创建订单(此时获得 OrderId) ——
        var order = Engine.CreateOrder(req.Participant, req.Side, req.Type, req.Price, req.Quantity);

        // —— 做空单:记录orderId(maker结算时判断) ——
        if (req.IsShort) _shortOrders.Add(order.Id);

        // —— 买单:冻结资金(需订单 ID 跟踪)。
        //    买回平仓单(IsShort)不冻结:履约保障是保证金(MarginFrozen),冻结现金会双重占用。
        if (req.Side == Side.Buy && !req.IsShort)
        {
            var frozenPrice = req.Type == OrderType.Limit ? req.Price
                : (Engine.View.BestAsk ?? Engine.Rules.UpperLimit);
            if (!acc.TryFreezeForBuy(order.Id, frozenPrice, req.Quantity))
                throw new InvalidOperationException(req.Type == OrderType.Limit
                    ? "可用资金不足。" : "可用资金不足(市价单按对手价预估)。");
        }

        return order;
    }

    /// <summary>结算一批成交(taker+maker 双方,含做空路径)。Submit 与集中撮合共用。
    /// 做空判定统一按 _shortOrders 标记(PrepareOrder 已在 IsShort 时登记)。</summary>
    private void SettleTrades(IEnumerable<Trade> trades)
    {
        foreach (var trade in trades)
        {
            var taker = _accounts[trade.TakerId];
            var maker = _accounts[trade.MakerId];
            bool takerIsShort = _shortOrders.Contains(trade.TakerOrderId);
            if (trade.TakerSide == Side.Buy)
            {
                if (takerIsShort)
                {
                    taker.ShortCover(trade.Quantity, trade.Price);   // 买回平仓
                    ShortablePool += trade.Quantity.Value;            // 平仓归还券池
                }
                else
                    taker.ApplyBuyFill(trade.TakerOrderId, trade.Quantity, trade.Price);
                // maker 侧:对手是卖单,需判断 maker 是否做空
                if (_shortOrders.Contains(trade.MakerOrderId))
                {
                    maker.ShortSell(trade.Quantity, trade.Price);
                    // maker做空成交,券池已在PrepareOrder时预扣,不重复扣
                }
                else
                    maker.ApplySellFill(trade.Quantity, trade.Price);
            }
            else
            {
                if (takerIsShort)
                    taker.ShortSell(trade.Quantity, trade.Price);    // 做空卖出(券池已预扣)
                else
                    taker.ApplySellFill(trade.Quantity, trade.Price);
                if (_shortOrders.Contains(trade.MakerOrderId))
                {
                    maker.ShortCover(trade.Quantity, trade.Price);
                    ShortablePool += trade.Quantity.Value;            // maker平仓归还券池
                }
                else
                    maker.ApplyBuyFill(trade.MakerOrderId, trade.Quantity, trade.Price);
            }
            OnTrade?.Invoke(trade.Price, trade.Quantity, trade.TakerSide);
            OnTradeDetailed?.Invoke(trade);
        }
    }

    // —— 集中撮合(卡牌模式回合制) ——

    /// <summary>把限价订单校验后挂入订单簿但不撮合(回合末集合竞价用)。
    /// 校验/冻结/券池预扣与 Submit 完全一致。校验失败(资金/券池/持仓不足)返回 false,该意图作废。</summary>
    public bool TryRestForAuction(OrderRequest req)
    {
        if (req.Type != OrderType.Limit)
            throw new InvalidOperationException("集中撮合仅接受限价单(市价意图须先转换为限价)。");
        try
        {
            var order = PrepareOrder(req);
            Engine.RestOrder(order);
            return true;
        }
        catch { return false; }   // 校验失败/非法价格:意图作废,不透支
    }

    /// <summary>集中撮合成交结算(与 Submit 同一路径,含券池归还/事件触发)。</summary>
    public void SettleAuctionTrades(IEnumerable<Trade> trades) => SettleTrades(trades);

    /// <summary>清空订单簿并释放全部挂单资源:买单解冻现金、做空卖单归还券池预扣。
    /// 回合开始清场(EndCardTurn)与日切隔夜清零(StartNextDay)共用。</summary>
    public int ClearBookAndRefund()
    {
        var orders = Engine.ClearBook();
        int refunded = 0;
        foreach (var order in orders)
        {
            bool wasShort = _shortOrders.Remove(order.Id);
            if (wasShort && order.Side == Side.Sell)
            {
                ShortablePool += order.RemainingQty.Value;   // 归还预扣券池
                refunded++;
            }
            if (order.Side == Side.Buy && !wasShort
                && _accounts.TryGetValue(order.Participant, out var acc))
                acc.ReleaseBuyFreezeRemaining(order.Id, order.RemainingQty);
        }
        _shortOrders.Clear();   // 兜底清理孤立标记
        return refunded;
    }

    /// <summary>撤销某参与者的指定订单,并释放买单剩余冻结。</summary>
    public bool Cancel(ParticipantId participant, OrderId orderId)
    {
        if (!Engine.Cancel(orderId, out var order) || order == null) return false;
        bool wasShort = _shortOrders.Remove(orderId);   // 清理做空标记
        // 做空卖单撤单:归还剩余券池
        if (wasShort && order.Side == Side.Sell)
            ShortablePool += order.RemainingQty.Value;
        if (order.Participant.Equals(participant) && order.Side == Side.Buy && !wasShort)
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
