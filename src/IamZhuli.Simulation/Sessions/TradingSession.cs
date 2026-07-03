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
    OrderId? CancelOrderId = null);

/// <summary>
/// 交易会话:撮合引擎 + 参与者账户的协调层。
/// 资金/持仓冻结与结算全部委托给 Account(订单级跟踪);本类只负责下单校验、撮合调用、成交回调路由。
/// </summary>
public sealed class TradingSession
{
    public MatchingEngine Engine { get; }
    private readonly Dictionary<ParticipantId, Account> _accounts = new();

    public TradingSession(MatchingEngine engine) => Engine = engine;

    /// <summary>每笔成交触发(参数=成交价、量、主动方方向)。</summary>
    public event Action<Price, Quantity, Side>? OnTrade;

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

    /// <summary>提交下单请求。返回订单结果(成交、状态)。</summary>
    public OrderResult Submit(OrderRequest req)
    {
        if (!_accounts.TryGetValue(req.Participant, out var acc))
            throw new InvalidOperationException($"参与者 {req.Participant} 未注册账户。");

        // —— 卖单:校验可卖(T+1) ——
        if (req.Side == Side.Sell && !acc.CanSell(req.Quantity))
            throw new InvalidOperationException("可卖持仓不足(含 T+1 锁定)。");

        // —— 创建订单(此时获得 OrderId) ——
        var order = Engine.CreateOrder(req.Participant, req.Side, req.Type, req.Price, req.Quantity);

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
        foreach (var trade in result.Trades)
        {
            var taker = _accounts[trade.TakerId];
            var maker = _accounts[trade.MakerId];
            if (trade.TakerSide == Side.Buy)
            {
                taker.ApplyBuyFill(trade.TakerOrderId, trade.Quantity, trade.Price);
                maker.ApplySellFill(trade.Quantity, trade.Price);
            }
            else
            {
                taker.ApplySellFill(trade.Quantity, trade.Price);
                maker.ApplyBuyFill(trade.MakerOrderId, trade.Quantity, trade.Price);
            }
            OnTrade?.Invoke(trade.Price, trade.Quantity, trade.TakerSide);
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
        if (order.Participant.Equals(participant) && order.Side == Side.Buy)
            _accounts[participant].ReleaseBuyFreezeRemaining(orderId, order.RemainingQty);
        return true;
    }

    /// <summary>日切:所有账户解锁 T+1 持仓。</summary>
    public void OnNewTradingDay()
    {
        foreach (var acc in _accounts.Values) acc.OnNewTradingDay();
    }
}
