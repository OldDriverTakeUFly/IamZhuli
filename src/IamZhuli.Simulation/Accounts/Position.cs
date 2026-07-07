using IamZhuli.Core;

namespace IamZhuli.Simulation.Accounts;

/// <summary>
/// 持仓。记录总持仓、可用持仓(T+1 解锁后可卖)、持仓成本。
/// T+1: 当日买入的筹码记入 T1Locked,次日开盘解锁为 Available。
/// </summary>
public sealed class Position
{
    public Quantity Total => new(Available.Value + T1Locked.Value);
    /// <summary>可用(可卖出)。</summary>
    public Quantity Available { get; private set; }
    /// <summary>当日买入锁定,次日解锁。</summary>
    public Quantity T1Locked { get; private set; }
    /// <summary>持仓成本价(加权平均买入价)。</summary>
    public Price AverageCost { get; private set; }

    public bool IsEmpty => Total.IsZero;

    // —— 空头持仓(融券做空) ——
    /// <summary>空头持仓量(手)。正数=做空了多少。0=无空头。</summary>
    public Quantity ShortQty { get; private set; } = Quantity.Zero;
    /// <summary>空头平均卖出价(做空成本)。平仓时按此价计算盈亏。</summary>
    public Price ShortCost { get; private set; } = Price.Zero;
    /// <summary>是否有空头持仓。</summary>
    public bool HasShort => ShortQty.Value > 0;

    /// <summary>融券做空:增加空头持仓,更新空头成本。</summary>
    public void ApplyShortSell(Quantity qty, Price price)
    {
        if (qty.IsZero) return;
        int newTotal = ShortQty.Value + qty.Value;
        if (newTotal == 0) { ShortCost = Price.Zero; return; }
        decimal oldCost = ShortCost.Value * ShortQty.Value;
        ShortCost = new Price((oldCost + price.Value * qty.Value) / newTotal);
        ShortQty = new Quantity(newTotal);
    }

    /// <summary>买回平仓:减少空头持仓。返回平仓盈亏(正=赚)。</summary>
    public decimal ApplyShortCover(Quantity qty, Price price)
    {
        if (qty.IsZero || ShortQty.Value == 0) return 0;
        int coverQty = Math.Min(qty.Value, ShortQty.Value);
        decimal pnl = (ShortCost.Value - price.Value) * coverQty * 100;   // 做空赚=高卖低买
        ShortQty = new Quantity(ShortQty.Value - coverQty);
        if (ShortQty.IsZero) ShortCost = Price.Zero;
        return pnl;
    }

    public Position() { }

    /// <summary>买入成交:增加锁定筹码,更新加权成本。</summary>
    public void ApplyBuy(Quantity qty, Price price)
    {
        if (qty.IsZero) return;
        T1Locked = new Quantity(T1Locked.Value + qty.Value);

        int newTotal = Total.Value;
        if (newTotal == 0)
        {
            AverageCost = price;
        }
        else
        {
            // 加权平均成本 = (原成本×原量 + 新买入价×新量) / 新总量
            decimal oldCost = AverageCost.Value * (newTotal - qty.Value);
            AverageCost = new Price((oldCost + price.Value * qty.Value) / newTotal);
        }
    }

    /// <summary>卖出成交:从可用筹码扣减。</summary>
    public void ApplySell(Quantity qty)
    {
        if (qty.Value > Available.Value)
            throw new InvalidOperationException($"可卖不足:需要{qty},可用{Available}");
        Available = new Quantity(Available.Value - qty.Value);
        if (Total.IsZero) AverageCost = Price.Zero;
    }

    /// <summary>T+1 解锁:次日开盘把锁定筹码转为可用。</summary>
    public void UnlockT1()
    {
        Available = new Quantity(Available.Value + T1Locked.Value);
        T1Locked = Quantity.Zero;
    }

    /// <summary>注入初始持仓(视作昨日已有,直接进入 Available,绕过 T+1)。仅供 NPC/做市商初始化用。</summary>
    public void Seed(Quantity qty, Price cost)
    {
        Available = new Quantity(Available.Value + qty.Value);
        AverageCost = cost;
    }

    /// <summary>该持仓按给定现价计算的浮动盈亏(金额,单位元)。</summary>
    public decimal FloatingProfit(Price markPrice)
        => Total.IsZero ? 0m : (markPrice.Value - AverageCost.Value) * Total.Value * 100;  // ×100股/手

    public override string ToString() =>
        IsEmpty ? "空仓" : $"{Total}(可用{Available}+T1锁{T1Locked}) 成本{AverageCost}";
}
