using IamZhuli.Simulation.Cards;
using Xunit;
using Xunit.Abstractions;

namespace IamZhuli.Presenter.Tests;

/// <summary>
/// 双牌组合全扫描:21张牌的两两组合(含同牌对,共231种),每种在真实对局里同回合打出,
/// 断言不崩溃+拍卖合法+全局不变量成立。按首牌分Theory,失败可精确定位组合。
/// 能量不够自动跳回合攒(上限10,最多3次跳过);部分组合合法失败(无持仓卖出等)不视为错误。
/// 特别关注:同牌对(累计校验)、多空同向、舆论+操盘交叉。
/// </summary>
public sealed class PairScanTests
{
    private readonly ITestOutputHelper _out;
    public PairScanTests(ITestOutputHelper o) => _out = o;

    private static readonly List<CardDefinition> Cards = CardDefinition.Library.ToList();

    public static TheoryData<string> FirstCards()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cards) data.Add(c.Name);
        return data;
    }

    private void ScanPair(string a, string b)
    {
        var defA = Cards.First(c => c.Name == a);
        var defB = Cards.First(c => c.Name == b);
        string tag = $"[{a}+{b}]";

        var core = Harness.NewGame("accumulate", a, b);

        // 攒能量:跳过回合直到够双牌(上限10,最多4次跳过)
        int need = defA.EnergyCost + defB.EnergyCost;
        int skips = 0;
        while (core.BuildSnapshot().CardState!.Energy < need && skips++ < 4)
            Harness.NextTurn(core);

        // 同回合连出(允许失败:蛰伏冷却/无持仓/资金不足等是合法拦截)
        bool p1 = Harness.Play(core, a);
        bool p2 = Harness.Play(core, b);

        core.EndCardTurn();
        var ra = core.LastAuction!;
        Assert.NotNull(ra);
        Assert.True(ra.Trades.All(t => t.Price > 0 && t.Qty > 0), $"{tag} 非法成交");

        // 走完 Resolve(延迟效果/散户反应/T+1边界)后校验不变量
        var snap = Harness.NextTurn(core);
        Invariants.Check(snap, tag);

        // 异常模式记录(双牌都成功但零成交:值得人工看的组合)
        if (p1 && p2 && ra.Volume == 0)
            _out.WriteLine($"⚠ {tag} 双牌成功但拍卖零成交(价格: {snap.LastPrice})");
    }

    [Theory]
    [MemberData(nameof(FirstCards))]
    public void Scan_AllPairs_ForFirstCard(string first)
    {
        int idx = Cards.FindIndex(c => c.Name == first);
        int count = 0;
        for (int j = idx; j < Cards.Count; j++)   // j>=idx:无序对去重(含同牌对)
        {
            ScanPair(first, Cards[j].Name);
            count++;
        }
        _out.WriteLine($"[{first}] × {count} 组合扫描完成");
    }
}
