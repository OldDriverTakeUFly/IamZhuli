using Xunit;

namespace IamZhuli.Presenter.Tests;

/// <summary>全局不变量断言(fuzz/组合扫描/矩阵共用):现金守恒/券池边界/冻结/价格/挂单自洽。</summary>
internal static class Invariants
{
    // ── 不变量:每步必守 ──

    public static void Check(MarketSnapshotDto s, string tag)
    {
        var a = s.Account;
        Assert.False(double.IsNaN((double)a.TotalEquity) || double.IsInfinity((double)a.TotalEquity),
            $"{tag} 权益非有限值");
        Assert.True(a.Cash >= 0, $"{tag} 现金为负: {a.Cash}");
        Assert.True(a.AvailableCash >= 0, $"{tag} 可用现金为负: {a.AvailableCash}");
        Assert.True(a.Cash - a.AvailableCash >= 0, $"{tag} 冻结为负: {a.Cash - a.AvailableCash}");
        Assert.True(a.MarginFrozen >= 0, $"{tag} 保证金为负: {a.MarginFrozen}");
        Assert.True(a.PositionTotal >= 0 && a.PositionAvailable >= 0 && a.PositionT1Locked >= 0,
            $"{tag} 持仓为负");
        Assert.True(a.PositionAvailable + a.PositionT1Locked <= a.PositionTotal + 1,
            $"{tag} 可卖+锁定 超过 总持仓");
        Assert.True(a.ShortQty >= 0, $"{tag} 空头为负: {a.ShortQty}");
        if (s.LastPrice is { } p)
            Assert.True(p >= s.LowerLimit - 0.001m && p <= s.UpperLimit + 0.001m,
                $"{tag} 价格越界: {p} 不在 [{s.LowerLimit},{s.UpperLimit}]");
        Assert.True(s.ShortablePool >= 0 && s.ShortablePool <= s.TotalShortable,
            $"{tag} 券池越界: {s.ShortablePool}/{s.TotalShortable}");
        Assert.True(s.RegulatorHeat >= 0 && s.RegulatorHeat <= 100, $"{tag} 交易热越界: {s.RegulatorHeat}");
        Assert.True(s.InfoHeat >= 0 && s.InfoHeat <= 100, $"{tag} 信息热越界: {s.InfoHeat}");
        if (s.CardState is { } cs)
        {
            Assert.True(cs.Energy >= 0 && cs.Energy <= 12, $"{tag} 能量越界: {cs.Energy}");
            Assert.True(cs.Hand.Count <= 5, $"{tag} 手牌超限: {cs.Hand.Count}");
            Assert.True(cs.CardsPlayed <= cs.MaxCardsPerTurn, $"{tag} 本回合出牌超限");
        }
        foreach (var ra in s.OpenOrders)
            Assert.True(ra.RemainingQty >= 0 && ra.FilledQty + ra.RemainingQty == ra.TotalQty,
                $"{tag} 挂单数量不自洽");
    }

}
