using IamZhuli.Core;
using IamZhuli.Engine;

namespace IamZhuli.Simulation.Accounts;

/// <summary>
/// 交易账户。管理现金、可用现金、持仓。
/// 买单冻结资金按订单精确跟踪:挂单时冻结,成交时按成交价结算并同步释放该成交部分的冻结,
/// 撤单/作废时释放剩余冻结。卖单只校验可卖持仓(T+1 已在 Position 体现),无需冻结资金。
/// </summary>
public sealed class Account
{
    public ParticipantId Id { get; }
    /// <summary>总现金(可用 + 冻结)。</summary>
    public decimal Cash { get; private set; }
    public Position Position { get; } = new();
    /// <summary>累计买入量(手),供出货比例判定。</summary>
    public int TotalBoughtQty { get; private set; }
    /// <summary>累计卖出量(手)。</summary>
    public int TotalSoldQty { get; private set; }

    /// <summary>订单冻结记录:订单ID → (冻结价, 冻结量)。</summary>
    private readonly Dictionary<OrderId, (Price price, Quantity qty)> _buyFreezes = new();

    /// <summary>冻结现金(挂买单占用的资金)。</summary>
    public decimal FrozenCash { get; private set; }
    /// <summary>可用现金 = 总现金 - 冻结。</summary>
    public decimal AvailableCash => Cash - FrozenCash;

    public Account(ParticipantId id, decimal initialCash)
    {
        Id = id;
        Cash = initialCash;
    }

    // —— 买单冻结(按订单跟踪) ——
    /// <summary>挂/下买单时冻结资金。返回是否足够。</summary>
    public bool TryFreezeForBuy(OrderId orderId, Price price, Quantity qty)
    {
        decimal need = price.Value * qty.Value * 100;
        if (need > AvailableCash) return false;
        FrozenCash += need;
        _buyFreezes[orderId] = (price, qty);
        return true;
    }

    /// <summary>
    /// 买单成交结算:按实际成交价扣现金、转入持仓;并按冻结价同步释放该成交部分的冻结(差额补回现金)。
    /// </summary>
    public void ApplyBuyFill(OrderId orderId, Quantity qty, Price fillPrice)
    {
        Cash -= fillPrice.Value * qty.Value * 100;
        Position.ApplyBuy(qty, fillPrice);
        TotalBoughtQty += qty.Value;

        // 释放该成交部分对应的冻结:冻结价×成交量 释放,差额(冻结价-成交价)补回现金
        if (_buyFreezes.TryGetValue(orderId, out var f))
        {
            FrozenCash -= f.price.Value * qty.Value * 100;
            Cash += (f.price.Value - fillPrice.Value) * qty.Value * 100;
        }
    }

    /// <summary>撤单/作废时释放该订单剩余(未成交)部分的冻结,并清除冻结记录。</summary>
    public void ReleaseBuyFreezeRemaining(OrderId orderId, Quantity remainingQty)
    {
        if (_buyFreezes.TryGetValue(orderId, out var f) && !remainingQty.IsZero)
            FrozenCash -= f.price.Value * remainingQty.Value * 100;
        _buyFreezes.Remove(orderId);
    }

    // —— 卖单 ——
    public bool CanSell(Quantity qty) => Position.Available.Value >= qty.Value;

    /// <summary>卖单成交:扣减持仓,增加现金。</summary>
    public void ApplySellFill(Quantity qty, Price fillPrice)
    {
        Position.ApplySell(qty);
        Cash += fillPrice.Value * qty.Value * 100;
        TotalSoldQty += qty.Value;
    }

    /// <summary>T+1 日切:解锁持仓。</summary>
    public void OnNewTradingDay() => Position.UnlockT1();

    /// <summary>非交易资金扣减(消息系统/水军等费用)。</summary>
    public void DebitCash(decimal amount) => Cash -= amount;

    /// <summary>总权益 = 现金 + 持仓市值。</summary>
    public decimal TotalEquity(Price markPrice)
        => Cash + (Position.Total.IsZero ? 0m : markPrice.Value * Position.Total.Value * 100);

    public override string ToString()
        => $"现金{Cash / 10000:F2}万(可用{AvailableCash / 10000:F2}万) 持仓{Position}";
}
