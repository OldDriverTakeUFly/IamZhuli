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

    // —— 融券做空 ——
    /// <summary>保证金(做空需要冻结的资金,用于爆仓保护)。</summary>
    public decimal MarginFrozen { get; private set; }

    /// <summary>融券做空:卖出借来的券,获得现金,冻结保证金。
    /// 保证金 = 卖出金额 × 保证金比例(默认50%)。</summary>
    public void ShortSell(Quantity qty, Price price, decimal marginRatio = 0.5m)
    {
        decimal proceeds = price.Value * qty.Value * 100;           // 卖出获得现金
        decimal margin = proceeds * marginRatio;                     // 冻结保证金
        Cash += proceeds;
        MarginFrozen += margin;
        Position.ApplyShortSell(qty, price);
        TotalSoldQty += qty.Value;
    }

    /// <summary>买回平仓:还券,释放保证金,结算盈亏。
    /// 买回花费从现金扣,保证金释放,空头盈亏已由 Position 算出。</summary>
    public void ShortCover(Quantity qty, Price price)
    {
        decimal cost = price.Value * qty.Value * 100;                // 买回花费
        Cash -= cost;
        decimal pnl = Position.ApplyShortCover(qty, price);          // 平仓盈亏
        Cash += pnl;                                                  // 盈亏结算到现金
        // 释放保证金(按平仓比例)
        decimal releaseRatio = Position.ShortQty.Value == 0 ? 1m :
            (double)qty.Value / (Position.ShortQty.Value + qty.Value) > 1 ? 1m :
            (decimal)qty.Value / (Position.ShortQty.Value + qty.Value);
        decimal release = MarginFrozen * releaseRatio;
        MarginFrozen -= release;
        Cash += release;
        TotalBoughtQty += qty.Value;
    }

    /// <summary>维持担保比例 = 总权益 / (空头持仓市值 + 保证金)。
    /// 低于 130% 时触发爆仓(强制平仓)。高于则安全。</summary>
    public decimal MaintenanceRatio(Price markPrice)
    {
        decimal shortValue = Position.HasShort ? markPrice.Value * Position.ShortQty.Value * 100 : 0;
        decimal totalDebt = shortValue + MarginFrozen;
        if (totalDebt <= 0) return 10m;   // 无负债,比例无限大
        return TotalEquity(markPrice) / totalDebt;
    }

    /// <summary>总权益 = 现金 + 多头市值 - 空头负债 + 保证金。
    /// 空头的盈亏已实时反映:空头持仓市值随价格变动。</summary>
    public decimal TotalEquity(Price markPrice)
    {
        decimal longValue = Position.Total.IsZero ? 0m : markPrice.Value * Position.Total.Value * 100;
        decimal shortDebt = Position.HasShort ? markPrice.Value * Position.ShortQty.Value * 100 : 0;
        // 空头:卖出时已收现金,持仓是负债(需买回)。权益 = 现金 + 多头 + 保证金 - 空头负债
        return Cash + longValue + MarginFrozen - shortDebt;
    }

    public override string ToString()
        => $"现金{Cash / 10000:F2}万(可用{AvailableCash / 10000:F2}万) 持仓{Position}";
}
